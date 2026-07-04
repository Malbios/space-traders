module SpaceKids.FakeSpaceTraders.App

open System
open System.Text.Json
open System.Threading.Tasks
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

/// Fault injection (§13a): explicitly deferred out of Milestone 2, built here for
/// Milestone 5 so the request queue's retry/reset/outage handling is exercised against
/// real HTTP responses, not just reasoned about.
type private FaultMode =
    | Normal
    | RateLimited
    | ServerError
    | Reset
    | Unreachable
    | DropAfterProcessing

let mutable private faultMode = Normal

let private parseFaultMode (name: string) : FaultMode =
    match name with
    | "429" -> RateLimited
    | "5xx" -> ServerError
    | "reset" -> Reset
    | "unreachable" -> Unreachable
    | "drop-after-processing" -> DropAfterProcessing
    | _ -> Normal

/// Applies the current fault mode before falling through to `respond`. `unreachable`
/// is modeled as a 503 rather than literally severing the TCP connection — an
/// in-process fake can't do that, and §13 treats "connection failures / 5xx across the
/// board" the same way regardless, so 503 is the faithful proxy.
let private applyFault (ctx: HttpContext) (respond: unit -> IResult) : Task<IResult> =
    task {
        match faultMode with
        | Normal -> return respond ()
        | RateLimited ->
            ctx.Response.Headers["Retry-After"] <- Microsoft.Extensions.Primitives.StringValues("1")
            return Results.StatusCode 429
        | ServerError -> return Results.StatusCode 500
        | Reset ->
            return
                Results.Json(
                    {| error = {| code = 4100; message = "Token invalid."; requestId = "fake-reset" |} |},
                    statusCode = 401
                )
        | Unreachable -> return Results.StatusCode 503
        | DropAfterProcessing ->
            // longer than any sane client-side HttpClient.Timeout, so this surfaces to
            // the caller as a post-send TaskCanceledException — the "ambiguous failure"
            // signal the request queue must never auto-retry.
            do! Task.Delay(30000)
            return respond ()
    }

let configureApp (app: WebApplication) =
    app.MapGet(
        "/my/agent",
        Func<HttpContext, Task<IResult>>(fun ctx -> applyFault ctx (fun () -> withAuth ctx (fun () -> ok agent)))
    )
    |> ignore

    app.MapGet(
        "/my/ships",
        Func<HttpContext, Task<IResult>>(fun ctx -> applyFault ctx (fun () -> withAuth ctx (fun () -> ok [ ship ])))
    )
    |> ignore

    app.MapGet(
        "/my/contracts",
        Func<HttpContext, Task<IResult>>(fun ctx -> applyFault ctx (fun () -> withAuth ctx (fun () -> ok [ contract ])))
    )
    |> ignore

    app.MapGet(
        "/systems/{systemSymbol}/waypoints",
        Func<HttpContext, Task<IResult>>(fun ctx -> applyFault ctx (fun () -> withAuth ctx (fun () -> ok waypoints)))
    )
    |> ignore

    app.MapGet(
        "/systems/{systemSymbol}/waypoints/{waypointSymbol}/market",
        Func<HttpContext, Task<IResult>>(fun ctx -> applyFault ctx (fun () -> withAuth ctx (fun () -> ok market)))
    )
    |> ignore

    app.MapPost(
        "/_fault/mode",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                use reader = new System.IO.StreamReader(ctx.Request.Body)
                let! body = reader.ReadToEndAsync()
                let doc = JsonDocument.Parse(body)
                faultMode <- parseFaultMode (doc.RootElement.GetProperty("mode").GetString())
                return Results.Ok()
            })
    )
    |> ignore

    app
