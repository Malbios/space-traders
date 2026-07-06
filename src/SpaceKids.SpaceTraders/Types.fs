namespace SpaceKids.SpaceTraders

type ShipRegistration = { role: string }

type RouteWaypoint =
    { symbol: string
      ``type``: string
      systemSymbol: string
      x: int
      y: int }

type Route =
    { destination: RouteWaypoint
      origin: RouteWaypoint
      departureTime: string
      arrival: string }

/// `route`/`flightMode` are always present on the real API's `nav` shape (whichever
/// endpoint embeds it — list/get/navigate/orbit/dock all return the same shape).
type ShipNav =
    { systemSymbol: string
      waypointSymbol: string
      route: Route
      status: string
      flightMode: string }

type ShipFuel = { current: int; capacity: int }

type CargoItem =
    { symbol: string
      name: string
      description: string
      units: int }

type ShipCargo =
    { capacity: int
      units: int
      inventory: CargoItem list }

/// ISO-8601 timestamps are kept as strings and parsed only at the comparison sites
/// that need them (reconciliation, wait-until checks) — Milestone 6 (§13).
type Cooldown =
    { shipSymbol: string
      totalSeconds: int
      remainingSeconds: int
      /// Real accounts get `null` here when a ship has no active cooldown, unlike
      /// the fake server's fixtures which always populate it.
      expiration: string option }

type Ship =
    { symbol: string
      registration: ShipRegistration
      nav: ShipNav
      fuel: ShipFuel
      cargo: ShipCargo
      cooldown: Cooldown }

type FuelConsumed = { amount: int; timestamp: string }

type ShipFuelDetailed =
    { current: int
      capacity: int
      consumed: FuelConsumed option }

/// `POST .../navigate` response.
type NavigateResult = { nav: ShipNav; fuel: ShipFuelDetailed }

/// `POST .../orbit` and `POST .../dock` response (both return only `nav`).
type NavResult = { nav: ShipNav }

type ExtractionYield = { symbol: string; units: int }

type Extraction =
    { shipSymbol: string
      ``yield``: ExtractionYield }

/// `POST .../extract` response.
type ExtractResult =
    { extraction: Extraction
      cooldown: Cooldown
      cargo: ShipCargo }

type MarketTransaction =
    { waypointSymbol: string
      shipSymbol: string
      tradeSymbol: string
      ``type``: string
      units: int
      pricePerUnit: int
      totalPrice: int
      timestamp: string }

type Agent =
    { symbol: string
      headquarters: string
      credits: int64
      startingFaction: string
      shipCount: int }

/// `POST .../purchase` and `POST .../sell` response.
type TradeResult =
    { agent: Agent
      cargo: ShipCargo
      transaction: MarketTransaction }

type Contract =
    { id: string
      factionSymbol: string
      ``type``: string
      accepted: bool
      fulfilled: bool
      /// Deprecated in the real API in favor of `deadlineToAccept` — some real accounts'
      /// contracts omit/null it, unlike our fake server's always-populated fixture.
      expiration: string option }

/// A waypoint's trait signals what's actually there (a market, a shipyard, a
/// mineable deposit, ...) — the inspector (visual-map feature) uses `symbol` to
/// gate its "load market"/"load shipyard" buttons, and shows `name`/`description`
/// for the player. Already present on the real API's `ListWaypoints` response; we
/// just weren't deserializing it before now.
type WaypointTrait =
    { symbol: string
      name: string
      description: string }

type Waypoint =
    { symbol: string
      ``type``: string
      systemSymbol: string
      x: int
      y: int
      traits: WaypointTrait list }

module Waypoint =
    /// SpaceTraders' own waypoint-symbol convention: `SYSTEM-WAYPOINT` (e.g.
    /// `X1-TEST-A1` is waypoint `A1` in system `X1-TEST`) — shared by every call
    /// site that only has a waypoint symbol on hand but needs a system symbol too
    /// (`getMarket`/`getShipyard` info blocks, the dashboard's on-demand
    /// market/shipyard fetch), so this logic exists exactly once.
    let systemSymbolOf (waypointSymbol: string) : string =
        let parts = waypointSymbol.Split('-')
        if parts.Length >= 2 then System.String.Join("-", parts.[0], parts.[1]) else waypointSymbol

type TradeGood = { symbol: string; name: string }

/// Only populated by the real API when a ship is present at the market (unlike
/// `exports`/`imports`/`exchange`, which are always visible) — verified against the
/// live OpenAPI spec (Milestone 9/Part B).
type MarketTradeGood =
    { symbol: string
      purchasePrice: int
      sellPrice: int }

type Market =
    { symbol: string
      exports: TradeGood list
      imports: TradeGood list
      exchange: TradeGood list
      /// The real API omits this key entirely (not an empty array) when no ship is
      /// present at the market — the OpenAPI spec confirms it's genuinely optional,
      /// unlike `exports`/`imports`/`exchange`.
      tradeGoods: MarketTradeGood list option }

type SurveyDeposit = { symbol: string }

type Survey =
    { signature: string
      symbol: string
      deposits: SurveyDeposit list
      expiration: string
      size: string }

/// `POST .../survey` response (Milestone 9/Part A). Only `cooldown` is consulted by
/// the scheduler today — `surveys` is carried through for a future milestone that
/// actually uses survey signatures to target extraction.
type SurveyResult = { cooldown: Cooldown; surveys: Survey list }

/// `POST /my/contracts/{contractId}/deliver` response. `Contract` already covers every
/// field the DSL's Auftrag record needs (§8) — extra real-API fields (`terms`,
/// `deadlineToAccept`) are simply ignored by `System.Text.Json`.
type DeliverContractResult = { contract: Contract; cargo: ShipCargo }

/// `POST /my/contracts/{contractId}/accept` response.
type AcceptContractResult = { contract: Contract; agent: Agent }

/// `POST /my/ships/{shipSymbol}/refuel` response.
type RefuelResult =
    { agent: Agent
      fuel: ShipFuelDetailed
      transaction: MarketTransaction }

/// `POST /my/ships` (purchase) response. Only `ship.symbol`/`agent.shipCount` are
/// needed for `PurchaseShipOk` — the full real `ship` object (frame/reactor/engine/
/// modules/mounts/crew) is out of scope for this milestone, so it's deliberately not
/// modeled; extra JSON fields are ignored by `System.Text.Json`.
type PurchasedShip = { symbol: string }

type PurchaseShipResult = { ship: PurchasedShip; agent: Agent }

/// `GET /my/contracts/{contractId}` response — nested under `contract`, unlike
/// `ListContracts`'s flat array.
type GetContractResult = { contract: Contract }

type ShipyardShipType = { ``type``: string }

/// The `ships` array's per-type purchase price is only populated by the real API when
/// a ship of yours is docked at that shipyard; `shipTypes` (always present) has no
/// price. Documented simplification (§8, same class as the existing "market is always
/// headquarters" one): the Werft record prefers `ships`' prices, falling back to
/// `shipTypes` with a price of 0 when `ships` is empty.
type ShipyardShipEntry = { ``type``: string; purchasePrice: int }

type Shipyard =
    { symbol: string
      shipTypes: ShipyardShipType list
      ships: ShipyardShipEntry list }

/// `GET /systems/{systemSymbol}/waypoints/{waypointSymbol}/shipyard` response —
/// nested under `shipyard`.
type GetShipyardResult = { shipyard: Shipyard }

type DataEnvelope<'a> = { data: 'a }
