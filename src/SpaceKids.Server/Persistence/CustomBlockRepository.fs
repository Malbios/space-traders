module SpaceKids.Server.Persistence.CustomBlockRepository

open System
open SpaceKids.Core.Dsl

/// Milestone 9/Part B (§9/§12): the first real writes to `custom_blocks`/
/// `custom_block_versions`, provisional since Milestone 1. Versions are append-only
/// from day one (§9: "persist a version number... not yet enforced beyond the
/// mismatch check" — that floor only means something if old versions are never
/// silently overwritten).

type CustomBlockSummary =
    { id: string
      name: string
      description: string option
      version: int }

let insert (dbPath: string) (name: string) (description: string option) : Async<string> =
    async {
        let id = Guid.NewGuid().ToString()
        let now = DateTime.UtcNow.ToString("o")
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            INSERT INTO custom_blocks (id, name, description, created_at, updated_at)
            VALUES ($id, $name, $description, $now, $now);
            """

        cmd.Parameters.AddWithValue("$id", id) |> ignore
        cmd.Parameters.AddWithValue("$name", name) |> ignore
        cmd.Parameters.AddWithValue("$description", description |> Option.map box |> Option.defaultValue (box DBNull.Value))
        |> ignore
        cmd.Parameters.AddWithValue("$now", now) |> ignore
        cmd.ExecuteNonQuery() |> ignore
        return id
    }

let rename (dbPath: string) (id: string) (name: string) : Async<unit> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "UPDATE custom_blocks SET name = $name, updated_at = $now WHERE id = $id;"
        cmd.Parameters.AddWithValue("$name", name) |> ignore
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o")) |> ignore
        cmd.Parameters.AddWithValue("$id", id) |> ignore
        cmd.ExecuteNonQuery() |> ignore
    }

let delete (dbPath: string) (id: string) : Async<unit> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "DELETE FROM custom_block_versions WHERE custom_block_id = $id; DELETE FROM custom_blocks WHERE id = $id;"
        cmd.Parameters.AddWithValue("$id", id) |> ignore
        cmd.ExecuteNonQuery() |> ignore
    }

let private nextVersion (dbPath: string) (customBlockId: string) : Async<int> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT COALESCE(MAX(version), 0) FROM custom_block_versions WHERE custom_block_id = $id;"
        cmd.Parameters.AddWithValue("$id", customBlockId) |> ignore
        let current = cmd.ExecuteScalar() :?> int64
        return int current + 1
    }

/// Saves a new version — never updates an existing row (see the module doc comment).
/// `definitionJson` is the workshop's Blockly workspace JSON (the definition-shell
/// block + its body); `compiled` is this one block's own compiled body (§10: "the
/// signature snapshot... recorded once per custom block" — the closure that pairs it
/// with everything *it* calls lives in whichever `CompiledProgram.customBlocks` map
/// references it, not here).
let saveVersion (dbPath: string) (customBlockId: string) (definitionJson: string) (compiled: CompiledCustomBlock) : Async<int> =
    async {
        let! version = nextVersion dbPath customBlockId
        let id = Guid.NewGuid().ToString()
        let now = DateTime.UtcNow.ToString("o")
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            INSERT INTO custom_block_versions (id, custom_block_id, version, definition_json, compiled_body_json, created_at)
            VALUES ($id, $blockId, $version, $defJson, $bodyJson, $now);
            """

        cmd.Parameters.AddWithValue("$id", id) |> ignore
        cmd.Parameters.AddWithValue("$blockId", customBlockId) |> ignore
        cmd.Parameters.AddWithValue("$version", version) |> ignore
        cmd.Parameters.AddWithValue("$defJson", definitionJson) |> ignore
        cmd.Parameters.AddWithValue("$bodyJson", JobStateJson.serializeCustomBlock compiled) |> ignore
        cmd.Parameters.AddWithValue("$now", now) |> ignore
        cmd.ExecuteNonQuery() |> ignore
        return version
    }

