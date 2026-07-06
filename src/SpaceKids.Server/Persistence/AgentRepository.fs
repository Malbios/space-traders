module SpaceKids.Server.Persistence.AgentRepository

open System

/// Single-user app — always the one most recently saved agent/token, no multi-agent
/// selection UI yet.
let saveAgent (dbPath: string) (symbol: string) (token: string) : Async<unit> =
    async {
        use conn = Database.openConnection dbPath
        use tx = conn.BeginTransaction()

        use agentCmd = conn.CreateCommand()
        agentCmd.Transaction <- tx
        agentCmd.CommandText <-
            """
            INSERT INTO agents (id, symbol, created_at)
            VALUES ($id, $symbol, $createdAt)
            ON CONFLICT(id) DO UPDATE SET symbol = excluded.symbol, created_at = excluded.created_at;
            """
        agentCmd.Parameters.AddWithValue("$id", symbol) |> ignore
        agentCmd.Parameters.AddWithValue("$symbol", symbol) |> ignore
        agentCmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o")) |> ignore
        agentCmd.ExecuteNonQuery() |> ignore

        use tokenCmd = conn.CreateCommand()
        tokenCmd.Transaction <- tx
        tokenCmd.CommandText <-
            """
            INSERT INTO api_tokens (agent_id, token, created_at)
            VALUES ($agentId, $token, $createdAt)
            ON CONFLICT(agent_id) DO UPDATE SET token = excluded.token, created_at = excluded.created_at;
            """
        tokenCmd.Parameters.AddWithValue("$agentId", symbol) |> ignore
        tokenCmd.Parameters.AddWithValue("$token", token) |> ignore
        tokenCmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o")) |> ignore
        tokenCmd.ExecuteNonQuery() |> ignore

        tx.Commit()
    }

let loadStoredAgent (dbPath: string) : Async<(string * string) option> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            """
            SELECT agents.symbol, api_tokens.token
            FROM agents
            JOIN api_tokens ON api_tokens.agent_id = agents.id
            ORDER BY agents.created_at DESC
            LIMIT 1;
            """
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            return Some(reader.GetString(0), reader.GetString(1))
        else
            return None
    }
