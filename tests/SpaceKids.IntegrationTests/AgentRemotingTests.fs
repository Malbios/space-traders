module AgentRemotingTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Xunit
open Microsoft.AspNetCore.Mvc.Testing
open SpaceKids.FakeSpaceTraders
open SpaceKids.FakeSpaceTraders.EntryPoint
open SpaceKids.SpaceTraders
open SpaceKids.Server
open SpaceKids.Server.Persistence

/// Milestone: entity inspector (visual-map feature) — exercises
/// `AgentRemoting.fetchWaypointMarket`/`fetchWaypointShipyard` against the fake
/// over real HTTP, through the real `RequestQueue`, matching `JobRunnerTests.fs`'s
/// own fixture shape.
type private AgentFixture() =
    let factory = new WebApplicationFactory<Program>()
    member _.Factory = factory
    member _.RawClient = factory.CreateClient()
    member _.Client = new SpaceTradersClient(factory.CreateClient())

    interface IDisposable with
        member _.Dispose() = factory.Dispose()

let private tempDbPath () =
    Path.Combine(Path.GetTempPath(), $"spacekids-agent-test-{Guid.NewGuid()}.db")

let private deleteDbFiles (dbPath: string) =
    SqliteConnection.ClearAllPools()

    for suffix in [ ""; "-shm"; "-wal" ] do
        let path = dbPath + suffix
        if File.Exists(path) then File.Delete(path)

let private withAgentTest (body: string -> unit) =
    RequestQueue.resetForTests ()
    App.resetForTests ()
    let dbPath = tempDbPath ()

    try
        MigrationRunner.run dbPath
        body dbPath
    finally
        RequestQueue.resetForTests ()
        App.resetForTests ()
        deleteDbFiles dbPath

/// The hosted `RequestQueue.Worker` background service doesn't reliably dispatch
/// in this `WebApplicationFactory` test-hosting context (confirmed by direct
/// observation in `JobRunnerTests.fs`) — drive the queue with a manual pump loop
/// instead, matching that file's own established pattern.
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

[<Fact>]
let ``fetchWaypointMarket returns Some for a waypoint with a market`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        let result =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.fetchWaypointMarket fixture.Client dbPath App.seededToken "X1-TEST-A1"
                |> Async.RunSynchronously)

        match result with
        | Some market -> Assert.Equal("X1-TEST-A1", market.symbol)
        | None -> Assert.Fail("expected a market for the headquarters waypoint"))

[<Fact>]
let ``fetchWaypointShipyard returns Some for a waypoint with a shipyard`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        let result =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.fetchWaypointShipyard fixture.Client dbPath App.seededToken "X1-TEST-A1"
                |> Async.RunSynchronously)

        match result with
        | Some shipyard -> Assert.Equal("X1-TEST-A1", shipyard.symbol)
        | None -> Assert.Fail("expected a shipyard for the headquarters waypoint"))

[<Fact>]
let ``fetchWaypointMarket returns None for a waypoint with no market`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        let result =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.fetchWaypointMarket fixture.Client dbPath App.seededToken "X1-TEST-B2"
                |> Async.RunSynchronously)

        Assert.True(result.IsNone))

/// The real API only includes priced `tradeGoods`/`ships` when one of the player's
/// own ships is physically at that waypoint — both seeded ships live at
/// headquarters (`X1-TEST-A1`), so these two facts lock in the ship-present half
/// of that behavior explicitly, not just implicitly via other tests.
[<Fact>]
let ``fetchWaypointMarket includes priced tradeGoods when a ship is present`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        let result =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.fetchWaypointMarket fixture.Client dbPath App.seededToken "X1-TEST-A1"
                |> Async.RunSynchronously)

        match result with
        | Some market -> Assert.True(market.tradeGoods |> Option.map (fun g -> not g.IsEmpty) |> Option.defaultValue false)
        | None -> Assert.Fail("expected a market for the headquarters waypoint"))

