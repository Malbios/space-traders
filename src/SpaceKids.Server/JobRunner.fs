module SpaceKids.Server.JobRunner

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open SpaceKids.Core.Dsl
open SpaceKids.Core.Scheduler
open SpaceKids.SpaceTraders

/// Mirrors `Persistence/JobStateJson.fs`'s own small private `options` value — used
/// here only to serialize a call's request payload (a `QueuedAction` DU, or an
/// info-read's `infoType`/`args`) for `RequestQueue.enqueue`'s debug-view logging.
let private jsonOptions =
    let o = JsonSerializerOptions()
    o.Converters.Add(JsonFSharpConverter())
    o

/// Milestone 7 (§14): the real persistent shell around the same pure `Step.step`
/// Milestone 6 built and tested. `jobs` is a write-through cache — the `jobs` table
/// is the source of truth (survives restarts), but every tick keeps the in-memory
/// copy current too, so a live job doesn't pay a DB round-trip just to read its own
/// state back.

/// §13's priority levels: 1 = interactive (player pressing step/run), 3 =
/// background job action. Milestone 10/Part A — every queue call below now takes
/// this as a parameter instead of a single hardcoded tier, so a fully automatic
/// background pilot (driven by `JobScheduler.tickOnce`) no longer looks identical
/// to a live button press to the request queue's own aging/ordering logic.
let backgroundPriority = 3

/// Longer than the ~1s tick loop and the ~60s sweep interval combined, so a
/// genuinely live job's lease never looks expired between two ticks, but short
/// enough that a truly dead process's lock is reclaimable well within one sweep
/// cycle (§14).
let leaseSeconds = 90.0

let private jobs = ConcurrentDictionary<JobId, JobState>()

/// Serializes "read this job, compute one `step`, write the result back" against
/// concurrent callers (the background scheduler tick vs. a manual UI step/run
/// click) — deliberately *not* held across `applyEffects` (which can itself
/// recurse back into `tick` after an HTTP round-trip), or the recursive call would
/// deadlock against this same non-reentrant lock.
let private tickLock = new SemaphoreSlim(1, 1)

let private realClock: Clock = { now = fun () -> System.DateTimeOffset.UtcNow }

/// Ephemeral simulation jobs (`simulateProgram`) never touch SQLite or ship locks.
let private isEphemeral (jobId: JobId) = jobId.StartsWith("__sim__")

/// While set, `tick` uses this clock for `__sim__` jobs so `sk_wait`/arrival waits
/// fast-forward instead of sleeping in real time.
let private simClockHolder = ref<Option<Clock * DateTimeOffset ref>>(None)

let private clockFor (jobId: JobId) : Clock =
    if isEphemeral jobId then
        match !simClockHolder with
        | Some(clock, _) -> clock
        | None -> realClock
    else
        realClock

type SimulationStep =
    { scope: string
      blockId: string
      detail: string option }

type SimulationRunResult =
    { success: bool
      status: string
      error: string option
      steps: SimulationStep list
      log: string list }

let private initialFrame: Frame =
    { scope = "main"
      position =
        [ { bodyRef = MainBody
            index = 0
            loopState = None } ]
      locals = Map.empty
      returnTarget = None }

/// A cooldown is only meaningful while still counting down — matches how
/// `ExtractBaseline`/reconciliation treat "no active cooldown" as `None`.
let private cooldownExpirationOf (cooldown: Cooldown) : string option =
    if cooldown.remainingSeconds > 0 then cooldown.expiration else None

let private toSnapshot (ship: Ship) : ShipSnapshot =
    { navStatus = ship.nav.status
      navWaypoint = ship.nav.waypointSymbol
      navArrival = Some ship.nav.route.arrival
      flightMode = ship.nav.flightMode
      cargoUnits = ship.cargo.units
      cargoInventory = ship.cargo.inventory |> List.map (fun i -> i.symbol, i.units) |> Map.ofList
      cooldownExpiration = cooldownExpirationOf ship.cooldown
      fuelCurrent = ship.fuel.current }

let private inventoryMap (cargo: ShipCargo) : Map<string, int> =
    cargo.inventory |> List.map (fun i -> i.symbol, i.units) |> Map.ofList

// --- Info-read record conversion (§8, Milestone 9/Part B) --------------------------
// Kept flat per §8's own instruction — a "friendly structured record", not a general
// nested object tree.

let private shipRecord (ship: Ship) : Value =
    VRecord(
        Map.ofList
            [ "Name", VString ship.symbol
              "Waypoint", VString ship.nav.waypointSymbol
              "Status", VString ship.nav.status
              "Fuel", VNumber(float ship.fuel.current)
              "CargoUnits", VNumber(float ship.cargo.units)
              "CargoCapacity", VNumber(float ship.cargo.capacity) ]
    )

let private contractRecord (c: Contract) : Value =
    VRecord(
        Map.ofList
            [ "Id", VString c.id
              "Type", VString c.``type``
              "Accepted", VBool c.accepted
              "Fulfilled", VBool c.fulfilled ]
    )

let private waypointRecord (w: Waypoint) : Value =
    let hasTrait symbol = w.traits |> List.exists (fun t -> t.symbol = symbol)

    VRecord(
        Map.ofList
            [ "Symbol", VString w.symbol
              "Type", VString w.``type``
              "System", VString w.systemSymbol
              "HasShipyard", VBool(hasTrait "SHIPYARD")
              "HasMarket", VBool(hasTrait "MARKETPLACE") ]
    )

let private agentRecord (a: Agent) : Value =
    VRecord(
        Map.ofList
            [ "Symbol", VString a.symbol
              "Headquarters", VString a.headquarters
              "Credits", VNumber(float a.credits)
              "StartingFaction", VString a.startingFaction
              "ShipCount", VNumber(float a.shipCount) ]
    )

let private systemRecord (s: StarSystem) : Value =
    VRecord(
        Map.ofList
            [ "Symbol", VString s.symbol
              "Sector", VString s.sectorSymbol
              "Type", VString s.``type``
              "X", VNumber(float s.x)
              "Y", VNumber(float s.y)
              "Name", VString(s.name |> Option.defaultValue "")
              "Constellation", VString(s.constellation |> Option.defaultValue "") ]
    )

let private factionRecord (f: Faction) : Value =
    VRecord(
        Map.ofList
            [ "Symbol", VString f.symbol
              "Name", VString f.name
              "Description", VString f.description
              "Headquarters", VString(f.headquarters |> Option.defaultValue "")
              "IsRecruiting", VBool f.isRecruiting ]
    )

let private factionReputationRecord (f: FactionReputation) : Value =
    VRecord(Map.ofList [ "Symbol", VString f.symbol; "Reputation", VNumber(float f.reputation) ])

let private jumpGateRecord (g: JumpGate) : Value =
    VRecord(
        Map.ofList
            [ "Symbol", VString g.symbol
              "Connections", VList(g.connections |> List.map VString) ]
    )

/// `UnitsRequired`/`UnitsFulfilled`, not the bare `Required`/`Fulfilled` names — the
/// DSL's record fields are a flat, cross-record namespace keyed by name only
/// (`Validator.fs`'s `exprKind`), and `Auftrag.Fulfilled` is already a Boolean
/// (contract fulfilled y/n); this field is a Number (units fulfilled so far). Using
/// the bare name would silently misclassify one or the other. Mirrors
/// `SpaceKids.SpaceTraders.Types.fs`'s own `ContractDeliverGood.unitsRequired`/
/// `unitsFulfilled` naming.
let private constructionMaterialRecord (m: ConstructionMaterial) : Value =
    VRecord(
        Map.ofList
            [ "TradeSymbol", VString m.tradeSymbol
              "UnitsRequired", VNumber(float m.required)
              "UnitsFulfilled", VNumber(float m.fulfilled) ]
    )

