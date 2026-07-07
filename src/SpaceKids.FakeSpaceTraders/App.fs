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
      shipCount = 2 }

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

let private seedShip (symbol: string) : Ship =
    { symbol = symbol
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
        { shipSymbol = symbol
          totalSeconds = 0
          remainingSeconds = 0
          expiration = Some((clock ()).ToString("o")) } }

/// Milestone 7 (§14): ship-lock rejection/reclaim is only testable with at least two
/// independently mutable ships (one locked, one free) — a single mutable `ship`
/// (Milestone 6) can't represent that. Keyed by ship symbol, guarded by the same
/// `shipLock` every mutation already went through.
let mutable private ships: Map<string, Ship> =
    [ "FAKE-AGENT-1", seedShip "FAKE-AGENT-1"; "FAKE-AGENT-2", seedShip "FAKE-AGENT-2" ]
    |> Map.ofList

let private shipLock = obj ()

/// Reads always go through the same lock writes use — `ships`/`agent` are plain
/// mutable fields with no volatile/memory-barrier guarantee otherwise, and a request
/// handler reading them runs on a different thread than whichever handler last wrote
/// them (this is exactly what Milestone 6's reconciliation calls do: a `GetShip` from
/// one request racing a `navigate`/`extract`/`sell` mutation from another).
let private readShip (shipSymbol: string) : Ship = lock shipLock (fun () -> ships.[shipSymbol])
let private readAllShips () : Ship list = lock shipLock (fun () -> ships |> Map.toList |> List.map snd)
let private readAgent () : Agent = lock shipLock (fun () -> agent)

let private routeShipSymbol (ctx: HttpContext) : string = ctx.Request.RouteValues.["shipSymbol"] :?> string
let private routeContractId (ctx: HttpContext) : string = ctx.Request.RouteValues.["contractId"] :?> string
let private routeWaypointSymbol (ctx: HttpContext) : string = ctx.Request.RouteValues.["waypointSymbol"] :?> string

/// Milestone 9/Part A: `acceptContract`/`deliverContract` mutate this, so the
/// seeded contracts become a `Map`-keyed-by-id like `ships`, guarded by the same lock.
/// Two seeded: `fake-contract-1` is already accepted (exercises the "active,
/// in-progress" contracts-tab display), `fake-contract-2` isn't (exercises the
/// Accept button).
let mutable private contracts: Map<string, Contract> =
    [ "fake-contract-1",
      { id = "fake-contract-1"
        factionSymbol = "COSMIC"
        ``type`` = "PROCUREMENT"
        terms =
          { deadline = "2026-12-31T00:00:00.000Z"
            payment = { onAccepted = 5000; onFulfilled = 20000 }
            deliver =
              [ { tradeSymbol = "IRON"; destinationSymbol = "X1-TEST-A1"; unitsRequired = 10; unitsFulfilled = 0 } ] }
        accepted = true
        fulfilled = false
        expiration = Some "2026-12-31T00:00:00.000Z"
        deadlineToAccept = None }
      "fake-contract-2",
      { id = "fake-contract-2"
        factionSymbol = "COSMIC"
        ``type`` = "PROCUREMENT"
        terms =
          { deadline = "2026-12-31T00:00:00.000Z"
            payment = { onAccepted = 3000; onFulfilled = 12000 }
            deliver =
              [ { tradeSymbol = "IRON"; destinationSymbol = "X1-TEST-A1"; unitsRequired = 5; unitsFulfilled = 0 } ] }
        accepted = false
        fulfilled = false
        expiration = None
        deadlineToAccept = Some "2026-12-31T00:00:00.000Z" } ]
    |> Map.ofList

let private readContract (contractId: string) : Contract = lock shipLock (fun () -> contracts.[contractId])
let private readAllContracts () : Contract list = lock shipLock (fun () -> contracts |> Map.toList |> List.map snd)

