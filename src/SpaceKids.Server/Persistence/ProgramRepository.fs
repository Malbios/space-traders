module SpaceKids.Server.Persistence.ProgramRepository

open System

/// Milestone 7 (§12/§14): the first real write to `programs`, sitting unused since
/// Milestone 1. One row per job start — `program_version` (here, the row's own
/// `created_at`) exists for a future watch-mode version-mismatch check (§15), not
/// because job resume depends on it (resume reads the program back out of
/// `jobs.execution_state_json` instead, see `JobStateJson.fs`).
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
