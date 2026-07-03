module SpaceKids.Server.Persistence.MigrationRunner

open System
open System.IO
open System.Reflection
open System.Text.RegularExpressions
open Microsoft.Data.Sqlite

let private versionOf (resourceName: string) : int =
    let fileName = resourceName.Split('.') |> Array.rev |> Array.item 1 // "...0001_initial", "sql"
    let m = Regex.Match(fileName, @"^(\d+)")
    int m.Value

let private readEmbeddedMigrations () : (int * string * string) list =
    let asm = Assembly.GetExecutingAssembly()
    asm.GetManifestResourceNames()
    |> Array.filter (fun n -> n.EndsWith(".sql"))
    |> Array.sort
    |> Array.map (fun name ->
        use stream = asm.GetManifestResourceStream(name)
        use reader = new StreamReader(stream)
        versionOf name, name, reader.ReadToEnd())
    |> Array.toList

let private tableExists (conn: SqliteConnection) (tableName: string) : bool =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $name;"
    cmd.Parameters.AddWithValue("$name", tableName) |> ignore
    cmd.ExecuteScalar() <> null

let private appliedVersions (conn: SqliteConnection) : Set<int> =
    if tableExists conn "schema_versions" then
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT version FROM schema_versions;"
        use reader = cmd.ExecuteReader()
        [ while reader.Read() do yield reader.GetInt32(0) ] |> Set.ofList
    else
        Set.empty

/// Applies any embedded Migrations/*.sql not yet recorded in schema_versions,
/// each in its own transaction. Safe to call on every startup.
let run (dbPath: string) : unit =
    use conn = Database.openConnection dbPath

    // journal_mode is persisted in the database file and cannot be changed
    // inside a transaction, so it's set here rather than in a migration script.
    (use walCmd = conn.CreateCommand()
     walCmd.CommandText <- "PRAGMA journal_mode = WAL;"
     walCmd.ExecuteNonQuery() |> ignore)

    let applied = appliedVersions conn

    for version, _name, sql in readEmbeddedMigrations () do
        if not (applied.Contains version) then
            use tx = conn.BeginTransaction()

            use runCmd = conn.CreateCommand()
            runCmd.Transaction <- tx
            runCmd.CommandText <- sql
            runCmd.ExecuteNonQuery() |> ignore

            use recordCmd = conn.CreateCommand()
            recordCmd.Transaction <- tx
            recordCmd.CommandText <- "INSERT INTO schema_versions (version, applied_at) VALUES ($version, $appliedAt);"
            recordCmd.Parameters.AddWithValue("$version", version) |> ignore
            recordCmd.Parameters.AddWithValue("$appliedAt", DateTime.UtcNow.ToString("o")) |> ignore
            recordCmd.ExecuteNonQuery() |> ignore

            tx.Commit()
