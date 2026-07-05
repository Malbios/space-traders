module SpaceKids.Server.JobRunner

open System
open System.Collections.Concurrent
open System.Threading
open SpaceKids.Core.Dsl
open SpaceKids.Core.Scheduler
open SpaceKids.SpaceTraders

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
    if cooldown.remainingSeconds > 0 then Some cooldown.expiration else None

let private toSnapshot (ship: Ship) : ShipSnapshot =
    { navStatus = ship.nav.status
      navWaypoint = ship.nav.waypointSymbol
      navArrival = Some ship.nav.route.arrival
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
              "Wegpunkt", VString ship.nav.waypointSymbol
              "Status", VString ship.nav.status
              "Treibstoff", VNumber(float ship.fuel.current)
              "Frachteinheiten", VNumber(float ship.cargo.units)
              "Frachtkapazität", VNumber(float ship.cargo.capacity) ]
    )

let private contractRecord (c: Contract) : Value =
    VRecord(
        Map.ofList
            [ "Id", VString c.id
              "Typ", VString c.``type``
              "Angenommen", VBool c.accepted
              "Erfüllt", VBool c.fulfilled ]
    )

let private waypointRecord (w: Waypoint) : Value =
    VRecord(Map.ofList [ "Symbol", VString w.symbol; "Typ", VString w.``type`` ])

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
                            r.cooldown.expiration,
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
                    return SurveyOk r.cooldown.expiration
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

    async {
        try
            return! RequestQueue.enqueue dbPath priority endpoint call
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
                        if not market.tradeGoods.IsEmpty then
                            market.tradeGoods
                            |> List.map (fun g ->
                                VRecord(
                                    Map.ofList
                                        [ "Name", VString g.symbol
                                          "Kaufpreis", VNumber(float g.purchasePrice)
                                          "Verkaufspreis", VNumber(float g.sellPrice) ]
                                ))
                        else
                            (market.exports @ market.imports @ market.exchange)
                            |> List.map (fun g ->
                                VRecord(
                                    Map.ofList [ "Name", VString g.name; "Kaufpreis", VNumber 0.0; "Verkaufspreis", VNumber 0.0 ]
                                ))

                    return InfoOk(VRecord(Map.ofList [ "Wegpunkt", VString waypointSymbol; "Handelswaren", VList goods ]))
                })
        | "getShipyard" ->
            let waypointSymbol = requireArg "waypointSymbol"
            let systemSymbol = Waypoint.systemSymbolOf waypointSymbol

            $"getShipyard:{waypointSymbol}",
            (fun () ->
                async {
                    let! r = client.GetShipyard(token, systemSymbol, waypointSymbol)

                    let types =
                        if not r.shipyard.ships.IsEmpty then
                            r.shipyard.ships
                            |> List.map (fun s ->
                                VRecord(Map.ofList [ "Typ", VString s.``type``; "Preis", VNumber(float s.purchasePrice) ]))
                        else
                            r.shipyard.shipTypes
                            |> List.map (fun t -> VRecord(Map.ofList [ "Typ", VString t.``type``; "Preis", VNumber 0.0 ]))

                    return InfoOk(VRecord(Map.ofList [ "Wegpunkt", VString waypointSymbol; "Schiffstypen", VList types ]))
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
                        |> List.map (fun i -> VRecord(Map.ofList [ "Name", VString i.name; "Einheiten", VNumber(float i.units) ]))

                    return
                        InfoOk(
                            VRecord(
                                Map.ofList
                                    [ "Einheiten", VNumber(float ship.cargo.units)
                                      "Kapazität", VNumber(float ship.cargo.capacity)
                                      "Waren", VList items ]
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
        | other -> failwith $"Unbekannter Info-Block: {other}"

    async {
        try
            return! RequestQueue.enqueue dbPath priority endpoint call
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
                // Milestone 7 (§14): the job just went terminal — release its ship
                // lock so another program can take the ship over immediately,
                // rather than waiting for the lease to expire.
                match jobs.TryGetValue jobId with
                | true, job -> do! Persistence.ShipLockRepository.release dbPath job.shipSymbol
                | false, _ -> ()
            | StartWait _ ->
                // Nothing to do here — `job.status`/`next_wake_at` already record
                // what it's waiting for (persisted by `tick`). The scheduler's tick
                // loop (`SpaceKids.Server.JobScheduler`) is what periodically
                // re-sends `WakeTick` once due, matching §14's own shell
                // description ("reads jobs due to wake, calls step...").
                ()
            | QueueApiCall(jobId, shipSymbol, action, attemptNumber) ->
                let! result = runAction client dbPath token priority shipSymbol action
                do! tick client dbPath token priority jobId (ApiResponseReceived(jobId, attemptNumber, result))
            | ReconcileShipState(jobId, shipSymbol, attemptNumber) ->
                let! result =
                    async {
                        try
                            let! ship =
                                RequestQueue.enqueue dbPath priority $"getShip:{shipSymbol}" (fun () ->
                                    client.GetShip(token, shipSymbol))

                            return ReconciliationShip(toSnapshot ship)
                        with ex ->
                            return classifyException ex
                    }

                do! tick client dbPath token priority jobId (ApiResponseReceived(jobId, attemptNumber, result))
            | ReconcileContractState(jobId, contractId, attemptNumber) ->
                let! result =
                    async {
                        try
                            let! r =
                                RequestQueue.enqueue dbPath priority $"getContract:{contractId}" (fun () ->
                                    client.GetContract(token, contractId))

                            return ReconciliationContract r.contract.accepted
                        with ex ->
                            return classifyException ex
                    }

                do! tick client dbPath token priority jobId (ApiResponseReceived(jobId, attemptNumber, result))
            | ReconcileFleetState(jobId, attemptNumber) ->
                let! result =
                    async {
                        try
                            let! ships =
                                RequestQueue.enqueue dbPath priority "listShips" (fun () -> client.ListShips(token))

                            return ReconciliationFleet(List.length ships)
                        with ex ->
                            return classifyException ex
                    }

                do! tick client dbPath token priority jobId (ApiResponseReceived(jobId, attemptNumber, result))
            | QueueInfoRead(jobId, shipSymbol, infoType, args, attemptNumber, _resultTarget) ->
                let! result = runInfoRead client dbPath token priority shipSymbol infoType args
                do! tick client dbPath token priority jobId (ApiResponseReceived(jobId, attemptNumber, result))
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
                        let job', effects = Step.step realClock job event
                        jobs[jobId] <- job'
                        do! persist dbPath job'
                        return Some effects
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
                do!
                    tick
                        client
                        dbPath
                        token
                        backgroundPriority
                        jobId
                        (ApiResponseReceived(jobId, attempt, ApiAmbiguous "Server wurde neu gestartet"))
            | Reconciling(attempt, _, baseline) ->
                let reconcileEffect =
                    match baseline with
                    | AcceptContractBaseline contractId -> ReconcileContractState(jobId, contractId, attempt)
                    | FleetBaseline _ -> ReconcileFleetState(jobId, attempt)
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
    (shipSymbol: string)
    (initialShip: Ship)
    (initialFleetShipCount: int)
    : Async<Result<JobId, string>> =
    async {
        let jobId = System.Guid.NewGuid().ToString()

        let job =
            { jobId = jobId
              program = program
              shipSymbol = shipSymbol
              status = Running
              stack = [ initialFrame ]
              lastKnownShip = Some(toSnapshot initialShip)
              lastKnownFleetShipCount = Some initialFleetShipCount
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

        let! lockResult = Persistence.ShipLockRepository.tryAcquire dbPath shipSymbol jobId leaseSeconds

        match lockResult with
        | Error _ ->
            jobs.TryRemove(jobId) |> ignore
            let cancelledJob = { job with status = Cancelled }
            do! persist dbPath cancelledJob
            return Error $"Schiff {shipSymbol} wird bereits von einem anderen Programm gesteuert."
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

/// Milestone 7: hydrates every row `JobScheduler`'s startup resume loaded from the
/// `jobs` table into the in-memory cache, without touching anything already
/// loaded (defensive — nothing should call this twice in practice).
let hydrate (rows: (JobId * string) seq) : unit =
    for jobId, executionStateJson in rows do
        if not (jobs.ContainsKey jobId) then
            jobs[jobId] <- Persistence.JobStateJson.deserializeJobState executionStateJson

/// Test-only: this module is a process-wide singleton (matching `RequestQueue`'s own
/// pattern), so tests that exercise it directly must reset state between cases.
let resetForTests () = jobs.Clear()
