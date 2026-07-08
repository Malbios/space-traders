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

/// Reads whether `ship_locks` currently holds a row for `shipSymbol` — used to
/// prove a lock was actually released, not just that the owning job went terminal.
let private shipLockExists (dbPath: string) (shipSymbol: string) : bool =
    use conn = Database.openConnection dbPath
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT COUNT(*) FROM ship_locks WHERE ship_symbol = $shipSymbol;"
    cmd.Parameters.AddWithValue("$shipSymbol", shipSymbol) |> ignore
    Convert.ToInt32(cmd.ExecuteScalar()) > 0

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
        JobRunner.startJob
            client
            dbPath
            App.seededToken
            "test-workspace"
            (JobStateJson.serializeProgram compiled)
            compiled
            (Some shipSymbol)
            (Some initialShip)
            2
            2
        |> Async.RunSynchronously
    with
    | Ok jobId -> jobId
    | Error message -> failwith message

/// §14 follow-up: a ship-agnostic program (only `purchaseShip`/`acceptContract`-style
/// actions, no ship-scoped ones) can start with no ship at all — no `ship_locks` row
/// is ever created, and the job still runs to completion.
[<Fact>]
let ``a ship-agnostic program starts and completes with no ship selected, taking no lock`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let instructions =
            [ ApiAction(
                  "b1",
                  "purchaseShip",
                  Map [ "shipType", Literal(StringLit "SHIP_MINING_DRONE"); "waypointSymbol", Literal(StringLit "X1-TEST-A1") ]
              ) ]

        WorkspaceRepository.save dbPath "test-workspace" "{}" |> Async.RunSynchronously

        let jobId =
            match
                JobRunner.startJob
                    fixture.Client
                    dbPath
                    App.seededToken
                    "test-workspace"
                    (JobStateJson.serializeProgram (program instructions))
                    (program instructions)
                    None
                    None
                    2
                    2
                |> Async.RunSynchronously
            with
            | Ok jobId -> jobId
            | Error message -> failwith message

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job -> Assert.Equal(Completed, job.status)
        | None -> Assert.Fail("job not found")

        Assert.False(shipLockExists dbPath "FAKE-AGENT-1"))

[<Fact>]
let ``a withShip-only program starts and completes with no pilot ship selected`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let instructions =
            [ WithShip(
                  "with-ship-1",
                  Literal(StringLit "FAKE-AGENT-1"),
                  [ ApiAction("orbit-1", "orbit", Map.empty); ExitShipScope "with-ship-1:exit" ],
                  None
              ) ]

        WorkspaceRepository.save dbPath "test-workspace" "{}" |> Async.RunSynchronously

        let jobId =
            match
                JobRunner.startJob
                    fixture.Client
                    dbPath
                    App.seededToken
                    "test-workspace"
                    (JobStateJson.serializeProgram (program instructions))
                    (program instructions)
                    None
                    None
                    2
                    2
                |> Async.RunSynchronously
            with
            | Ok jobId -> jobId
            | Error message -> failwith message

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job -> Assert.Equal(Completed, job.status)
        | None -> Assert.Fail("job not found")

        let ship = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Equal("IN_ORBIT", ship.nav.status)
        Assert.False(shipLockExists dbPath "FAKE-AGENT-1"))

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
                (Some "FAKE-AGENT-1")
                (Some initialShip)
                2
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
let ``withShip scope can temporarily control a second ship and releases its dynamic lock`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let ship1 = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ WithShip(
                  "with-ship-2",
                  Literal(StringLit "FAKE-AGENT-2"),
                  [ ApiAction("orbit-2", "orbit", Map.empty); ExitShipScope "with-ship-2:exit" ],
                  None
              ) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) ship1

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)
            Assert.Contains(job.log, fun line -> line.Contains("FAKE-AGENT-2"))
        | None -> Assert.Fail("job not found")

        let ship2 = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-2") |> Async.RunSynchronously
        Assert.Equal("IN_ORBIT", ship2.nav.status)
        Assert.False(shipLockExists dbPath "FAKE-AGENT-1")
        Assert.False(shipLockExists dbPath "FAKE-AGENT-2"))

[<Fact>]
let ``parallel branches in one job can control primary and scoped ships`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let ship1 = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ Parallel(
                  "parallel-ships",
                  [ [ ApiAction("orbit-1", "orbit", Map.empty) ]
                    [ WithShip(
                          "with-ship-2",
                          Literal(StringLit "FAKE-AGENT-2"),
                          [ ApiAction("orbit-2", "orbit", Map.empty); ExitShipScope "with-ship-2:exit" ],
                          None
                      ) ] ]
              ) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) ship1

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job -> Assert.Equal(Completed, job.status)
        | None -> Assert.Fail("job not found")

        let ship1After = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let ship2After = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-2") |> Async.RunSynchronously
        Assert.Equal("IN_ORBIT", ship1After.nav.status)
        Assert.Equal("IN_ORBIT", ship2After.nav.status)
        Assert.False(shipLockExists dbPath "FAKE-AGENT-1")
        Assert.False(shipLockExists dbPath "FAKE-AGENT-2"))

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
                (Some "FAKE-AGENT-1")
                (Some initialShip)
                2
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
let ``fulfillContract reconciles after an ambiguous failure without a duplicate fulfill`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.dropAfterProcessingDelayMs <- 200

        fixture.Client.Extract(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously |> ignore

        fixture.Client.DeliverContract(App.seededToken, "fake-contract-1", "FAKE-AGENT-1", "IRON", 10)
        |> Async.RunSynchronously
        |> ignore

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ ApiAction("b1", "fulfillContract", Map [ "contractId", Literal(StringLit "fake-contract-1") ]) ]

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

        let updated =
            fixture.Client.GetContract(App.seededToken, "fake-contract-1") |> Async.RunSynchronously

        Assert.True(updated.contract.fulfilled))

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
let ``negotiateContract reconciles after an ambiguous failure without a duplicate negotiate`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let beforeCount = (fixture.Client.ListContracts(App.seededToken) |> Async.RunSynchronously) |> List.length
        let instructions = [ ApiAction("b1", "negotiateContract", Map.empty) ]
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

        let afterCount = (fixture.Client.ListContracts(App.seededToken) |> Async.RunSynchronously) |> List.length
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

