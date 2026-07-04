module SpaceKids.Server.JobRemoting

open Bolero.Remoting
open Bolero.Remoting.Server
open SpaceKids.Core.Scheduler
open SpaceKids.SpaceTraders
open SpaceKids.Client.Main

/// Server-side implementation of JobService (Milestone 6, §14/§19): starts/steps/runs
/// a compiled program against one real ship, driven by JobRunner's in-memory
/// foreground loop. Token lookup reuses AgentRemoting's stored-token mechanism — no
/// second lookup path.
type JobRemoteHandler(client: SpaceTradersClient, ctx: IRemoteContext) =
    inherit RemoteHandler<JobService>()

    let dbPath = Persistence.Database.defaultDbPath

    let toDto (jobOpt: JobState option) : JobStatusDto option =
        jobOpt
        |> Option.map (fun job ->
            let statusName, detail =
                match job.status with
                | Running -> "Running", None
                | AwaitingApiResponse _ -> "AwaitingApiResponse", None
                | WaitingForArrival until -> "WaitingForArrival", Some(until.ToString("o"))
                | WaitingForCooldown until -> "WaitingForCooldown", Some(until.ToString("o"))
                | Reconciling _ -> "Reconciling", None
                | Completed -> "Completed", None
                | Failed message -> "Failed", Some message

            { status = statusName
              statusDetail = detail
              log = job.log })

    override this.Handler =
        {
            startJob =
                fun (token, shipSymbol, workspaceJson) ->
                    async {
                        let! ship =
                            RequestQueue.enqueue dbPath 1 $"getShip:{shipSymbol}" (fun () ->
                                client.GetShip(token, shipSymbol))
                        // Custom-block calls aren't in scope this milestone (§9's real
                        // mechanism is Milestone 9) — a lookup that always misses is
                        // correct here; the compiler/validator already reject any
                        // program that references one.
                        match SpaceKids.Core.Dsl.Compiler.compileWorkspace (fun _ -> None) workspaceJson with
                        | Error errors ->
                            let message = errors |> List.map (fun e -> e.message) |> String.concat "; "
                            return failwith message
                        | Ok program -> return JobRunner.startJob program shipSymbol ship
                    }
            step =
                fun jobId ->
                    async {
                        let! stored = Persistence.AgentRepository.loadStoredAgent dbPath

                        match stored with
                        | None -> return JobRunner.getStatus jobId |> toDto
                        | Some(_, token) ->
                            do! JobRunner.stepOnce client dbPath token jobId
                            return JobRunner.getStatus jobId |> toDto
                    }
            run =
                fun jobId ->
                    async {
                        let! stored = Persistence.AgentRepository.loadStoredAgent dbPath

                        match stored with
                        | None -> return JobRunner.getStatus jobId |> toDto
                        | Some(_, token) ->
                            do! JobRunner.runToCompletion client dbPath token jobId
                            return JobRunner.getStatus jobId |> toDto
                    }
            getStatus = fun jobId -> async { return JobRunner.getStatus jobId |> toDto }
        }
