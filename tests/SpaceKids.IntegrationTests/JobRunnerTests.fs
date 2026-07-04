module JobRunnerTests

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Xunit
open Microsoft.AspNetCore.Mvc.Testing
open SpaceKids.Core.Dsl
open SpaceKids.Core.Scheduler
open SpaceKids.FakeSpaceTraders
open SpaceKids.FakeSpaceTraders.EntryPoint
open SpaceKids.SpaceTraders
open SpaceKids.Server
open SpaceKids.Server.Persistence

/// Milestone 6 (§14/§19): exercises `JobRunner` — the minimal foreground shell driving
/// the pure `Step.step` core — against the fake, over real HTTP, through the real
/// `RequestQueue`. Drives the queue via `RequestQueue.dispatchNextForTests()` on a
/// manual pump loop rather than relying on the real `RequestQueue.Worker` background
/// service: in this `WebApplicationFactory` test-hosting context the hosted-service
/// Worker does not reliably dispatch (confirmed by direct observation — jobs never
/// left `AwaitingApiResponse`), so this matches the same manual-pump convention the
/// existing Milestone 5 queue tests already use, rather than a new, untested path.
type private JobFixture() =
    let factory = new WebApplicationFactory<Program>()
    member _.Factory = factory
    member _.RawClient = factory.CreateClient()
    member _.Client = new SpaceTradersClient(factory.CreateClient())

    interface IDisposable with
        member _.Dispose() = factory.Dispose()

let private tempDbPath () =
    Path.Combine(Path.GetTempPath(), $"spacekids-job-test-{Guid.NewGuid()}.db")

let private deleteDbFiles (dbPath: string) =
    SqliteConnection.ClearAllPools()

    for suffix in [ ""; "-shm"; "-wal" ] do
        let path = dbPath + suffix
        if File.Exists(path) then File.Delete(path)

let private setFaultMode (rawClient: HttpClient) (mode: string) =
    let body = JsonSerializer.Serialize({| mode = mode |})
    use content = new StringContent(body, Encoding.UTF8, "application/json")
    rawClient.PostAsync("/_fault/mode", content) |> Async.AwaitTask |> Async.RunSynchronously |> ignore

/// Runs `work` (a function of a cancellation token) with a background loop draining
/// the queue every 10ms for as long as `work` runs, and a hard overall deadline so a
/// genuine bug can never hang the test process — it fails loudly instead.
let private withPumpedQueue (deadlineSeconds: float) (work: unit -> 'a) : 'a =
    use cts = new CancellationTokenSource()

    let pump =
        Task.Run(fun () ->
            while not cts.Token.IsCancellationRequested do
                try
                    RequestQueue.dispatchNextForTests () |> Async.RunSynchronously |> ignore
                with _ ->
                    ()

                Thread.Sleep(10))

    try
        let workTask = Task.Run(fun () -> work ())

        if not (workTask.Wait(TimeSpan.FromSeconds(deadlineSeconds))) then
            failwith $"Test exceeded its {deadlineSeconds}s deadline — treat as a bug, not a slow test."

        workTask.Result
    finally
        cts.Cancel()
        try
            pump.Wait(TimeSpan.FromSeconds(2.0)) |> ignore
        with _ ->
            ()

/// Runs `body` against a fresh temp db with every process-wide singleton this test
/// touches (`RequestQueue`, `JobRunner`, the fake's mutable ship/agent/fault/clock
/// state) reset first and restored after.
let private withJobTest (rawClient: HttpClient) (body: string -> unit) =
    RequestQueue.resetForTests ()
    JobRunner.resetForTests ()
    App.resetForTests ()
    setFaultMode rawClient "normal"
    let dbPath = tempDbPath ()

    try
        MigrationRunner.run dbPath
        body dbPath
    finally
        setFaultMode rawClient "normal"
        RequestQueue.resetForTests ()
        JobRunner.resetForTests ()
        App.resetForTests ()
        deleteDbFiles dbPath

let private program (instructions: Instruction list) : CompiledProgram =
    { version = 1
      customBlocks = Map.empty
      instructions = instructions }

[<Fact>]
let ``happy path runs orbit, navigate, dock, extract, and sell to completion`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.fixedTravelSeconds <- 0.2
        App.fixedCooldownSeconds <- 0.2

        let instructions =
            [ ApiAction("b1", "orbit", Map.empty)
              ApiAction("b2", "navigate", Map [ "destination", Literal(StringLit "X1-TEST-B2") ])
              ApiAction("b3", "dock", Map.empty)
              ApiAction("b4", "extract", Map.empty)
              ApiAction(
                  "b5",
                  "sellGood",
                  Map [ "tradeSymbol", Literal(StringLit "IRON"); "units", Literal(NumberLit 5.0) ]
              ) ]

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let jobId = JobRunner.startJob (program instructions) "FAKE-AGENT-1" initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)
            // extracted a fixed 10 units of IRON, sold 5 — exactly one of each.
            Assert.Equal(5, job.lastKnownShip.Value.cargoUnits)
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``navigate reconciles after an ambiguous failure without a duplicate navigate`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.fixedTravelSeconds <- 0.3
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "navigate", Map [ "destination", Literal(StringLit "X1-TEST-B2") ]) ]
        let jobId = JobRunner.startJob (program instructions) "FAKE-AGENT-1" initialShip

        setFaultMode fixture.RawClient "drop-after-processing"

        withPumpedQueue 20.0 (fun () ->
            let stepTask = JobRunner.stepOnce client dbPath App.seededToken jobId |> Async.StartAsTask
            Thread.Sleep(1500) // past dropAfterProcessingDelayMs so the navigate mutation has landed
            setFaultMode fixture.RawClient "normal"
            stepTask.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
            // the reconciliation cascade above settles the job into WaitingForArrival
            // (or straight to Completed, if the arrival time had already passed) — a
            // single `stepOnce` is a true single step, so drive any remaining wait
            // through to completion the same way the "run" button would.
            JobRunner.runToCompletion client dbPath App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job -> Assert.Equal(Completed, job.status)
        | None -> Assert.Fail("job not found")

        let finalShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Equal("X1-TEST-B2", finalShip.nav.waypointSymbol))

