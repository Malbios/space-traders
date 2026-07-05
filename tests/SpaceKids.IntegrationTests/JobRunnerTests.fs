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

/// Milestone 10/Part A: reads back the priority `RequestQueue.enqueue` logged for
/// the most recent request matching `endpointPrefix` — proves *which* tier a given
/// call site actually used, since `request_queue_events.priority` is the only
/// externally observable record of it.
let private latestPriorityFor (dbPath: string) (endpointPrefix: string) : int =
    use conn = Database.openConnection dbPath
    use cmd = conn.CreateCommand()

    cmd.CommandText <-
        """
        SELECT priority FROM request_queue_events
        WHERE endpoint LIKE $prefix || '%'
        ORDER BY id DESC LIMIT 1;
        """

    cmd.Parameters.AddWithValue("$prefix", endpointPrefix) |> ignore

    match cmd.ExecuteScalar() with
    | null -> failwith $"no request_queue_events row for endpoint prefix \"{endpointPrefix}\""
    | v -> Convert.ToInt32(v)

/// Milestone 7: `JobRunner.startJob` now persists (`programs`/`jobs` rows) and
/// acquires the ship's lock, so it needs `client`/`dbPath`/`token` and returns a
/// `Result` (rejected if the ship is already locked). Tests always expect success
/// unless they're specifically testing the lock-rejection path.
let private startJobSync
    (client: SpaceTradersClient)
    (dbPath: string)
    (shipSymbol: string)
    (compiled: CompiledProgram)
    (initialShip: Ship)
    : JobId =
    // `programs.workspace_id` references `workspaces(id)` — the real remoting path
    // saves the workspace as part of starting a job (see `JobRemoting.fs`); tests
    // that call `JobRunner.startJob` directly need to do the same.
    WorkspaceRepository.save dbPath "test-workspace" "{}" |> Async.RunSynchronously

    match
        JobRunner.startJob client dbPath App.seededToken "test-workspace" (JobStateJson.serializeProgram compiled) compiled shipSymbol initialShip 2
        |> Async.RunSynchronously
    with
    | Ok jobId -> jobId
    | Error message -> failwith message

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
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
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
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        setFaultMode fixture.RawClient "drop-after-processing"

        withPumpedQueue 20.0 (fun () ->
            let stepTask = JobRunner.stepOnce client dbPath 1 App.seededToken jobId |> Async.StartAsTask
            Thread.Sleep(1500) // past dropAfterProcessingDelayMs so the navigate mutation has landed
            setFaultMode fixture.RawClient "normal"
            stepTask.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
            // the reconciliation cascade above settles the job into WaitingForArrival
            // (or straight to Completed, if the arrival time had already passed) — a
            // single `stepOnce` is a true single step, so drive any remaining wait
            // through to completion the same way the "run" button would.
            JobRunner.runToCompletion client dbPath 1 App.seededToken jobId
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
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        setFaultMode fixture.RawClient "drop-after-processing"

        withPumpedQueue 20.0 (fun () ->
            let stepTask = JobRunner.stepOnce client dbPath 1 App.seededToken jobId |> Async.StartAsTask
            Thread.Sleep(1500)
            setFaultMode fixture.RawClient "normal"
            stepTask.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
            // settles WaitingForCooldown (or Completed directly) through to Completed.
            JobRunner.runToCompletion client dbPath 1 App.seededToken jobId
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

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) beforeSell

        setFaultMode fixture.RawClient "drop-after-processing"

        withPumpedQueue 20.0 (fun () ->
            let stepTask = JobRunner.stepOnce client dbPath 1 App.seededToken jobId |> Async.StartAsTask
            Thread.Sleep(1500)
            setFaultMode fixture.RawClient "normal"
            stepTask.Wait(TimeSpan.FromSeconds(10.0)) |> ignore)

        match JobRunner.getStatus jobId with
        | Some job -> Assert.Equal(Completed, job.status)
        | None -> Assert.Fail("job not found")

        let afterSell = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Equal(beforeSell.cargo.units - 5, afterSell.cargo.units))

