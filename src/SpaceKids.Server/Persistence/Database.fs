module SpaceKids.Server.Persistence.Database

open System.IO
open Microsoft.Data.Sqlite

/// The real, permanent database file (Milestone 0's `spacekids.spike.db` is gone).
let defaultDbPath = Path.Combine(Directory.GetCurrentDirectory(), "spacekids.db")

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
