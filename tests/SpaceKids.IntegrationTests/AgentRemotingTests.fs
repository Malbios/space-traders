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