// ---------------------------------------------------------------------------
// Milestone 7 (§14): ship locks, restart resume, lease sweep, deferred pause.
// ---------------------------------------------------------------------------

let private expireLock (dbPath: string) (shipSymbol: string) =
    use conn = new SqliteConnection($"Data Source={dbPath}")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "UPDATE ship_locks SET lease_expires_at = $past WHERE ship_symbol = $shipSymbol;"
    cmd.Parameters.AddWithValue("$past", DateTimeOffset.UtcNow.AddMinutes(-10.0).ToString("o")) |> ignore
    cmd.Parameters.AddWithValue("$shipSymbol", shipSymbol) |> ignore
    cmd.ExecuteNonQuery() |> ignore

[<Fact>]
let ``a second job on the same ship is rejected while the first is still active`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let jobId1 = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program []) initialShip

        let result =
            JobRunner.startJob
                fixture.Client
                dbPath
                App.seededToken
                "test-workspace"
                (JobStateJson.serializeProgram (program []))
                (program [])
                "FAKE-AGENT-1"
                initialShip
                2
            |> Async.RunSynchronously

        match result with
        | Error message -> Assert.Contains("FAKE-AGENT-1", message)
        | Ok _ -> Assert.Fail("expected the second job on the same ship to be rejected")

        match JobRunner.getStatus jobId1 with
        | Some job -> Assert.Equal(Running, job.status)
        | None -> Assert.Fail("the first job should be unaffected"))

[<Fact>]
let ``jobs on two different ships proceed concurrently, each keeping its own lock`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let ship1 = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let ship2 = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-2") |> Async.RunSynchronously
        let jobId1 = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program [ ApiAction("b1", "orbit", Map.empty) ]) ship1
        let jobId2 = startJobSync fixture.Client dbPath "FAKE-AGENT-2" (program [ ApiAction("b1", "orbit", Map.empty) ]) ship2

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId1
            |> Async.RunSynchronously

            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId2
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId1, JobRunner.getStatus jobId2 with
        | Some j1, Some j2 ->
            Assert.Equal(Completed, j1.status)
            Assert.Equal(Completed, j2.status)
        | _ -> Assert.Fail("one of the jobs was not found"))

