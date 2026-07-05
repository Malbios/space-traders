module Tests

open System
open System.IO
open Microsoft.Data.Sqlite
open Xunit
open SpaceKids.Core.Dsl
open SpaceKids.Server
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

// --- CustomBlockRepository (Milestone 9/Part B, §9/§12) --------------------------------

let private aCompiledBlock (inputs: string list) (output: string option) : CompiledCustomBlock =
    { signature =
        { inputs = inputs |> List.map (fun n -> { name = n; inputType = "Zahl" })
          output = output
          outputFields = None }
      instructions = []
      returnExpr = None }

[<Fact>]
let ``custom block repository round-trips a saved definition, always returning the latest version`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let id = CustomBlockRepository.insert dbPath "Verdopple" None |> Async.RunSynchronously

        let v1 =
            CustomBlockRepository.saveVersion dbPath id """{"blocks":{}}""" (aCompiledBlock [ "Zahl" ] (Some "Zahl"))
            |> Async.RunSynchronously

        Assert.Equal(1, v1)

        let v2 =
            CustomBlockRepository.saveVersion
                dbPath
                id
                """{"blocks":{"v":2}}"""
                (aCompiledBlock [ "Zahl"; "Zahl2" ] (Some "Zahl"))
            |> Async.RunSynchronously

        Assert.Equal(2, v2)

        match CustomBlockRepository.load dbPath id |> Async.RunSynchronously with
        | Some definition ->
            Assert.Equal(2, definition.signature.inputs.Length)
            Assert.Equal("""{"blocks":{"v":2}}""", definition.workspaceJson)
        | None -> Assert.Fail("expected the block to load")
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``custom block repository lists and renames blocks`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let id = CustomBlockRepository.insert dbPath "Baue Erz ab" None |> Async.RunSynchronously
        CustomBlockRepository.saveVersion dbPath id "{}" (aCompiledBlock [] None) |> Async.RunSynchronously |> ignore

        let before = CustomBlockRepository.list dbPath |> Async.RunSynchronously
        Assert.Contains(before, fun b -> b.id = id && b.name = "Baue Erz ab" && b.version = 1)

        CustomBlockRepository.rename dbPath id "Baue Rohstoffe ab" |> Async.RunSynchronously

        let after = CustomBlockRepository.list dbPath |> Async.RunSynchronously
        Assert.Contains(after, fun b -> b.id = id && b.name = "Baue Rohstoffe ab")
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``findUsages is empty for a custom block nothing references`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let id = CustomBlockRepository.insert dbPath "Unbenutzt" None |> Async.RunSynchronously
        CustomBlockRepository.saveVersion dbPath id "{}" (aCompiledBlock [] None) |> Async.RunSynchronously |> ignore

        let usages = CustomBlockRepository.findUsages dbPath id |> Async.RunSynchronously
        Assert.Empty(usages)
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``findUsages lists a program that references the custom block`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let id = CustomBlockRepository.insert dbPath "Baue Erz ab" None |> Async.RunSynchronously
        CustomBlockRepository.saveVersion dbPath id "{}" (aCompiledBlock [] None) |> Async.RunSynchronously |> ignore

        WorkspaceRepository.save dbPath "blockly-spike" "{}" |> Async.RunSynchronously

        let program: CompiledProgram =
            { version = 1
              customBlocks = Map [ id, aCompiledBlock [] None ]
              instructions = [] }

        ProgramRepository.insert dbPath "blockly-spike" (JobStateJson.serializeProgram program)
        |> Async.RunSynchronously
        |> ignore

        let usages = CustomBlockRepository.findUsages dbPath id |> Async.RunSynchronously
        Assert.Contains(usages, fun u -> u.Contains("Programm"))
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``findUsages lists another custom block that calls it`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let innerId = CustomBlockRepository.insert dbPath "Innen" None |> Async.RunSynchronously
        CustomBlockRepository.saveVersion dbPath innerId "{}" (aCompiledBlock [] None) |> Async.RunSynchronously |> ignore

        let outerId = CustomBlockRepository.insert dbPath "Aussen" None |> Async.RunSynchronously

        let outerCompiled =
            { signature = { inputs = []; output = None; outputFields = None }
              instructions = [ CallCustomBlock("c1", innerId, Map.empty, None) ]
              returnExpr = None }

        CustomBlockRepository.saveVersion dbPath outerId "{}" outerCompiled |> Async.RunSynchronously |> ignore

        let usages = CustomBlockRepository.findUsages dbPath innerId |> Async.RunSynchronously
        Assert.Contains(usages, fun u -> u.Contains("Aussen"))
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``delete removes the custom block and its versions`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let id = CustomBlockRepository.insert dbPath "Wegwerfen" None |> Async.RunSynchronously
        CustomBlockRepository.saveVersion dbPath id "{}" (aCompiledBlock [] None) |> Async.RunSynchronously |> ignore

        CustomBlockRepository.delete dbPath id |> Async.RunSynchronously

        let loaded = CustomBlockRepository.load dbPath id |> Async.RunSynchronously
        Assert.True(loaded.IsNone)
    finally
        deleteDbFiles dbPath