/// Milestone 9/Part A: `purchaseShip` adds a new ship to the fleet — numbered past the
/// two seeded ships, reset alongside them between tests.
let mutable private nextShipNumber = 3

/// Mirrors the real API's "priced market/shipyard data only shows up when a ship
/// of yours is physically there" behavior (§8, `SpaceTraders/Types.fs:144-162`) —
/// both seeded ships live at `headquarters`, so this is only ever true there.
let private hasShipAt (waypointSymbol: string) : bool =
    readAllShips () |> List.exists (fun s -> s.nav.waypointSymbol = waypointSymbol)

let private shipyardFixture (waypointSymbol: string) : Shipyard =
    { symbol = waypointSymbol
      shipTypes = [ { ``type`` = "SHIP_MINING_DRONE" } ]
      ships =
        if hasShipAt waypointSymbol then
            [ { ``type`` = "SHIP_MINING_DRONE"; purchasePrice = 50000 } ]
        else
            [] }

let private waypoints =
    [ { symbol = headquarters
        ``type`` = "PLANET"
        systemSymbol = systemSymbol
        x = 0
        y = 0
        traits =
          [ { symbol = "MARKETPLACE"; name = "Marktplatz"; description = "Ein Ort zum Handeln." }
            { symbol = "SHIPYARD"; name = "Werft"; description = "Hier können Schiffe gekauft werden." } ] }
      { symbol = "X1-TEST-B2"
        ``type`` = "ASTEROID_FIELD"
        systemSymbol = systemSymbol
        x = 5
        y = 8
        traits =
          [ { symbol = "COMMON_METAL_DEPOSITS"
              name = "Häufige Metallvorkommen"
              description = "Hier lassen sich häufige Metalle abbauen." } ] }
      /// Same MARKETPLACE+SHIPYARD traits as `headquarters`, but no ship is ever
      /// seeded here — the "no ship present" counterpart to `headquarters`, so both
      /// halves of the real API's ship-presence-gated pricing are reachable.
      { symbol = "X1-TEST-C3"
        ``type`` = "PLANET"
        systemSymbol = systemSymbol
        x = -6
        y = 4
        traits =
          [ { symbol = "MARKETPLACE"; name = "Marktplatz"; description = "Ein Ort zum Handeln." }
            { symbol = "SHIPYARD"; name = "Werft"; description = "Hier können Schiffe gekauft werden." } ] } ]

/// Mirrors the real API: a waypoint without the matching trait 404s rather than
/// answering with fixture data regardless — needed so the entity inspector's
/// (visual-map feature) "no market/shipyard here" path is actually exercised
/// against something, not just always-present fixtures.
let private hasTrait (waypointSymbol: string) (traitSymbol: string) : bool =
    waypoints
    |> List.tryFind (fun w -> w.symbol = waypointSymbol)
    |> Option.map (fun w -> w.traits |> List.exists (fun t -> t.symbol = traitSymbol))
    |> Option.defaultValue false

let private marketFixture (waypointSymbol: string) : Market =
    { symbol = waypointSymbol
      exports = [ { symbol = "FOOD"; name = "Food" } ]
      imports = [ { symbol = "FUEL"; name = "Fuel" } ]
      exchange = [ { symbol = "IRON"; name = "Iron" } ]
      tradeGoods =
        if hasShipAt waypointSymbol then
            Some
                [ { symbol = "FOOD"; purchasePrice = 8; sellPrice = 12 }
                  { symbol = "FUEL"; purchasePrice = 5; sellPrice = 9 }
                  { symbol = "IRON"; purchasePrice = 10; sellPrice = 10 } ]
        else
            None }

let private authorized (ctx: HttpContext) : bool =
    let header = ctx.Request.Headers.Authorization.ToString()
    header = $"Bearer {seededToken}"

let private withAuth (ctx: HttpContext) (respond: unit -> Task<IResult>) : Task<IResult> =
    if authorized ctx then respond () else Task.FromResult(Results.Unauthorized())

