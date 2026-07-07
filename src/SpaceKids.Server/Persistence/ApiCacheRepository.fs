module SpaceKids.Server.Persistence.ApiCacheRepository

open System

/// Read-through cache for rarely-changing SpaceTraders GET payloads (§12). Dashboard
/// galaxy catalog tolerates staleness; reconciliation/job paths never use this.
/// Returns cached JSON regardless of age — stale-while-revalidate reads use this.
let tryGet (dbPath: string) (cacheKey: string) : Async<(string * DateTimeOffset) option> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT response_json, fetched_at
            FROM api_cache
            WHERE cache_key = $cacheKey;
            """

        cmd.Parameters.AddWithValue("$cacheKey", cacheKey) |> ignore

        use reader = cmd.ExecuteReader()

        if reader.Read() then
            return Some(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1)))
        else
            return None
    }

let tryGetFresh (dbPath: string) (cacheKey: string) (maxAge: TimeSpan) : Async<string option> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT response_json, fetched_at
            FROM api_cache
            WHERE cache_key = $cacheKey;
            """

        cmd.Parameters.AddWithValue("$cacheKey", cacheKey) |> ignore

        use reader = cmd.ExecuteReader()

        if reader.Read() then
            let fetchedAt = DateTimeOffset.Parse(reader.GetString(1))

            if DateTimeOffset.UtcNow - fetchedAt <= maxAge then
                return Some(reader.GetString(0))
            else
                return None
        else
            return None
    }

let put (dbPath: string) (cacheKey: string) (responseJson: string) : Async<unit> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            INSERT INTO api_cache (cache_key, response_json, fetched_at)
            VALUES ($cacheKey, $responseJson, $fetchedAt)
            ON CONFLICT(cache_key) DO UPDATE SET
                response_json = excluded.response_json,
                fetched_at = excluded.fetched_at;
            """

        cmd.Parameters.AddWithValue("$cacheKey", cacheKey) |> ignore
        cmd.Parameters.AddWithValue("$responseJson", responseJson) |> ignore
        cmd.Parameters.AddWithValue("$fetchedAt", DateTimeOffset.UtcNow.ToString("o")) |> ignore
        cmd.ExecuteNonQuery() |> ignore
    }

let deleteKey (dbPath: string) (cacheKey: string) : Async<unit> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "DELETE FROM api_cache WHERE cache_key = $cacheKey;"
        cmd.Parameters.AddWithValue("$cacheKey", cacheKey) |> ignore
        cmd.ExecuteNonQuery() |> ignore
    }

let invalidateForAgent (dbPath: string) (agentSymbol: string) : Async<unit> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "DELETE FROM api_cache WHERE cache_key LIKE $prefix;"
        cmd.Parameters.AddWithValue("$prefix", "galaxy:" + agentSymbol + ":%") |> ignore
        cmd.ExecuteNonQuery() |> ignore
    }