// --- ProgramRepository (saved/named multiple-program library) -------------------------

[<Fact>]
let ``create makes a named program with a blank workspace ready to save into`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let id = ProgramRepository.create dbPath "Mein erstes Programm" |> Async.RunSynchronously

        let workspaceJson = WorkspaceRepository.load dbPath id |> Async.RunSynchronously
        Assert.True(workspaceJson.IsSome)

        let programs = ProgramRepository.list dbPath |> Async.RunSynchronously
        Assert.Contains(programs, fun p -> p.id = id && p.name = "Mein erstes Programm")
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``rename updates the program's name in list`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let id = ProgramRepository.create dbPath "Altbezeichnung" |> Async.RunSynchronously
        ProgramRepository.rename dbPath id "Neubezeichnung" |> Async.RunSynchronously

        let programs = ProgramRepository.list dbPath |> Async.RunSynchronously
        Assert.Contains(programs, fun p -> p.id = id && p.name = "Neubezeichnung")
        Assert.DoesNotContain(programs, fun p -> p.name = "Altbezeichnung")
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``delete succeeds for a program with no active job`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let id = ProgramRepository.create dbPath "Unbenutzt" |> Async.RunSynchronously

        match ProgramRepository.delete dbPath Locale.De id |> Async.RunSynchronously with
        | Ok() -> ()
        | Error message -> Assert.Fail($"expected deletion to succeed, got: {message}")

        let programs = ProgramRepository.list dbPath |> Async.RunSynchronously
        Assert.DoesNotContain(programs, fun p -> p.id = id)
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``delete succeeds for a program whose only job history is terminal`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let id = ProgramRepository.create dbPath "Fertig geflogen" |> Async.RunSynchronously
        let programSnapshotId = ProgramRepository.insert dbPath id "{}" |> Async.RunSynchronously

        JobRepository.insert dbPath "job-1" programSnapshotId "FAKE-AGENT-1" "Completed" "{}" None
        |> Async.RunSynchronously

        match ProgramRepository.delete dbPath Locale.De id |> Async.RunSynchronously with
        | Ok() -> ()
        | Error message -> Assert.Fail($"expected deletion to succeed despite terminal history, got: {message}")
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``delete is refused while a non-terminal job is flying the program`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let id = ProgramRepository.create dbPath "Gerade aktiv" |> Async.RunSynchronously
        let programSnapshotId = ProgramRepository.insert dbPath id "{}" |> Async.RunSynchronously

        JobRepository.insert dbPath "job-2" programSnapshotId "FAKE-AGENT-1" "Running" "{}" None
        |> Async.RunSynchronously

        match ProgramRepository.delete dbPath Locale.De id |> Async.RunSynchronously with
        | Error message -> Assert.Contains("Pilot", message)
        | Ok() -> Assert.Fail("expected deletion to be refused while a pilot is active")

        let programs = ProgramRepository.list dbPath |> Async.RunSynchronously
        Assert.Contains(programs, fun p -> p.id = id)
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``delete refusal message is English when the locale is English (Milestone 12)`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let id = ProgramRepository.create dbPath "Active" |> Async.RunSynchronously
        let programSnapshotId = ProgramRepository.insert dbPath id "{}" |> Async.RunSynchronously

        JobRepository.insert dbPath "job-3" programSnapshotId "FAKE-AGENT-1" "Running" "{}" None
        |> Async.RunSynchronously

        match ProgramRepository.delete dbPath Locale.En id |> Async.RunSynchronously with
        | Error message -> Assert.Contains("pilot", message)
        | Ok() -> Assert.Fail("expected deletion to be refused while a pilot is active")
    finally
        deleteDbFiles dbPath

