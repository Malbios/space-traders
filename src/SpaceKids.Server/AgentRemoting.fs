module SpaceKids.Server.AgentRemoting

open System
open System.Text.Json
open Bolero.Remoting
open Bolero.Remoting.Server
open SpaceKids.SpaceTraders
open SpaceKids.Client.Main

let private jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

let fetchSystemsCached (client: SpaceTradersClient) (dbPath: string) (agentSymbol: string) (token: string) : Async<StarSystem list> =
    GalaxyHydration.fetchSystemsCached client dbPath agentSymbol token

/// Takes the agent already fetched by the caller — no need to re-fetch it.
/// Priority 1 (§13's top tier): these are direct interactive user actions, not
/// background/job work.
///
/// `includeGalaxyCatalog` gates the expensive, rarely-changing reads (`ListSystems`
/// walks every paginated page in the galaxy; HQ waypoints/market are similar).
/// Pilot-completion refreshes pass `false` and the client merges ship/contract/agent
/// deltas onto the cached galaxy snapshot — see `refreshDashboard` below.
let loadRestOfState
    (client: SpaceTradersClient)
    (dbPath: string)
    (agent: Agent)
    (token: string)
    (includeGalaxyCatalog: bool)
    : Async<DashboardState> =
    async {
        let! ships = RequestQueue.enqueue dbPath 1 "GET /my/ships" None (fun () -> client.ListShips(token))
        let! contracts = RequestQueue.enqueue dbPath 1 "GET /my/contracts" None (fun () -> client.ListContracts(token))
        let hqSystem = Waypoint.systemSymbolOf agent.headquarters

        let! systems, waypoints, markets =
            if includeGalaxyCatalog then
                async {
                    let! systems = fetchSystemsCached client dbPath agent.symbol token
                    let! snapshot = GalaxyHydration.buildCatalogSnapshot client dbPath agent token
                    return systems, snapshot.waypoints, snapshot.markets
                }
            else
                async {
                    return [], [], []
                }

        return
            { agent = agent
              ships = ships
              contracts = contracts
              systems = systems
              selectedSystemSymbol = hqSystem
              waypoints = waypoints
              markets = markets }
    }

let private loadDashboardWithToken (client: SpaceTradersClient) (dbPath: string) (token: string) (includeGalaxyCatalog: bool) : Async<DashboardState option> =
    async {
        try
            let! agent = RequestQueue.enqueue dbPath 1 "GET /my/agent" None (fun () -> client.GetAgent(token))
            let! state = loadRestOfState client dbPath agent token includeGalaxyCatalog
            return Some state
        with _ ->
            let! current = Persistence.AgentRepository.loadStoredAgent dbPath

            match current with
            | Some(_, freshToken) when freshToken <> token ->
                try
                    let! agent = client.GetAgent(freshToken)
                    RequestQueue.clearServerReset ()
                    let! state = loadRestOfState client dbPath agent freshToken includeGalaxyCatalog
                    return Some state
                with _ ->
                    return None
            | _ -> return None
    }

let loadSystemWaypoints (client: SpaceTradersClient) (dbPath: string) (token: string) (systemSymbol: string) : Async<Result<Waypoint list, string>> =
    async {
        try
            let! waypoints =
                RequestQueue.enqueue dbPath 1 $"GET /systems/{systemSymbol}/waypoints" None (fun () ->
                    client.ListWaypoints(token, systemSymbol))

            return Ok waypoints
        with
        | SpaceTradersApiException(statusCode, _) ->
            return Error $"Wegpunkte konnten nicht geladen werden (Status {statusCode})."
        | ex ->
            return Error $"Verbindung zu SpaceTraders fehlgeschlagen: {ex.Message}"
    }

let loadPublicAgents (client: SpaceTradersClient) (dbPath: string) (token: string) : Async<Result<Agent list, string>> =
    async {
        try
            let! agents = RequestQueue.enqueue dbPath 1 "GET /agents" None (fun () -> client.ListAgents(token))
            return Ok agents
        with
        | SpaceTradersApiException(statusCode, _) ->
            return Error $"Agenten konnten nicht geladen werden (Status {statusCode})."
        | ex ->
            return Error $"Verbindung zu SpaceTraders fehlgeschlagen: {ex.Message}"
    }

