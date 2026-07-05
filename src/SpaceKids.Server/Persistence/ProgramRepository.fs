module SpaceKids.Server.Persistence.ProgramRepository

open System

/// Milestone 7 (§12/§14): the first real write to `programs`, sitting unused since
/// Milestone 1. One row per job start — an immutable compiled snapshot, not the
/// named program itself (see `ProgramSummary`/`create` below) — `created_at` exists
/// for the structural-mismatch check (§9/§15) to find "the most recent snapshot for
/// this program", not because job resume depends on it (resume reads the program
/// back out of `jobs.execution_state_json` instead, see `JobStateJson.fs`).
let insert (dbPath: string) (workspaceId: string) (compiledDslJson: string) : Async<string> =
    async {
        let id = Guid.NewGuid().ToString()
        let now = DateTime.UtcNow.ToString("o")
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            INSERT INTO programs (id, agent_id, workspace_id, name, compiled_dsl_json, created_at, updated_at)
            VALUES ($id, NULL, $workspaceId, $name, $json, $now, $now);
            """

        cmd.Parameters.AddWithValue("$id", id) |> ignore
        cmd.Parameters.AddWithValue("$workspaceId", workspaceId) |> ignore
        cmd.Parameters.AddWithValue("$name", workspaceId) |> ignore
        cmd.Parameters.AddWithValue("$json", compiledDslJson) |> ignore
        cmd.Parameters.AddWithValue("$now", now) |> ignore
        cmd.ExecuteNonQuery() |> ignore
        return id
    }

/// Saved/named multiple-program library (§15/§19): the parent row for a named,
/// editable program, deliberately id-aligned with its own `workspaces` row — the
/// program's editable Blockly JSON lives there (overwrite-based, no history needed),
/// while `programs` above still captures an immutable compiled snapshot per run.

type ProgramSummary = { id: string; name: string }

/// Creates the named-program row and a blank sibling `workspaces` row sharing its
/// id, so a freshly-created program has somewhere to save into immediately.
let create (dbPath: string) (name: string) : Async<string> =
    async {
        let id = Guid.NewGuid().ToString()
        let now = DateTime.UtcNow.ToString("o")
        use conn = Database.openConnection dbPath

        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            INSERT INTO program_definitions (id, name, created_at, updated_at)
            VALUES ($id, $name, $now, $now);
            """

        cmd.Parameters.AddWithValue("$id", id) |> ignore
        cmd.Parameters.AddWithValue("$name", name) |> ignore
        cmd.Parameters.AddWithValue("$now", now) |> ignore
        cmd.ExecuteNonQuery() |> ignore

        do! WorkspaceRepository.save dbPath id """{"blocks":{"languageVersion":0,"blocks":[]}}"""
        return id
    }

let rename (dbPath: string) (id: string) (name: string) : Async<unit> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "UPDATE program_definitions SET name = $name, updated_at = $now WHERE id = $id;"
        cmd.Parameters.AddWithValue("$name", name) |> ignore
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o")) |> ignore
        cmd.Parameters.AddWithValue("$id", id) |> ignore
        cmd.ExecuteNonQuery() |> ignore
    }

let list (dbPath: string) : Async<ProgramSummary list> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT id, name FROM program_definitions ORDER BY name;"
        use reader = cmd.ExecuteReader()
        let results = ResizeArray<ProgramSummary>()

        while reader.Read() do
            results.Add { id = reader.GetString(0); name = reader.GetString(1) }

        return List.ofSeq results
    }

/// The structural-mismatch check (§9/§15): the most recently *run* compiled
/// snapshot for this program (not its current editable workspace JSON, which is
/// always freshly recompiled against live definitions and so could never go
/// stale) — `None` for a program that's never actually been run yet, nothing to
/// compare against.
let latestCompiledSnapshot (dbPath: string) (programId: string) : Async<string option> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT compiled_dsl_json FROM programs
            WHERE workspace_id = $id AND compiled_dsl_json IS NOT NULL
            ORDER BY created_at DESC LIMIT 1;
            """

        cmd.Parameters.AddWithValue("$id", programId) |> ignore

        match cmd.ExecuteScalar() with
        | null -> return None
        | :? DBNull -> return None
        | json -> return Some(json :?> string)
    }

/// Delete-refusal check: refused only while a currently non-terminal job actually
/// flies this program (matches `jobs.program_id` -> its `programs` snapshot row's
/// `workspace_id`, which is this program's own id) — a completed/cancelled job's
/// history doesn't block deletion, only a live pilot does.
let private terminalStates = [ "Completed"; "Failed"; "Cancelled" ]

let delete (dbPath: string) (locale: SpaceKids.Core.Dsl.Locale) (id: string) : Async<Result<unit, string>> =
    async {
        use conn = Database.openConnection dbPath

        use checkCmd = conn.CreateCommand()

        checkCmd.CommandText <-
            $"""
            SELECT COUNT(*) FROM jobs j
            JOIN programs p ON p.id = j.program_id
            WHERE p.workspace_id = $id AND j.state NOT IN ({String.Join(",", terminalStates |> List.map (fun s -> $"'{s}'"))});
            """

        checkCmd.Parameters.AddWithValue("$id", id) |> ignore
        let activeCount = checkCmd.ExecuteScalar() :?> int64

        if activeCount > 0L then
            let message =
                match locale with
                | SpaceKids.Core.Dsl.De -> "Kann nicht gelöscht werden — ein Pilot fliegt dieses Programm gerade."
                | SpaceKids.Core.Dsl.En -> "Cannot be deleted — a pilot is currently flying this program."

            return Error message
        else
            use deleteCmd = conn.CreateCommand()
            deleteCmd.CommandText <- "DELETE FROM program_definitions WHERE id = $id;"
            deleteCmd.Parameters.AddWithValue("$id", id) |> ignore
            deleteCmd.ExecuteNonQuery() |> ignore
            return Ok()
    }
