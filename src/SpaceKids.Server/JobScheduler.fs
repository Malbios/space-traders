module SpaceKids.Server.JobScheduler

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open SpaceKids.Core.Scheduler
open SpaceKids.SpaceTraders

/// Milestone 7 (§14): the real persistent scheduler shell around `JobRunner`/
/// `Step.step`. Module-level functions (not just methods on the hosted service)
/// so `SpaceKids.IntegrationTests` can call `resumeAll`/`tickOnce`/`sweep`
/// directly and deterministically, without fighting `BackgroundService`'s
/// fire-and-forget `StartAsync` timing.

let private isTerminal (status: JobStatus) : bool =
    match status with
    | Completed
    | Failed _
    | Cancelled -> true
    | _ -> false

/// Single-user app — the one stored token, if a player has pasted one yet.
/// Nothing to drive without it; jobs simply stay loaded and wait.
let private currentToken (dbPath: string) : Async<string option> =
    async {
        let! stored = Persistence.AgentRepository.loadStoredAgent dbPath
        return stored |> Option.map snd
    }

/// Loads every non-terminal job (§14: "load jobs in active or waiting states"),
/// then either recovers it (it still owns its ship's lock) or pauses it (its lock
/// was already reclaimed by another job while this process was down). Called once
/// at scheduler startup — and, in tests, to simulate "the process restarted".
let resumeAll (client: SpaceTradersClient) (dbPath: string) : Async<unit> =
    async {
        let! rows = Persistence.JobRepository.loadNonTerminal dbPath
        JobRunner.hydrate (rows |> List.map (fun r -> r.id, r.executionStateJson))
        let! tokenOpt = currentToken dbPath

        match tokenOpt with
        | None -> ()
        | Some token ->
            for row in rows do
                // A ship-agnostic job (§14 follow-up) never took a lock in the first
                // place — nothing to re-acquire, just recover it directly.
                match row.assignedShipSymbol with
                | None -> do! JobRunner.recoverJob client dbPath token row.id
                | Some shipSymbol ->
                    let! lockResult =
                        Persistence.ShipLockRepository.tryAcquire dbPath shipSymbol row.id JobRunner.leaseSeconds

                    match lockResult with
                    | Ok _ -> do! JobRunner.recoverJob client dbPath token row.id
                    | Error _ -> do! JobRunner.pause client dbPath token row.id
    }

let tickOnce (client: SpaceTradersClient) (dbPath: string) : Async<unit> =
    async {
        let! tokenOpt = currentToken dbPath

        match tokenOpt with
        | None -> ()
        | Some token ->
            for job in JobRunner.listJobs () do
                if not (isTerminal job.status) then
                    do! JobRunner.stepOnce client dbPath JobRunner.backgroundPriority token job.jobId

                    // `stepOnce` may have just made this job terminal — and, in doing
                    // so, already released its ship lock (`JobRunner.fs`'s `JobFailed`/
                    // `JobCompleted`/`JobCancelled` effect handling). Re-check the
                    // *current* status rather than trusting the pre-step `job` snapshot
                    // above: refreshing the lease unconditionally here would resurrect
                    // a lock this same tick just correctly released, permanently
                    // wedging that ship until the next lease-expiry sweep/reclaim.
                    match JobRunner.getStatus job.jobId, job.shipSymbol with
                    | Some current, Some sym when not (isTerminal current.status) ->
                        do! Persistence.ShipLockRepository.refreshLease dbPath sym job.jobId JobRunner.leaseSeconds
                    | _ -> ()

                    match JobRunner.getStatus job.jobId with
                    | Some current when not (isTerminal current.status) ->
                        for sym in current.dynamicShipLocks |> List.distinct do
                            do! Persistence.ShipLockRepository.refreshLease dbPath sym job.jobId JobRunner.leaseSeconds

                        for sym in current.parallelBranches |> List.collect (fun b -> b.job.dynamicShipLocks) |> List.distinct do
                            do! Persistence.ShipLockRepository.refreshLease dbPath sym job.jobId JobRunner.leaseSeconds
                    | _ -> ()
    }

/// §14: an expired lease with no competing acquirer still gets its job paused
/// visibly rather than lingering — only for locks whose job isn't one of our own
/// live in-memory jobs; the tick loop above is what keeps a genuinely active job's
/// lease fresh, so a lock we still hold never shows up here.
let sweep (client: SpaceTradersClient) (dbPath: string) : Async<unit> =
    async {
        let! tokenOpt = currentToken dbPath

        match tokenOpt with
        | None -> ()
        | Some token ->
            let! expired = Persistence.ShipLockRepository.findExpired dbPath

            let liveNonTerminalIds =
                JobRunner.listJobs ()
                |> List.filter (fun j -> not (isTerminal j.status))
                |> List.map (fun j -> j.jobId)
                |> Set.ofList

            for shipSymbol, jobId in expired do
                if not (liveNonTerminalIds.Contains jobId) then
                    do! JobRunner.pauseOrphan client dbPath token jobId
                    do! Persistence.ShipLockRepository.release dbPath shipSymbol
    }

type SchedulerService(client: SpaceTradersClient, dbPath: string) =
    inherit BackgroundService()

    let tickIntervalMs = 1000.0
    let sweepEveryNTicks = 60

    new(client: SpaceTradersClient) = new SchedulerService(client, Persistence.Database.defaultDbPath)

    override this.ExecuteAsync(stoppingToken: CancellationToken) : Task =
        task {
            do! resumeAll client dbPath

            try
                use timer = new PeriodicTimer(TimeSpan.FromMilliseconds(tickIntervalMs))
                let mutable tickCount = 0
                let mutable ticked = true

                while ticked do
                    let! t = timer.WaitForNextTickAsync(stoppingToken).AsTask()
                    ticked <- t

                    if ticked then
                        tickCount <- tickCount + 1
                        do! tickOnce client dbPath

                        if tickCount % sweepEveryNTicks = 0 then
                            do! sweep client dbPath
            with :? OperationCanceledException -> ()
        }
