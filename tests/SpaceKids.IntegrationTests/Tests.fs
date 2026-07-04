module Tests

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open Microsoft.Data.Sqlite
open Xunit
open Microsoft.AspNetCore.Mvc.Testing
open SpaceKids.FakeSpaceTraders
open SpaceKids.FakeSpaceTraders.EntryPoint
open SpaceKids.SpaceTraders
open SpaceKids.Server
open SpaceKids.Server.Persistence

/// Proves the same SpaceTradersClient code that will hit the real API also runs
/// green against the in-process fake (§13a) — no dependency on SpaceTraders' uptime.
type FakeSpaceTradersFixture() =
    let factory = new WebApplicationFactory<Program>()
    member _.Factory = factory
    member _.RawClient = factory.CreateClient()
    member _.Client = new SpaceTradersClient(factory.CreateClient())
    interface System.IDisposable with
        member _.Dispose() = factory.Dispose()

[<Fact>]
let ``GetAgent returns the seeded agent`` () =
    use fixture = new FakeSpaceTradersFixture()
    let agent = fixture.Client.GetAgent(App.seededToken) |> Async.RunSynchronously
    Assert.Equal("FAKE-AGENT", agent.symbol)
    Assert.Equal("X1-TEST-A1", agent.headquarters)

[<Fact>]
let ``ListShips returns the seeded ship`` () =
    use fixture = new FakeSpaceTradersFixture()
    let ships = fixture.Client.ListShips(App.seededToken) |> Async.RunSynchronously
    Assert.Single(ships) |> ignore
    Assert.Equal("FAKE-AGENT-1", ships.[0].symbol)

[<Fact>]
let ``ListContracts returns the seeded contract`` () =
    use fixture = new FakeSpaceTradersFixture()
    let contracts = fixture.Client.ListContracts(App.seededToken) |> Async.RunSynchronously
    Assert.Single(contracts) |> ignore
    Assert.Equal("fake-contract-1", contracts.[0].id)

[<Fact>]
let ``ListWaypoints returns the seeded waypoints`` () =
    use fixture = new FakeSpaceTradersFixture()
    let waypoints = fixture.Client.ListWaypoints(App.seededToken, "X1-TEST") |> Async.RunSynchronously
    Assert.Equal(2, waypoints.Length)

[<Fact>]
let ``GetMarket returns the seeded market`` () =
    use fixture = new FakeSpaceTradersFixture()
    let market = fixture.Client.GetMarket(App.seededToken, "X1-TEST", "X1-TEST-A1") |> Async.RunSynchronously
    Assert.Equal("X1-TEST-A1", market.symbol)

[<Fact>]
let ``A wrong token raises SpaceTradersApiException with 401`` () =
    use fixture = new FakeSpaceTradersFixture()
    let ex =
        Assert.Throws<SpaceTradersApiException>(fun () ->
            fixture.Client.GetAgent("wrong-token") |> Async.RunSynchronously |> ignore)
    match ex :> exn with
    | SpaceTradersApiException(statusCode, _) -> Assert.Equal(401, statusCode)
    | _ -> Assert.Fail("expected SpaceTradersApiException")

// ---------------------------------------------------------------------------
// Milestone 5: request queue (§13/§19) — priority/aging, retry classification,
// server-reset detection, and API-unreachable handling, exercised against the fake's
// fault injection (§13a) where the fake can produce the failure at all.
// ---------------------------------------------------------------------------

let private tempDbPath () =
    Path.Combine(Path.GetTempPath(), $"spacekids-queue-test-{Guid.NewGuid()}.db")

let private deleteDbFiles (dbPath: string) =
    SqliteConnection.ClearAllPools()
    for suffix in [ ""; "-shm"; "-wal" ] do
        let path = dbPath + suffix
        if File.Exists(path) then File.Delete(path)

let private setFaultMode (rawClient: HttpClient) (mode: string) =
    let body = JsonSerializer.Serialize({| mode = mode |})
    use content = new StringContent(body, Encoding.UTF8, "application/json")
    rawClient.PostAsync("/_fault/mode", content) |> Async.AwaitTask |> Async.RunSynchronously |> ignore