let private constructionRecord (c: Construction) : Value =
    VRecord(
        Map.ofList
            [ "Symbol", VString c.symbol
              "IsComplete", VBool c.isComplete
              "Materials", VList(c.materials |> List.map constructionMaterialRecord) ]
    )

let private navRecord (n: ShipNav) : Value =
    VRecord(
        Map.ofList
            [ "Waypoint", VString n.waypointSymbol
              "System", VString n.systemSymbol
              "Status", VString n.status
              "FlightMode", VString n.flightMode ]
    )

let private cooldownRecord (c: Cooldown) : Value =
    VRecord(
        Map.ofList
            [ "Ship", VString c.shipSymbol
              "TotalSeconds", VNumber(float c.totalSeconds)
              "RemainingSeconds", VNumber(float c.remainingSeconds)
              "Expiration", VString(c.expiration |> Option.defaultValue "") ]
    )

let private priceRecord (t: PriceTransaction) : Value =
    VRecord(
        Map.ofList
            [ "Waypoint", VString t.waypointSymbol
              "Ship", VString t.shipSymbol
              "TotalPrice", VNumber(float t.totalPrice) ]
    )

let private moduleList (modules: InstalledShipModule list) : Value =
    VList(
        modules
        |> List.map (fun m -> VRecord(Map.ofList [ "Symbol", VString m.symbol; "Name", VString m.name ]))
    )

let private mountList (mounts: InstalledShipMount list) : Value =
    VList(
        mounts
        |> List.map (fun m -> VRecord(Map.ofList [ "Symbol", VString m.symbol; "Name", VString m.name ]))
    )

let private supplyChainList (entries: SupplyChainEntry list) : Value =
    VList(
        entries
        |> List.map (fun e ->
            VRecord(Map.ofList [ "Export", VString e.exportSymbol; "Import", VString e.importSymbol ]))
    )

let private optionalNumber (value: int option) : Value =
    VNumber(value |> Option.map float |> Option.defaultValue 0.0)

let private requirementsRecord (r: ShipComponentRequirements) : Value =
    VRecord(Map.ofList [ "Power", optionalNumber r.power; "Crew", optionalNumber r.crew; "Slots", optionalNumber r.slots ])

let private shipyardFrameRecord (f: ShipyardShipFrame) : Value =
    VRecord(
        Map.ofList
            [ "Symbol", VString f.symbol
              "Name", VString f.name
              "Description", VString f.description
              "ModuleSlots", VNumber(float f.moduleSlots)
              "MountingPoints", VNumber(float f.mountingPoints)
              "FuelCapacity", VNumber(float f.fuelCapacity)
              "Requirements", requirementsRecord f.requirements ]
    )

let private shipyardReactorRecord (r: ShipyardShipReactor) : Value =
    VRecord(
        Map.ofList
            [ "Symbol", VString r.symbol
              "Name", VString r.name
              "Description", VString r.description
              "PowerOutput", VNumber(float r.powerOutput)
              "Requirements", requirementsRecord r.requirements ]
    )

let private shipyardEngineRecord (e: ShipyardShipEngine) : Value =
    VRecord(
        Map.ofList
            [ "Symbol", VString e.symbol
              "Name", VString e.name
              "Description", VString e.description
              "Speed", VNumber(float e.speed)
              "Requirements", requirementsRecord e.requirements ]
    )

let private shipyardModuleRecord (m: ShipyardShipModule) : Value =
    VRecord(
        Map.ofList
            [ "Symbol", VString m.symbol
              "Name", VString m.name
              "Description", VString m.description
              "Capacity", optionalNumber m.capacity
              "Range", optionalNumber m.range
              "Requirements", requirementsRecord m.requirements ]
    )

let private shipyardMountRecord (m: ShipyardShipMount) : Value =
    VRecord(
        Map.ofList
            [ "Symbol", VString m.symbol
              "Name", VString m.name
              "Description", VString m.description
              "Strength", optionalNumber m.strength
              "Deposits", VList(m.deposits |> Option.defaultValue [] |> List.map VString)
              "Requirements", requirementsRecord m.requirements ]
    )

let private shipyardCrewRecord (c: ShipyardShipCrew) : Value =
    VRecord(Map.ofList [ "Required", VNumber(float c.required); "Capacity", VNumber(float c.capacity) ])

/// The `ships` array's full-detail entry (only ever populated when a ship of yours
/// is docked there — see `ShipyardShipEntry`'s own doc comment in
/// `SpaceTraders/Types.fs` for the "field names not yet verified against a live
/// response" caveat this inherits).
let private shipyardShipEntryRecord (s: ShipyardShipEntry) : Value =
    VRecord(
        Map.ofList
            [ "Type", VString s.``type``
              "Name", VString s.name
              "Description", VString s.description
              "Supply", VString s.supply
              "Activity", VString(s.activity |> Option.defaultValue "")
              "Price", VNumber(float s.purchasePrice)
              "Frame", shipyardFrameRecord s.frame
              "Reactor", shipyardReactorRecord s.reactor
              "Engine", shipyardEngineRecord s.engine
              "Modules", VList(s.modules |> List.map shipyardModuleRecord)
              "Mounts", VList(s.mounts |> List.map shipyardMountRecord)
              "Crew", shipyardCrewRecord s.crew ]
    )

/// `RequestQueue.enqueue`'s `AmbiguousFailure` can arrive wrapped in an
/// `AggregateException` depending on the Async<->Task interop path it crosses (the
/// same nesting the Milestone 5 tests already had to account for) — unwrap before
/// classifying so a real ambiguous failure is never misreported as a hard failure.
let rec private classifyException (ex: exn) : ApiResult =
    match ex with
    | RequestQueue.AmbiguousFailure msg -> ApiAmbiguous msg
    | :? System.AggregateException as agg when agg.InnerExceptions.Count = 1 -> classifyException agg.InnerExceptions.[0]
    | _ -> ApiFailed ex.Message