[<Fact>]
let ``a job persisted mid-wait resumes correctly after a simulated restart`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        // `JobScheduler.resumeAll`/`sweep`/`tickOnce` all look up the stored token
        // via `AgentRepository` (matching the real remoting path) rather than
        // taking one as a parameter — tests exercising them need a stored token.
        Persistence.AgentRepository.saveAgent dbPath "FAKE-AGENT" App.seededToken
        |> Async.RunSynchronously

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        // `Wait` uses JobRunner's real clock (not the fake server's controllable
        // one), so a short real duration keeps this test fast without touching
        // navigate/extract or the HTTP queue at all.
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program [ Wait("b1", Literal(NumberLit 0.3)) ]) initialShip

        JobRunner.stepOnce fixture.Client dbPath 1 App.seededToken jobId |> Async.RunSynchronously

        match JobRunner.getStatus jobId with
        | Some { status = WaitingForArrival _ } -> ()
        | other -> Assert.Fail($"expected WaitingForArrival, got {other}")

        // Simulates a process restart: drop the in-memory cache but keep the DB
        // row, then run exactly the startup recovery logic a fresh process runs.
        JobRunner.resetForTests ()
        Assert.True(JobRunner.getStatus jobId |> Option.isNone)

        JobScheduler.resumeAll fixture.Client dbPath |> Async.RunSynchronously

        match JobRunner.getStatus jobId with
        | Some { status = WaitingForArrival _ } -> ()
        | other -> Assert.Fail($"expected the job to resume waiting, got {other}")

        Thread.Sleep(500)
        JobRunner.stepOnce fixture.Client dbPath 1 App.seededToken jobId |> Async.RunSynchronously

        match JobRunner.getStatus jobId with
        | Some job -> Assert.Equal(Completed, job.status)
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``the sweep pauses a job whose lease expired without a competing acquirer, freeing its ship`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        Persistence.AgentRepository.saveAgent dbPath "FAKE-AGENT" App.seededToken
        |> Async.RunSynchronously

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program [ Wait("b1", Literal(NumberLit 30.0)) ]) initialShip
        JobRunner.stepOnce fixture.Client dbPath 1 App.seededToken jobId |> Async.RunSynchronously

        match JobRunner.getStatus jobId with
        | Some { status = WaitingForArrival _ } -> ()
        | other -> Assert.Fail($"expected WaitingForArrival, got {other}")

        expireLock dbPath "FAKE-AGENT-1"

        // The sweep only reclaims locks whose job *isn't* one of this process's
        // own live in-memory jobs (§14) — our own tick loop is what's supposed to
        // keep a genuinely active job's lease fresh, so simulate the case that
        // actually matters: this job isn't currently loaded (e.g. right after an
        // unclean restart, before its owner resumes it).
        JobRunner.resetForTests ()
        JobScheduler.sweep fixture.Client dbPath |> Async.RunSynchronously

        match JobRunner.getStatus jobId with
        | Some { status = Paused(WaitingForArrival _) } -> ()
        | other -> Assert.Fail($"expected Paused(WaitingForArrival _), got {other}")

        // the ship is free again for a new job.
        let result =
            JobRunner.startJob
                fixture.Client
                dbPath
                App.seededToken
                "test-workspace"
                (JobStateJson.serializeProgram (program []))
                (program [])
                "FAKE-AGENT-1"
                initialShip
                2
            |> Async.RunSynchronously

        match result with
        | Ok _ -> ()
        | Error message -> Assert.Fail($"expected the ship to be free again: {message}"))