/// `RequestQueue.enqueue`'s exceptions can arrive wrapped in an
/// `AggregateException` depending on the Async<->Task interop path they cross
/// (the same nesting `JobRunner.fs`'s own `classifyException` already has to
/// account for) — unwrap before pattern-matching so a real 404 is never missed.
let rec private isNotFound (ex: exn) : bool =
    match ex with
    | SpaceTradersApiException(404, _) -> true
    | :? System.AggregateException as agg when agg.InnerExceptions.Count = 1 -> isNotFound agg.InnerExceptions.[0]
    | _ -> false

/// Entity inspector (visual-map feature): player-triggered ("Markt laden"
/// button), so priority 1 like every other direct interactive call here. A
/// waypoint without the `MARKETPLACE` trait shouldn't even show the button, but
/// this stays defensive either way rather than propagating the API's error. A
/// standalone module function (not inlined into the handler's `Handler`
/// property) — takes `client`/`dbPath` as plain parameters, not captured from a
/// class instance, so tests can call it directly without instantiating a Bolero
/// remote handler.
let fetchWaypointMarket (client: SpaceTradersClient) (dbPath: string) (token: string) (waypointSymbol: string) : Async<Market option> =
    async {
        try
            let requestJson = Some(JsonSerializer.Serialize({| waypointSymbol = waypointSymbol |}))

            let! market =
                RequestQueue.enqueue dbPath 1 $"GET /systems/{{system}}/waypoints/{waypointSymbol}/market" requestJson (fun () ->
                    client.GetMarket(token, Waypoint.systemSymbolOf waypointSymbol, waypointSymbol))

            return Some market
        with ex when isNotFound ex ->
            return None
    }

let fetchWaypointShipyard (client: SpaceTradersClient) (dbPath: string) (token: string) (waypointSymbol: string) : Async<Shipyard option> =
    async {
        try
            let requestJson = Some(JsonSerializer.Serialize({| waypointSymbol = waypointSymbol |}))

            let! result =
                RequestQueue.enqueue dbPath 1 $"GET /systems/{{system}}/waypoints/{waypointSymbol}/shipyard" requestJson (fun () ->
                    client.GetShipyard(token, Waypoint.systemSymbolOf waypointSymbol, waypointSymbol))

            // Same real-API omission behavior as `Market.tradeGoods` (see the doc
            // comment on that field in `Types.fs`): when no ship of ours is docked
            // here, the real API omits the `ships` key entirely rather than sending
            // an empty array. Plain `System.Text.Json` then binds the F# list
            // constructor parameter to `null`, not `[]` — normalize it here so
            // `shipyard.ships.IsEmpty` downstream never sees a null reference.
            // `transactions` gets the same defensive treatment — not yet observed
            // omitted on a real response, but it's the same "plain list, no Option
            // wrapper" shape that already bit `ships` once.
            let normalized =
                { result with
                    ships = (if isNull (box result.ships) then [] else result.ships)
                    transactions = (if isNull (box result.transactions) then [] else result.transactions) }

            return Some normalized
        with ex when isNotFound ex ->
            return None
    }

/// Contracts-tab "Accept" button (player-triggered, priority 1 like every other
/// direct interactive call here). Standalone module function, same reason as
/// `fetchWaypointMarket`/`fetchWaypointShipyard` above: testable without a
/// Bolero remote handler instance.
let acceptContract (client: SpaceTradersClient) (dbPath: string) (token: string) (contractId: string) : Async<Result<unit, string>> =
    async {
        try
            let! _ =
                RequestQueue.enqueue dbPath 1 $"POST /my/contracts/{contractId}/accept" None (fun () ->
                    client.AcceptContract(token, contractId))

            return Ok ()
        with
        | SpaceTradersApiException(statusCode, _) ->
            return Error $"Der Auftrag konnte nicht angenommen werden (Status {statusCode})."
        | ex ->
            return Error $"Verbindung zu SpaceTraders fehlgeschlagen: {ex.Message}"
    }