let private latestVersionRow (dbPath: string) (customBlockId: string) : Async<(string * string option) option> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT definition_json, compiled_body_json FROM custom_block_versions
            WHERE custom_block_id = $id ORDER BY version DESC LIMIT 1;
            """

        cmd.Parameters.AddWithValue("$id", customBlockId) |> ignore
        use reader = cmd.ExecuteReader()

        if reader.Read() then
            let definitionJson = reader.GetString(0)
            let compiledBodyJson = if reader.IsDBNull(1) then None else Some(reader.GetString(1))
            return Some(definitionJson, compiledBodyJson)
        else
            return None
    }

/// The `lookup: string -> CustomBlockDefinition option` function `Compiler`/
/// `Validator` already expect (Milestone 4 built them against a supplied function
/// deliberately, so `SpaceKids.Core` never depends on persistence).
let load (dbPath: string) (customBlockId: string) : Async<CustomBlockDefinition option> =
    async {
        let! row = latestVersionRow dbPath customBlockId

        match row with
        | None -> return None
        | Some(definitionJson, compiledBodyJson) ->
            let signature =
                match compiledBodyJson with
                | Some json -> (JobStateJson.deserializeCustomBlock json).signature
                | None -> { inputs = []; output = None; outputFields = None }

            return
                Some
                    { id = customBlockId
                      signature = signature
                      workspaceJson = definitionJson }
    }

let list (dbPath: string) : Async<CustomBlockSummary list> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT cb.id, cb.name, cb.description, COALESCE(MAX(cbv.version), 0)
            FROM custom_blocks cb
            LEFT JOIN custom_block_versions cbv ON cbv.custom_block_id = cb.id
            GROUP BY cb.id, cb.name, cb.description
            ORDER BY cb.name;
            """

        use reader = cmd.ExecuteReader()
        let results = ResizeArray<CustomBlockSummary>()

        while reader.Read() do
            results.Add
                { id = reader.GetString(0)
                  name = reader.GetString(1)
                  description = if reader.IsDBNull(2) then None else Some(reader.GetString(2))
                  version = int (reader.GetInt64(3)) }

        return List.ofSeq results
    }

/// Delete-refusal check (§9c: "refused, with a German message listing where it's
/// used"). Programs already capture their full transitive `customBlocks` closure, so
/// a plain key lookup suffices there; a custom block's own stored compiled body has
/// no closure of its own (see `saveVersion`'s doc comment), so cross-block usage is
/// checked via a plain substring match on the serialized body for the target id — a
/// pragmatic simplification (ids are GUIDs, so a false-positive substring match is
/// vanishingly unlikely), not a full instruction-tree walk.
let findUsages (dbPath: string) (customBlockId: string) (locale: Locale) : Async<string list> =
    async {
        use conn = Database.openConnection dbPath

        let usedByPrograms =
            async {
                use cmd = conn.CreateCommand()

                // Joined against `program_definitions` deliberately: `programs` is an
                // append-only table of immutable compiled-job snapshots going back to
                // before Milestone 11's named-program library existed (every job used to
                // compile against one hardcoded shared workspace, literally named
                // "blockly-spike") -- a plain `SELECT ... FROM programs` would treat any
                // such orphaned pre-Milestone-11 snapshot (or a since-deleted program's
                // old snapshots) as permanent, invisible, undeletable "usage." This join
                // also fixes a second bug for free: `programs.name` is always the raw
                // workspace id/GUID (see `ProgramRepository.insert`), not the program's
                // real display name -- `program_definitions.name` is.
                cmd.CommandText <-
                    """
                    SELECT pd.name, p.compiled_dsl_json
                    FROM programs p
                    JOIN program_definitions pd ON pd.id = p.workspace_id;
                    """

                use reader = cmd.ExecuteReader()
                let names = ResizeArray<string>()

                let programLabel =
                    match locale with
                    | De -> "Programm"
                    | En -> "Program"

                while reader.Read() do
                    if not (reader.IsDBNull(1)) then
                        let program = JobStateJson.deserializeProgram (reader.GetString(1))
                        if program.customBlocks.ContainsKey customBlockId then
                            names.Add $"{programLabel} \"{reader.GetString(0)}\""

                // A program run multiple times while referencing the block would
                // otherwise list its own name once per historical snapshot row.
                return names |> Seq.distinct |> List.ofSeq
            }

        let usedByOtherBlocks =
            async {
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    """
                    SELECT cb.id, cb.name, cbv.compiled_body_json
                    FROM custom_blocks cb
                    JOIN custom_block_versions cbv ON cbv.custom_block_id = cb.id
                    WHERE cb.id <> $id
                    AND cbv.version = (SELECT MAX(version) FROM custom_block_versions WHERE custom_block_id = cb.id);
                    """

                cmd.Parameters.AddWithValue("$id", customBlockId) |> ignore
                use reader = cmd.ExecuteReader()
                let names = ResizeArray<string>()

                let customBlockLabel =
                    match locale with
                    | De -> "Eigener Block"
                    | En -> "Custom block"

                while reader.Read() do
                    if not (reader.IsDBNull(2)) && reader.GetString(2).Contains(customBlockId) then
                        names.Add $"{customBlockLabel} \"{reader.GetString(1)}\""

                return List.ofSeq names
            }

        let! fromPrograms = usedByPrograms
        let! fromBlocks = usedByOtherBlocks
        return fromPrograms @ fromBlocks
    }
