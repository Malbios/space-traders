module SpaceKids.Server.RequestQueue

open System
open System.Collections.Generic
open System.Net.Http
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open SpaceKids.Server.Persistence
open SpaceKids.SpaceTraders

/// Mirrors `Persistence/JobStateJson.fs`'s own small private `options` value — same
/// two-line pattern, kept local here too, since serializing a DU-shaped `'a` (e.g.
/// `ApiResult`) with vanilla `System.Text.Json` throws without `JsonFSharpConverter`.
let private jsonOptions =
    let o = JsonSerializerOptions()
    o.Converters.Add(JsonFSharpConverter())
    o

/// Milestone 5 (§13/§19): a real priority queue with aging, replacing the Milestone 2
/// `SemaphoreSlim` stub. Still exactly one physical call in flight at a time — the real
/// API is rate-limited per token regardless of how many logical actions are queued, so
/// serializing (rather than adding concurrency) is the correct behavior, not a leftover
/// simplification.
///
/// Full per-action-type reconciliation (deciding whether an ambiguous action already
/// happened, using a pre-call baseline) is explicitly out of scope here — §13 defers it
/// to Milestone 6, once jobs exist to have baselines. This module's job stops at
/// *classifying* a failure as definite/rate-limited/ambiguous/reset/possibly-an-outage.

/// Surfaced to the original caller on a post-send timeout (§13): never auto-retried,
/// since retrying could double-execute an action whose actual outcome is unknown.
exception AmbiguousFailure of message: string

/// Surfaced to the original caller on HTTP 401 (§13): the server has been reset: there
/// is nothing to retry, a new agent/token is required.
exception ServerResetDetected

type QueueEvent =
    { requestedAt: DateTime
      endpoint: string
      status: string
      priority: int
      attempt: int
      requestJson: string option
      responseJson: string option }

type QueueStatus =
    { pendingCount: int
      serverResetDetected: bool
      unreachableSince: DateTime option
      recentEvents: QueueEvent list }

let mutable private maxAttempts = 5
let private agingIntervalSeconds = 5.0
let private minPriority = 2
let private unreachableProbeBaseSeconds = 5.0
let private unreachableProbeMaxSeconds = 60.0

let private backoffMs (attempt: int) = min 10000 (500 * pown 2 attempt)

/// Test-only: production always uses the real bound (5) — lowered in tests so
/// exhausting retries doesn't mean summing several real backoff delays.
let setMaxAttemptsForTests (n: int) = maxAttempts <- n

type private PendingItem =
    { Endpoint: string
      BasePriority: int
      EnqueuedAt: DateTime
      /// Re-runs this logical call's classify-and-maybe-retry logic from scratch;
      /// only re-added to the pending list when the previous attempt looked like an
      /// outage rather than a one-off failure.
      Run: unit -> Async<unit> }

let private pending = List<PendingItem>()
let private pendingLock = obj ()
let private serverResetFlag = ref false
let private unreachableSinceFlag: DateTime option ref = ref None
let private failedProbeCount = ref 0
let private workSignal = new SemaphoreSlim(0)

/// Test-only: this module is a process-wide singleton (one real app, one queue), so
/// tests that exercise it directly must reset state between cases themselves.
let resetForTests () =
    lock pendingLock (fun () -> pending.Clear())
    serverResetFlag.Value <- false
    unreachableSinceFlag.Value <- None
    failedProbeCount.Value <- 0
    maxAttempts <- 5

let private logEvent
    (dbPath: string)
    (endpoint: string)
    (requestedAt: DateTime)
    (priority: int)
    (attempt: int)
    (status: string)
    (requestJson: string option)
    (responseMetadataJson: string option)
    =
    use conn = Database.openConnection dbPath
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """
        INSERT INTO request_queue_events (requested_at, endpoint, status, request_json, response_metadata_json, priority, attempt)
        VALUES ($requestedAt, $endpoint, $status, $requestJson, $responseMetadataJson, $priority, $attempt);
        """
    cmd.Parameters.AddWithValue("$requestedAt", requestedAt.ToString("o")) |> ignore
    cmd.Parameters.AddWithValue("$endpoint", endpoint) |> ignore
    cmd.Parameters.AddWithValue("$status", status) |> ignore
    cmd.Parameters.AddWithValue("$requestJson", requestJson |> Option.map box |> Option.defaultValue (box DBNull.Value))
    |> ignore
    cmd.Parameters.AddWithValue(
        "$responseMetadataJson",
        responseMetadataJson |> Option.map box |> Option.defaultValue (box DBNull.Value)
    )
    |> ignore
    cmd.Parameters.AddWithValue("$priority", priority) |> ignore
    cmd.Parameters.AddWithValue("$attempt", attempt) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private markServerReset (dbPath: string) =
    serverResetFlag.Value <- true
    use conn = Database.openConnection dbPath
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """
        UPDATE agents SET server_reset_detected = 1
        WHERE id = (SELECT id FROM agents ORDER BY created_at DESC LIMIT 1);
        """
    cmd.ExecuteNonQuery() |> ignore

