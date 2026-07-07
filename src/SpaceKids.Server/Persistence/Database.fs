module SpaceKids.Server.Persistence.Database

open System
open System.IO
open Microsoft.Data.Sqlite

let private isTestHost () =
    AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase)

/// True for temp/isolated DB files that tests are allowed to write.
let private isClearlyTestDb (fullPath: string) =
    let file = Path.GetFileName(fullPath)

    file.Contains(".test-spacekids", StringComparison.OrdinalIgnoreCase)
    || file.StartsWith("spacekids-test-", StringComparison.OrdinalIgnoreCase)
    || file.StartsWith("spacekids-job-test-", StringComparison.OrdinalIgnoreCase)
    || file.StartsWith("spacekids-agent-test-", StringComparison.OrdinalIgnoreCase)
    || file.StartsWith("spacekids-testhost", StringComparison.OrdinalIgnoreCase)

let private resolveDefaultPath () =
    match Environment.GetEnvironmentVariable("SPACEKIDS_DB_PATH") with
    | null | "" -> Path.Combine(Directory.GetCurrentDirectory(), "spacekids.db")
    | overridePath -> overridePath

let private isolatedTestFallback () =
    Path.Combine(Path.GetTempPath(), "spacekids-testhost-isolated.db")

/// The real, permanent database file (Milestone 0's `spacekids.spike.db` is gone).
/// Overridable via `SPACEKIDS_DB_PATH` so live/manual verification runs (e.g. a
/// Playwright script driving the real server binary against the fake API) can
/// point at a throwaway file instead of silently sharing -- and corrupting --
/// whatever real agent/token data lives in the default path.
///
/// Under `dotnet test`, never binds to `src/SpaceKids.Server/spacekids.db` (or any
/// other non-test path) on first access — Server assemblies can load before test
/// bootstrap code runs. Set `SPACEKIDS_ALLOW_LIVE_DB=1` for the rare intentional
/// live-DB test run.
let defaultDbPath =
    let path = Path.GetFullPath(resolveDefaultPath())

    if
        isTestHost ()
        && Environment.GetEnvironmentVariable("SPACEKIDS_ALLOW_LIVE_DB") <> "1"
        && not (isClearlyTestDb path)
    then
        let fallback = isolatedTestFallback ()
        Environment.SetEnvironmentVariable("SPACEKIDS_DB_PATH", fallback) |> ignore
        fallback
    else
        path

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