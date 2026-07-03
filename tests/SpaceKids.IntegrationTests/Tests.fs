module Tests

open Xunit
open Microsoft.AspNetCore.Mvc.Testing
open SpaceKids.FakeSpaceTraders
open SpaceKids.FakeSpaceTraders.EntryPoint
open SpaceKids.SpaceTraders

/// Proves the same SpaceTradersClient code that will hit the real API also runs
/// green against the in-process fake (§13a) — no dependency on SpaceTraders' uptime.
type FakeSpaceTradersFixture() =
    let factory = new WebApplicationFactory<Program>()
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