/// Called after a fresh token is accepted (`submitToken`, a fresh agent row) to resume
/// dispatch — recovery from a reset is manual, per §13.
let clearServerReset () = serverResetFlag.Value <- false

let getStatus (dbPath: string) : Async<QueueStatus> =
    async {
        let pendingCount = lock pendingLock (fun () -> pending.Count)
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            """
            SELECT requested_at, endpoint, status, priority, attempt, request_json, response_metadata_json
            FROM request_queue_events
            ORDER BY id DESC
            LIMIT 20;
            """
        use reader = cmd.ExecuteReader()

        let events =
            [ while reader.Read() do
                  yield
                      { requestedAt = DateTime.Parse(reader.GetString(0))
                        endpoint = reader.GetString(1)
                        status = reader.GetString(2)
                        priority = reader.GetInt32(3)
                        attempt = reader.GetInt32(4)
                        requestJson = if reader.IsDBNull(5) then None else Some(reader.GetString(5))
                        responseJson = if reader.IsDBNull(6) then None else Some(reader.GetString(6)) } ]

        return
            { pendingCount = pendingCount
              serverResetDetected = serverResetFlag.Value
              unreachableSince = unreachableSinceFlag.Value
              recentEvents = events }
    }

/// Enqueues one logical call. `priority` is 1 (most urgent) through 5 (least), per
/// §13's levels; a waiting item's *effective* priority improves over time (aging) so a
/// low-priority item can't starve forever behind a stream of high-priority ones.
let enqueue
    (dbPath: string)
    (priority: int)
    (endpoint: string)
    (requestJson: string option)
    (call: unit -> Async<'a>)
    : Async<'a> =
    async {
        let tcs = TaskCompletionSource<'a>()
        let enqueuedAt = DateTime.UtcNow

        let rec attempt (attemptNum: int) : Async<unit> =
            async {
                let requestedAt = DateTime.UtcNow

                try
                    let! result = call ()
                    let responseJson = try Some(JsonSerializer.Serialize(result, jsonOptions)) with _ -> None
                    logEvent dbPath endpoint requestedAt priority attemptNum "ok" requestJson responseJson
                    failedProbeCount.Value <- 0
                    unreachableSinceFlag.Value <- None
                    tcs.SetResult result
                with
                    | SpaceTradersRateLimitException(retryAfterSeconds, _) when attemptNum < maxAttempts ->
                        logEvent dbPath endpoint requestedAt priority attemptNum "rate-limited" requestJson None
                        do! Async.Sleep(max 0 (int (retryAfterSeconds * 1000.0)))
                        return! attempt (attemptNum + 1)
                    | :? HttpRequestException as ex when attemptNum < maxAttempts ->
                        logEvent
                            dbPath
                            endpoint
                            requestedAt
                            priority
                            attemptNum
                            "definite-failure-retry"
                            requestJson
                            (Some(JsonSerializer.Serialize({| error = ex.Message |})))
                        do! Async.Sleep(backoffMs attemptNum)
                        return! attempt (attemptNum + 1)
                    | :? HttpRequestException as ex ->
                        // retries exhausted against what looks like an outage, not a single
                        // bad call — keep the item queued rather than failing the caller.
                        logEvent
                            dbPath
                            endpoint
                            requestedAt
                            priority
                            attemptNum
                            "unreachable"
                            requestJson
                            (Some(JsonSerializer.Serialize({| error = ex.Message |})))
                        unreachableSinceFlag.Value <- Some(unreachableSinceFlag.Value |> Option.defaultValue DateTime.UtcNow)
                        failedProbeCount.Value <- failedProbeCount.Value + 1
                        lock pendingLock (fun () ->
                            pending.Add(
                                { Endpoint = endpoint
                                  BasePriority = priority
                                  EnqueuedAt = enqueuedAt
                                  Run = fun () -> attempt attemptNum }
                            ))
                        workSignal.Release() |> ignore
                    | :? TaskCanceledException as ex ->
                        logEvent
                            dbPath
                            endpoint
                            requestedAt
                            priority
                            attemptNum
                            "ambiguous"
                            requestJson
                            (Some(JsonSerializer.Serialize({| error = ex.Message |})))
                        tcs.SetException(AmbiguousFailure ex.Message)
                    | SpaceTradersApiException(401, _) ->
                        logEvent dbPath endpoint requestedAt priority attemptNum "server-reset" requestJson None
                        markServerReset dbPath
                        tcs.SetException ServerResetDetected
                    | SpaceTradersApiException(statusCode, body) when statusCode >= 500 && attemptNum < maxAttempts ->
                        logEvent
                            dbPath
                            endpoint
                            requestedAt
                            priority
                            attemptNum
                            "server-error-retry"
                            requestJson
                            (Some(JsonSerializer.Serialize({| error = body |})))
                        do! Async.Sleep(backoffMs attemptNum)
                        return! attempt (attemptNum + 1)
                    | SpaceTradersApiException(statusCode, body) when statusCode >= 500 ->
                        logEvent
                            dbPath
                            endpoint
                            requestedAt
                            priority
                            attemptNum
                            "unreachable"
                            requestJson
                            (Some(JsonSerializer.Serialize({| error = body |})))
                        unreachableSinceFlag.Value <- Some(unreachableSinceFlag.Value |> Option.defaultValue DateTime.UtcNow)
                        failedProbeCount.Value <- failedProbeCount.Value + 1
                        lock pendingLock (fun () ->
                            pending.Add(
                                { Endpoint = endpoint
                                  BasePriority = priority
                                  EnqueuedAt = enqueuedAt
                                  Run = fun () -> attempt attemptNum }
                            ))
                        workSignal.Release() |> ignore
                    | ex ->
                        logEvent
                            dbPath
                            endpoint
                            requestedAt
                            priority
                            attemptNum
                            "error"
                            requestJson
                            (Some(JsonSerializer.Serialize({| error = ex.Message |})))
                        tcs.SetException ex
            }

        let item =
            { Endpoint = endpoint
              BasePriority = priority
              EnqueuedAt = enqueuedAt
              Run = fun () -> attempt 0 }

        lock pendingLock (fun () -> pending.Add item)
        workSignal.Release() |> ignore

        return! Async.AwaitTask tcs.Task
    }

let private effectivePriority (now: DateTime) (item: PendingItem) =
    let waited = (now - item.EnqueuedAt).TotalSeconds
    let demotions = int (waited / agingIntervalSeconds)
    max minPriority (item.BasePriority - demotions)

let private pickNext () : PendingItem option =
    lock pendingLock (fun () ->
        if pending.Count = 0 then
            None
        else
            let now = DateTime.UtcNow

            let best =
                pending
                |> Seq.sortBy (fun item -> effectivePriority now item, item.EnqueuedAt)
                |> Seq.head

            pending.Remove(best) |> ignore
            Some best)

/// Test-only: pops and runs the single most-urgent pending item (if any), synchronously
/// from the caller's point of view — lets ordering/aging/retry tests control exactly
/// when a dispatch happens instead of racing a live `Worker`.
let dispatchNextForTests () : Async<bool> =
    async {
        match pickNext () with
        | Some item ->
            do! item.Run()
            return true
        | None -> return false
    }

/// Drains the pending list one item at a time (§13/§19): recomputes each item's
/// effective priority (aging) before every dispatch, and — while the queue believes the
/// server was reset or the API is unreachable — pauses dispatch instead of hammering it.
type Worker() =
    inherit BackgroundService()

    override this.ExecuteAsync(stoppingToken: CancellationToken) : Task =
        task {
            try
                while not stoppingToken.IsCancellationRequested do
                    if serverResetFlag.Value then
                        do! Task.Delay(TimeSpan.FromSeconds(1.0), stoppingToken)
                    else
                        match unreachableSinceFlag.Value with
                        | Some since ->
                            let backoffSeconds =
                                min unreachableProbeMaxSeconds (unreachableProbeBaseSeconds * pown 2.0 failedProbeCount.Value)

                            if (DateTime.UtcNow - since).TotalSeconds < backoffSeconds then
                                do! Task.Delay(TimeSpan.FromSeconds(1.0), stoppingToken)
                            else
                                match pickNext () with
                                | Some item -> do! Async.StartAsTask(item.Run(), cancellationToken = stoppingToken) :> Task
                                | None -> do! Task.Delay(TimeSpan.FromSeconds(1.0), stoppingToken)
                        | None ->
                            match pickNext () with
                            | Some item -> do! Async.StartAsTask(item.Run(), cancellationToken = stoppingToken) :> Task
                            | None ->
                                try
                                    do! workSignal.WaitAsync(TimeSpan.FromSeconds(1.0), stoppingToken) |> Async.AwaitTask |> Async.Ignore
                                with :? OperationCanceledException -> ()
            with :? OperationCanceledException -> ()
        }