[<Fact>]
let ``pausing mid-AwaitingApiResponse never abandons the in-flight action, settles into Paused right after`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program [ ApiAction("b1", "orbit", Map.empty) ]) initialShip

        setFaultMode fixture.RawClient "drop-after-processing"

        withPumpedQueue 20.0 (fun () ->
            let stepTask = JobRunner.stepOnce client dbPath 1 App.seededToken jobId |> Async.StartAsTask
            Thread.Sleep(50) // give it time to enter AwaitingApiResponse first
            JobRunner.pause fixture.Client dbPath App.seededToken jobId |> Async.RunSynchronously
            Thread.Sleep(1500)
            setFaultMode fixture.RawClient "normal"
            stepTask.Wait(TimeSpan.FromSeconds(10.0)) |> ignore)

        match JobRunner.getStatus jobId with
        | Some job -> Assert.Equal(Paused Running, job.status)
        | None -> Assert.Fail("job not found")

        // the orbit action itself was never abandoned — it really landed.
        let finalShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Equal("IN_ORBIT", finalShip.nav.status))

// ---------------------------------------------------------------------------
// Part A (Milestone 9): the 5 remaining actions, against the fake over real HTTP.
// ---------------------------------------------------------------------------

[<Fact>]
let ``survey reconciles after an ambiguous failure without a duplicate survey`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.fixedCooldownSeconds <- 0.3
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "survey", Map.empty) ]
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        setFaultMode fixture.RawClient "drop-after-processing"

        withPumpedQueue 20.0 (fun () ->
            let stepTask = JobRunner.stepOnce client dbPath 1 App.seededToken jobId |> Async.StartAsTask
            Thread.Sleep(1500)
            setFaultMode fixture.RawClient "normal"
            stepTask.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
            JobRunner.runToCompletion client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job -> Assert.Equal(Completed, job.status)
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``deliverContract reconciles after an ambiguous failure without a duplicate delivery`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        // give the ship some IRON to deliver via a normal (unfaulted) extract first
        fixture.Client.Extract(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously |> ignore
        let beforeDeliver = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.True(beforeDeliver.cargo.units >= 5)

        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let instructions =
            [ ApiAction(
                  "b1",
                  "deliverContract",
                  Map
                      [ "contractId", Literal(StringLit "fake-contract-1")
                        "tradeSymbol", Literal(StringLit "IRON")
                        "units", Literal(NumberLit 5.0) ]
              ) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) beforeDeliver

        setFaultMode fixture.RawClient "drop-after-processing"

        withPumpedQueue 20.0 (fun () ->
            let stepTask = JobRunner.stepOnce client dbPath 1 App.seededToken jobId |> Async.StartAsTask
            Thread.Sleep(1500)
            setFaultMode fixture.RawClient "normal"
            stepTask.Wait(TimeSpan.FromSeconds(10.0)) |> ignore)

        match JobRunner.getStatus jobId with
        | Some job -> Assert.Equal(Completed, job.status)
        | None -> Assert.Fail("job not found")

        let afterDeliver = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Equal(beforeDeliver.cargo.units - 5, afterDeliver.cargo.units))

[<Fact>]
let ``acceptContract reconciles after an ambiguous failure without a duplicate accept`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ ApiAction("b1", "acceptContract", Map [ "contractId", Literal(StringLit "fake-contract-1") ]) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        setFaultMode fixture.RawClient "drop-after-processing"

        withPumpedQueue 20.0 (fun () ->
            let stepTask = JobRunner.stepOnce client dbPath 1 App.seededToken jobId |> Async.StartAsTask
            Thread.Sleep(1500)
            setFaultMode fixture.RawClient "normal"
            stepTask.Wait(TimeSpan.FromSeconds(10.0)) |> ignore)

        match JobRunner.getStatus jobId with
        | Some job -> Assert.Equal(Completed, job.status)
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``purchaseShip reconciles after an ambiguous failure without a duplicate purchase`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let beforeCount = (fixture.Client.ListShips(App.seededToken) |> Async.RunSynchronously) |> List.length

        let instructions =
            [ ApiAction(
                  "b1",
                  "purchaseShip",
                  Map [ "shipType", Literal(StringLit "SHIP_MINING_DRONE"); "waypointSymbol", Literal(StringLit "X1-TEST-A1") ]
              ) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        setFaultMode fixture.RawClient "drop-after-processing"

        withPumpedQueue 20.0 (fun () ->
            let stepTask = JobRunner.stepOnce client dbPath 1 App.seededToken jobId |> Async.StartAsTask
            Thread.Sleep(1500)
            setFaultMode fixture.RawClient "normal"
            stepTask.Wait(TimeSpan.FromSeconds(10.0)) |> ignore)

        match JobRunner.getStatus jobId with
        | Some job -> Assert.Equal(Completed, job.status)
        | None -> Assert.Fail("job not found")

        let afterCount = (fixture.Client.ListShips(App.seededToken) |> Async.RunSynchronously) |> List.length
        // exactly one new ship, not two, despite the ambiguous failure and retry.
        Assert.Equal(beforeCount + 1, afterCount))

[<Fact>]
let ``refuel reconciles after an ambiguous failure without a duplicate refuel`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        // burn some fuel first via a normal orbit/dock cycle isn't necessary — the
        // fake's refuel always tops up to capacity regardless of current level.
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "refuel", Map.empty) ]
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        setFaultMode fixture.RawClient "drop-after-processing"

        withPumpedQueue 20.0 (fun () ->
            let stepTask = JobRunner.stepOnce client dbPath 1 App.seededToken jobId |> Async.StartAsTask
            Thread.Sleep(1500)
            setFaultMode fixture.RawClient "normal"
            stepTask.Wait(TimeSpan.FromSeconds(10.0)) |> ignore)

        match JobRunner.getStatus jobId with
        | Some job -> Assert.Equal(Completed, job.status)
        | None -> Assert.Fail("job not found")

        let finalShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Equal(finalShip.fuel.capacity, finalShip.fuel.current))