/// Executes one `QueuedAction` against the real `SpaceTradersClient`, through
/// `RequestQueue.enqueue` (§13's global-queue principle — no bypass), mapping the
/// outcome onto the scheduler's own `ApiResult` shape.
let private runAction
    (client: SpaceTradersClient)
    (dbPath: string)
    (token: string)
    (priority: int)
    (shipSymbol: string)
    (action: QueuedAction)
    : Async<ApiResult> =
    let endpoint, call =
        match action with
        | DoNavigate destination ->
            $"navigate:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.Navigate(token, shipSymbol, destination)
                    return NavigateOk(r.nav.status, r.nav.waypointSymbol, r.nav.route.arrival)
                })
        | DoOrbit ->
            $"orbit:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.Orbit(token, shipSymbol)
                    return NavResultOk(r.nav.status, r.nav.waypointSymbol)
                })
        | DoDock ->
            $"dock:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.Dock(token, shipSymbol)
                    return NavResultOk(r.nav.status, r.nav.waypointSymbol)
                })
        | DoExtract ->
            $"extract:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.Extract(token, shipSymbol)

                    return
                        ExtractOk(
                            r.cooldown.expiration |> Option.defaultValue (DateTime.UtcNow.ToString("o")),
                            r.cargo.units,
                            inventoryMap r.cargo,
                            r.extraction.``yield``.symbol,
                            r.extraction.``yield``.units
                        )
                })
        | DoBuy(tradeSymbol, units) ->
            $"buy:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.BuyGood(token, shipSymbol, tradeSymbol, units)

                    return
                        TradeOk(
                            r.cargo.units,
                            inventoryMap r.cargo,
                            r.transaction.``type``,
                            r.transaction.tradeSymbol,
                            r.transaction.units,
                            r.transaction.totalPrice
                        )
                })
        | DoSell(tradeSymbol, units) ->
            $"sell:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.SellGood(token, shipSymbol, tradeSymbol, units)

                    return
                        TradeOk(
                            r.cargo.units,
                            inventoryMap r.cargo,
                            r.transaction.``type``,
                            r.transaction.tradeSymbol,
                            r.transaction.units,
                            r.transaction.totalPrice
                        )
                })
        | DoSurvey ->
            $"survey:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.Survey(token, shipSymbol)
                    return SurveyOk(r.cooldown.expiration |> Option.defaultValue (DateTime.UtcNow.ToString("o")))
                })
        | DoDeliverContract(contractId, tradeSymbol, units) ->
            $"deliverContract:{contractId}",
            (fun () ->
                async {
                    let! r = client.DeliverContract(token, contractId, shipSymbol, tradeSymbol, units)
                    return DeliverOk(r.cargo.units, inventoryMap r.cargo, r.contract.fulfilled)
                })
        | DoAcceptContract contractId ->
            $"acceptContract:{contractId}",
            (fun () ->
                async {
                    let! r = client.AcceptContract(token, contractId)
                    return AcceptContractOk r.contract.accepted
                })
        | DoFulfillContract contractId ->
            $"fulfillContract:{contractId}",
            (fun () ->
                async {
                    let! r = client.FulfillContract(token, contractId)
                    return FulfillContractOk r.contract.fulfilled
                })
        | DoNegotiateContract ->
            $"negotiateContract:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.NegotiateContract(token, shipSymbol)
                    return NegotiateContractOk r.contract.id
                })
        | DoPurchaseShip(shipType, waypointSymbol) ->
            $"purchaseShip:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.PurchaseShip(token, shipType, waypointSymbol)
                    return PurchaseShipOk(r.ship.symbol, r.agent.shipCount)
                })
        | DoRefuel ->
            $"refuel:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.Refuel(token, shipSymbol)
                    return RefuelOk r.fuel.current
                })
        | DoJettison(tradeSymbol, units) ->
            $"jettison:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.Jettison(token, shipSymbol, tradeSymbol, units)

                    return
                        TradeOk(
                            r.cargo.units,
                            inventoryMap r.cargo,
                            "JETTISON",
                            tradeSymbol,
                            units,
                            0
                        )
                })
        | DoJump waypointSymbol ->
            $"jump:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.Jump(token, shipSymbol, waypointSymbol)
                    return NavigateOk(r.nav.status, r.nav.waypointSymbol, r.nav.route.arrival)
                })
        | DoWarp waypointSymbol ->
            $"warp:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.Warp(token, shipSymbol, waypointSymbol)
                    return NavigateOk(r.nav.status, r.nav.waypointSymbol, r.nav.route.arrival)
                })
        | DoTransferCargo(tradeSymbol, units, targetShipSymbol) ->
            $"transfer:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.TransferCargo(token, shipSymbol, tradeSymbol, units, targetShipSymbol)

                    return
                        TradeOk(
                            r.cargo.units,
                            inventoryMap r.cargo,
                            "TRANSFER",
                            tradeSymbol,
                            units,
                            0
                        )
                })
        | DoSiphon ->
            $"siphon:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.Siphon(token, shipSymbol)

                    return
                        ExtractOk(
                            r.cooldown.expiration |> Option.defaultValue (DateTime.UtcNow.ToString("o")),
                            r.cargo.units,
                            inventoryMap r.cargo,
                            "FUEL",
                            1
                        )
                })
        | DoScrapShip ->
            $"scrap:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.ScrapShip(token, shipSymbol)
                    return ScrapOk r.agent.shipCount
                })
        | DoRepair ->
            $"repair:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.Repair(token, shipSymbol)
                    return RefuelOk r.ship.fuel.current
                })
        | DoRefine produce ->
            $"refine:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.Refine(token, shipSymbol, produce)

                    return
                        ExtractOk(
                            r.cooldown.expiration |> Option.defaultValue (DateTime.UtcNow.ToString("o")),
                            r.cargo.units,
                            inventoryMap r.cargo,
                            produce,
                            1
                        )
                })
        | DoScanShips ->
            $"scanShips:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.ScanShips(token, shipSymbol)
                    return SurveyOk(r.cooldown.expiration |> Option.defaultValue (DateTime.UtcNow.ToString("o")))
                })
        | DoScanSystems ->
            $"scanSystems:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.ScanSystems(token, shipSymbol)
                    return SurveyOk(r.cooldown.expiration |> Option.defaultValue (DateTime.UtcNow.ToString("o")))
                })
        | DoScanWaypoints ->
            $"scanWaypoints:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.ScanWaypoints(token, shipSymbol)
                    return SurveyOk(r.cooldown.expiration |> Option.defaultValue (DateTime.UtcNow.ToString("o")))
                })
        | DoInstallModule moduleSymbol ->
            $"installModule:{shipSymbol}",
            (fun () ->
                async {
                    let! _ = client.InstallModule(token, shipSymbol, moduleSymbol)
                    return ActionOk
                })
        | DoRemoveModule moduleSymbol ->
            $"removeModule:{shipSymbol}",
            (fun () ->
                async {
                    let! _ = client.RemoveModule(token, shipSymbol, moduleSymbol)
                    return ActionOk
                })
        | DoInstallMount mountSymbol ->
            $"installMount:{shipSymbol}",
            (fun () ->
                async {
                    let! _ = client.InstallMount(token, shipSymbol, mountSymbol)
                    return ActionOk
                })
        | DoRemoveMount mountSymbol ->
            $"removeMount:{shipSymbol}",
            (fun () ->
                async {
                    let! _ = client.RemoveMount(token, shipSymbol, mountSymbol)
                    return ActionOk
                })
        | DoCreateChart ->
            $"createChart:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.CreateChart(token, shipSymbol)
                    return SurveyOk(r.cooldown.expiration |> Option.defaultValue (DateTime.UtcNow.ToString("o")))
                })
        | DoExtractWithSurvey surveySignature ->
            $"extractWithSurvey:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.ExtractWithSurvey(token, shipSymbol, surveySignature)

                    return
                        ExtractOk(
                            r.cooldown.expiration |> Option.defaultValue (DateTime.UtcNow.ToString("o")),
                            r.cargo.units,
                            inventoryMap r.cargo,
                            r.extraction.``yield``.symbol,
                            r.extraction.``yield``.units
                        )
                })
        | DoSupplyConstruction(waypointSymbol, tradeSymbol, units) ->
            let systemSymbol = Waypoint.systemSymbolOf waypointSymbol

            $"supplyConstruction:{waypointSymbol}",
            (fun () ->
                async {
                    let! r = client.SupplyConstruction(token, systemSymbol, waypointSymbol, shipSymbol, tradeSymbol, units)

                    return
                        TradeOk(
                            r.cargo.units,
                            inventoryMap r.cargo,
                            "SUPPLY",
                            tradeSymbol,
                            units,
                            0
                        )
                })
        | DoPatchShipNav flightMode ->
            $"patchShipNav:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.PatchShipNav(token, shipSymbol, flightMode)
                    return NavResultOk(r.nav.status, r.nav.waypointSymbol)
                })

    let requestJson = try Some(JsonSerializer.Serialize(action, jsonOptions)) with _ -> None

    async {
        try
            return! RequestQueue.enqueue dbPath priority endpoint requestJson call
        with ex ->
            return classifyException ex
    }

