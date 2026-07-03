module SpaceKids.Server.RequestQueue

open System
open System.Text.Json
open System.Threading
open SpaceKids.Server.Persistence

/// Milestone 2 stub (§13): route every SpaceTraders call through here so no ad hoc
/// HTTP path exists to rewire later. One request at a time, logged — no priorities or
/// backoff sophistication yet (that's Milestone 5).
let private gate = new SemaphoreSlim(1, 1)

let private logEvent (dbPath: string) (endpoint: string) (requestedAt: DateTime) (status: string) (responseMetadataJson: string option) =
    use conn = Database.openConnection dbPath
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """
        INSERT INTO request_queue_events (requested_at, endpoint, status, response_metadata_json)
        VALUES ($requestedAt, $endpoint, $status, $responseMetadataJson);
        """
    cmd.Parameters.AddWithValue("$requestedAt", requestedAt.ToString("o")) |> ignore
    cmd.Parameters.AddWithValue("$endpoint", endpoint) |> ignore
    cmd.Parameters.AddWithValue("$status", status) |> ignore
    cmd.Parameters.AddWithValue("$responseMetadataJson", responseMetadataJson |> Option.map box |> Option.defaultValue (box DBNull.Value))
    |> ignore
    cmd.ExecuteNonQuery() |> ignore

let enqueue (dbPath: string) (endpoint: string) (call: unit -> Async<'a>) : Async<'a> =
    async {
        do! Async.AwaitTask(gate.WaitAsync())
        let requestedAt = DateTime.UtcNow
        try
            try
                let! result = call ()
                logEvent dbPath endpoint requestedAt "ok" None
                return result
            with ex ->
                logEvent dbPath endpoint requestedAt "error" (Some(JsonSerializer.Serialize({| error = ex.Message |})))
                return raise ex
        finally
            gate.Release() |> ignore
    }