// --- Structural-mismatch check, real call site (§9/§15) --------------------------------

let private aProgramCallingCustomBlock (customBlockId: string) (compiledBlock: CompiledCustomBlock) : CompiledProgram =
    { version = 1
      customBlocks = Map [ customBlockId, compiledBlock ]
      instructions = [] }

[<Fact>]
let ``staleWarnings is empty when nothing referenced has changed since the program was last run`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let blockId = CustomBlockRepository.insert dbPath "Verdopple" None |> Async.RunSynchronously
        let compiledBlock = aCompiledBlock [ "Zahl" ] (Some "Zahl")
        CustomBlockRepository.saveVersion dbPath blockId "{}" compiledBlock |> Async.RunSynchronously |> ignore

        let programId = ProgramRepository.create dbPath "Nutzt Verdopple" |> Async.RunSynchronously
        let snapshotJson = JobStateJson.serializeProgram (aProgramCallingCustomBlock blockId compiledBlock)
        ProgramRepository.insert dbPath programId snapshotJson |> Async.RunSynchronously |> ignore

        let warnings = ProgramRemoting.staleWarnings dbPath Locale.De programId |> Async.RunSynchronously
        Assert.Empty(warnings)
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``staleWarnings flags a program whose custom block signature has since changed`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let blockId = CustomBlockRepository.insert dbPath "Verdopple" None |> Async.RunSynchronously
        let originalBlock = aCompiledBlock [ "Zahl" ] (Some "Zahl")
        CustomBlockRepository.saveVersion dbPath blockId "{}" originalBlock |> Async.RunSynchronously |> ignore

        let programId = ProgramRepository.create dbPath "Nutzt Verdopple" |> Async.RunSynchronously
        let snapshotJson = JobStateJson.serializeProgram (aProgramCallingCustomBlock blockId originalBlock)
        ProgramRepository.insert dbPath programId snapshotJson |> Async.RunSynchronously |> ignore

        // The block gains a second input after the program's snapshot was taken.
        let changedBlock = aCompiledBlock [ "Zahl"; "Anzahl" ] (Some "Zahl")
        CustomBlockRepository.saveVersion dbPath blockId "{}" changedBlock |> Async.RunSynchronously |> ignore

        let warnings = ProgramRemoting.staleWarnings dbPath Locale.De programId |> Async.RunSynchronously
        Assert.Single(warnings) |> ignore
        Assert.Contains(blockId, warnings.[0])
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``staleWarnings message is English when the locale is English (Milestone 12)`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let blockId = CustomBlockRepository.insert dbPath "Double" None |> Async.RunSynchronously
        let originalBlock = aCompiledBlock [ "Zahl" ] (Some "Zahl")
        CustomBlockRepository.saveVersion dbPath blockId "{}" originalBlock |> Async.RunSynchronously |> ignore

        let programId = ProgramRepository.create dbPath "Uses Double" |> Async.RunSynchronously
        let snapshotJson = JobStateJson.serializeProgram (aProgramCallingCustomBlock blockId originalBlock)
        ProgramRepository.insert dbPath programId snapshotJson |> Async.RunSynchronously |> ignore

        let changedBlock = aCompiledBlock [ "Zahl"; "Anzahl" ] (Some "Zahl")
        CustomBlockRepository.saveVersion dbPath blockId "{}" changedBlock |> Async.RunSynchronously |> ignore

        let warnings = ProgramRemoting.staleWarnings dbPath Locale.En programId |> Async.RunSynchronously
        let warning = Assert.Single(warnings)
        Assert.Contains("has changed", warning)
    finally
        deleteDbFiles dbPath

[<Fact>]
let ``staleWarnings is empty for a program that has never been run`` () =
    let dbPath = tempDbPath ()
    try
        MigrationRunner.run dbPath
        let programId = ProgramRepository.create dbPath "Noch nie geflogen" |> Async.RunSynchronously

        let warnings = ProgramRemoting.staleWarnings dbPath Locale.De programId |> Async.RunSynchronously
        Assert.Empty(warnings)
    finally
        deleteDbFiles dbPath
