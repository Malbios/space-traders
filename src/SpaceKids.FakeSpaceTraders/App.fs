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

/// Milestone 6: navigate/extract durations are driven by this clock (not
/// `DateTime.Now` directly) so integration tests can reason about exact
/// arrival/cooldown timestamps without real wall-clock delays. Deliberately public
/// (mutable) so `SpaceKids.IntegrationTests` can override it in-process.
let mutable clock: unit -> DateTimeOffset = fun () -> DateTimeOffset.UtcNow

/// Deliberately short — this is a fake for fast, deterministic tests, not a
/// simulation of real in-game travel/mining durations.
let mutable fixedTravelSeconds = 3.0
let mutable fixedCooldownSeconds = 3.0
let private fixedPricePerUnit = 10

let mutable private agent =
    { symbol = "FAKE-AGENT"
      headquarters = headquarters
      credits = 175000L
      startingFaction = "COSMIC"
      shipCount = 1 }

let private routeWaypoint (symbol: string) : RouteWaypoint =
    { symbol = symbol
      ``type`` = "PLANET"
      systemSymbol = systemSymbol
      x = 0
      y = 0 }

let private makeRoute (destination: string) (origin: string) (departure: DateTimeOffset) (arrival: DateTimeOffset) : Route =
    { destination = routeWaypoint destination
      origin = routeWaypoint origin
      departureTime = departure.ToString("o")
      arrival = arrival.ToString("o") }

/// First fake endpoints with real side effects (Milestone 6) — every prior endpoint
/// was read-only/immutable; `ship`/`agent` become mutable module state, guarded by a
/// plain lock since ASP.NET Core can dispatch requests concurrently.
let mutable private ship =
    { symbol = "FAKE-AGENT-1"
      registration = { role = "COMMAND" }
      nav =
        { systemSymbol = systemSymbol
          waypointSymbol = headquarters
          route = makeRoute headquarters headquarters (clock ()) (clock ())
          status = "DOCKED"
          flightMode = "CRUISE" }
      fuel = { current = 400; capacity = 400 }
      cargo = { capacity = 40; units = 0; inventory = [] }
      cooldown =
        { shipSymbol = "FAKE-AGENT-1"
          totalSeconds = 0
          remainingSeconds = 0
          expiration = (clock ()).ToString("o") } }

let private shipLock = obj ()

/// Reads always go through the same lock writes use — `ship`/`agent` are plain
/// mutable fields with no volatile/memory-barrier guarantee otherwise, and a request
/// handler reading them runs on a different thread than whichever handler last wrote
/// them (this is exactly what Milestone 6's reconciliation calls do: a `GetShip` from
/// one request racing a `navigate`/`extract`/`sell` mutation from another).
let private readShip () : Ship = lock shipLock (fun () -> ship)
let private readAgent () : Agent = lock shipLock (fun () -> agent)

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

let private withAuth (ctx: HttpContext) (respond: unit -> Task<IResult>) : Task<IResult> =
    if authorized ctx then respond () else Task.FromResult(Results.Unauthorized())