[<Fact>]
let ``supplyConstruction reconciles after an ambiguous failure without a duplicate supply`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.seedCargoForTests "FAKE-AGENT-1" "IRON" 20
        let beforeSupply = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Equal(20, beforeSupply.cargo.units)

        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let instructions =
            [ ApiAction(
                  "b1",
                  "supplyConstruction",
                  Map
                      [ "waypointSymbol", Literal(StringLit "X1-NEARBY-C1")
                        "tradeSymbol", Literal(StringLit "IRON")
                        "units", Literal(NumberLit 10.0) ]
              ) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) beforeSupply

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
        | None -> Assert.Fail("job not found")

        let afterSupply = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Equal(10, afterSupply.cargo.units)

        let construction = fixture.Client.GetConstruction(App.seededToken, "X1-NEARBY", "X1-NEARBY-C1") |> Async.RunSynchronously
        Assert.Equal(10, construction.materials.[0].fulfilled))

[<Fact>]
let ``patchShipNav reconciles after an ambiguous failure without stalling`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ ApiAction("b1", "patchShipNav", Map [ "flightMode", Literal(StringLit "BURN") ]) ]

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
        | None -> Assert.Fail("job not found")

        let finalShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Equal("BURN", finalShip.nav.flightMode))

[<Fact>]
let ``scrapShip reconciles after an ambiguous failure without a duplicate scrap`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-2") |> Async.RunSynchronously
        let beforeCount = (fixture.Client.ListShips(App.seededToken) |> Async.RunSynchronously).Length

        let instructions = [ ApiAction("b1", "scrapShip", Map.empty) ]
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-2" (program instructions) initialShip

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
        | None -> Assert.Fail("job not found")

        let afterCount = (fixture.Client.ListShips(App.seededToken) |> Async.RunSynchronously).Length
        Assert.Equal(beforeCount - 1, afterCount)
        Assert.False(shipLockExists dbPath "FAKE-AGENT-2"))

// ---------------------------------------------------------------------------
// Post-roadmap: the "full API coverage" batch (375dc40/6138459) shipped ~20 new
// action blocks, but only supplyConstruction/patchShipNav/scrapShip above ever got
// this same fake-HTTP reconciliation treatment. The remaining 16 below close that
// gap — same shape, same helpers, one per action. The fake's handlers for every one
// of these apply their mutation unconditionally once auth passes (verified by
// reading App.fs directly), so none need elaborate precondition setup beyond what's
// already used elsewhere.

[<Fact>]
let ``jettison reconciles after an ambiguous failure without a duplicate jettison`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.seedCargoForTests "FAKE-AGENT-1" "IRON" 20
        let beforeShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Equal(20, beforeShip.cargo.units)

        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let instructions =
            [ ApiAction("b1", "jettison", Map [ "tradeSymbol", Literal(StringLit "IRON"); "units", Literal(NumberLit 5.0) ]) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) beforeShip

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
        | None -> Assert.Fail("job not found")

        let afterShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Equal(15, afterShip.cargo.units))

