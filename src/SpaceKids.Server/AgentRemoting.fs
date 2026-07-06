module SpaceKids.Server.AgentRemoting

open System.Text.Json
open Bolero.Remoting
open Bolero.Remoting.Server
open SpaceKids.SpaceTraders
open SpaceKids.Client.Main

/// Takes the agent already fetched by the caller — no need to re-fetch it.
/// Priority 1 (§13's top tier): these are direct interactive user actions, not
/// background/job work.
let private loadRestOfState (client: SpaceTradersClient) (dbPath: string) (agent: Agent) (token: string) : Async<DashboardState> =
    async {
        let! ships = RequestQueue.enqueue dbPath 1 "GET /my/ships" None (fun () -> client.ListShips(token))
        let! contracts = RequestQueue.enqueue dbPath 1 "GET /my/contracts" None (fun () -> client.ListContracts(token))
        let! waypoints =
            RequestQueue.enqueue dbPath 1 "GET /systems/{system}/waypoints" None (fun () ->
                client.ListWaypoints(token, Waypoint.systemSymbolOf agent.headquarters))
        let! market =
            RequestQueue.enqueue dbPath 1 "GET /systems/{system}/waypoints/{waypoint}/market" None (fun () ->
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
                            // Deliberately bypasses `RequestQueue.enqueue` for this one
                            // verification call: the Worker pauses *all* dispatching while
                            // `serverResetFlag` is set (§13), and that flag can only be
                            // cleared below, after a fresh token is confirmed valid — routing
                            // this call through the same paused queue would mean nothing could
                            // ever clear it (the exact deadlock a real user hit). A one-off
                            // interactive token check doesn't need the queue's
                            // retry/backoff/priority-aging machinery anyway. Every other call
                            // in `loadRestOfState` below still goes through the queue normally.
                            let! agent = client.GetAgent(token)
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
                            try
                                let! agent = RequestQueue.enqueue dbPath 1 "GET /my/agent" None (fun () -> client.GetAgent(token))
                                let! state = loadRestOfState client dbPath agent token
                                return Some state
                            with _ ->
                                // The token used above can go stale mid-flight: this call is
                                // queued behind `RequestQueue`'s Worker, which pauses entirely
                                // once a reset is detected, so a login that completes *while
                                // this request is still queued* leaves it holding the old
                                // token by the time it's finally dispatched. Check whether a
                                // fresher token was saved since, and retry once with it
                                // directly (same queue-bypass reasoning as `submitToken`)
                                // before concluding this is a genuine, unrecovered reset.
                                let! current = Persistence.AgentRepository.loadStoredAgent dbPath

                                match current with
                                | Some(_, freshToken) when freshToken <> token ->
                                    try
                                        let! agent = client.GetAgent(freshToken)
                                        // A stale closure's 401 above already paused the shared
                                        // Worker via `markServerReset` — proving this fresher
                                        // token works means the account is fine, so resume it
                                        // (same as `submitToken`'s own recovery clear).
                                        RequestQueue.clearServerReset ()
                                        let! state = loadRestOfState client dbPath agent freshToken
                                        return Some state
                                    with _ -> return None
                                | _ ->
                                    // The stored token can be dead (post server-reset)
                                    // independently of anything the player just did -- this
                                    // fires automatically at page load and every few
                                    // `MapTick`s (`Main.fs`), so it must degrade to "not
                                    // logged in" rather than crash the request; the
                                    // token/login form is already what renders when this is
                                    // `None`.
                                    return None
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