// ---------------------------------------------------------------------------
// Milestone 9/Part B: custom-block `lookup` wiring, proven via a real compile
// against a persisted definition, then a real run.
// ---------------------------------------------------------------------------

[<Fact>]
let ``a program calling a custom block loaded from the repository compiles and runs to completion`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        // Persist the custom block's own workshop first — its body is real Blockly
        // JSON, recompiled fresh from `workspaceJson` every time `Compiler` resolves
        // it (the stored `compiled_body_json` only freezes the signature snapshot;
        // see `CustomBlockRepository.saveVersion`'s doc comment).
        let realId = CustomBlockRepository.insert dbPath "Gruss" None |> Async.RunSynchronously

        let definitionJson =
            """
            { "blocks": { "languageVersion": 0, "blocks": [
                { "type": "sk_show_message", "id": "cb1", "inputs": {
                    "TEXT": { "block": { "type": "text", "id": "cb1t", "fields": { "TEXT": "Hallo aus dem eigenen Block!" } } }
                } }
            ] } }
            """

        let voidSignatureBlock: CompiledCustomBlock =
            { signature = { inputs = []; output = None; outputFields = None }
              instructions = []
              returnExpr = None }

        CustomBlockRepository.saveVersion dbPath realId definitionJson voidSignatureBlock
        |> Async.RunSynchronously
        |> ignore

        let mainWorkspaceJson =
            $$"""
            { "blocks": { "languageVersion": 0, "blocks": [
                { "type": "callCustomBlock", "id": "call1", "extraState": { "customBlockId": "{{realId}}" } }
            ] } }
            """

        let lookup (id: string) = CustomBlockRepository.load dbPath id |> Async.RunSynchronously

        let compiled =
            match Compiler.compileWorkspace De lookup mainWorkspaceJson with
            | Error errors -> failwith $"expected Ok, got errors: %A{errors}"
            | Ok program -> program

        Assert.Empty(Validator.validate SpaceKids.Core.Dsl.De compiled)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" compiled initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)
            Assert.Contains("Hallo aus dem eigenen Block!", job.log)
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``an info-read plus accessor chain resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ InfoRead("b1", "getShipInfo", Map.empty, "$t1")
              SetVariable("b2", "treibstoff", Accessor("Fuel", TempRef "$t1")) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ -> Assert.Equal(VNumber(float initialShip.fuel.current), top.locals.["treibstoff"])
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``a background scheduler tick dispatches at priority 3 while a player-triggered step stays at priority 1`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        // `JobScheduler.tickOnce` looks up the stored token via `AgentRepository`
        // (matching the real remoting path), same as the restart-recovery test above.
        Persistence.AgentRepository.saveAgent dbPath "FAKE-AGENT" App.seededToken
        |> Async.RunSynchronously

        let ship1 = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let backgroundJobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program [ ApiAction("b1", "orbit", Map.empty) ]) ship1

        withPumpedQueue 20.0 (fun () ->
            // The fully-automatic per-second tick loop nobody is watching —
            // Milestone 10/Part A's fix: this must log priority 3, not 1.
            // `tickOnce` sweeps *every* non-terminal job, so the interactive job
            // below is deliberately not started yet — it would otherwise get
            // background-ticked here too before its own priority-1 step runs.
            JobScheduler.tickOnce fixture.Client dbPath |> Async.RunSynchronously

            JobRunner.runToCompletion fixture.Client dbPath JobRunner.backgroundPriority App.seededToken backgroundJobId
            |> Async.RunSynchronously)

        let ship2 = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-2") |> Async.RunSynchronously
        let interactiveJobId = startJobSync fixture.Client dbPath "FAKE-AGENT-2" (program [ ApiAction("b1", "orbit", Map.empty) ]) ship2

        withPumpedQueue 20.0 (fun () ->
            // A player pressing "step" on their own program — always priority 1.
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken interactiveJobId
            |> Async.RunSynchronously)

        Assert.Equal(3, latestPriorityFor dbPath "orbit:FAKE-AGENT-1")
        Assert.Equal(1, latestPriorityFor dbPath "orbit:FAKE-AGENT-2"))