/// Runs `body` against a fresh temp db with the queue's process-wide state reset first
/// and the fake's fault mode reset to normal after — this module's mutable state and
/// the fake's fault mode are both process-wide singletons, so tests must not leak
/// state into each other despite xunit running this file's tests sequentially (same
/// test class = same collection = no parallelism between them).
let private withQueueTest (rawClient: HttpClient) (body: string -> unit) =
    RequestQueue.resetForTests ()
    setFaultMode rawClient "normal"
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        body dbPath
    finally
        setFaultMode rawClient "normal"
        RequestQueue.resetForTests ()
        deleteDbFiles dbPath

[<Fact>]
let ``a higher-priority item dispatches before a lower-priority one queued first`` () =
    use fixture = new FakeSpaceTradersFixture()
    withQueueTest fixture.RawClient (fun dbPath ->
        let order = ResizeArray<string>()
        let track name =
            fun () ->
                async {
                    lock order (fun () -> order.Add name)
                    return ()
                }

        let lowTask = RequestQueue.enqueue dbPath 5 "low" (track "low") |> Async.StartAsTask
        let highTask = RequestQueue.enqueue dbPath 1 "high" (track "high") |> Async.StartAsTask
        System.Threading.Thread.Sleep(50) // let both land in the pending list

        RequestQueue.dispatchNextForTests () |> Async.RunSynchronously |> ignore
        RequestQueue.dispatchNextForTests () |> Async.RunSynchronously |> ignore
        Async.RunSynchronously (Async.AwaitTask lowTask)
        Async.RunSynchronously (Async.AwaitTask highTask)

        Assert.Equal<string list>([ "high"; "low" ], List.ofSeq order))

[<Fact>]
let ``aging lets a long-waiting low-priority item catch up to a newer higher-priority one`` () =
    use fixture = new FakeSpaceTradersFixture()
    withQueueTest fixture.RawClient (fun dbPath ->
        let order = ResizeArray<string>()
        let track name =
            fun () ->
                async {
                    lock order (fun () -> order.Add name)
                    return ()
                }

        // priority 5 waits past one aging interval (5s) so its effective priority
        // becomes 4 by the time "newer" (genuinely priority 4) is enqueued — the tie
        // is then broken by enqueue order (FIFO), proving aging moved it, not luck.
        let oldTask = RequestQueue.enqueue dbPath 5 "old" (track "old") |> Async.StartAsTask
        System.Threading.Thread.Sleep(5500)
        let newerTask = RequestQueue.enqueue dbPath 4 "newer" (track "newer") |> Async.StartAsTask
        System.Threading.Thread.Sleep(50) // let it land in the pending list

        RequestQueue.dispatchNextForTests () |> Async.RunSynchronously |> ignore
        RequestQueue.dispatchNextForTests () |> Async.RunSynchronously |> ignore
        Async.RunSynchronously (Async.AwaitTask oldTask)
        Async.RunSynchronously (Async.AwaitTask newerTask)

        Assert.Equal<string list>([ "old"; "newer" ], List.ofSeq order))

[<Fact>]
let ``a 429 is retried automatically once Retry-After has been honored`` () =
    use fixture = new FakeSpaceTradersFixture()
    withQueueTest fixture.RawClient (fun dbPath ->
        setFaultMode fixture.RawClient "429"
        let callTask =
            RequestQueue.enqueue dbPath 1 "GET /my/agent" (fun () -> fixture.Client.GetAgent(App.seededToken))
            |> Async.StartAsTask

        System.Threading.Thread.Sleep(50) // let it land in the pending list
        let dispatchTask = RequestQueue.dispatchNextForTests () |> Async.StartAsTask
        System.Threading.Thread.Sleep(200) // still inside the 429's Retry-After wait
        setFaultMode fixture.RawClient "normal"

        Async.RunSynchronously (Async.AwaitTask dispatchTask) |> ignore
        let agent = Async.RunSynchronously (Async.AwaitTask callTask)
        Assert.Equal("FAKE-AGENT", agent.symbol))

[<Fact>]
let ``a definite (never-reached-the-server) failure is retried automatically`` () =
    use fixture = new FakeSpaceTradersFixture()
    withQueueTest fixture.RawClient (fun dbPath ->
        let mutable calls = 0
        let call () =
            async {
                calls <- calls + 1
                if calls = 1 then
                    raise (HttpRequestException("simulated: never reached the server"))
                return "ok"
            }

        let callTask = RequestQueue.enqueue dbPath 1 "synthetic" call |> Async.StartAsTask
        System.Threading.Thread.Sleep(50) // let it land in the pending list
        RequestQueue.dispatchNextForTests () |> Async.RunSynchronously |> ignore
        let result = Async.RunSynchronously (Async.AwaitTask callTask)
        Assert.Equal("ok", result)
        Assert.Equal(2, calls))