/// Factions tab: list all factions plus the agent's reputation with each.
let loadFactions (client: SpaceTradersClient) (dbPath: string) (token: string) : Async<Result<FactionsSnapshot, string>> =
    async {
        try
            let! factions =
                RequestQueue.enqueue dbPath 1 "GET /factions" None (fun () -> client.ListFactions(token))

            let! reputations =
                RequestQueue.enqueue dbPath 1 "GET /my/factions" None (fun () -> client.ListMyFactions(token))

            return
                Ok
                    { factions = factions
                      reputations = reputations |> List.map (fun r -> r.symbol, r.reputation) }
        with
        | SpaceTradersApiException(statusCode, _) ->
            return Error $"Fraktionsdaten konnten nicht geladen werden (Status {statusCode})."
        | ex ->
            return Error $"Verbindung zu SpaceTraders fehlgeschlagen: {ex.Message}"
    }

/// Contracts-tab "Fulfill" button — same shape as `acceptContract` above.
let fulfillContract (client: SpaceTradersClient) (dbPath: string) (token: string) (contractId: string) : Async<Result<unit, string>> =
    async {
        try
            let! _ =
                RequestQueue.enqueue dbPath 1 $"POST /my/contracts/{contractId}/fulfill" None (fun () ->
                    client.FulfillContract(token, contractId))

            return Ok ()
        with
        | SpaceTradersApiException(statusCode, _) ->
            return Error $"Der Auftrag konnte nicht abgeschlossen werden (Status {statusCode})."
        | ex ->
            return Error $"Verbindung zu SpaceTraders fehlgeschlagen: {ex.Message}"
    }