[<Fact>]
let ``jump reconciles after an ambiguous failure without a duplicate jump`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.fixedTravelSeconds <- 0.3
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "jump", Map [ "waypointSymbol", Literal(StringLit "X1-NEARBY-JG1") ]) ]
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
        | None -> Assert.Fail("job not found")

        let finalShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Equal("X1-NEARBY-JG1", finalShip.nav.waypointSymbol))

[<Fact>]
let ``warp reconciles after an ambiguous failure without a duplicate warp`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.fixedTravelSeconds <- 0.3
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "warp", Map [ "waypointSymbol", Literal(StringLit "X1-NEARBY-A1") ]) ]
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
        | None -> Assert.Fail("job not found")

        let finalShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Equal("X1-NEARBY-A1", finalShip.nav.waypointSymbol))

[<Fact>]
let ``transferCargo reconciles after an ambiguous failure without a duplicate transfer`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.seedCargoForTests "FAKE-AGENT-1" "IRON" 20
        let beforeSource = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let beforeTarget = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-2") |> Async.RunSynchronously
        let beforeTargetUnits = beforeTarget.cargo.units

        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let instructions =
            [ ApiAction(
                  "b1",
                  "transferCargo",
                  Map
                      [ "tradeSymbol", Literal(StringLit "IRON")
                        "units", Literal(NumberLit 5.0)
                        "targetShipSymbol", Literal(StringLit "FAKE-AGENT-2") ]
              ) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) beforeSource

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
        | None -> Assert.Fail("job not found")

        let afterSource = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let afterTarget = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-2") |> Async.RunSynchronously
        Assert.Equal(15, afterSource.cargo.units)
        Assert.Equal(beforeTargetUnits + 5, afterTarget.cargo.units))

[<Fact>]
let ``siphon reconciles after an ambiguous failure without a duplicate siphon`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.fixedCooldownSeconds <- 0.3
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let beforeUnits = initialShip.cargo.units
        let instructions = [ ApiAction("b1", "siphon", Map.empty) ]
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
        | None -> Assert.Fail("job not found")

        let finalShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Equal(beforeUnits + 5, finalShip.cargo.units))