/// Executes one info-read block (§8/§14, Milestone 9/Part B) against the real
/// `SpaceTradersClient`, through `RequestQueue.enqueue` like every other call — a GET,
/// so no baseline/reconciliation is needed, only the converted `Value` result.
let private runInfoRead
    (client: SpaceTradersClient)
    (dbPath: string)
    (token: string)
    (priority: int)
    (shipSymbol: string)
    (infoType: string)
    (args: Map<string, string>)
    : Async<ApiResult> =
    let requireArg (name: string) =
        match args.TryFind name with
        | Some v -> v
        | None -> failwith $"Fehlendes Argument \"{name}\" für Info-Block {infoType}."

    let endpoint, call =
        match infoType with
        | "getShipInfo" ->
            $"getShipInfo:{shipSymbol}",
            (fun () ->
                async {
                    let! ship = client.GetShip(token, shipSymbol)
                    return InfoOk(shipRecord ship)
                })
        | "getFleetInfo" ->
            "getFleetInfo",
            (fun () ->
                async {
                    let! ships = client.ListShips(token)
                    return InfoOk(VList(ships |> List.map shipRecord))
                })
        | "getWaypoints" ->
            let systemSymbol = requireArg "systemSymbol"

            $"getWaypoints:{systemSymbol}",
            (fun () ->
                async {
                    let! waypoints = client.ListWaypoints(token, systemSymbol)
                    return InfoOk(VList(waypoints |> List.map waypointRecord))
                })
        | "getMarket" ->
            let waypointSymbol = requireArg "waypointSymbol"
            let systemSymbol = Waypoint.systemSymbolOf waypointSymbol

            $"getMarket:{waypointSymbol}",
            (fun () ->
                async {
                    let! market = client.GetMarket(token, systemSymbol, waypointSymbol)

                    // `tradeGoods` (with prices) is only populated by the real API
                    // when a ship is present at the market; fall back to the
                    // always-visible export/import/exchange names with no price —
                    // a documented simplification (§8), same class as elsewhere in
                    // this project.
                    let goods =
                        match market.tradeGoods with
                        | Some tradeGoods when not tradeGoods.IsEmpty ->
                            tradeGoods
                            |> List.map (fun g ->
                                VRecord(
                                    Map.ofList
                                        [ "Name", VString g.symbol
                                          "BuyPrice", VNumber(float g.purchasePrice)
                                          "SellPrice", VNumber(float g.sellPrice) ]
                                ))
                        | _ ->
                            (market.exports @ market.imports @ market.exchange)
                            |> List.map (fun g ->
                                VRecord(Map.ofList [ "Name", VString g.name; "BuyPrice", VNumber 0.0; "SellPrice", VNumber 0.0 ]))

                    return InfoOk(VRecord(Map.ofList [ "Waypoint", VString waypointSymbol; "Goods", VList goods ]))
                })
        | "getShipyard" ->
            let waypointSymbol = requireArg "waypointSymbol"
            let systemSymbol = Waypoint.systemSymbolOf waypointSymbol

            $"getShipyard:{waypointSymbol}",
            (fun () ->
                async {
                    let! r = client.GetShipyard(token, systemSymbol, waypointSymbol)

                    let types =
                        if not r.ships.IsEmpty then
                            r.ships |> List.map shipyardShipEntryRecord
                        else
                            r.shipTypes
                            |> List.map (fun t -> VRecord(Map.ofList [ "Type", VString t.``type``; "Price", VNumber 0.0 ]))

                    return InfoOk(VRecord(Map.ofList [ "Waypoint", VString waypointSymbol; "Types", VList types ]))
                })
        | "getContracts" ->
            "getContracts",
            (fun () ->
                async {
                    let! contracts = client.ListContracts(token)
                    return InfoOk(VList(contracts |> List.map contractRecord))
                })
        | "getCargo" ->
            $"getCargo:{shipSymbol}",
            (fun () ->
                async {
                    let! ship = client.GetShip(token, shipSymbol)

                    let items =
                        ship.cargo.inventory
                        |> List.map (fun i -> VRecord(Map.ofList [ "Name", VString i.name; "Units", VNumber(float i.units) ]))

                    return
                        InfoOk(
                            VRecord(
                                Map.ofList
                                    [ "Units", VNumber(float ship.cargo.units)
                                      "Capacity", VNumber(float ship.cargo.capacity)
                                      "Goods", VList items ]
                            )
                        )
                })
        | "getFuel" ->
            $"getFuel:{shipSymbol}",
            (fun () ->
                async {
                    let! ship = client.GetShip(token, shipSymbol)
                    return InfoOk(VNumber(float ship.fuel.current))
                })
        | "getCredits" ->
            "getCredits",
            (fun () ->
                async {
                    let! agent = client.GetAgent(token)
                    return InfoOk(VNumber(float agent.credits))
                })
        | "getRepairCost" ->
            $"getRepairCost:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.GetRepairCost(token, shipSymbol)
                    return InfoOk(priceRecord r.transaction)
                })
        | "getScrapValue" ->
            $"getScrapValue:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.GetScrapValue(token, shipSymbol)
                    return InfoOk(priceRecord r.transaction)
                })
        | "getWaypoint" ->
            let waypointSymbol = requireArg "waypointSymbol"
            let systemSymbol = Waypoint.systemSymbolOf waypointSymbol

            $"getWaypoint:{waypointSymbol}",
            (fun () ->
                async {
                    let! w = client.GetWaypoint(token, systemSymbol, waypointSymbol)
                    return InfoOk(waypointRecord w)
                })
        | "getMyAgent" ->
            "getMyAgent",
            (fun () ->
                async {
                    let! agent = client.GetAgent(token)
                    return InfoOk(agentRecord agent)
                })
        | "getPublicAgent" ->
            let agentSymbol = requireArg "agentSymbol"

            $"getPublicAgent:{agentSymbol}",
            (fun () ->
                async {
                    let! agent = client.GetPublicAgent(token, agentSymbol)
                    return InfoOk(agentRecord agent)
                })
        | "getPublicAgents" ->
            "getPublicAgents",
            (fun () ->
                async {
                    let! agents = client.ListAgents(token)
                    return InfoOk(VList(agents |> List.map agentRecord))
                })
        | "getCooldown" ->
            $"getCooldown:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.GetShipCooldown(token, shipSymbol)
                    return InfoOk(cooldownRecord r.cooldown)
                })
        | "getNav" ->
            $"getNav:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.GetShipNav(token, shipSymbol)
                    return InfoOk(navRecord r.nav)
                })
        | "getSupplyChain" ->
            "getSupplyChain",
            (fun () ->
                async {
                    let! entries = client.GetSupplyChain(token)
                    return InfoOk(supplyChainList entries)
                })
        | "getShipModules" ->
            $"getShipModules:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.GetShipModules(token, shipSymbol)
                    return InfoOk(moduleList r.modules)
                })
        | "getShipMounts" ->
            $"getShipMounts:{shipSymbol}",
            (fun () ->
                async {
                    let! r = client.GetShipMounts(token, shipSymbol)
                    return InfoOk(mountList r.mounts)
                })
        | "getConstruction" ->
            let waypointSymbol = requireArg "waypointSymbol"
            let systemSymbol = Waypoint.systemSymbolOf waypointSymbol

            $"getConstruction:{waypointSymbol}",
            (fun () ->
                async {
                    let! c = client.GetConstruction(token, systemSymbol, waypointSymbol)
                    return InfoOk(constructionRecord c)
                })
        | "getJumpGate" ->
            let waypointSymbol = requireArg "waypointSymbol"
            let systemSymbol = Waypoint.systemSymbolOf waypointSymbol

            $"getJumpGate:{waypointSymbol}",
            (fun () ->
                async {
                    let! g = client.GetJumpGate(token, systemSymbol, waypointSymbol)
                    return InfoOk(jumpGateRecord g)
                })
        | "getSystems" ->
            "getSystems",
            (fun () ->
                async {
                    let! agent = client.GetAgent(token)
                    let! systems = GalaxyHydration.fetchSystemsCached client dbPath agent.symbol token
                    return InfoOk(VList(systems |> List.map systemRecord))
                })
        | "getSystem" ->
            let systemSymbol = requireArg "systemSymbol"

            $"getSystem:{systemSymbol}",
            (fun () ->
                async {
                    let! s = client.GetSystem(token, systemSymbol)
                    return InfoOk(systemRecord s)
                })
        | "getFaction" ->
            let factionSymbol = requireArg "factionSymbol"

            $"getFaction:{factionSymbol}",
            (fun () ->
                async {
                    let! f = client.GetFaction(token, factionSymbol)
                    return InfoOk(factionRecord f)
                })
        | "getFactions" ->
            "getFactions",
            (fun () ->
                async {
                    let! factions = client.ListFactions(token)
                    return InfoOk(VList(factions |> List.map factionRecord))
                })
        | "getMyFactions" ->
            "getMyFactions",
            (fun () ->
                async {
                    let! factions = client.ListMyFactions(token)
                    return InfoOk(VList(factions |> List.map factionReputationRecord))
                })
        | other ->
            let locale =
                Persistence.SettingsRepository.getLocale dbPath |> Async.RunSynchronously |> SpaceKids.Core.Dsl.Locale.ofString

            match locale with
            | SpaceKids.Core.Dsl.De -> failwith $"Unbekannter Info-Block: {other}"
            | SpaceKids.Core.Dsl.En -> failwith $"Unknown info block: {other}"

    let requestJson =
        try
            Some(JsonSerializer.Serialize({| infoType = infoType; args = args |}, jsonOptions))
        with _ ->
            None

    async {
        try
            return! RequestQueue.enqueue dbPath priority endpoint requestJson call
        with ex ->
            return classifyException ex
    }

