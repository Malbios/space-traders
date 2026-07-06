module SpaceKids.Server.Persistence.Backup

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting

/// Overridable via `SPACEKIDS_BACKUPS_DIR` for the same reason `Database.defaultDbPath`
/// is overridable via `SPACEKIDS_DB_PATH` -- a live-verification run against a
/// throwaway db shouldn't also litter the real backups directory.
let defaultBackupsDir =
    match Environment.GetEnvironmentVariable("SPACEKIDS_BACKUPS_DIR") with
    | null | "" -> Path.Combine(Directory.GetCurrentDirectory(), "backups")
    | overridePath -> overridePath

let defaultRetainCount = 7

let private backupFileName () =
    $"backup_{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.db"

/// WAL mode means a plain file copy of a live database isn't a consistent
/// snapshot (§12) — VACUUM INTO produces one while the app keeps running.
let runBackup (dbPath: string) (backupsDir: string) : unit =
    Directory.CreateDirectory(backupsDir) |> ignore
    let targetPath = Path.Combine(backupsDir, backupFileName ())
    use conn = Database.openConnection dbPath
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "VACUUM INTO $path;"
    cmd.Parameters.AddWithValue("$path", targetPath) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let pruneOldBackups (backupsDir: string) (retain: int) : unit =
    if Directory.Exists(backupsDir) then
        let files = Directory.GetFiles(backupsDir, "backup_*.db") |> Array.sortDescending
        files
        |> Array.skip (min retain files.Length)
        |> Array.iter File.Delete

let runBackupAndPrune (dbPath: string) (backupsDir: string) (retain: int) : unit =
    runBackup dbPath backupsDir
    pruneOldBackups backupsDir retain

/// Backs up on start, then hourly, then once more on clean shutdown (§12).
type BackupService(dbPath: string, backupsDir: string, retain: int) =
    inherit BackgroundService()

    new() = new BackupService(Database.defaultDbPath, defaultBackupsDir, defaultRetainCount)

    member private this.BaseStopAsync(cancellationToken: CancellationToken) =
        base.StopAsync(cancellationToken)

    override this.ExecuteAsync(stoppingToken: CancellationToken) : Task =
        task {
            use timer = new PeriodicTimer(TimeSpan.FromHours(1.0))
            runBackupAndPrune dbPath backupsDir retain
            try
                let mutable ticked = true
                while ticked do
                    let! t = timer.WaitForNextTickAsync(stoppingToken).AsTask()
                    ticked <- t
                    if ticked then
                        runBackupAndPrune dbPath backupsDir retain
            with :? OperationCanceledException -> ()
        }

    override this.StopAsync(cancellationToken: CancellationToken) : Task =
        task {
            runBackupAndPrune dbPath backupsDir retain
            do! this.BaseStopAsync(cancellationToken)
        }