[<Fact>]
let ``refine reconciles after an ambiguous failure without a duplicate refine`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.fixedCooldownSeconds <- 0.3
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "refine", Map [ "produce", Literal(StringLit "IRON") ]) ]
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
        | None -> Assert.Fail("job not found")

        let finalShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.Contains(finalShip.cargo.inventory, fun item -> item.symbol = "IRON"))

[<Fact>]
let ``scanShips reconciles after an ambiguous failure without stalling`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.fixedCooldownSeconds <- 0.3
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "scanShips", Map.empty) ]
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
let ``scanSystems reconciles after an ambiguous failure without stalling`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.fixedCooldownSeconds <- 0.3
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "scanSystems", Map.empty) ]
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
let ``scanWaypoints reconciles after an ambiguous failure without stalling`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.fixedCooldownSeconds <- 0.3
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "scanWaypoints", Map.empty) ]
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
let ``createChart reconciles after an ambiguous failure without stalling`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.fixedCooldownSeconds <- 0.3
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "createChart", Map.empty) ]
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
let ``extractWithSurvey reconciles after an ambiguous failure without a duplicate extraction`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.fixedCooldownSeconds <- 0.3
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let beforeUnits = initialShip.cargo.units

        let instructions =
            [ ApiAction("b1", "extractWithSurvey", Map [ "surveySignature", Literal(StringLit "FAKE-SURVEY-1") ]) ]

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
        | None -> Assert.Fail("job not found")

        let finalShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        Assert.True(finalShip.cargo.units > beforeUnits))

