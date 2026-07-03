module SpaceKids.Server.Persistence

open System.IO
open Microsoft.Data.Sqlite

/// Milestone 0 spike persistence only: proves save/reload round-trips through SQLite
/// (plan.md Milestone 0 Part B). The real `workspaces` table (§12), WAL mode,
/// busy_timeout, migrations, and backups are Milestone 1 scope.
let private dbPath = Path.Combine(Directory.GetCurrentDirectory(), "spacekids.spike.db")
let private connectionString = $"Data Source={dbPath}"

let private ensureSchema () =
    use conn = new SqliteConnection(connectionString)
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """
        CREATE TABLE IF NOT EXISTS spike_workspaces (
            container_id TEXT PRIMARY KEY,
            workspace_json TEXT NOT NULL
        );
        """
    cmd.ExecuteNonQuery() |> ignore

do ensureSchema ()

let saveWorkspace (containerId: string) (workspaceJson: string) : Async<unit> =
    async {
        use conn = new SqliteConnection(connectionString)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            """
            INSERT INTO spike_workspaces (container_id, workspace_json)
            VALUES ($containerId, $json)
            ON CONFLICT(container_id) DO UPDATE SET workspace_json = excluded.workspace_json;
            """
        cmd.Parameters.AddWithValue("$containerId", containerId) |> ignore
        cmd.Parameters.AddWithValue("$json", workspaceJson) |> ignore
        cmd.ExecuteNonQuery() |> ignore
    }

let loadWorkspace (containerId: string) : Async<string option> =
    async {
        use conn = new SqliteConnection(connectionString)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT workspace_json FROM spike_workspaces WHERE container_id = $containerId;"
        cmd.Parameters.AddWithValue("$containerId", containerId) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            return Some(reader.GetString(0))
        else
            return None
    }