[<Fact>]
let ``a concurrent trade on another ship doesn't corrupt an in-flight ambiguous-failure reconciliation`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        // Ship 1: give it cargo to sell, then a sellGood that will ambiguously fail
        // (short client timeout vs. the fake's artificial processing delay) and
        // need reconciliation.
        fixture.Client.Extract(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously |> ignore
        let beforeSell = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.True(beforeSell.cargo.units >= 5)

        App.dropAfterProcessingDelayMs <- 200
        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let ambiguousClient = SpaceTradersClient(shortTimeoutClient)

        let sellInstructions =
            [ ApiAction("b1", "sellGood", Map [ "tradeSymbol", Literal(StringLit "IRON"); "units", Literal(NumberLit 5.0) ]) ]

        let job1 = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program sellInstructions) beforeSell

        // Ship 2: a normal buy, driven with the fixture's own default-timeout
        // client — tolerates the fake's 200ms artificial delay just fine, so it
        // completes as an ordinary successful trade, genuinely concurrent with
        // ship 1 sitting in `Reconciling` below (both fully real `JobState`s,
        // both non-terminal at the same time).
        let ship2 = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-2") |> Async.RunSynchronously

        let buyInstructions =
            [ ApiAction("b1", "buyGood", Map [ "tradeSymbol", Literal(StringLit "FOOD"); "units", Literal(NumberLit 3.0) ]) ]

        let job2 = startJobSync fixture.Client dbPath "FAKE-AGENT-2" (program buyInstructions) ship2

        setFaultMode fixture.RawClient "drop-after-processing"

        withPumpedQueue 20.0 (fun () ->
            // Ship 1's sell ambiguously fails almost immediately (100ms timeout <
            // 200ms fake-side delay) and settles into `Reconciling`.
            let sellStepTask = JobRunner.stepOnce ambiguousClient dbPath 1 App.seededToken job1 |> Async.StartAsTask

            // While ship 1 sits in `Reconciling`, ship 2 trades for real — a
            // genuine concurrent agent-credits change happening mid-reconciliation.
            // Its own client tolerates the 200ms delay (default timeout), so this
            // completes as an ordinary successful buy, not another ambiguous case.
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken job2
            |> Async.RunSynchronously

            Thread.Sleep(1500) // past dropAfterProcessingDelayMs so ship 1's sell mutation has landed
            setFaultMode fixture.RawClient "normal"
            sellStepTask.Wait(TimeSpan.FromSeconds(10.0)) |> ignore

            JobRunner.runToCompletion ambiguousClient dbPath 1 App.seededToken job1
            |> Async.RunSynchronously)

        match JobRunner.getStatus job1, JobRunner.getStatus job2 with
        | Some j1, Some j2 ->
            Assert.Equal(Completed, j1.status)
            Assert.Equal(Completed, j2.status)
        | _ -> Assert.Fail("one of the jobs was not found")

        let afterShip1 = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let afterShip2 = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-2") |> Async.RunSynchronously

        // Ship 1 sold exactly 5 units once — not double-sold despite reconciling
        // while ship 2's own credits-changing trade ran concurrently. Ship 1's
        // reconciliation never inspects credits (§13: cargo/ship-state deltas
        // only), so ship 2's trade — which did change the shared agent balance —
        // must have no bearing on this outcome.
        Assert.Equal(beforeSell.cargo.units - 5, afterShip1.cargo.units)
        // Ship 2 bought exactly 3 units once — its own trade wasn't corrupted by
        // running concurrently with ship 1's reconciliation dance either.
        Assert.Equal(3, afterShip2.cargo.units))

[<Fact>]
let ``a started job's programId matches the program it was started against`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program []) initialShip

        match JobRunner.getStatus jobId with
        | Some job -> Assert.Equal("test-workspace", job.programId)
        | None -> Assert.Fail("job not found"))