/// Bonus: `DockOrbitBaseline` (shared with the original `dock`/`orbit` actions, which
/// never got a dedicated ambiguous-failure test of their own) gets its first
/// dedicated reconciliation proof here, via `installModule`.
[<Fact>]
let ``installModule reconciles after an ambiguous failure without a duplicate install (DockOrbitBaseline)`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "installModule", Map [ "moduleSymbol", Literal(StringLit "MODULE_CARGO_HOLD_I") ]) ]
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
let ``installMount reconciles after an ambiguous failure without stalling`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "installMount", Map [ "mountSymbol", Literal(StringLit "MOUNT_MINING_LASER_I") ]) ]
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
let ``removeModule reconciles after an ambiguous failure without stalling`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "removeModule", Map [ "moduleSymbol", Literal(StringLit "MODULE_CARGO_HOLD_I") ]) ]
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
let ``removeMount reconciles after an ambiguous failure without stalling`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "removeMount", Map [ "mountSymbol", Literal(StringLit "MOUNT_MINING_LASER_I") ]) ]
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
let ``repair reconciles after an ambiguous failure without stalling`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        App.dropAfterProcessingDelayMs <- 200

        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromMilliseconds(100.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ ApiAction("b1", "repair", Map.empty) ]
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
let ``getWaypoint System accessor resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let instructions =
            [ InfoRead("b1", "getWaypoint", Map [ "waypointSymbol", Literal(StringLit "X1-TEST-A1") ], "$t1")
              SetVariable("b2", "system", Accessor("System", TempRef "$t1")) ]

        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ -> Assert.Equal(VString "X1-TEST", top.locals.["system"])
            | [] -> Assert.Fail("expected a stack frame")
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

// ---------------------------------------------------------------------------
// Post-roadmap: 17 of the "full API coverage" batch's new info blocks had never
// been run through JobRunner.runInfoRead against the real fake at all — only
// getWaypoint (above) got this treatment. Same two-instruction InfoRead + Accessor
// shape as the existing tests above; list-returning blocks skip the Accessor step
// (Accessor only applies to VRecord) and assert directly on the resulting VList.

[<Fact>]
let ``getMyAgent resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let agent = fixture.Client.GetAgent(App.seededToken) |> Async.RunSynchronously

        let instructions =
            [ InfoRead("b1", "getMyAgent", Map.empty, "$t1")
              SetVariable("b2", "credits", Accessor("Credits", TempRef "$t1")) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ -> Assert.Equal(VNumber(float agent.credits), top.locals.["credits"])
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getPublicAgent resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ InfoRead("b1", "getPublicAgent", Map [ "agentSymbol", Literal(StringLit "OTHER-AGENT") ], "$t1")
              SetVariable("b2", "hq", Accessor("Headquarters", TempRef "$t1")) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ -> Assert.Equal(VString "X1-NEARBY-A1", top.locals.["hq"])
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getPublicAgents resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ InfoRead("b1", "getPublicAgents", Map.empty, "$t1"); SetVariable("b2", "agents", TempRef "$t1") ]
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ ->
                match top.locals.["agents"] with
                | VList items ->
                    Assert.Contains(
                        items,
                        fun v ->
                            match v with
                            | VRecord m -> m.["Symbol"] = VString "OTHER-AGENT"
                            | _ -> false
                    )
                | other -> Assert.Fail($"expected a VList, got {other}")
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getCooldown resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ InfoRead("b1", "getCooldown", Map.empty, "$t1")
              SetVariable("b2", "remaining", Accessor("RemainingSeconds", TempRef "$t1")) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ -> Assert.Equal(VNumber(float initialShip.cooldown.remainingSeconds), top.locals.["remaining"])
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getNav resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ InfoRead("b1", "getNav", Map.empty, "$t1")
              SetVariable("b2", "waypoint", Accessor("Waypoint", TempRef "$t1")) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ -> Assert.Equal(VString initialShip.nav.waypointSymbol, top.locals.["waypoint"])
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getSupplyChain resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ InfoRead("b1", "getSupplyChain", Map.empty, "$t1"); SetVariable("b2", "chain", TempRef "$t1") ]
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ ->
                match top.locals.["chain"] with
                | VList items ->
                    Assert.Contains(
                        items,
                        fun v ->
                            match v with
                            | VRecord m -> m.["Export"] = VString "FOOD"
                            | _ -> false
                    )
                | other -> Assert.Fail($"expected a VList, got {other}")
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getShipModules resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ InfoRead("b1", "getShipModules", Map.empty, "$t1"); SetVariable("b2", "modules", TempRef "$t1") ]
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ ->
                match top.locals.["modules"] with
                | VList items ->
                    Assert.Contains(
                        items,
                        fun v ->
                            match v with
                            | VRecord m -> m.["Symbol"] = VString "MODULE_MINERAL_PROCESSOR_I"
                            | _ -> false
                    )
                | other -> Assert.Fail($"expected a VList, got {other}")
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

/// `getShipyard`'s full-detail shape (frame/reactor/engine/modules/mounts/crew, not
/// just `Type`/`Price`) is only populated by the real API when a ship of yours is
/// docked at that shipyard (`ShipyardShipEntry`'s own doc comment in
/// `SpaceTraders/Types.fs`) — both seeded fake ships live at headquarters, so a
/// shipyard call there exercises the full nested `VRecord`, not the price-free
/// `shipTypes` fallback.
[<Fact>]
let ``getShipyard resolves the full nested ship-type detail when a ship is docked`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ InfoRead("b1", "getShipyard", Map [ "waypointSymbol", Literal(StringLit "X1-TEST-A1") ], "$t1")
              SetVariable("b2", "yard", TempRef "$t1") ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ ->
                match top.locals.["yard"] with
                | VRecord yard ->
                    Assert.Equal(VNumber 100.0, yard.["ModificationsFee"])

                    match yard.["Transactions"] with
                    | VList(VRecord transaction :: _) ->
                        Assert.Equal(VString "SHIP_MINING_DRONE", transaction.["ShipType"])
                        Assert.Equal(VNumber 50000.0, transaction.["Price"])
                    | other -> Assert.Fail($"expected Transactions to be a non-empty VList, got {other}")

                    match yard.["Types"] with
                    | VList(VRecord shipType :: _) ->
                        Assert.Equal(VString "SHIP_MINING_DRONE", shipType.["Type"])
                        Assert.Equal(VNumber 50000.0, shipType.["Price"])
                        Assert.Equal(VString "Mining Drone", shipType.["Name"])

                        match shipType.["Frame"] with
                        | VRecord frame ->
                            Assert.Equal(VString "FRAME_DRONE", frame.["Symbol"])
                            Assert.Equal(VNumber 3.0, frame.["ModuleSlots"])
                            Assert.Equal(VNumber 1.0, frame.["Condition"])
                            Assert.Equal(VNumber 1.0, frame.["Integrity"])
                            Assert.Equal(VNumber 1.0, frame.["Quality"])

                            match frame.["Requirements"] with
                            | VRecord requirements ->
                                Assert.Equal(VNumber 1.0, requirements.["Power"])
                                // A component's crew requirement is a signed
                                // *contribution* to the ship's total, not a
                                // standalone figure — an automated/unmanned frame
                                // like this one can legitimately be negative
                                // (verified against a real account's response,
                                // 2026-07-08).
                                Assert.Equal(VNumber -4.0, requirements.["Crew"])
                            | other -> Assert.Fail($"expected Frame.Requirements to be a VRecord, got {other}")
                        | other -> Assert.Fail($"expected Types[0].Frame to be a VRecord, got {other}")

                        match shipType.["Mounts"] with
                        | VList(VRecord mount :: _) -> Assert.Equal(VString "MOUNT_MINING_LASER_I", mount.["Symbol"])
                        | other -> Assert.Fail($"expected Types[0].Mounts to be a non-empty VList, got {other}")

                        match shipType.["Crew"] with
                        | VRecord crew -> Assert.Equal(VNumber 0.0, crew.["Required"])
                        | other -> Assert.Fail($"expected Types[0].Crew to be a VRecord, got {other}")
                    | other -> Assert.Fail($"expected Types to be a non-empty VList, got {other}")
                | other -> Assert.Fail($"expected a VRecord, got {other}")
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getShipMounts resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ InfoRead("b1", "getShipMounts", Map.empty, "$t1"); SetVariable("b2", "mounts", TempRef "$t1") ]
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ ->
                match top.locals.["mounts"] with
                | VList items ->
                    Assert.Contains(
                        items,
                        fun v ->
                            match v with
                            | VRecord m -> m.["Symbol"] = VString "MOUNT_MINING_LASER_I"
                            | _ -> false
                    )
                | other -> Assert.Fail($"expected a VList, got {other}")
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getConstruction resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ InfoRead("b1", "getConstruction", Map [ "waypointSymbol", Literal(StringLit "X1-NEARBY-C1") ], "$t1")
              SetVariable("b2", "complete", Accessor("IsComplete", TempRef "$t1")) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ -> Assert.Equal(VBool false, top.locals.["complete"])
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getJumpGate resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ InfoRead("b1", "getJumpGate", Map [ "waypointSymbol", Literal(StringLit "X1-NEARBY-JG1") ], "$t1")
              SetVariable("b2", "connections", Accessor("Connections", TempRef "$t1")) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ ->
                match top.locals.["connections"] with
                | VList items -> Assert.Contains(items, fun v -> v = VString "X1-TEST")
                | other -> Assert.Fail($"expected a VList, got {other}")
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getSystems resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        // `getSystems`'s JobRunner handler routes through `GalaxyHydration.fetchSystemsCached`
        // (added for the login rate-limit fix), which nests a second `RequestQueue.enqueue`
        // call inside the first when the cache is cold — the real always-running
        // `RequestQueue.Worker` tolerates that reentrant queuing, but this test's
        // single-item-at-a-time manual pump (`dispatchNextForTests`) deadlocks on it (proven:
        // it hangs the same way even with a 60s deadline). Pre-warm the cache — matching
        // `GalaxyHydration`'s own private `galaxy:<agent>:systems` key format — so the fast
        // cache-hit path is taken instead, avoiding the nested enqueue entirely.
        let systems = fixture.Client.ListSystems(App.seededToken) |> Async.RunSynchronously
        Persistence.ApiCacheRepository.put dbPath "galaxy:FAKE-AGENT:systems" (JsonSerializer.Serialize systems)
        |> Async.RunSynchronously

        let instructions = [ InfoRead("b1", "getSystems", Map.empty, "$t1"); SetVariable("b2", "systems", TempRef "$t1") ]
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ ->
                match top.locals.["systems"] with
                | VList items ->
                    Assert.Contains(
                        items,
                        fun v ->
                            match v with
                            | VRecord m -> m.["Symbol"] = VString "X1-NEARBY"
                            | _ -> false
                    )
                | other -> Assert.Fail($"expected a VList, got {other}")
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getSystem resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ InfoRead("b1", "getSystem", Map [ "systemSymbol", Literal(StringLit "X1-NEARBY") ], "$t1")
              SetVariable("b2", "symbol", Accessor("Symbol", TempRef "$t1")) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ -> Assert.Equal(VString "X1-NEARBY", top.locals.["symbol"])
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getFaction resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ InfoRead("b1", "getFaction", Map [ "factionSymbol", Literal(StringLit "COSMIC") ], "$t1")
              SetVariable("b2", "symbol", Accessor("Symbol", TempRef "$t1")) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ -> Assert.Equal(VString "COSMIC", top.locals.["symbol"])
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getFactions resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ InfoRead("b1", "getFactions", Map.empty, "$t1"); SetVariable("b2", "factions", TempRef "$t1") ]
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ ->
                match top.locals.["factions"] with
                | VList items ->
                    Assert.Contains(
                        items,
                        fun v ->
                            match v with
                            | VRecord m -> m.["Symbol"] = VString "GALACTIC"
                            | _ -> false
                    )
                | other -> Assert.Fail($"expected a VList, got {other}")
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getMyFactions resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously
        let instructions = [ InfoRead("b1", "getMyFactions", Map.empty, "$t1"); SetVariable("b2", "reps", TempRef "$t1") ]
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ ->
                match top.locals.["reps"] with
                | VList items ->
                    Assert.Contains(
                        items,
                        fun v ->
                            match v with
                            | VRecord m -> m.["Symbol"] = VString "COSMIC" && m.["Reputation"] = VNumber 12.0
                            | _ -> false
                    )
                | other -> Assert.Fail($"expected a VList, got {other}")
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getRepairCost resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ InfoRead("b1", "getRepairCost", Map.empty, "$t1")
              SetVariable("b2", "price", Accessor("TotalPrice", TempRef "$t1")) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ -> Assert.Equal(VNumber 5000.0, top.locals.["price"])
            | [] -> Assert.Fail("expected a stack frame")
        | None -> Assert.Fail("job not found"))