// --- Persistence helpers (Milestone 7) ----------------------------------------------

/// Mirrors §14's job statuses closely enough for dashboard/DB purposes — every
/// `JobStatus` case maps to exactly one tag.
let statusName (status: JobStatus) : string =
    match status with
    | Running -> "Running"
    | AwaitingApiResponse _ -> "AwaitingApiResponse"
    | WaitingForArrival _ -> "WaitingForArrival"
    | WaitingForCooldown _ -> "WaitingForCooldown"
    | Reconciling _ -> "Reconciling"
    | AwaitingInfoResponse _ -> "AwaitingInfoResponse"
    | WaitingForShipLock _ -> "WaitingForShipLock"
    | Paused _ -> "Paused"
    | Cancelled -> "Cancelled"
    | Completed -> "Completed"
    | Failed _ -> "Failed"

let private nextWakeAtOf (status: JobStatus) : DateTimeOffset option =
    match status with
    | WaitingForArrival until
    | WaitingForCooldown until -> Some until
    | _ -> None

let private lastErrorOf (status: JobStatus) : string option =
    match status with
    | Failed message -> Some message
    | _ -> None

let private persist (dbPath: string) (job: JobState) : Async<unit> =
    if isEphemeral job.jobId then
        async { return () }
    else
        Persistence.JobRepository.update
            dbPath
            job.jobId
            (statusName job.status)
            (Persistence.JobStateJson.serializeJobState job)
            (Step.currentBlockId job)
            (nextWakeAtOf job.status)
            (lastErrorOf job.status)

/// Hydrates a job from its `jobs` row into the in-memory cache if it isn't already
/// there — used both at scheduler startup (loading every non-terminal job) and when
/// reclaiming an orphaned ship lock for a job that isn't currently loaded.
let private ensureLoaded (dbPath: string) (jobId: JobId) : Async<unit> =
    async {
        if not (jobs.ContainsKey jobId) then
            let! rowOpt = Persistence.JobRepository.loadById dbPath jobId

            match rowOpt with
            | Some row -> jobs[jobId] <- Persistence.JobStateJson.deserializeJobState row.executionStateJson
            | None -> ()
    }

// --- Effects / tick ------------------------------------------------------------------

