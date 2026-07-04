module SpaceKids.Server.JobRunner

open System.Collections.Concurrent
open SpaceKids.Core.Dsl
open SpaceKids.Core.Scheduler
open SpaceKids.SpaceTraders

/// The minimal foreground loop §14 says stands in for Milestone 7's real persistent
/// shell — in-memory job storage only (no `jobs`/`ship_locks` tables touched this
/// milestone), but driving the exact same `Step.step` Milestone 7 will persist.

/// Interactive foreground program = priority 1, §13's top tier ("player pressing
/// step/run").
let private interactivePriority = 1

let private jobs = ConcurrentDictionary<JobId, JobState>()

let private realClock: Clock = { now = fun () -> System.DateTimeOffset.UtcNow }

let private initialFrame: Frame =
    { scope = "main"
      position =
        [ { bodyRef = MainBody
            index = 0
            loopState = None } ]
      locals = Map.empty }

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
      cooldownExpiration = cooldownExpirationOf ship.cooldown }

let private inventoryMap (cargo: ShipCargo) : Map<string, int> =
    cargo.inventory |> List.map (fun i -> i.symbol, i.units) |> Map.ofList

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

    async {
        try
            return! RequestQueue.enqueue dbPath interactivePriority endpoint call
        with ex ->
            return classifyException ex
    }

/// Executes a `step` result's effect list, feeding results back into `step` (via
/// `tick`) as needed. Each job only ever has one instruction in flight at a time, so
/// `jobId` alone is a sufficient dispatch key (§14).
let rec private applyEffects
    (client: SpaceTradersClient)
    (dbPath: string)
    (token: string)
    (effects: Effect list)
    : Async<unit> =
    async {
        for effect in effects do
            match effect with
            | LogMessage(jobId, text) ->
                match jobs.TryGetValue jobId with
                | true, job -> jobs[jobId] <- { job with log = text :: job.log }
                | false, _ -> ()
            | JobCompleted _
            | JobFailed _ -> () // status is already set on the JobState by `step`
            | StartWait _ ->
                // Nothing to do here — `job.status` already records what it's waiting
                // for (§14: "Persist `next_wake_at` and resume later", a persisted
                // column from Milestone 7 onward; here it's just the in-memory
                // `JobState`). The run/step loop (`runToCompletion`/`stepOnce`) is what
                // periodically re-sends `WakeTick`, matching §14's own shell
                // description ("reads jobs due to wake, calls step...") — a polling
                // loop, not one call recursively sleeping through every wait inline.
                ()
            | QueueApiCall(jobId, shipSymbol, action, attemptNumber) ->
                let! result = runAction client dbPath token shipSymbol action
                do! tick client dbPath token jobId (ApiResponseReceived(jobId, attemptNumber, result))
            | ReconcileShipState(jobId, shipSymbol, attemptNumber) ->
                let! result =
                    async {
                        try
                            let! ship =
                                RequestQueue.enqueue dbPath interactivePriority $"getShip:{shipSymbol}" (fun () ->
                                    client.GetShip(token, shipSymbol))

                            return ReconciliationShip(toSnapshot ship)
                        with ex ->
                            return classifyException ex
                    }

                do! tick client dbPath token jobId (ApiResponseReceived(jobId, attemptNumber, result))
    }

/// One scheduler tick: pulls the job, calls the pure core, applies the effects it
/// produced.
and private tick
    (client: SpaceTradersClient)
    (dbPath: string)
    (token: string)
    (jobId: JobId)
    (event: SchedulerEvent)
    : Async<unit> =
    async {
        match jobs.TryGetValue jobId with
        | false, _ -> ()
        | true, job ->
            let job', effects = Step.step realClock job event
            jobs[jobId] <- job'
            do! applyEffects client dbPath token effects
    }

/// Starts a new job in-memory (no `jobs` table row — Milestone 7 scope) and returns
/// its id.
let startJob (program: CompiledProgram) (shipSymbol: string) (initialShip: Ship) : JobId =
    let jobId = System.Guid.NewGuid().ToString()

    jobs[jobId] <-
        { jobId = jobId
          program = program
          shipSymbol = shipSymbol
          status = Running
          stack = [ initialFrame ]
          lastKnownShip = Some(toSnapshot initialShip)
          log = [] }

    jobId

/// Drives the job through exactly one scheduler tick (one `WakeTick`) — step mode.
let stepOnce (client: SpaceTradersClient) (dbPath: string) (token: string) (jobId: JobId) : Async<unit> =
    tick client dbPath token jobId WakeTick

/// Drives the job to completion (or failure) — run mode. A genuine polling loop
/// (§14: "reads jobs due to wake, calls step..."): each `WakeTick` either makes free
/// progress, dispatches the next action, or (while waiting on an arrival/cooldown
/// that isn't due yet) is a no-op — so this polls at a short fixed interval rather
/// than trying to compute exactly when the next wake is due.
let rec runToCompletion (client: SpaceTradersClient) (dbPath: string) (token: string) (jobId: JobId) : Async<unit> =
    async {
        do! stepOnce client dbPath token jobId

        match jobs.TryGetValue jobId with
        | true, { status = Completed }
        | true, { status = Failed _ } -> ()
        | true, _ ->
            do! Async.Sleep 50
            return! runToCompletion client dbPath token jobId
        | false, _ -> ()
    }

let getStatus (jobId: JobId) : JobState option =
    match jobs.TryGetValue jobId with
    | true, j -> Some j
    | false, _ -> None

/// Test-only: this module is a process-wide singleton (matching `RequestQueue`'s own
/// pattern), so tests that exercise it directly must reset state between cases.
let resetForTests () = jobs.Clear()