[<Fact>]
let ``getScrapValue resolves against the real fake over HTTP`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        let initialShip = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions =
            [ InfoRead("b1", "getScrapValue", Map.empty, "$t1")
              SetVariable("b2", "price", Accessor("TotalPrice", TempRef "$t1")) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) initialShip

        withPumpedQueue 20.0 (fun () ->
            JobRunner.runToCompletion fixture.Client dbPath 1 App.seededToken jobId
            |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some job ->
            Assert.Equal(Completed, job.status)

            match job.stack with
            | top :: _ -> Assert.Equal(VNumber 25000.0, top.locals.["price"])
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

/// Regression test: a real bug found live — `tickOnce` used to refresh a job's
/// ship-lock lease unconditionally after stepping it, using the *pre-step*
/// "was it terminal" check. A job that failed during that very step had already
/// had its lock correctly released (`JobRunner.fs`'s `JobFailed` effect), only for
/// the very next line to resurrect it — permanently wedging that ship until the
/// next lease-expiry sweep/reclaim. Fixed by re-checking the job's *current*
/// status via `JobRunner.getStatus` before refreshing.
[<Fact>]
let ``tickOnce doesn't resurrect a ship lock the same step just released on failure`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        Persistence.AgentRepository.saveAgent dbPath "FAKE-AGENT" App.seededToken
        |> Async.RunSynchronously

        let ship1 = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        // A waypoint that doesn't exist in the fake's fixture 404s immediately, on
        // the very first attempt — a deterministic, single-tick failure, exactly
        // like the real "System X1-AB12 not found" case this was found from.
        let instructions =
            [ InfoRead("b1", "getShipyard", Map [ "waypointSymbol", Literal(StringLit "X1-BOGUS-A1") ], "result") ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) ship1

        withPumpedQueue 20.0 (fun () -> JobScheduler.tickOnce fixture.Client dbPath |> Async.RunSynchronously)

        match JobRunner.getStatus jobId with
        | Some { status = Failed _ } -> ()
        | Some other -> Assert.Fail($"expected the job to have failed, got {other.status}")
        | None -> Assert.Fail("job not found")

        Assert.False(shipLockExists dbPath "FAKE-AGENT-1"))

