module Tests

open System
open System.IO
open Microsoft.Data.Sqlite
open Xunit
open SpaceKids.Server.Persistence

let private tempDbPath () =
    Path.Combine(Path.GetTempPath(), $"spacekids-test-{Guid.NewGuid()}.db")

let private tempBackupsDir () =
    Path.Combine(Path.GetTempPath(), $"spacekids-test-backups-{Guid.NewGuid()}")

let private deleteDbFiles (dbPath: string) =
    // Microsoft.Data.Sqlite pools native connections by default, which keeps the file
    // locked on Windows even after every SqliteConnection in this test has been disposed.
    SqliteConnection.ClearAllPools()
    for suffix in [ ""; "-shm"; "-wal" ] do
        let path = dbPath + suffix
        if File.Exists(path) then File.Delete(path)

let private expectedTables =
    [ "schema_versions"; "agents"; "api_tokens"; "workspaces"; "programs"
      "custom_blocks"; "custom_block_versions"; "jobs"; "job_logs"
      "ship_locks"; "api_cache"; "request_queue_events" ]

let private tableNames (conn: SqliteConnection) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT name FROM sqlite_master WHERE type = 'table';"
    use reader = cmd.ExecuteReader()
    [ while reader.Read() do yield reader.GetString(0) ]

[<Fact>]
let ``migrations create all core tables and are idempotent`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        MigrationRunner.run dbPath // second call must not fail or duplicate

        use conn = Database.openConnection dbPath
        let tables = tableNames conn
        for expected in expectedTables do
            Assert.Contains(expected, tables)

        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT COUNT(*) FROM schema_versions WHERE version = 1;"
        Assert.Equal(1L, cmd.ExecuteScalar() :?> int64)
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``migrations enable WAL mode`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA journal_mode;"
        Assert.Equal("wal", (cmd.ExecuteScalar() :?> string).ToLowerInvariant())
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``workspace repository round-trips saved JSON`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let json = """{"blocks":{"languageVersion":0}}"""
        WorkspaceRepository.save dbPath "blockly-spike" json |> Async.RunSynchronously

        let loaded = WorkspaceRepository.load dbPath "blockly-spike" |> Async.RunSynchronously
        Assert.Equal(Some json, loaded)
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``backup produces a file and prunes beyond retention`` () =
    let dbPath = tempDbPath ()
    let backupsDir = tempBackupsDir ()
    try
        MigrationRunner.run dbPath

        Backup.runBackup dbPath backupsDir
        Assert.Single(Directory.GetFiles(backupsDir, "backup_*.db")) |> ignore

        for _ in 1 .. 3 do
            System.Threading.Thread.Sleep(1100) // filenames are second-resolution; force distinct names
            Backup.runBackup dbPath backupsDir

        Backup.pruneOldBackups backupsDir 2
        Assert.Equal(2, Directory.GetFiles(backupsDir, "backup_*.db").Length)
    finally
        deleteDbFiles dbPath
        if Directory.Exists(backupsDir) then Directory.Delete(backupsDir, recursive = true)
