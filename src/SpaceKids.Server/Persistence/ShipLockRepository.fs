module SpaceKids.Server.Persistence.ShipLockRepository

open System

type private ExistingLock = { jobId: string; leaseExpiresAt: DateTimeOffset }

let private readLock (conn: Microsoft.Data.Sqlite.SqliteConnection) (shipSymbol: string) : ExistingLock option =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT job_id, lease_expires_at FROM ship_locks WHERE ship_symbol = $shipSymbol;"
    cmd.Parameters.AddWithValue("$shipSymbol", shipSymbol) |> ignore
    use reader = cmd.ExecuteReader()

    if reader.Read() then
        Some { jobId = reader.GetString(0); leaseExpiresAt = DateTimeOffset.Parse(reader.GetString(1)) }
    else
        None

let private rollbackQuietly (conn: Microsoft.Data.Sqlite.SqliteConnection) : unit =
    try
        use rollbackCmd = conn.CreateCommand()
        rollbackCmd.CommandText <- "ROLLBACK;"
        rollbackCmd.ExecuteNonQuery() |> ignore
    with _ ->
        ()

let private upsert (conn: Microsoft.Data.Sqlite.SqliteConnection) (shipSymbol: string) (jobId: string) (leaseExpiresAt: DateTimeOffset) : unit =
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

/// Check-on-acquire lease reclaim (§14): acquires `shipSymbol`'s lock for `jobId`.
/// - No existing lock, or an existing lock already owned by `jobId` (a resuming job
///   refreshing its own lease) -> acquired, `None` (nothing reclaimed).
/// - An existing lock owned by a *different* job whose lease has expired -> reclaimed;
///   returns `Some orphanedJobId` so the caller can pause that job with a German
///   explanation, per §14.
/// - An existing, still-live lock owned by a different job -> `Error existingJobId`;
///   the caller renders "Schiff {symbol} wird bereits von einem anderen Programm
///   gesteuert."
///
/// The read-then-write decision is wrapped in one `BEGIN IMMEDIATE` transaction on a
/// single connection: `BEGIN IMMEDIATE` takes SQLite's write lock up front (rather
/// than at the first write, as a plain/deferred transaction would), so a second
/// concurrent `tryAcquire` for the same ship blocks on `Database.openConnection`'s own
/// `busy_timeout` until this one commits, instead of both reading "no lock yet" and
/// both believing they won it — a real race found in review, since two separate
/// un-transacted calls (the previous shape) had no such serialization.
let tryAcquire
    (dbPath: string)
    (shipSymbol: string)
    (jobId: string)
    (leaseSeconds: float)
    : Async<Result<string option, string>> =
    async {
        use conn = Database.openConnection dbPath
        use beginCmd = conn.CreateCommand()
        beginCmd.CommandText <- "BEGIN IMMEDIATE;"
        beginCmd.ExecuteNonQuery() |> ignore

        try
            let existing = readLock conn shipSymbol
            let leaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds)

            let result =
                match existing with
                | None ->
                    upsert conn shipSymbol jobId leaseExpiresAt
                    Ok None
                | Some lock when lock.jobId = jobId ->
                    upsert conn shipSymbol jobId leaseExpiresAt
                    Ok None
                | Some lock when lock.leaseExpiresAt < DateTimeOffset.UtcNow ->
                    upsert conn shipSymbol jobId leaseExpiresAt
                    Ok(Some lock.jobId)
                | Some lock -> Error lock.jobId

            use commitCmd = conn.CreateCommand()
            commitCmd.CommandText <- "COMMIT;"
            commitCmd.ExecuteNonQuery() |> ignore

            return result
        with ex ->
            rollbackQuietly conn
            return raise ex
    }

/// Extends a lock's lease without changing ownership — called every scheduler tick
/// for a job that's still active, so its lease never expires while genuinely running.
/// Guarded by `job_id = $jobId` (a plain `UPDATE`, not an upsert): if ownership already
/// moved on to a different job (e.g. this job's lease expired and a new job legitimately
/// reclaimed it via `tryAcquire` before this call ran), this is a silent no-op instead of
/// resurrecting a lock transfer that had already correctly happened — a real race found
/// in review (a stale in-flight tick calling this after the ship was reassigned).
let refreshLease (dbPath: string) (shipSymbol: string) (jobId: string) (leaseSeconds: float) : Async<unit> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "UPDATE ship_locks SET lease_expires_at = $leaseExpiresAt WHERE ship_symbol = $shipSymbol AND job_id = $jobId;"
        cmd.Parameters.AddWithValue("$leaseExpiresAt", DateTimeOffset.UtcNow.AddSeconds(leaseSeconds).ToString("o")) |> ignore
        cmd.Parameters.AddWithValue("$shipSymbol", shipSymbol) |> ignore
        cmd.Parameters.AddWithValue("$jobId", jobId) |> ignore
        cmd.ExecuteNonQuery() |> ignore
    }

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