/// Executes a `step` result's effect list, feeding results back into `step` (via
/// `tick`) as needed. Each job only ever has one instruction in flight at a time, so
/// `jobId` alone is a sufficient dispatch key (§14).
let rec private applyEffects
    (client: SpaceTradersClient)
    (dbPath: string)
    (token: string)
    (priority: int)
    (effects: Effect list)
    : Async<unit> =
    async {
        for effect in effects do
            match effect with
            | LogMessage(jobId, text) ->
                do! tickLock.WaitAsync() |> Async.AwaitTask

                let updated =
                    try
                        match jobs.TryGetValue jobId with
                        | true, job ->
                            let job' = { job with log = text :: job.log }
                            jobs[jobId] <- job'
                            Some job'
                        | false, _ -> None
                    finally
                        tickLock.Release() |> ignore

                match updated with
                | Some job -> do! persist dbPath job
                | None -> ()
            | JobCompleted jobId
            | JobFailed(jobId, _)
            | JobCancelled jobId ->
                if not (isEphemeral jobId) then
                    // Milestone 7 (§14): the job just went terminal — release its ship
                    // lock so another program can take the ship over immediately,
                    // rather than waiting for the lease to expire.
                    match jobs.TryGetValue jobId with
                    | true, job ->
                        match job.shipSymbol with
                        | Some sym -> do! Persistence.ShipLockRepository.release dbPath sym
                        | None -> ()

                        for sym in job.dynamicShipLocks |> List.distinct do
                            do! Persistence.ShipLockRepository.release dbPath sym

                        for sym in job.parallelBranches |> List.collect (fun b -> b.job.dynamicShipLocks) |> List.distinct do
                            do! Persistence.ShipLockRepository.release dbPath sym
                    | false, _ -> ()
            | StartWait _ ->
                // Nothing to do here — `job.status`/`next_wake_at` already record
                // what it's waiting for (persisted by `tick`). The scheduler's tick
                // loop (`SpaceKids.Server.JobScheduler`) is what periodically
                // re-sends `WakeTick` once due, matching §14's own shell
                // description ("reads jobs due to wake, calls step...").
                ()
            | QueueApiCall(jobId, shipSymbol, action, attemptNumber) ->
                // Ship-scoped actions always have `Some` here by the time execution
                // reaches this point (`JobRemoting.fs`'s upfront `programRequiresShip`
                // gate refuses to start such a job without one); agnostic actions
                // (`acceptContract`/`purchaseShip`) ignore this value entirely.
                let! result = runAction client dbPath token priority (shipSymbol |> Option.defaultValue "") action
                do! tick client dbPath token priority jobId (ApiResponseReceived(jobId, attemptNumber, result))
            | QueueBranchApiCall(jobId, branchId, shipSymbol, action, attemptNumber) ->
                let! result = runAction client dbPath token priority (shipSymbol |> Option.defaultValue "") action
                do! tick client dbPath token priority jobId (BranchApiResponseReceived(jobId, branchId, attemptNumber, result))
            | ReconcileShipState(jobId, shipSymbol, attemptNumber) ->
                let shipSymbol = shipSymbol |> Option.defaultValue ""

                let! result =
                    async {
                        try
                            let! ship =
                                RequestQueue.enqueue dbPath priority $"getShip:{shipSymbol}" None (fun () ->
                                    client.GetShip(token, shipSymbol))

                            return ReconciliationShip(toSnapshot ship)
                        with ex ->
                            return classifyException ex
                    }

                do! tick client dbPath token priority jobId (ApiResponseReceived(jobId, attemptNumber, result))
            | ReconcileBranchShipState(jobId, branchId, shipSymbol, attemptNumber) ->
                let shipSymbol = shipSymbol |> Option.defaultValue ""

                let! result =
                    async {
                        try
                            let! ship =
                                RequestQueue.enqueue dbPath priority $"getShip:{shipSymbol}" None (fun () ->
                                    client.GetShip(token, shipSymbol))

                            return ReconciliationShip(toSnapshot ship)
                        with ex ->
                            return classifyException ex
                    }

                do! tick client dbPath token priority jobId (BranchApiResponseReceived(jobId, branchId, attemptNumber, result))
            | ReconcileContractState(jobId, contractId, attemptNumber, field) ->
                let! result =
                    async {
                        try
                            let! r =
                                RequestQueue.enqueue dbPath priority $"getContract:{contractId}" None (fun () ->
                                    client.GetContract(token, contractId))

                            return
                                match field with
                                | CheckAccepted -> ReconciliationContract r.contract.accepted
                                | CheckFulfilled -> ReconciliationContractFulfilled r.contract.fulfilled
                        with ex ->
                            return classifyException ex
                    }

                do! tick client dbPath token priority jobId (ApiResponseReceived(jobId, attemptNumber, result))
            | ReconcileBranchContractState(jobId, branchId, contractId, attemptNumber, field) ->
                let! result =
                    async {
                        try
                            let! r =
                                RequestQueue.enqueue dbPath priority $"getContract:{contractId}" None (fun () ->
                                    client.GetContract(token, contractId))

                            return
                                match field with
                                | CheckAccepted -> ReconciliationContract r.contract.accepted
                                | CheckFulfilled -> ReconciliationContractFulfilled r.contract.fulfilled
                        with ex ->
                            return classifyException ex
                    }

                do! tick client dbPath token priority jobId (BranchApiResponseReceived(jobId, branchId, attemptNumber, result))
            | ReconcileFleetState(jobId, attemptNumber) ->
                let! result =
                    async {
                        try
                            let! ships =
                                RequestQueue.enqueue dbPath priority "listShips" None (fun () -> client.ListShips(token))

                            return ReconciliationFleet(List.length ships)
                        with ex ->
                            return classifyException ex
                    }

                do! tick client dbPath token priority jobId (ApiResponseReceived(jobId, attemptNumber, result))
            | ReconcileContractsState(jobId, attemptNumber) ->
                let! result =
                    async {
                        try
                            let! contracts =
                                RequestQueue.enqueue dbPath priority "listContracts" None (fun () -> client.ListContracts(token))

                            return ReconciliationContracts(List.length contracts)
                        with ex ->
                            return classifyException ex
                    }

                do! tick client dbPath token priority jobId (ApiResponseReceived(jobId, attemptNumber, result))
            | ReconcileBranchFleetState(jobId, branchId, attemptNumber) ->
                let! result =
                    async {
                        try
                            let! ships =
                                RequestQueue.enqueue dbPath priority "listShips" None (fun () -> client.ListShips(token))

                            return ReconciliationFleet(List.length ships)
                        with ex ->
                            return classifyException ex
                    }

                do! tick client dbPath token priority jobId (BranchApiResponseReceived(jobId, branchId, attemptNumber, result))
            | ReconcileBranchContractsState(jobId, branchId, attemptNumber) ->
                let! result =
                    async {
                        try
                            let! contracts =
                                RequestQueue.enqueue dbPath priority "listContracts" None (fun () -> client.ListContracts(token))

                            return ReconciliationContracts(List.length contracts)
                        with ex ->
                            return classifyException ex
                    }

                do! tick client dbPath token priority jobId (BranchApiResponseReceived(jobId, branchId, attemptNumber, result))
            | QueueInfoRead(jobId, shipSymbol, infoType, args, attemptNumber, _resultTarget) ->
                let! result = runInfoRead client dbPath token priority (shipSymbol |> Option.defaultValue "") infoType args
                do! tick client dbPath token priority jobId (ApiResponseReceived(jobId, attemptNumber, result))
            | QueueBranchInfoRead(jobId, branchId, shipSymbol, infoType, args, attemptNumber, _resultTarget) ->
                let! result = runInfoRead client dbPath token priority (shipSymbol |> Option.defaultValue "") infoType args
                do! tick client dbPath token priority jobId (BranchApiResponseReceived(jobId, branchId, attemptNumber, result))
            | AcquireShipScope(jobId, blockId, shipSymbol, _hasElse) ->
                let acquireAndFetch =
                    async {
                        let! result =
                            async {
                                try
                                    let! ship =
                                        RequestQueue.enqueue dbPath priority $"getShip:{shipSymbol}" None (fun () ->
                                            client.GetShip(token, shipSymbol))

                                    return Choice1Of2(toSnapshot ship)
                                with ex ->
                                    return Choice2Of2 ex.Message
                            }

                        match result with
                        | Choice1Of2 snapshot ->
                            do! tick client dbPath token priority jobId (ShipScopeAcquired(jobId, blockId, shipSymbol, snapshot))
                        | Choice2Of2 message ->
                            if not (isEphemeral jobId) then
                                do! Persistence.ShipLockRepository.release dbPath shipSymbol

                            do! tick client dbPath token priority jobId (ShipScopeUnavailable(jobId, blockId, shipSymbol, message))
                    }

                if isEphemeral jobId then
                    do! acquireAndFetch
                else
                    let! lockResult = Persistence.ShipLockRepository.tryAcquire dbPath shipSymbol jobId leaseSeconds

                    match lockResult with
                    | Error _ -> ()
                    | Ok _orphanedJobIdOpt -> do! acquireAndFetch
            | AcquireBranchShipScope(jobId, branchId, blockId, shipSymbol, _hasElse) ->
                let acquireAndFetch =
                    async {
                        let! result =
                            async {
                                try
                                    let! ship =
                                        RequestQueue.enqueue dbPath priority $"getShip:{shipSymbol}" None (fun () ->
                                            client.GetShip(token, shipSymbol))

                                    return Choice1Of2(toSnapshot ship)
                                with ex ->
                                    return Choice2Of2 ex.Message
                            }

                        match result with
                        | Choice1Of2 snapshot ->
                            do! tick client dbPath token priority jobId (BranchShipScopeAcquired(jobId, branchId, blockId, shipSymbol, snapshot))
                        | Choice2Of2 message ->
                            if not (isEphemeral jobId) then
                                do! Persistence.ShipLockRepository.release dbPath shipSymbol

                            do! tick client dbPath token priority jobId (BranchShipScopeUnavailable(jobId, branchId, blockId, shipSymbol, message))
                    }

                if isEphemeral jobId then
                    do! acquireAndFetch
                else
                    let! lockResult = Persistence.ShipLockRepository.tryAcquire dbPath shipSymbol jobId leaseSeconds

                    match lockResult with
                    | Error _ -> ()
                    | Ok _orphanedJobIdOpt -> do! acquireAndFetch
            | ReleaseShipScope(jobId, shipSymbol) ->
                if not (isEphemeral jobId) then
                    do! Persistence.ShipLockRepository.release dbPath shipSymbol
    }

