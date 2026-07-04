module SpaceKids.Server.Persistence.ShipLockRepository

open System

type private ExistingLock = { jobId: string; leaseExpiresAt: DateTimeOffset }

let private readLock (dbPath: string) (shipSymbol: string) : Async<ExistingLock option> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT job_id, lease_expires_at FROM ship_locks WHERE ship_symbol = $shipSymbol;"
        cmd.Parameters.AddWithValue("$shipSymbol", shipSymbol) |> ignore
        use reader = cmd.ExecuteReader()

        if reader.Read() then
            return Some { jobId = reader.GetString(0); leaseExpiresAt = DateTimeOffset.Parse(reader.GetString(1)) }
        else
            return None
    }

let private upsert (dbPath: string) (shipSymbol: string) (jobId: string) (leaseExpiresAt: DateTimeOffset) : Async<unit> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            INSERT INTO ship_locks (ship_symbol, job_id, locked_at, lease_expires_at)
            VALUES ($shipSymbol, $jobId, $now, $leaseExpiresAt)
            ON CONFLICT(ship_symbol) DO UPDATE SET
                job_id = excluded.job_id,
                locked_at = excluded.locked_at,
                lease_expires_at = excluded.lease_expires_at;
            """

        cmd.Parameters.AddWithValue("$shipSymbol", shipSymbol) |> ignore
        cmd.Parameters.AddWithValue("$jobId", jobId) |> ignore
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o")) |> ignore
        cmd.Parameters.AddWithValue("$leaseExpiresAt", leaseExpiresAt.ToString("o")) |> ignore
        cmd.ExecuteNonQuery() |> ignore
    }

/// Check-on-acquire lease reclaim (§14): acquires `shipSymbol`'s lock for `jobId`.
/// - No existing lock, or an existing lock already owned by `jobId` (a resuming job
///   refreshing its own lease) -> acquired, `None` (nothing reclaimed).
/// - An existing lock owned by a *different* job whose lease has expired -> reclaimed;
///   returns `Some orphanedJobId` so the caller can pause that job with a German
///   explanation, per §14.
/// - An existing, still-live lock owned by a different job -> `Error existingJobId`;
///   the caller renders "Schiff {symbol} wird bereits von einem anderen Programm
///   gesteuert."
let tryAcquire
    (dbPath: string)
    (shipSymbol: string)
    (jobId: string)
    (leaseSeconds: float)
    : Async<Result<string option, string>> =
    async {
        let! existing = readLock dbPath shipSymbol
        let leaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds)

        match existing with
        | None ->
            do! upsert dbPath shipSymbol jobId leaseExpiresAt
            return Ok None
        | Some lock when lock.jobId = jobId ->
            do! upsert dbPath shipSymbol jobId leaseExpiresAt
            return Ok None
        | Some lock when lock.leaseExpiresAt < DateTimeOffset.UtcNow ->
            do! upsert dbPath shipSymbol jobId leaseExpiresAt
            return Ok(Some lock.jobId)
        | Some lock -> return Error lock.jobId
    }

/// Extends a lock's lease without changing ownership — called every scheduler tick
/// for a job that's still active, so its lease never expires while genuinely running.
let refreshLease (dbPath: string) (shipSymbol: string) (jobId: string) (leaseSeconds: float) : Async<unit> =
    upsert dbPath shipSymbol jobId (DateTimeOffset.UtcNow.AddSeconds(leaseSeconds))

let release (dbPath: string) (shipSymbol: string) : Async<unit> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "DELETE FROM ship_locks WHERE ship_symbol = $shipSymbol;"
        cmd.Parameters.AddWithValue("$shipSymbol", shipSymbol) |> ignore
        cmd.ExecuteNonQuery() |> ignore
    }

/// Low-frequency sweep (§14): locks whose lease has already expired — a job that
/// isn't (or is no longer) in the live in-memory set still gets paused visibly
/// rather than lingering, even with no competing acquirer to trigger reclaim.
let findExpired (dbPath: string) : Async<(string * string) list> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT ship_symbol, job_id FROM ship_locks WHERE lease_expires_at < $now;"
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o")) |> ignore
        use reader = cmd.ExecuteReader()
        return [ while reader.Read() do yield reader.GetString(0), reader.GetString(1) ]
    }