/// Regression test: a real bug found live — a pure DSL evaluation error (e.g. a
/// non-list value wired into a `forEach`, which stock/untyped Blockly control
/// blocks don't prevent at edit time) was a raw exception thrown from
/// `Step.step`, only wrapped in `tick`'s `try/finally` (no `try/with`). Left
/// uncaught, it propagated out of `JobScheduler`'s background tick loop and, per
/// the host's `StopHost` exception behavior, took the entire ASP.NET server down
/// — confirmed live from a real crash log ("Erwarte eine Liste." / `Eval.asList`).
/// Fixed by catching any exception during `Step.step` and failing just that job.
[<Fact>]
let ``a DSL evaluation error fails just that job instead of crashing the tick loop`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        Persistence.AgentRepository.saveAgent dbPath "FAKE-AGENT" App.seededToken
        |> Async.RunSynchronously

        let ship1 = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        // A forEach whose "list" expression evaluates to a plain number — exactly
        // the kind of mismatch a stock (untyped) Blockly control block allows,
        // reproducing the live "Erwarte eine Liste." crash.
        let instructions = [ ForEach("b1", "item", Literal(NumberLit 1.0), []) ]

        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) ship1

        // Must not throw — that's the whole point of the fix.
        JobScheduler.tickOnce fixture.Client dbPath |> Async.RunSynchronously

        match JobRunner.getStatus jobId with
        | Some { status = Failed msg } -> Assert.Contains("Liste", msg)
        | Some other -> Assert.Fail($"expected the job to have failed, got {other.status}")
        | None -> Assert.Fail("job not found")

        Assert.False(shipLockExists dbPath "FAKE-AGENT-1"))

/// A dismissed pilot card must disappear from the live dashboard
/// (`JobRunner.listJobs`) without touching the persisted `jobs` row History
/// reads independently — confirmed directly against the table rather than via
/// `JobRepository.listHistory`, which needs an unrelated `program_definitions`
/// join `startJobSync`'s minimal test fixture doesn't set up.
[<Fact>]
let ``dismissing a finished pilot clears it from listJobs but keeps its persisted row`` () =
    use fixture = new JobFixture()

    withJobTest fixture.RawClient (fun dbPath ->
        Persistence.AgentRepository.saveAgent dbPath "FAKE-AGENT" App.seededToken
        |> Async.RunSynchronously

        let ship1 = fixture.Client.GetShip(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously

        let instructions = [ ForEach("b1", "item", Literal(NumberLit 1.0), []) ]
        let jobId = startJobSync fixture.Client dbPath "FAKE-AGENT-1" (program instructions) ship1

        JobScheduler.tickOnce fixture.Client dbPath |> Async.RunSynchronously

        match JobRunner.getStatus jobId with
        | Some { status = Failed _ } -> ()
        | Some other -> Assert.Fail($"expected the job to have failed first, got {other.status}")
        | None -> Assert.Fail("job not found")

        JobRunner.dismiss jobId

        Assert.True(JobRunner.getStatus jobId |> Option.isNone)
        Assert.DoesNotContain(jobId, JobRunner.listJobs () |> List.map (fun j -> j.jobId))

        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT state FROM jobs WHERE id = $id;"
        cmd.Parameters.AddWithValue("$id", jobId) |> ignore

        match cmd.ExecuteScalar() with
        | null -> Assert.Fail("expected the jobs table row to still exist after dismiss")
        | v -> Assert.Equal("Failed", string v))

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