/// One scheduler tick: pulls the job, calls the pure core, persists the result,
/// applies the effects it produced. The read-compute-write critical section is
/// lock-guarded (see `tickLock`); effect application happens after releasing it.
and private tick
    (client: SpaceTradersClient)
    (dbPath: string)
    (token: string)
    (priority: int)
    (jobId: JobId)
    (event: SchedulerEvent)
    : Async<unit> =
    async {
        let! computed =
            async {
                do! tickLock.WaitAsync() |> Async.AwaitTask

                try
                    match jobs.TryGetValue jobId with
                    | false, _ -> return None
                    | true, job ->
                        try
                            let job', effects = Step.step (clockFor jobId) job event
                            jobs[jobId] <- job'
                            do! persist dbPath job'
                            return Some effects
                        with ex ->
                            // A pure-evaluation error (e.g. `Eval.asList` rejecting a
                            // non-list value wired into a `forEach`) is a program bug,
                            // not an infrastructure one — it must fail this one job,
                            // never escape `tick` itself. Left uncaught, this exception
                            // propagates out of `JobScheduler`'s background tick loop
                            // and crashes the entire host (confirmed live: a single bad
                            // program took the whole server down). Same graceful
                            // "Failed" outcome and `JobFailed` effect (ship-lock
                            // release) an API failure already gets.
                            let failedJob = { job with status = Failed ex.Message }
                            jobs[jobId] <- failedJob
                            do! persist dbPath failedJob
                            return Some [ JobFailed(jobId, ex.Message) ]
                finally
                    tickLock.Release() |> ignore
            }

        match computed with
        | Some effects -> do! applyEffects client dbPath token priority effects
        | None -> ()
    }

/// Recovers a job whose owning process may have died mid-action (Milestone 7,
/// §14) — used both at scheduler startup for every non-terminal job, and when
/// reclaiming an orphaned ship lock. An unresolved in-flight call is treated as
/// ambiguous, reusing the exact reconciliation path Milestone 6 already built and
/// tested — "unknown outcome" is what ambiguous failures already mean, not a new
/// case. A waiting job needs no special handling: the tick loop's normal due-check
/// (`until <= now`) already resumes it, arbitrarily overdue or not (clock-skew
/// catch-up, §14).
let recoverJob (client: SpaceTradersClient) (dbPath: string) (token: string) (jobId: JobId) : Async<unit> =
    async {
        match jobs.TryGetValue jobId with
        | false, _ -> ()
        | true, job ->
            match job.status with
            | Running -> do! tick client dbPath token backgroundPriority jobId WakeTick
            | AwaitingApiResponse(attempt, _, _) ->
                // Milestone 12 (bilingual support): this is a background scheduler-
                // recovery path, not a per-request handler, so there's no live request
                // to read a locale from — a synchronous local SQLite read (same
                // bridging pattern `JobRemoting.fs`'s `customBlockLookup` already
                // uses) is the pragmatic choice here.
                let locale =
                    Persistence.SettingsRepository.getLocale dbPath
                    |> Async.RunSynchronously
                    |> SpaceKids.Core.Dsl.Locale.ofString

                let message =
                    match locale with
                    | SpaceKids.Core.Dsl.De -> "Server wurde neu gestartet"
                    | SpaceKids.Core.Dsl.En -> "Server was restarted"

                do!
                    tick
                        client
                        dbPath
                        token
                        backgroundPriority
                        jobId
                        (ApiResponseReceived(jobId, attempt, ApiAmbiguous message))
            | Reconciling(attempt, _, baseline) ->
                let reconcileEffect =
                    match baseline with
                    | AcceptContractBaseline contractId -> ReconcileContractState(jobId, contractId, attempt, CheckAccepted)
                    | FulfillContractBaseline contractId -> ReconcileContractState(jobId, contractId, attempt, CheckFulfilled)
                    | FleetBaseline _ -> ReconcileFleetState(jobId, attempt)
                    | ContractsCountBaseline _ -> ReconcileContractsState(jobId, attempt)
                    | _ -> ReconcileShipState(jobId, job.shipSymbol, attempt)

                do! applyEffects client dbPath token backgroundPriority [ reconcileEffect ]
            | AwaitingInfoResponse(attempt, infoType, infoArgs, resultTarget) ->
                // A GET is always safe to retry (§8/§14) — no ambiguous-failure
                // framing needed, just re-issue the same fetch directly.
                do!
                    applyEffects
                        client
                        dbPath
                        token
                        backgroundPriority
                        [ QueueInfoRead(jobId, job.shipSymbol, infoType, infoArgs, attempt, resultTarget) ]
            | WaitingForArrival _
            | WaitingForCooldown _
            | WaitingForShipLock _
            | Paused _
            | Cancelled
            | Completed
            | Failed _ -> ()
    }

/// Pauses a job whose ship lock lease has expired — either reclaimed by a new
/// `startJob` call or found by the sweep (§14). Recovers it first (exactly as if
/// the process had just restarted for this one job) so a pause never masks an
/// unresolved in-flight action; the recovery settling into an interruptible status
/// is what lets the subsequent `PauseRequested` actually take effect immediately
/// rather than only setting `pausePending`.
let pauseOrphan (client: SpaceTradersClient) (dbPath: string) (token: string) (jobId: JobId) : Async<unit> =
    async {
        do! ensureLoaded dbPath jobId
        do! recoverJob client dbPath token jobId
        do! tick client dbPath token backgroundPriority jobId PauseRequested
    }

/// Starts a new job, persisting it (`programs` + `jobs` rows) and acquiring the
/// target ship's lock (§14) — rejecting a second job on a ship still actively
/// locked by another, and pausing an orphaned job whose lease had expired.
let startJob
    (client: SpaceTradersClient)
    (dbPath: string)
    (token: string)
    (workspaceId: string)
    (compiledDslJson: string)
    (program: CompiledProgram)
    (shipSymbol: string option)
    (initialShip: Ship option)
    (initialFleetShipCount: int)
    (initialContractCount: int)
    : Async<Result<JobId, string>> =
    async {
        let jobId = System.Guid.NewGuid().ToString()

        let job =
            { jobId = jobId
              programId = workspaceId
              program = program
              shipSymbol = shipSymbol
              status = Running
              stack = [ initialFrame ]
              lastKnownShip = initialShip |> Option.map toSnapshot
              lastKnownFleetShipCount = Some initialFleetShipCount
              lastKnownContractCount = Some initialContractCount
              currentShipStack = []
              dynamicShipLocks = []
              parallelBranches = []
              log = []
              pausePending = false
              cancelPending = false }

        // The job row must exist before the lock row can reference it
        // (`ship_locks.job_id REFERENCES jobs(id)`) — inserted tentatively as
        // `Running`, then rolled back to `Cancelled` below if the lock turns out
        // to be unavailable.
        jobs[jobId] <- job
        let! programId = Persistence.ProgramRepository.insert dbPath workspaceId compiledDslJson

        do! Persistence.JobRepository.insert
                dbPath
                jobId
                programId
                shipSymbol
                (statusName Running)
                (Persistence.JobStateJson.serializeJobState job)
                (Step.currentBlockId job)

        // A ship-agnostic job (§14 follow-up) never takes a `ship_locks` lease at
        // all — nothing to acquire, nothing to roll back.
        match shipSymbol with
        | None -> return Ok jobId
        | Some sym ->
            let! lockResult = Persistence.ShipLockRepository.tryAcquire dbPath sym jobId leaseSeconds

            match lockResult with
            | Error _ ->
                jobs.TryRemove(jobId) |> ignore
                let cancelledJob = { job with status = Cancelled }
                do! persist dbPath cancelledJob
                return Error $"Schiff {sym} wird bereits von einem anderen Programm gesteuert."
            | Ok orphanedJobIdOpt ->
                match orphanedJobIdOpt with
                | Some orphanId -> do! pauseOrphan client dbPath token orphanId
                | None -> ()

                return Ok jobId
    }

/// Drives the job through exactly one scheduler tick (one `WakeTick`) — step mode.
/// `priority` (Milestone 10/Part A, §13): callers pass 1 for a player-triggered
/// step/run (`JobRemoting.fs`) or `backgroundPriority` (3) for `JobScheduler`'s
/// fully automatic tick loop — the request queue's aging/ordering only means
/// anything once background traffic is actually distinguishable from a live
/// button press.
let stepOnce (client: SpaceTradersClient) (dbPath: string) (priority: int) (token: string) (jobId: JobId) : Async<unit> =
    tick client dbPath token priority jobId WakeTick