[<Fact>]
let ``fetchWaypointShipyard includes priced ships when a ship is present`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        let result =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.fetchWaypointShipyard fixture.Client dbPath App.seededToken "X1-TEST-A1"
                |> Async.RunSynchronously)

        match result with
        | Some shipyard -> Assert.NotEmpty(shipyard.ships)
        | None -> Assert.Fail("expected a shipyard for the headquarters waypoint"))

/// `X1-TEST-C3` has the same MARKETPLACE+SHIPYARD traits as headquarters, but no
/// ship is ever seeded there — the "no ship present" counterpart.
[<Fact>]
let ``fetchWaypointMarket omits tradeGoods when no ship is present`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        let result =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.fetchWaypointMarket fixture.Client dbPath App.seededToken "X1-TEST-C3"
                |> Async.RunSynchronously)

        match result with
        | Some market -> Assert.True(market.tradeGoods.IsNone)
        | None -> Assert.Fail("expected a market for X1-TEST-C3"))

[<Fact>]
let ``fetchWaypointShipyard has empty priced ships but non-empty shipTypes when no ship is present`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        let result =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.fetchWaypointShipyard fixture.Client dbPath App.seededToken "X1-TEST-C3"
                |> Async.RunSynchronously)

        match result with
        | Some shipyard ->
            Assert.Empty(shipyard.ships)
            Assert.NotEmpty(shipyard.shipTypes)
        | None -> Assert.Fail("expected a shipyard for X1-TEST-C3"))

/// Regression test: the real API omits the `ships` key entirely (not an empty
/// array) when no ship is present — `X1-TEST-D4`'s fixture simulates that raw
/// shape. Before the null-normalization fix in `fetchWaypointShipyard`, this
/// crashed with a `NullReferenceException` instead of returning `Some` with an
/// empty `ships` list.
[<Fact>]
let ``fetchWaypointShipyard normalizes a missing ships key to an empty list`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        let result =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.fetchWaypointShipyard fixture.Client dbPath App.seededToken "X1-TEST-D4"
                |> Async.RunSynchronously)

        match result with
        | Some shipyard -> Assert.Empty(shipyard.ships)
        | None -> Assert.Fail("expected a shipyard for X1-TEST-D4"))

/// Contracts tab: `fake-contract-2` is seeded unaccepted specifically so this
/// (and the Accept button in the client) has something to exercise.
[<Fact>]
let ``acceptContract accepts a not-yet-accepted seeded contract`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        let result =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.acceptContract fixture.Client dbPath App.seededToken "fake-contract-2"
                |> Async.RunSynchronously)

        Assert.Equal(Ok(), result)

        let updated =
            withPumpedQueue 20.0 (fun () -> fixture.Client.GetContract(App.seededToken, "fake-contract-2") |> Async.RunSynchronously)

        Assert.True(updated.contract.accepted))

[<Fact>]
let ``fulfillContract completes an accepted contract whose deliveries are done`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        fixture.Client.Extract(App.seededToken, "FAKE-AGENT-1") |> Async.RunSynchronously |> ignore

        fixture.Client.DeliverContract(App.seededToken, "fake-contract-1", "FAKE-AGENT-1", "IRON", 10)
        |> Async.RunSynchronously
        |> ignore

        let result =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.fulfillContract fixture.Client dbPath App.seededToken "fake-contract-1"
                |> Async.RunSynchronously)

        Assert.Equal(Ok(), result)

        let updated =
            withPumpedQueue 20.0 (fun () -> fixture.Client.GetContract(App.seededToken, "fake-contract-1") |> Async.RunSynchronously)

        Assert.True(updated.contract.fulfilled))