[<Fact>]
let ``drop-after-processing surfaces as AmbiguousFailure and is not retried`` () =
    use fixture = new FakeSpaceTradersFixture()
    withQueueTest fixture.RawClient (fun dbPath ->
        setFaultMode fixture.RawClient "drop-after-processing"
        let shortTimeoutClient = fixture.Factory.CreateClient()
        shortTimeoutClient.Timeout <- TimeSpan.FromSeconds(1.0)
        let client = SpaceTradersClient(shortTimeoutClient)

        let callTask =
            RequestQueue.enqueue dbPath 1 "GET /my/agent" (fun () -> client.GetAgent(App.seededToken))
            |> Async.StartAsTask

        System.Threading.Thread.Sleep(50) // let it land in the pending list
        RequestQueue.dispatchNextForTests () |> Async.RunSynchronously |> ignore

        let ex =
            Assert.Throws<AggregateException>(fun () -> callTask.Wait())

        // Async<->Task interop can nest one AggregateException inside another;
        // GetBaseException unwraps down to the actual exception RequestQueue raised.
        Assert.IsType<RequestQueue.AmbiguousFailure>(ex.GetBaseException()) |> ignore
        let status = RequestQueue.getStatus dbPath |> Async.RunSynchronously
        Assert.Equal(0, status.pendingCount))

[<Fact>]
let ``a 401 marks the queue's server-reset state and further dispatch stops`` () =
    use fixture = new FakeSpaceTradersFixture()
    withQueueTest fixture.RawClient (fun dbPath ->
        setFaultMode fixture.RawClient "reset"
        let callTask =
            RequestQueue.enqueue dbPath 1 "GET /my/agent" (fun () -> fixture.Client.GetAgent(App.seededToken))
            |> Async.StartAsTask

        System.Threading.Thread.Sleep(50) // let it land in the pending list
        RequestQueue.dispatchNextForTests () |> Async.RunSynchronously |> ignore

        let ex = Assert.Throws<AggregateException>(fun () -> callTask.Wait())
        Assert.IsType<RequestQueue.ServerResetDetected>(ex.GetBaseException()) |> ignore

        let status = RequestQueue.getStatus dbPath |> Async.RunSynchronously
        Assert.True(status.serverResetDetected)

        RequestQueue.clearServerReset ()
        let statusAfterClear = RequestQueue.getStatus dbPath |> Async.RunSynchronously
        Assert.False(statusAfterClear.serverResetDetected))

[<Fact>]
let ``repeated 5xx marks the queue unreachable without failing the caller, then resumes`` () =
    use fixture = new FakeSpaceTradersFixture()
    withQueueTest fixture.RawClient (fun dbPath ->
        RequestQueue.setMaxAttemptsForTests 2
        setFaultMode fixture.RawClient "5xx"

        let callTask =
            RequestQueue.enqueue dbPath 3 "GET /my/agent" (fun () -> fixture.Client.GetAgent(App.seededToken))
            |> Async.StartAsTask

        System.Threading.Thread.Sleep(50) // let it land in the pending list
        // exhausts the (lowered) retry budget and re-queues instead of failing the caller
        RequestQueue.dispatchNextForTests () |> Async.RunSynchronously |> ignore

        let statusDuringOutage = RequestQueue.getStatus dbPath |> Async.RunSynchronously
        Assert.True(statusDuringOutage.unreachableSince.IsSome)
        Assert.Equal(1, statusDuringOutage.pendingCount)
        Assert.False(callTask.IsCompleted)

        setFaultMode fixture.RawClient "normal"
        RequestQueue.dispatchNextForTests () |> Async.RunSynchronously |> ignore

        let agent = Async.RunSynchronously (Async.AwaitTask callTask)
        Assert.Equal("FAKE-AGENT", agent.symbol)
        let statusAfterRecovery = RequestQueue.getStatus dbPath |> Async.RunSynchronously
        Assert.True(statusAfterRecovery.unreachableSince.IsNone))
