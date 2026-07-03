module SpaceKids.Server.Persistence.WorkspaceRepository

open System

let save (dbPath: string) (id: string) (workspaceJson: string) : Async<unit> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            """
            INSERT INTO workspaces (id, workspace_json, updated_at)
            VALUES ($id, $json, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET workspace_json = excluded.workspace_json, updated_at = excluded.updated_at;
            """
        cmd.Parameters.AddWithValue("$id", id) |> ignore
        cmd.Parameters.AddWithValue("$json", workspaceJson) |> ignore
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o")) |> ignore
        cmd.ExecuteNonQuery() |> ignore
    }

let load (dbPath: string) (id: string) : Async<string option> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT workspace_json FROM workspaces WHERE id = $id;"
        cmd.Parameters.AddWithValue("$id", id) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            return Some(reader.GetString(0))
        else
            return None
    }
