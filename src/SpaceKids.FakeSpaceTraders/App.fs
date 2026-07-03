module SpaceKids.FakeSpaceTraders.App

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open SpaceKids.SpaceTraders

/// Not a game simulator (§13a) — just enough fixed, deterministic state for the
/// endpoints this project actually consumes to answer coherently.
let seededToken = "FAKE_TOKEN_1"
let private systemSymbol = "X1-TEST"
let private headquarters = "X1-TEST-A1"

let private agent =
    { symbol = "FAKE-AGENT"
      headquarters = headquarters
      credits = 175000L
      startingFaction = "COSMIC"
      shipCount = 1 }

let private ship =
    { symbol = "FAKE-AGENT-1"
      registration = { role = "COMMAND" }
      nav =
        { systemSymbol = systemSymbol
          waypointSymbol = headquarters
          status = "DOCKED" }
      fuel = { current = 400; capacity = 400 } }

let private contract =
    { id = "fake-contract-1"
      factionSymbol = "COSMIC"
      ``type`` = "PROCUREMENT"
      accepted = true
      fulfilled = false
      expiration = "2026-12-31T00:00:00.000Z" }

let private waypoints =
    [ { symbol = headquarters
        ``type`` = "PLANET"
        systemSymbol = systemSymbol
        x = 0
        y = 0 }
      { symbol = "X1-TEST-B2"
        ``type`` = "ASTEROID_FIELD"
        systemSymbol = systemSymbol
        x = 5
        y = 8 } ]

let private market =
    { symbol = headquarters
      exports = [ { symbol = "FOOD"; name = "Food" } ]
      imports = [ { symbol = "FUEL"; name = "Fuel" } ]
      exchange = [ { symbol = "IRON"; name = "Iron" } ] }

let private authorized (ctx: HttpContext) : bool =
    let header = ctx.Request.Headers.Authorization.ToString()
    header = $"Bearer {seededToken}"

let private withAuth (ctx: HttpContext) (respond: unit -> IResult) : IResult =
    if authorized ctx then respond () else Results.Unauthorized()

let private ok (data: 'a) : IResult = Results.Ok({| data = data |})

let configureApp (app: WebApplication) =
    app.MapGet(
        "/my/agent",
        Func<HttpContext, IResult>(fun ctx -> withAuth ctx (fun () -> ok agent))
    )
    |> ignore

    app.MapGet(
        "/my/ships",
        Func<HttpContext, IResult>(fun ctx -> withAuth ctx (fun () -> ok [ ship ]))
    )
    |> ignore

    app.MapGet(
        "/my/contracts",
        Func<HttpContext, IResult>(fun ctx -> withAuth ctx (fun () -> ok [ contract ]))
    )
    |> ignore

    app.MapGet(
        "/systems/{systemSymbol}/waypoints",
        Func<HttpContext, IResult>(fun ctx -> withAuth ctx (fun () -> ok waypoints))
    )
    |> ignore

    app.MapGet(
        "/systems/{systemSymbol}/waypoints/{waypointSymbol}/market",
        Func<HttpContext, IResult>(fun ctx -> withAuth ctx (fun () -> ok market))
    )
    |> ignore

    app