/// Server-side implementation of AgentService (Milestone 2, §19): every SpaceTraders
/// call goes through RequestQueue.enqueue (§13's global-queue principle applies from
/// day one, even before the real priority/backoff queue lands in Milestone 5).
///
/// Known simplifying assumption: the market fetched is the agent's own headquarters
/// waypoint, not discovered via waypoint traits — true for most starting waypoints in
/// this game, but a real limitation if a given account's HQ isn't a marketplace. See
/// docs/decisions.md.
type AgentRemoteHandler(client: SpaceTradersClient, ctx: IRemoteContext) =
    inherit RemoteHandler<AgentService>()

    let dbPath = Persistence.Database.defaultDbPath

    override this.Handler =
        {
            submitToken =
                fun token ->
                    async {
                        try
                            let! agent = client.GetAgent(token)
                            let! stored = Persistence.AgentRepository.loadStoredAgent dbPath

                            let tokenChanged =
                                match stored with
                                | Some(symbol, existingToken) -> symbol <> agent.symbol || existingToken <> token
                                | None -> true

                            do! Persistence.AgentRepository.saveAgent dbPath agent.symbol token

                            if tokenChanged then
                                do! Persistence.ApiCacheRepository.invalidateForAgent dbPath agent.symbol

                            RequestQueue.clearServerReset ()
                            let! state = loadRestOfState client dbPath agent token false
                            return Ok state
                        with
                        | SpaceTradersApiException(statusCode, _) ->
                            return Error $"Der Token wurde von SpaceTraders abgelehnt (Status {statusCode})."
                        | ex ->
                            return Error $"Verbindung zu SpaceTraders fehlgeschlagen: {ex.Message}"
                    }
            loadDashboard =
                fun () ->
                    async {
                        let! stored = Persistence.AgentRepository.loadStoredAgent dbPath

                        match stored with
                        | None -> return None
                        | Some(_, token) -> return! loadDashboardWithToken client dbPath token true
                    }
            refreshDashboard =
                fun () ->
                    async {
                        let! stored = Persistence.AgentRepository.loadStoredAgent dbPath

                        match stored with
                        | None -> return None
                        | Some(_, token) -> return! loadDashboardWithToken client dbPath token false
                    }
            getGalaxyCatalog =
                fun () ->
                    async {
                        let! stored = Persistence.AgentRepository.loadStoredAgent dbPath

                        match stored with
                        | None -> return Error "Nicht angemeldet."
                        | Some(_, token) ->
                            try
                                let! agent =
                                    RequestQueue.enqueue dbPath 1 "GET /my/agent" None (fun () -> client.GetAgent(token))

                                let! snapshot = GalaxyHydration.buildCatalogSnapshot client dbPath agent token
                                return Ok snapshot
                            with
                            | SpaceTradersApiException(statusCode, _) ->
                                return Error $"Galaxiedaten konnten nicht geladen werden (Status {statusCode})."
                            | ex ->
                                return Error $"Galaxiedaten konnten nicht geladen werden: {ex.Message}"
                    }
            reloadGalaxy =
                fun () ->
                    async {
                        let! stored = Persistence.AgentRepository.loadStoredAgent dbPath

                        match stored with
                        | None -> return Error "Nicht angemeldet."
                        | Some(agentSymbol, token) ->
                            try
                                do! GalaxyHydration.reloadGalaxy client dbPath agentSymbol token
                                return Ok ()
                            with ex ->
                                return Error $"Galaxie konnte nicht aktualisiert werden: {ex.Message}"
                    }
            reloadSystem =
                fun systemSymbol ->
                    async {
                        let! stored = Persistence.AgentRepository.loadStoredAgent dbPath

                        match stored with
                        | None -> return Error "Nicht angemeldet."
                        | Some(_, token) ->
                            try
                                let! agent =
                                    RequestQueue.enqueue dbPath 1 "GET /my/agent" None (fun () -> client.GetAgent(token))

                                let! snapshot = GalaxyHydration.reloadSystem client dbPath agent token systemSymbol
                                return Ok snapshot
                            with
                            | SpaceTradersApiException(statusCode, _) ->
                                return Error $"Systemdaten konnten nicht aktualisiert werden (Status {statusCode})."
                            | ex ->
                                return Error $"Systemdaten konnten nicht aktualisiert werden: {ex.Message}"
                    }
            getWaypointMarket =
                fun waypointSymbol ->
                    async {
                        let! stored = Persistence.AgentRepository.loadStoredAgent dbPath
                        match stored with
                        | None -> return None
                        | Some(_, token) -> return! fetchWaypointMarket client dbPath token waypointSymbol
                    }
            getWaypointShipyard =
                fun waypointSymbol ->
                    async {
                        let! stored = Persistence.AgentRepository.loadStoredAgent dbPath
                        match stored with
                        | None -> return None
                        | Some(_, token) -> return! fetchWaypointShipyard client dbPath token waypointSymbol
                    }
            acceptContract =
                fun contractId ->
                    async {
                        let! stored = Persistence.AgentRepository.loadStoredAgent dbPath
                        match stored with
                        | None -> return Error "Nicht angemeldet."
                        | Some(_, token) -> return! acceptContract client dbPath token contractId
                    }
            fulfillContract =
                fun contractId ->
                    async {
                        let! stored = Persistence.AgentRepository.loadStoredAgent dbPath
                        match stored with
                        | None -> return Error "Nicht angemeldet."
                        | Some(_, token) -> return! fulfillContract client dbPath token contractId
                    }
            loadFactions =
                fun () ->
                    async {
                        let! stored = Persistence.AgentRepository.loadStoredAgent dbPath
                        match stored with
                        | None -> return Error "Nicht angemeldet."
                        | Some(_, token) -> return! loadFactions client dbPath token
                    }
            loadSystemWaypoints =
                fun systemSymbol ->
                    async {
                        let! stored = Persistence.AgentRepository.loadStoredAgent dbPath
                        match stored with
                        | None -> return Error "Nicht angemeldet."
                        | Some(_, token) -> return! loadSystemWaypoints client dbPath token systemSymbol
                    }
            loadPublicAgents =
                fun () ->
                    async {
                        let! stored = Persistence.AgentRepository.loadStoredAgent dbPath
                        match stored with
                        | None -> return Error "Nicht angemeldet."
                        | Some(_, token) -> return! loadPublicAgents client dbPath token
                    }
        }