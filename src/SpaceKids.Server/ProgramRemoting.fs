module SpaceKids.Server.ProgramRemoting

open Bolero.Remoting
open Bolero.Remoting.Server
open SpaceKids.Client.Main

/// The structural-mismatch check (§9/§15), wired to its first real call site:
/// opening a saved program compares its last-run compiled snapshot against
/// currently-live custom-block definitions, surfaced as dismissible warnings
/// rather than blocking the load. A standalone module function (not inlined into
/// the handler's `Handler` property, matching `AgentRemoting.fs`'s own
/// `fetchWaypointMarket`/`fetchWaypointShipyard` shape) so tests can call it
/// directly without instantiating a Bolero remote handler.
let staleWarnings (dbPath: string) (locale: SpaceKids.Core.Dsl.Locale) (programId: string) : Async<string list> =
    async {
        let! snapshotJson = Persistence.ProgramRepository.latestCompiledSnapshot dbPath programId

        match snapshotJson with
        | None -> return []
        | Some json ->
            let compiled = Persistence.JobStateJson.deserializeProgram json

            let customBlockLookup (customBlockId: string) : SpaceKids.Core.Dsl.CustomBlockDefinition option =
                Persistence.CustomBlockRepository.load dbPath customBlockId |> Async.RunSynchronously

            return
                SpaceKids.Core.Dsl.Validator.revalidateAgainstCurrentDefinitions locale customBlockLookup compiled
                |> List.map (fun e -> e.message)
    }

/// Server-side implementation of ProgramService: the saved/named multiple-program
/// library — create/list/rename/delete, and Part C's structural-mismatch check
/// folded into `loadDefinition`.
type ProgramRemoteHandler(ctx: IRemoteContext) =
    inherit RemoteHandler<ProgramService>()

    let dbPath = Persistence.Database.defaultDbPath

    let toSummaryDto (p: Persistence.ProgramRepository.ProgramSummary) : ProgramSummaryDto = { id = p.id; name = p.name }

    let currentLocale () : Async<SpaceKids.Core.Dsl.Locale> =
        async {
            let! raw = Persistence.SettingsRepository.getLocale dbPath
            return SpaceKids.Core.Dsl.Locale.ofString raw
        }

    override this.Handler =
        {
            list = fun () -> async { let! programs = Persistence.ProgramRepository.list dbPath in return programs |> List.map toSummaryDto }
            create = fun name -> Persistence.ProgramRepository.create dbPath name
            loadDefinition =
                fun id ->
                    async {
                        let! workspaceJson = Persistence.WorkspaceRepository.load dbPath id
                        let! locale = currentLocale ()
                        let! warnings = staleWarnings dbPath locale id
                        return (workspaceJson, warnings)
                    }
            rename = fun (id, name) -> Persistence.ProgramRepository.rename dbPath id name
            delete =
                fun id ->
                    async {
                        let! locale = currentLocale ()
                        return! Persistence.ProgramRepository.delete dbPath locale id
                    }
        }