let private ok (data: 'a) : IResult = Results.Ok({| data = data |})

let private readJsonBody (ctx: HttpContext) : Task<JsonDocument> =
    task {
        use reader = new System.IO.StreamReader(ctx.Request.Body)
        let! body = reader.ReadToEndAsync()
        return JsonDocument.Parse(body)
    }

/// Fault injection (§13a): explicitly deferred out of Milestone 2, built here for
/// Milestone 5 so the request queue's retry/reset/outage handling is exercised against
/// real HTTP responses, not just reasoned about. Milestone 6's action endpoints route
/// through the same switch, so reconciliation can be exercised the same way.
type private FaultMode =
    | Normal
    | RateLimited
    | ServerError
    | Reset
    | Unreachable
    | DropAfterProcessing

let mutable private faultMode = Normal

/// Longer than any sane client-side `HttpClient.Timeout` in production, so this
/// surfaces to the caller as a post-send `TaskCanceledException` — the "ambiguous
/// failure" signal the request queue must never auto-retry. Deliberately overridable
/// (mutable, public) so reconciliation integration tests don't have to wait 30 real
/// seconds for the delayed mutation to land.
let mutable dropAfterProcessingDelayMs = 30000

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
///
/// `DropAfterProcessing` delays *before* calling `respond` (so any state mutation the
/// endpoint performs still happens, just after the artificial delay) — this is exactly
/// what makes the Milestone 6 reconciliation scenario faithful: the client times out
/// and never sees the response, but a subsequent `GetShip` already reflects the change.
let private applyFault (ctx: HttpContext) (respond: unit -> Task<IResult>) : Task<IResult> =
    task {
        match faultMode with
        | Normal -> return! respond ()
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
            do! Task.Delay(dropAfterProcessingDelayMs)
            return! respond ()
    }

let configureApp (app: WebApplication) =
    app.MapGet(
        "/my/agent",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault ctx (fun () -> withAuth ctx (fun () -> task { return ok (readAgent ()) })))
    )
    |> ignore

    app.MapGet(
        "/my/ships",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault ctx (fun () -> withAuth ctx (fun () -> task { return ok [ readShip () ] })))
    )
    |> ignore

    app.MapGet(
        "/my/ships/{shipSymbol}",
        Func<HttpContext, Task<IResult>>(fun ctx -> applyFault ctx (fun () -> withAuth ctx (fun () -> task { return ok (readShip ()) })))
    )
    |> ignore

    app.MapGet(
        "/my/contracts",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault ctx (fun () -> withAuth ctx (fun () -> task { return ok [ contract ] })))
    )
    |> ignore

    app.MapGet(
        "/systems/{systemSymbol}/waypoints",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault ctx (fun () -> withAuth ctx (fun () -> task { return ok waypoints })))
    )
    |> ignore

    app.MapGet(
        "/systems/{systemSymbol}/waypoints/{waypointSymbol}/market",
        Func<HttpContext, Task<IResult>>(fun ctx -> applyFault ctx (fun () -> withAuth ctx (fun () -> task { return ok market })))
    )
    |> ignore

    app.MapPost(
        "/my/ships/{shipSymbol}/navigate",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                let! doc = readJsonBody ctx
                                let dest = doc.RootElement.GetProperty("waypointSymbol").GetString()
                                let departure = clock ()
                                let arrival = departure.AddSeconds(fixedTravelSeconds)

                                let responseNav =
                                    { ship.nav with
                                        waypointSymbol = dest
                                        route = makeRoute dest ship.nav.waypointSymbol departure arrival
                                        status = "IN_TRANSIT" }

                                lock shipLock (fun () -> ship <- { ship with nav = { responseNav with status = "IN_ORBIT" } })

                                return
                                    ok
                                        { nav = responseNav
                                          fuel = { current = ship.fuel.current; capacity = ship.fuel.capacity; consumed = None } }
                            })))
    )
    |> ignore

    app.MapPost(
        "/my/ships/{shipSymbol}/orbit",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                lock shipLock (fun () -> ship <- { ship with nav = { ship.nav with status = "IN_ORBIT" } })
                                return ok { nav = ship.nav }
                            })))
    )
    |> ignore

    app.MapPost(
        "/my/ships/{shipSymbol}/dock",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                lock shipLock (fun () -> ship <- { ship with nav = { ship.nav with status = "DOCKED" } })
                                return ok { nav = ship.nav }
                            })))
    )
    |> ignore

    app.MapPost(
        "/my/ships/{shipSymbol}/extract",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                let yieldSymbol = "IRON"
                                let yieldUnits = 10
                                let expiration = (clock ()).AddSeconds(fixedCooldownSeconds)

                                lock
                                    shipLock
                                    (fun () ->
                                        let newInventory =
                                            match ship.cargo.inventory |> List.tryFind (fun i -> i.symbol = yieldSymbol) with
                                            | Some _ ->
                                                ship.cargo.inventory
                                                |> List.map (fun i ->
                                                    if i.symbol = yieldSymbol then
                                                        { i with units = i.units + yieldUnits }
                                                    else
                                                        i)
                                            | None ->
                                                ship.cargo.inventory
                                                @ [ { symbol = yieldSymbol
                                                      name = "Iron"
                                                      description = "Iron ore"
                                                      units = yieldUnits } ]

                                        ship <-
                                            { ship with
                                                cargo =
                                                    { ship.cargo with
                                                        units = ship.cargo.units + yieldUnits
                                                        inventory = newInventory }
                                                cooldown =
                                                    // Ceiling, not truncation — `remainingSeconds > 0` is how
                                                    // callers (JobRunner.cooldownExpirationOf) detect an active
                                                    // cooldown at all; truncating a sub-1-second fixedCooldownSeconds
                                                    // down to 0 would make a genuinely active cooldown invisible.
                                                    { shipSymbol = ship.symbol
                                                      totalSeconds = int (ceil fixedCooldownSeconds)
                                                      remainingSeconds = int (ceil fixedCooldownSeconds)
                                                      expiration = expiration.ToString("o") } })

                                return
                                    ok
                                        { extraction =
                                            { shipSymbol = ship.symbol
                                              ``yield`` = { symbol = yieldSymbol; units = yieldUnits } }
                                          cooldown = ship.cooldown
                                          cargo = ship.cargo }
                            })))
    )
    |> ignore

    app.MapPost(
        "/my/ships/{shipSymbol}/purchase",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                let! doc = readJsonBody ctx
                                let tradeSymbol = doc.RootElement.GetProperty("symbol").GetString()
                                let units = doc.RootElement.GetProperty("units").GetInt32()
                                let totalPrice = units * fixedPricePerUnit

                                lock
                                    shipLock
                                    (fun () ->
                                        let newInventory =
                                            match ship.cargo.inventory |> List.tryFind (fun i -> i.symbol = tradeSymbol) with
                                            | Some _ ->
                                                ship.cargo.inventory
                                                |> List.map (fun i ->
                                                    if i.symbol = tradeSymbol then { i with units = i.units + units } else i)
                                            | None ->
                                                ship.cargo.inventory
                                                @ [ { symbol = tradeSymbol
                                                      name = tradeSymbol
                                                      description = ""
                                                      units = units } ]

                                        ship <-
                                            { ship with
                                                cargo = { ship.cargo with units = ship.cargo.units + units; inventory = newInventory } }

                                        agent <- { agent with credits = agent.credits - int64 totalPrice })

                                return
                                    ok
                                        { agent = agent
                                          cargo = ship.cargo
                                          transaction =
                                            { waypointSymbol = ship.nav.waypointSymbol
                                              shipSymbol = ship.symbol
                                              tradeSymbol = tradeSymbol
                                              ``type`` = "PURCHASE"
                                              units = units
                                              pricePerUnit = fixedPricePerUnit
                                              totalPrice = totalPrice
                                              timestamp = (clock ()).ToString("o") } }
                            })))
    )
    |> ignore

    app.MapPost(
        "/my/ships/{shipSymbol}/sell",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                let! doc = readJsonBody ctx
                                let tradeSymbol = doc.RootElement.GetProperty("symbol").GetString()
                                let units = doc.RootElement.GetProperty("units").GetInt32()
                                let totalPrice = units * fixedPricePerUnit

                                lock
                                    shipLock
                                    (fun () ->
                                        let newInventory =
                                            ship.cargo.inventory
                                            |> List.map (fun i ->
                                                if i.symbol = tradeSymbol then
                                                    { i with units = max 0 (i.units - units) }
                                                else
                                                    i)
                                            |> List.filter (fun i -> i.units > 0)

                                        ship <-
                                            { ship with
                                                cargo =
                                                    { ship.cargo with
                                                        units = max 0 (ship.cargo.units - units)
                                                        inventory = newInventory } }

                                        agent <- { agent with credits = agent.credits + int64 totalPrice })

                                return
                                    ok
                                        { agent = agent
                                          cargo = ship.cargo
                                          transaction =
                                            { waypointSymbol = ship.nav.waypointSymbol
                                              shipSymbol = ship.symbol
                                              tradeSymbol = tradeSymbol
                                              ``type`` = "SELL"
                                              units = units
                                              pricePerUnit = fixedPricePerUnit
                                              totalPrice = totalPrice
                                              timestamp = (clock ()).ToString("o") } }
                            })))
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

/// Test-only: resets mutable ship/agent/fault state between test cases (this module is
/// a process-wide singleton, matching `RequestQueue`/`JobRunner`'s own pattern).
let resetForTests () =
    faultMode <- Normal
    dropAfterProcessingDelayMs <- 30000
    clock <- fun () -> DateTimeOffset.UtcNow
    fixedTravelSeconds <- 3.0
    fixedCooldownSeconds <- 3.0
    agent <-
        { symbol = "FAKE-AGENT"
          headquarters = headquarters
          credits = 175000L
          startingFaction = "COSMIC"
          shipCount = 1 }

    ship <-
        { symbol = "FAKE-AGENT-1"
          registration = { role = "COMMAND" }
          nav =
            { systemSymbol = systemSymbol
              waypointSymbol = headquarters
              route = makeRoute headquarters headquarters (clock ()) (clock ())
              status = "DOCKED"
              flightMode = "CRUISE" }
          fuel = { current = 400; capacity = 400 }
          cargo = { capacity = 40; units = 0; inventory = [] }
          cooldown =
            { shipSymbol = "FAKE-AGENT-1"
              totalSeconds = 0
              remainingSeconds = 0
              expiration = (clock ()).ToString("o") } }