[<Fact>]
let ``loadFactions returns all factions and the agent's reputations`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        let result =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.loadFactions fixture.Client dbPath App.seededToken |> Async.RunSynchronously)

        match result with
        | Ok snapshot ->
            Assert.Contains(snapshot.factions, fun f -> f.symbol = "COSMIC")
            Assert.Contains(snapshot.factions, fun f -> f.symbol = "GALACTIC")
            Assert.True(snapshot.reputations |> List.exists (fun (s, r) -> s = "COSMIC" && r = 12))
            Assert.True(snapshot.reputations |> List.exists (fun (s, r) -> s = "GALACTIC" && r = 3))
        | Error message -> Assert.Fail(message))

[<Fact>]
let ``loadPublicAgents returns seeded public agents`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        let result =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.loadPublicAgents fixture.Client dbPath App.seededToken |> Async.RunSynchronously)

        match result with
        | Ok agents ->
            Assert.Contains(agents, fun a -> a.symbol = "FAKE-AGENT")
            Assert.Contains(agents, fun a -> a.symbol = "OTHER-AGENT")
        | Error message -> Assert.Fail(message))

[<Fact>]
let ``loadSystemWaypoints returns waypoints for a different system`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        let result =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.loadSystemWaypoints fixture.Client dbPath App.seededToken "X1-NEARBY"
                |> Async.RunSynchronously)

        match result with
        | Ok waypoints -> Assert.NotEmpty(waypoints)
        | Error message -> Assert.Fail(message))

[<Fact>]
let ``loadRestOfState skips the galaxy catalog when includeGalaxyCatalog is false`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        let agent = fixture.Client.GetAgent(App.seededToken) |> Async.RunSynchronously
        AgentRepository.saveAgent dbPath agent.symbol App.seededToken |> Async.RunSynchronously

        let full =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.loadRestOfState fixture.Client dbPath agent App.seededToken true
                |> Async.RunSynchronously)

        let refresh =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.loadRestOfState fixture.Client dbPath agent App.seededToken false
                |> Async.RunSynchronously)

        Assert.NotEmpty(full.systems)
        Assert.NotEmpty(full.waypoints)
        Assert.NotEmpty(full.markets)
        Assert.Empty(refresh.systems)
        Assert.Empty(refresh.waypoints)
        Assert.Empty(refresh.markets)
        Assert.NotEmpty(refresh.ships))

[<Fact>]
let ``fetchSystemsCached serves from api_cache without a second ListSystems call`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        let agent = fixture.Client.GetAgent(App.seededToken) |> Async.RunSynchronously

        let countSystemsRequests () =
            use conn = Database.openConnection dbPath
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM request_queue_events WHERE endpoint LIKE 'GET /systems%';"
            Convert.ToInt32(cmd.ExecuteScalar())

        let before = countSystemsRequests ()

        let first =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.fetchSystemsCached fixture.Client dbPath agent.symbol App.seededToken
                |> Async.RunSynchronously)

        let afterFirst = countSystemsRequests ()

        let second =
            withPumpedQueue 20.0 (fun () ->
                AgentRemoting.fetchSystemsCached fixture.Client dbPath agent.symbol App.seededToken
                |> Async.RunSynchronously)

        let afterSecond = countSystemsRequests ()

        Assert.NotEmpty(first)
        Assert.Equal(first.Length, second.Length)
        Assert.True(afterFirst > before)
        Assert.Equal(afterFirst, afterSecond))

[<Fact>]
let ``token login path does not call ListSystems`` () =
    use fixture = new AgentFixture()

    withAgentTest (fun dbPath ->
        let agent = fixture.Client.GetAgent(App.seededToken) |> Async.RunSynchronously

        let countSystemsRequests () =
            use conn = Database.openConnection dbPath
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM request_queue_events WHERE endpoint = 'GET /systems';"
            Convert.ToInt32(cmd.ExecuteScalar())

        let before = countSystemsRequests ()

        withPumpedQueue 20.0 (fun () ->
            AgentRemoting.loadRestOfState fixture.Client dbPath agent App.seededToken false
            |> Async.RunSynchronously
            |> ignore)

        Assert.Equal(before, countSystemsRequests ()))
