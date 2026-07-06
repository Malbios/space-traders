module SpaceKids.Server.Persistence.Database

open System.IO
open Microsoft.Data.Sqlite

/// The real, permanent database file (Milestone 0's `spacekids.spike.db` is gone).
/// Overridable via `SPACEKIDS_DB_PATH` so live/manual verification runs (e.g. a
/// Playwright script driving the real server binary against the fake API) can
/// point at a throwaway file instead of silently sharing -- and corrupting --
/// whatever real agent/token data lives in the default path.
let defaultDbPath =
    match System.Environment.GetEnvironmentVariable("SPACEKIDS_DB_PATH") with
    | null | "" -> Path.Combine(Directory.GetCurrentDirectory(), "spacekids.db")
    | overridePath -> overridePath

let connectionString (dbPath: string) = $"Data Source={dbPath}"

/// busy_timeout is a per-connection setting (unlike journal_mode=WAL, which is
/// persisted in the database file itself) — every connection must set it.
let openConnection (dbPath: string) : SqliteConnection =
    let conn = new SqliteConnection(connectionString dbPath)
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "PRAGMA busy_timeout = 5000;"
    cmd.ExecuteNonQuery() |> ignore
    conn
