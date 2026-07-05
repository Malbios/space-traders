module SpaceKids.Server.AgentRemoting

open Bolero.Remoting
open Bolero.Remoting.Server
open SpaceKids.SpaceTraders
open SpaceKids.Client.Main

/// Takes the agent already fetched by the caller — no need to re-fetch it.
/// Priority 1 (§13's top tier): these are direct interactive user actions, not
/// background/job work.
let private loadRestOfState (client: SpaceTradersClient) (dbPath: string) (agent: Agent) (token: string) : Async<DashboardState> =
    async {
        let! ships = RequestQueue.enqueue dbPath 1 "GET /my/ships" (fun () -> client.ListShips(token))
        let! contracts = RequestQueue.enqueue dbPath 1 "GET /my/contracts" (fun () -> client.ListContracts(token))
        let! waypoints =
            RequestQueue.enqueue dbPath 1 "GET /systems/{system}/waypoints" (fun () ->
                client.ListWaypoints(token, Waypoint.systemSymbolOf agent.headquarters))
        let! market =
            RequestQueue.enqueue dbPath 1 "GET /systems/{system}/waypoints/{waypoint}/market" (fun () ->
                client.GetMarket(token, Waypoint.systemSymbolOf agent.headquarters, agent.headquarters))
        return
            { agent = agent
              ships = ships
              contracts = contracts
              waypoints = waypoints
              markets = [ market ] }
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
            let! market =
                RequestQueue.enqueue dbPath 1 $"GET /systems/{{system}}/waypoints/{waypointSymbol}/market" (fun () ->
                    client.GetMarket(token, Waypoint.systemSymbolOf waypointSymbol, waypointSymbol))

            return Some market
        with ex when isNotFound ex ->
            return None
    }

let fetchWaypointShipyard (client: SpaceTradersClient) (dbPath: string) (token: string) (waypointSymbol: string) : Async<Shipyard option> =
    async {
        try
            let! result =
                RequestQueue.enqueue dbPath 1 $"GET /systems/{{system}}/waypoints/{waypointSymbol}/shipyard" (fun () ->
                    client.GetShipyard(token, Waypoint.systemSymbolOf waypointSymbol, waypointSymbol))

            return Some result.shipyard
        with ex when isNotFound ex ->
            return None
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
                            let! agent = RequestQueue.enqueue dbPath 1 "GET /my/agent" (fun () -> client.GetAgent(token))
                            do! Persistence.AgentRepository.saveAgent dbPath agent.symbol token
                            // a fresh, accepted token means any prior server-reset is resolved (§13).
                            RequestQueue.clearServerReset ()
                            let! state = loadRestOfState client dbPath agent token
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
                        | Some(_, token) ->
                            let! agent = RequestQueue.enqueue dbPath 1 "GET /my/agent" (fun () -> client.GetAgent(token))
                            let! state = loadRestOfState client dbPath agent token
                            return Some state
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
        }
