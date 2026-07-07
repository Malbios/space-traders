module SpaceKids.Server.CustomBlockRemoting

open Bolero.Remoting
open Bolero.Remoting.Server
open SpaceKids.Client.Main

/// Server-side implementation of CustomBlockService (§9b/§9c/§9d, Milestone 9/Part
/// D): the Blockwerkstatt's block library — create/list/rename/delete, and saving a
/// new version (re-deriving the signature fresh from the just-edited workshop JSON,
/// then compiling+persisting it, exactly as running a program compiles fresh against
/// live definitions — see `JobRemoting.fs`'s `customBlockLookup`).
type CustomBlockRemoteHandler(ctx: IRemoteContext) =
    inherit RemoteHandler<CustomBlockService>()

    let dbPath = Persistence.Database.defaultDbPath

    let toSummaryDto (b: Persistence.CustomBlockRepository.CustomBlockSummary) : CustomBlockSummaryDto =
        { id = b.id; name = b.name; version = b.version }

    /// Milestone 13 (bilingual compile errors): every message this handler can
    /// return is translated by the stored locale setting, same pattern as
    /// `JobRemoting.fs`'s/`ProgramRemoting.fs`'s own `currentLocale()`.
    let currentLocale () : Async<SpaceKids.Core.Dsl.Locale> =
        async {
            let! raw = Persistence.SettingsRepository.getLocale dbPath
            return SpaceKids.Core.Dsl.Locale.ofString raw
        }

    override this.Handler =
        {
            list = fun () -> async { let! blocks = Persistence.CustomBlockRepository.list dbPath in return blocks |> List.map toSummaryDto }
            create = fun name -> Persistence.CustomBlockRepository.insert dbPath name None
            loadDefinition =
                fun id ->
                    async {
                        let! def = Persistence.CustomBlockRepository.load dbPath id
                        return def |> Option.map (fun d -> d.workspaceJson)
                    }
            save =
                fun (id, workspaceJson) ->
                    async {
                        let! locale = currentLocale ()

                        let topBlocks = SpaceKids.Core.Dsl.BlocklyJson.parseWorkspace workspaceJson

                        if not (topBlocks |> List.exists (fun b -> b.blockType = "sk_custom_block_def")) then
                            let message =
                                match locale with
                                | SpaceKids.Core.Dsl.De ->
                                    "Bitte ziehe einen \"Eigener Block\"-Block aus der Toolbox auf die Fläche und baue die Logik in \"Ergebnis\" (Rückgabewert) oder \"Inhalt\" (Anweisungen) ein."
                                | SpaceKids.Core.Dsl.En ->
                                    "Drag a \"Custom block\" from the toolbox onto the canvas and build your logic in \"Result\" (return value) or \"Body\" (statements)."

                            return Error message

                        // The signature to persist must reflect *this* save's mutator
                        // edits, not whatever version is already on disk — derived
                        // fresh from the raw JSON (`Compiler.deriveCustomBlockSignature`),
                        // the server-side counterpart to the client's own `readSignature`.
                        let signature = SpaceKids.Core.Dsl.Compiler.deriveCustomBlockSignature workspaceJson

                        let lookup (lookupId: string) : SpaceKids.Core.Dsl.CustomBlockDefinition option =
                            if lookupId = id then
                                Some
                                    { id = id
                                      signature = signature
                                      workspaceJson = workspaceJson }
                            else
                                Persistence.CustomBlockRepository.load dbPath lookupId |> Async.RunSynchronously

                        match SpaceKids.Core.Dsl.Compiler.resolveCustomBlockCall locale lookup id with
                        | Error errors ->
                            let message = errors |> List.map (fun e -> e.message) |> String.concat "; "
                            return Error message
                        | Ok compiledMap ->
                            let compiled = compiledMap.[id]
                            let! version = Persistence.CustomBlockRepository.saveVersion dbPath id workspaceJson compiled
                            return Ok version
                    }
            rename = fun (id, name) -> Persistence.CustomBlockRepository.rename dbPath id name
            delete =
                fun id ->
                    async {
                        let! locale = currentLocale ()
                        let! usages = Persistence.CustomBlockRepository.findUsages dbPath id locale

                        if usages.IsEmpty then
                            do! Persistence.CustomBlockRepository.delete dbPath id
                            return Ok()
                        else
                            let usageList = usages |> String.concat ", "

                            let message =
                                match locale with
                                | SpaceKids.Core.Dsl.De -> $"Kann nicht gelöscht werden — wird noch verwendet von: {usageList}."
                                | SpaceKids.Core.Dsl.En -> $"Cannot be deleted — still used by: {usageList}."

                            return Error message
                    }
        }
