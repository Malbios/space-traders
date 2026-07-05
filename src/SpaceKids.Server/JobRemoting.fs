module SpaceKids.Server.JobRemoting

open Bolero.Remoting
open Bolero.Remoting.Server
open SpaceKids.Core.Scheduler
open SpaceKids.SpaceTraders
open SpaceKids.Client.Main

/// Server-side implementation of JobService (§14/§15/§19): starts/steps/runs/
/// pauses/resumes/cancels compiled programs against real ships, driven by
/// `JobRunner`'s persistent shell and `JobScheduler`'s background tick loop. Token
/// lookup reuses `AgentRemoting`'s stored-token mechanism — no second lookup path.
type JobRemoteHandler(client: SpaceTradersClient, ctx: IRemoteContext) =
    inherit RemoteHandler<JobService>()

    let dbPath = Persistence.Database.defaultDbPath

    /// Milestone 7 scope (see `docs/decisions.md`): still only the single
    /// Milestone-0 spike workspace — no saved/named multiple programs or routing
    /// yet, so there's exactly one `workspaces` row to reference here.
    let workspaceId = "blockly-spike"

    let statusDetail (status: JobStatus) : string option =
        match status with
        | WaitingForArrival until -> Some(until.ToString("o"))
        | WaitingForCooldown until -> Some(until.ToString("o"))
        | Failed message -> Some message
        | _ -> None

    let toDto (jobOpt: JobState option) : JobStatusDto option =
        jobOpt
        |> Option.map (fun job ->
            { status = JobRunner.statusName job.status
              statusDetail = statusDetail job.status
              log = job.log
              blockIdPerFrame = Step.blockIdPerFrame job })

    let toSummaryDto (job: JobState) : JobSummaryDto =
        { jobId = job.jobId
          shipSymbol = job.shipSymbol
          status = JobRunner.statusName job.status
          statusDetail = statusDetail job.status
          lastLogLine = job.log |> List.tryHead }

    let currentToken () : Async<string option> =
        async {
            let! stored = Persistence.AgentRepository.loadStoredAgent dbPath
            return stored |> Option.map snd
        }

    /// The `lookup: string -> CustomBlockDefinition option` `Compiler.compileWorkspace`
    /// expects is synchronous by design (Milestone 4 built it that way deliberately, so
    /// `SpaceKids.Core` never depends on persistence or async plumbing) — bridging to
    /// the repository's `Async` here, rather than making Core's compiler async, keeps
    /// that boundary intact. A local SQLite read is fast enough that blocking the
    /// request thread for it is an acceptable, deliberate tradeoff.
    let customBlockLookup (customBlockId: string) : SpaceKids.Core.Dsl.CustomBlockDefinition option =
        Persistence.CustomBlockRepository.load dbPath customBlockId |> Async.RunSynchronously

    override this.Handler =
        {
            startJob =
                // The client-supplied `token` is ignored in favor of the stored
                // one, matching every other handler here — the token persists
                // server-side once submitted (§2), so a fresh page load (whose
                // `tokenInput` starts out empty again) must not silently send an
                // empty token and misreport a real server reset (a 401 from an
                // empty token classifies exactly like one).
                fun (_clientSuppliedToken, shipSymbol, workspaceJson) ->
                    async {
                        let! tokenOpt = currentToken ()

                        match tokenOpt with
                        | None -> return Error "Bitte zuerst ein SpaceTraders-Token anmelden."
                        | Some token ->
                            let! ship =
                                RequestQueue.enqueue dbPath 1 $"getShip:{shipSymbol}" (fun () ->
                                    client.GetShip(token, shipSymbol))

                            let! agent = RequestQueue.enqueue dbPath 1 "getAgent" (fun () -> client.GetAgent(token))

                            // Milestone 9/Part B: `customBlockLookup` resolves real,
                            // persisted custom-block definitions now. Note:
                            // `Validator.revalidateAgainstCurrentDefinitions` (the §9
                            // structural mismatch check) is deliberately *not* called
                            // here — its own doc comment says it's "only meaningful
                            // when re-checking an already-compiled program later";
                            // since compile and run happen in this same request
                            // against the same live definitions, nothing could have
                            // drifted yet, so calling it here would always be a no-op.
                            // Its real call site arrives with a saved-program library.
                            let compiled =
                                SpaceKids.Core.Dsl.Compiler.compileWorkspace customBlockLookup workspaceJson
                                |> Result.bind (fun program ->
                                    match SpaceKids.Core.Dsl.Validator.validate program with
                                    | [] -> Ok program
                                    | errors -> Error errors)

                            match compiled with
                            | Error errors ->
                                let message = errors |> List.map (fun e -> e.message) |> String.concat "; "
                                return Error message
                            | Ok program ->
                                // `programs.workspace_id` references `workspaces(id)`
                                // — ensure that row exists regardless of whether the
                                // player has clicked "Speichern" yet; the program
                                // being run is exactly the workspace state to persist.
                                do! Persistence.WorkspaceRepository.save dbPath workspaceId workspaceJson
                                let compiledDslJson = Persistence.JobStateJson.serializeProgram program
                                return!
                                    JobRunner.startJob
                                        client
                                        dbPath
                                        token
                                        workspaceId
                                        compiledDslJson
                                        program
                                        shipSymbol
                                        ship
                                        agent.shipCount
                    }
            step =
                fun jobId ->
                    async {
                        let! tokenOpt = currentToken ()

                        match tokenOpt with
                        | None -> return JobRunner.getStatus jobId |> toDto
                        | Some token ->
                            do! JobRunner.stepOnce client dbPath 1 token jobId
                            return JobRunner.getStatus jobId |> toDto
                    }
            run =
                fun jobId ->
                    async {
                        let! tokenOpt = currentToken ()

                        match tokenOpt with
                        | None -> return JobRunner.getStatus jobId |> toDto
                        | Some token ->
                            do! JobRunner.runToCompletion client dbPath 1 token jobId
                            return JobRunner.getStatus jobId |> toDto
                    }
            getStatus = fun jobId -> async { return JobRunner.getStatus jobId |> toDto }
            pause =
                fun jobId ->
                    async {
                        let! tokenOpt = currentToken ()
                        match tokenOpt with
                        | Some token -> do! JobRunner.pause client dbPath token jobId
                        | None -> ()
                    }
            resume =
                fun jobId ->
                    async {
                        let! tokenOpt = currentToken ()
                        match tokenOpt with
                        | Some token -> do! JobRunner.resume client dbPath token jobId
                        | None -> ()
                    }
            cancel =
                fun jobId ->
                    async {
                        let! tokenOpt = currentToken ()
                        match tokenOpt with
                        | Some token -> do! JobRunner.cancel client dbPath token jobId
                        | None -> ()
                    }
            listJobs = fun () -> async { return JobRunner.listJobs () |> List.map toSummaryDto }
        }