[<Fact>]
let ``extract reconciles after an ambiguous failure without a duplicate extraction`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.fixedCooldownSeconds <- 0.3
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "extract", Map.empty) ]
        let jobId = JobRunner.startJob (program instructions) "FAKE-AGENT-1" initialShip

        setFaultMode fixture.RawClient "drop-after-processing"

        withPumpedQueue 20.0 (fun () ->
            let stepTask = JobRunner.stepOnce client dbPath App.seededToken jobId |> Async.StartAsTask
            Thread.Sleep(1500)
            setFaultMode fixture.RawClient "normal"
            stepTask.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
            // settles WaitingForCooldown (or Completed directly) through to Completed.
            JobRunner.runToCompletion client dbPath App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job -> Assert.Equal(Completed, job.status)
        | None -> Assert.Fail("job not found")

        let finalShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        // extract yields a fixed 10 units per call — exactly one extraction happened,
        // not two, despite the ambiguous failure and reconciliation retry.
        Assert.Equal(10, finalShip.cargo.units))

[<Fact>]
let ``sellGood reconciles after an ambiguous failure without a duplicate sell`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        // give the ship some cargo to sell via a normal (unfaulted) extract first
        fixture.Client.Extract(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously |> ignore
        let beforeSell = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.True(beforeSell.cargo.units >= 5)

        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let instructions =
            [ ApiAction(
                  "b1",
                  "sellGood",
                  Map [ "tradeSymbol", Literal(StringLit "IRON"); "units", Literal(NumberLit 5.0) ]
              ) ]

        let jobId = JobRunner.startJob (program instructions) "FAKE-AGENT-1" beforeSell

        setFaultMode fixture.RawClient "drop-after-processing"

        withPumpedQueue 20.0 (fun () ->
            let stepTask = JobRunner.stepOnce client dbPath App.seededToken jobId |> Async.StartAsTask
            Thread.Sleep(1500)
            setFaultMode fixture.RawClient "normal"
            stepTask.Wait(TimeSpan.FromSeconds(10.0)) |> ignore)

        match JobRunner.getStatus jobId with
        | Some job -> Assert.Equal(Completed, job.status)
        | None -> Assert.Fail("job not found")

        let afterSell = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Equal(beforeSell.cargo.units - 5, afterSell.cargo.units))
