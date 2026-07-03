namespace SpaceKids.SpaceTraders

type ShipRegistration = { role: string }

type ShipNav =
    { systemSymbol: string
      waypointSymbol: string
      status: string }

type ShipFuel = { current: int; capacity: int }

type Ship =
    { symbol: string
      registration: ShipRegistration
      nav: ShipNav
      fuel: ShipFuel }

type Agent =
    { symbol: string
      headquarters: string
      credits: int64
      startingFaction: string
      shipCount: int }

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