/// Drives the job to completion (or failure) — run mode. A genuine polling loop
/// (§14: "reads jobs due to wake, calls step..."): each `WakeTick` either makes free
/// progress, dispatches the next action, or (while waiting on an arrival/cooldown
/// that isn't due yet) is a no-op — so this polls at a short fixed interval rather
/// than trying to compute exactly when the next wake is due.
let rec runToCompletion (client: SpaceTradersClient) (dbPath: string) (priority: int) (token: string) (jobId: JobId) : Async<unit> =
    async {
        do! stepOnce client dbPath priority token jobId

        match jobs.TryGetValue jobId with
        | true, { status = Completed }
        | true, { status = Failed _ }
        | true, { status = Cancelled } -> ()
        | true, _ ->
            do! Async.Sleep 50
            return! runToCompletion client dbPath priority token jobId
        | false, _ -> ()
    }

/// Milestone 7 (§15): pilot-card controls — always player-triggered, so always
/// priority 1.
let pause (client: SpaceTradersClient) (dbPath: string) (token: string) (jobId: JobId) : Async<unit> =
    tick client dbPath token 1 jobId PauseRequested

let resume (client: SpaceTradersClient) (dbPath: string) (token: string) (jobId: JobId) : Async<unit> =
    tick client dbPath token 1 jobId ResumeRequested

let cancel (client: SpaceTradersClient) (dbPath: string) (token: string) (jobId: JobId) : Async<unit> =
    tick client dbPath token 1 jobId CancelRequested

let getStatus (jobId: JobId) : JobState option =
    match jobs.TryGetValue jobId with
    | true, j -> Some j
    | false, _ -> None

/// Milestone 7 (§15): every job currently loaded in memory — everything
/// non-terminal (loaded at scheduler startup, see `JobScheduler.fs`) plus anything
/// that went terminal since, until the next restart drops it. No job-history
/// browser this milestone — see `docs/decisions.md`.
let listJobs () : JobState list = jobs.Values |> List.ofSeq

/// Clears a finished pilot card from the live dashboard (`listJobs`) on request —
/// purely an in-memory removal, no API call/token needed. Safe: the persisted
/// `jobs` SQL table (`JobRepository.listHistory`) is written independently on
/// every `persist` call and never deleted here, so History still shows this job
/// afterward. Only removes a job that's actually terminal — a client-side bug
/// dispatching this for a still-running job is a no-op, not data loss.
let dismiss (jobId: JobId) : unit =
    match jobs.TryGetValue jobId with
    | true, { status = Completed }
    | true, { status = Failed _ }
    | true, { status = Cancelled } -> jobs.TryRemove(jobId) |> ignore
    | true, _
    | false, _ -> ()

/// Milestone 7: hydrates every row `JobScheduler`'s startup resume loaded from the
/// `jobs` table into the in-memory cache, without touching anything already
/// loaded (defensive — nothing should call this twice in practice).
let hydrate (rows: (JobId * string) seq) : unit =
    for jobId, executionStateJson in rows do
        if not (jobs.ContainsKey jobId) then
            jobs[jobId] <- Persistence.JobStateJson.deserializeJobState executionStateJson

let private simulationDetail (job: JobState) : string option =
    match job.status with
    | WaitingForArrival until -> Some($"wait until {until:o}")
    | WaitingForCooldown until -> Some($"cooldown until {until:o}")
    | Failed message -> Some message
    | _ -> job.log |> List.tryHead

let private captureSimulationStep (job: JobState) : SimulationStep =
    match Step.blockIdPerFrame job |> List.tryHead with
    | Some(scope, Some blockId) ->
        let detail =
            match job.log |> List.tryHead with
            | Some line -> Some line
            | None -> Step.findInstructionAnywhere job.program blockId |> Option.map Step.describeInstruction

        { scope = scope; blockId = blockId; detail = detail }
    | Some(scope, None) -> { scope = scope; blockId = ""; detail = simulationDetail job }
    | None -> { scope = "main"; blockId = ""; detail = simulationDetail job }

let private startEphemeralJob
    (program: CompiledProgram)
    (shipSymbol: string option)
    (initialShip: Ship option)
    (initialFleetShipCount: int)
    (initialContractCount: int)
    : JobId =
    let jobId = "__sim__" + System.Guid.NewGuid().ToString()

    let job =
        { jobId = jobId
          programId = "__simulate__"
          program = program
          shipSymbol = shipSymbol
          status = Running
          stack = [ initialFrame ]
          lastKnownShip = initialShip |> Option.map toSnapshot
          lastKnownFleetShipCount = Some initialFleetShipCount
          lastKnownContractCount = Some initialContractCount
          currentShipStack = []
          dynamicShipLocks = []
          parallelBranches = []
          log = []
          pausePending = false
          cancelPending = false }

    jobs[jobId] <- job
    jobId

let private isTerminalStatus (status: JobStatus) =
    match status with
    | Completed | Failed _ | Cancelled -> true
    | _ -> false

let rec private runSimulationLoop
    (client: SpaceTradersClient)
    (dbPath: string)
    (token: string)
    (jobId: JobId)
    (steps: SimulationStep list)
    (simNow: DateTimeOffset ref)
    : Async<SimulationRunResult> =
    async {
        match jobs.TryGetValue jobId with
        | false, _ ->
            return
                { success = false
                  status = "Failed"
                  error = Some "Simulation job disappeared."
                  steps = steps
                  log = [] }
        | true, job when isTerminalStatus job.status ->
            jobs.TryRemove(jobId) |> ignore

            let success, err =
                match job.status with
                | Completed -> true, None
                | Failed message -> false, Some message
                | Cancelled -> false, Some "Cancelled"
                | _ -> false, Some(statusName job.status)

            return
                { success = success
                  status = statusName job.status
                  error = err
                  steps = steps
                  log = List.rev job.log }
        | true, job ->
            let step = captureSimulationStep job

            let steps' =
                match steps with
                | last :: _ when last.blockId = step.blockId && last.detail = step.detail -> steps
                | _ -> steps @ [ step ]

            match job.status with
            | WaitingForArrival until
            | WaitingForCooldown until ->
                simNow.Value <- until.AddMilliseconds(1)
                do! tick client dbPath token 1 jobId WakeTick
                return! runSimulationLoop client dbPath token jobId steps' simNow
            | _ ->
                do! stepOnce client dbPath 1 token jobId
                return! runSimulationLoop client dbPath token jobId steps' simNow
    }

/// Compile-time companion to `startJob`: runs a program ephemerally (no `jobs`/`programs`
/// rows, no ship-lock leases) and returns a block-by-block trace plus the program log.
let simulateProgram
    (client: SpaceTradersClient)
    (dbPath: string)
    (token: string)
    (program: CompiledProgram)
    (shipSymbol: string option)
    (initialShip: Ship option)
    (initialFleetShipCount: int)
    (initialContractCount: int)
    : Async<SimulationRunResult> =
    async {
        let simNow = ref System.DateTimeOffset.UtcNow
        let simClock: Clock = { now = fun () -> simNow.Value }
        simClockHolder := Some(simClock, simNow)

        try
            let jobId =
                startEphemeralJob program shipSymbol initialShip initialFleetShipCount initialContractCount

            return! runSimulationLoop client dbPath token jobId [] simNow
        finally
            simClockHolder := None
    }

/// Test-only: this module is a process-wide singleton (matching `RequestQueue`'s own
/// pattern), so tests that exercise it directly must reset state between cases.
let resetForTests () = jobs.Clear()
