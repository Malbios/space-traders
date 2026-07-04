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
      expiration: string }

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
      expiration: string }

type Waypoint =
    { symbol: string
      ``type``: string
      systemSymbol: string
      x: int
      y: int }

type TradeGood = { symbol: string; name: string }

type Market =
    { symbol: string
      exports: TradeGood list
      imports: TradeGood list
      exchange: TradeGood list }

type DataEnvelope<'a> = { data: 'a }
