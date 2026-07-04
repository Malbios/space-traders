module SpaceKids.Server.AgentRemoting

open Bolero.Remoting
open Bolero.Remoting.Server
open SpaceKids.SpaceTraders
open SpaceKids.Client.Main

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

    /// Takes the agent already fetched by the caller — no need to re-fetch it.
    /// Priority 1 (§13's top tier): these are direct interactive user actions, not
    /// background/job work.
    let loadRestOfState (agent: Agent) (token: string) : Async<DashboardState> =
        async {
            let! ships = RequestQueue.enqueue dbPath 1 "GET /my/ships" (fun () -> client.ListShips(token))
            let! contracts = RequestQueue.enqueue dbPath 1 "GET /my/contracts" (fun () -> client.ListContracts(token))
            let! waypoints =
                RequestQueue.enqueue dbPath 1 "GET /systems/{system}/waypoints" (fun () ->
                    client.ListWaypoints(token, agent.headquarters.Substring(0, agent.headquarters.LastIndexOf('-'))))
            let! market =
                RequestQueue.enqueue dbPath 1 "GET /systems/{system}/waypoints/{waypoint}/market" (fun () ->
                    client.GetMarket(token, agent.headquarters.Substring(0, agent.headquarters.LastIndexOf('-')), agent.headquarters))
            return
                { agent = agent
                  ships = ships
                  contracts = contracts
                  waypoints = waypoints
                  markets = [ market ] }
        }

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
                            let! state = loadRestOfState agent token
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
                            let! state = loadRestOfState agent token
                            return Some state
                    }
        }