let private ok (data: 'a) : IResult = Results.Ok({| data = data |})

/// Mirrors the real API's `?page=`/`?limit=` pagination on list endpoints (default
/// page 1/limit 10) — needed so `SpaceTradersClient.GetAllPages`'s multi-page walk
/// has something real to exercise, not just a client-side assumption.
let private readPaging (ctx: HttpContext) : int * int =
    let intQuery (name: string) (fallback: int) =
        match ctx.Request.Query.TryGetValue(name) with
        | true, v ->
            match Int32.TryParse(v.ToString()) with
            | true, n -> n
            | _ -> fallback
        | _ -> fallback

    intQuery "page" 1, intQuery "limit" 10

let private okPaged (items: 'a list) (page: int) (limit: int) : IResult =
    let pageItems = items |> List.skip (min items.Length ((page - 1) * limit)) |> List.truncate limit
    Results.Ok({| data = pageItems; meta = {| total = items.Length; page = page; limit = limit |} |})

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
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                let page, limit = readPaging ctx
                                return okPaged (readAllShips ()) page limit
                            })))
    )
    |> ignore

    app.MapGet(
        "/my/ships/{shipSymbol}",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault ctx (fun () -> withAuth ctx (fun () -> task { return ok (readShip (routeShipSymbol ctx)) })))
    )
    |> ignore

    app.MapGet(
        "/my/contracts",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                let page, limit = readPaging ctx
                                return okPaged (readAllContracts ()) page limit
                            })))
    )
    |> ignore

    app.MapGet(
        "/my/contracts/{contractId}",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () -> withAuth ctx (fun () -> task { return ok {| contract = readContract (routeContractId ctx) |} })))
    )
    |> ignore

    app.MapGet(
        "/systems/{systemSymbol}/waypoints/{waypointSymbol}/shipyard",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                let waypointSymbol = routeWaypointSymbol ctx

                                if hasTrait waypointSymbol "SHIPYARD" then
                                    return ok (shipyardFixture waypointSymbol)
                                else
                                    return Results.NotFound()
                            })))
    )
    |> ignore

    app.MapGet(
        "/systems/{systemSymbol}/waypoints",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                let page, limit = readPaging ctx
                                return okPaged waypoints page limit
                            })))
    )
    |> ignore

    app.MapGet(
        "/systems/{systemSymbol}/waypoints/{waypointSymbol}/market",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                let waypointSymbol = routeWaypointSymbol ctx

                                if hasTrait waypointSymbol "MARKETPLACE" then
                                    return ok (marketFixture waypointSymbol)
                                else
                                    return Results.NotFound()
                            })))
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
                                let shipSymbol = routeShipSymbol ctx
                                let! doc = readJsonBody ctx
                                let dest = doc.RootElement.GetProperty("waypointSymbol").GetString()
                                let departure = clock ()
                                let arrival = departure.AddSeconds(fixedTravelSeconds)
                                let current = readShip shipSymbol

                                let responseNav =
                                    { current.nav with
                                        waypointSymbol = dest
                                        route = makeRoute dest current.nav.waypointSymbol departure arrival
                                        status = "IN_TRANSIT" }

                                let updated =
                                    { current with nav = { responseNav with status = "IN_ORBIT" } }

                                lock shipLock (fun () -> ships <- ships.Add(shipSymbol, updated))

                                return
                                    ok
                                        { nav = responseNav
                                          fuel = { current = updated.fuel.current; capacity = updated.fuel.capacity; consumed = None } }
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
                                let shipSymbol = routeShipSymbol ctx

                                let updated =
                                    lock
                                        shipLock
                                        (fun () ->
                                            let current = ships.[shipSymbol]
                                            let updated = { current with nav = { current.nav with status = "IN_ORBIT" } }
                                            ships <- ships.Add(shipSymbol, updated)
                                            updated)

                                return ok { nav = updated.nav }
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
                                let shipSymbol = routeShipSymbol ctx

                                let updated =
                                    lock
                                        shipLock
                                        (fun () ->
                                            let current = ships.[shipSymbol]
                                            let updated = { current with nav = { current.nav with status = "DOCKED" } }
                                            ships <- ships.Add(shipSymbol, updated)
                                            updated)

                                return ok { nav = updated.nav }
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
                                let shipSymbol = routeShipSymbol ctx
                                let yieldSymbol = "IRON"
                                let yieldUnits = 10
                                let expiration = (clock ()).AddSeconds(fixedCooldownSeconds)

                                let updated =
                                    lock
                                        shipLock
                                        (fun () ->
                                            let current = ships.[shipSymbol]

                                            let newInventory =
                                                match current.cargo.inventory |> List.tryFind (fun i -> i.symbol = yieldSymbol) with
                                                | Some _ ->
                                                    current.cargo.inventory
                                                    |> List.map (fun i ->
                                                        if i.symbol = yieldSymbol then
                                                            { i with units = i.units + yieldUnits }
                                                        else
                                                            i)
                                                | None ->
                                                    current.cargo.inventory
                                                    @ [ { symbol = yieldSymbol
                                                          name = "Iron"
                                                          description = "Iron ore"
                                                          units = yieldUnits } ]

                                            let updated =
                                                { current with
                                                    cargo =
                                                        { current.cargo with
                                                            units = current.cargo.units + yieldUnits
                                                            inventory = newInventory }
                                                    cooldown =
                                                        // Ceiling, not truncation — `remainingSeconds > 0` is how
                                                        // callers (JobRunner.cooldownExpirationOf) detect an active
                                                        // cooldown at all; truncating a sub-1-second fixedCooldownSeconds
                                                        // down to 0 would make a genuinely active cooldown invisible.
                                                        { shipSymbol = current.symbol
                                                          totalSeconds = int (ceil fixedCooldownSeconds)
                                                          remainingSeconds = int (ceil fixedCooldownSeconds)
                                                          expiration = Some(expiration.ToString("o")) } }

                                            ships <- ships.Add(shipSymbol, updated)
                                            updated)

                                return
                                    ok
                                        { extraction =
                                            { shipSymbol = updated.symbol
                                              ``yield`` = { symbol = yieldSymbol; units = yieldUnits } }
                                          cooldown = updated.cooldown
                                          cargo = updated.cargo }
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
                                let shipSymbol = routeShipSymbol ctx
                                let! doc = readJsonBody ctx
                                let tradeSymbol = doc.RootElement.GetProperty("symbol").GetString()
                                let units = doc.RootElement.GetProperty("units").GetInt32()
                                let totalPrice = units * fixedPricePerUnit

                                let updated =
                                    lock
                                        shipLock
                                        (fun () ->
                                            let current = ships.[shipSymbol]

                                            let newInventory =
                                                match current.cargo.inventory |> List.tryFind (fun i -> i.symbol = tradeSymbol) with
                                                | Some _ ->
                                                    current.cargo.inventory
                                                    |> List.map (fun i ->
                                                        if i.symbol = tradeSymbol then { i with units = i.units + units } else i)
                                                | None ->
                                                    current.cargo.inventory
                                                    @ [ { symbol = tradeSymbol
                                                          name = tradeSymbol
                                                          description = ""
                                                          units = units } ]

                                            let updated =
                                                { current with
                                                    cargo = { current.cargo with units = current.cargo.units + units; inventory = newInventory } }

                                            ships <- ships.Add(shipSymbol, updated)
                                            agent <- { agent with credits = agent.credits - int64 totalPrice }
                                            updated)

                                return
                                    ok
                                        { agent = agent
                                          cargo = updated.cargo
                                          transaction =
                                            { waypointSymbol = updated.nav.waypointSymbol
                                              shipSymbol = updated.symbol
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
                                let shipSymbol = routeShipSymbol ctx
                                let! doc = readJsonBody ctx
                                let tradeSymbol = doc.RootElement.GetProperty("symbol").GetString()
                                let units = doc.RootElement.GetProperty("units").GetInt32()
                                let totalPrice = units * fixedPricePerUnit

                                let updated =
                                    lock
                                        shipLock
                                        (fun () ->
                                            let current = ships.[shipSymbol]

                                            let newInventory =
                                                current.cargo.inventory
                                                |> List.map (fun i ->
                                                    if i.symbol = tradeSymbol then
                                                        { i with units = max 0 (i.units - units) }
                                                    else
                                                        i)
                                                |> List.filter (fun i -> i.units > 0)

                                            let updated =
                                                { current with
                                                    cargo =
                                                        { current.cargo with
                                                            units = max 0 (current.cargo.units - units)
                                                            inventory = newInventory } }

                                            ships <- ships.Add(shipSymbol, updated)
                                            agent <- { agent with credits = agent.credits + int64 totalPrice }
                                            updated)

                                return
                                    ok
                                        { agent = agent
                                          cargo = updated.cargo
                                          transaction =
                                            { waypointSymbol = updated.nav.waypointSymbol
                                              shipSymbol = updated.symbol
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
        "/my/ships/{shipSymbol}/survey",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                let shipSymbol = routeShipSymbol ctx
                                let expiration = (clock ()).AddSeconds(fixedCooldownSeconds)

                                let updatedCooldown =
                                    lock
                                        shipLock
                                        (fun () ->
                                            let current = ships.[shipSymbol]

                                            let newCooldown =
                                                { shipSymbol = current.symbol
                                                  totalSeconds = int (ceil fixedCooldownSeconds)
                                                  remainingSeconds = int (ceil fixedCooldownSeconds)
                                                  expiration = Some(expiration.ToString("o")) }

                                            ships <- ships.Add(shipSymbol, { current with cooldown = newCooldown })
                                            newCooldown)

                                return
                                    ok
                                        { cooldown = updatedCooldown
                                          surveys =
                                            [ { signature = "FAKE-SURVEY-1"
                                                symbol = shipSymbol
                                                deposits = [ { symbol = "IRON" } ]
                                                expiration = expiration.ToString("o")
                                                size = "MODERATE" } ] }
                            })))
    )
    |> ignore

    app.MapPost(
        "/my/contracts/{contractId}/deliver",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                let contractId = routeContractId ctx
                                let! doc = readJsonBody ctx
                                let shipSymbol = doc.RootElement.GetProperty("shipSymbol").GetString()
                                let tradeSymbol = doc.RootElement.GetProperty("tradeSymbol").GetString()
                                let units = doc.RootElement.GetProperty("units").GetInt32()

                                let updatedCargo, updatedContract =
                                    lock
                                        shipLock
                                        (fun () ->
                                            let current = ships.[shipSymbol]

                                            let newInventory =
                                                current.cargo.inventory
                                                |> List.map (fun i ->
                                                    if i.symbol = tradeSymbol then
                                                        { i with units = max 0 (i.units - units) }
                                                    else
                                                        i)
                                                |> List.filter (fun i -> i.units > 0)

                                            let updatedShip =
                                                { current with
                                                    cargo =
                                                        { current.cargo with
                                                            units = max 0 (current.cargo.units - units)
                                                            inventory = newInventory } }

                                            ships <- ships.Add(shipSymbol, updatedShip)
                                            let updatedContract = { contracts.[contractId] with fulfilled = true }
                                            contracts <- contracts.Add(contractId, updatedContract)
                                            updatedShip.cargo, updatedContract)

                                return ok { contract = updatedContract; cargo = updatedCargo }
                            })))
    )
    |> ignore

    app.MapPost(
        "/my/contracts/{contractId}/accept",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                let contractId = routeContractId ctx

                                let updatedContract =
                                    lock
                                        shipLock
                                        (fun () ->
                                            let updated = { contracts.[contractId] with accepted = true }
                                            contracts <- contracts.Add(contractId, updated)
                                            updated)

                                return ok { contract = updatedContract; agent = readAgent () }
                            })))
    )
    |> ignore

    app.MapPost(
        "/my/ships",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                let! doc = readJsonBody ctx
                                let waypointSymbol = doc.RootElement.GetProperty("waypointSymbol").GetString()
                                let price = 50000

                                let newShipSymbol =
                                    lock
                                        shipLock
                                        (fun () ->
                                            let symbol = $"FAKE-AGENT-{nextShipNumber}"
                                            nextShipNumber <- nextShipNumber + 1
                                            let newShip = { seedShip symbol with nav = { (seedShip symbol).nav with waypointSymbol = waypointSymbol } }
                                            ships <- ships.Add(symbol, newShip)
                                            agent <- { agent with credits = agent.credits - int64 price; shipCount = agent.shipCount + 1 }
                                            symbol)

                                return ok { ship = { symbol = newShipSymbol }; agent = readAgent () }
                            })))
    )
    |> ignore

    app.MapPost(
        "/my/ships/{shipSymbol}/refuel",
        Func<HttpContext, Task<IResult>>(fun ctx ->
            applyFault
                ctx
                (fun () ->
                    withAuth
                        ctx
                        (fun () ->
                            task {
                                let shipSymbol = routeShipSymbol ctx

                                let updatedFuel, unitsBought =
                                    lock
                                        shipLock
                                        (fun () ->
                                            let current = ships.[shipSymbol]
                                            let unitsBought = current.fuel.capacity - current.fuel.current
                                            let updated = { current with fuel = { current.fuel with current = current.fuel.capacity } }
                                            ships <- ships.Add(shipSymbol, updated)
                                            let totalPrice = unitsBought * fixedPricePerUnit
                                            agent <- { agent with credits = agent.credits - int64 totalPrice }
                                            updated.fuel, unitsBought)

                                let totalPrice = unitsBought * fixedPricePerUnit

                                return
                                    ok
                                        { agent = readAgent ()
                                          fuel = { current = updatedFuel.current; capacity = updatedFuel.capacity; consumed = None }
                                          transaction =
                                            { waypointSymbol = headquarters
                                              shipSymbol = shipSymbol
                                              tradeSymbol = "FUEL"
                                              ``type`` = "PURCHASE"
                                              units = unitsBought
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
          shipCount = 2 }

    ships <-
        [ "FAKE-AGENT-1", seedShip "FAKE-AGENT-1"; "FAKE-AGENT-2", seedShip "FAKE-AGENT-2" ]
        |> Map.ofList

    nextShipNumber <- 3

    contracts <-
        [ "fake-contract-1",
          { id = "fake-contract-1"
            factionSymbol = "COSMIC"
            ``type`` = "PROCUREMENT"
            terms =
              { deadline = "2026-12-31T00:00:00.000Z"
                payment = { onAccepted = 5000; onFulfilled = 20000 }
                deliver =
                  [ { tradeSymbol = "IRON"; destinationSymbol = "X1-TEST-A1"; unitsRequired = 10; unitsFulfilled = 0 } ] }
            accepted = true
            fulfilled = false
            expiration = Some "2026-12-31T00:00:00.000Z"
            deadlineToAccept = None }
          "fake-contract-2",
          { id = "fake-contract-2"
            factionSymbol = "COSMIC"
            ``type`` = "PROCUREMENT"
            terms =
              { deadline = "2026-12-31T00:00:00.000Z"
                payment = { onAccepted = 3000; onFulfilled = 12000 }
                deliver =
                  [ { tradeSymbol = "IRON"; destinationSymbol = "X1-TEST-A1"; unitsRequired = 5; unitsFulfilled = 0 } ] }
            accepted = false
            fulfilled = false
            expiration = None
            deadlineToAccept = Some "2026-12-31T00:00:00.000Z" } ]
        |> Map.ofList
