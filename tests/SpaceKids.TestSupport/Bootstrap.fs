namespace SpaceKids.TestSupport

open System
open System.IO
open System.Reflection

/// Called from `TestBootstrap.fs` (compiled first) so each test assembly uses its own
/// `.test-spacekids.db` when server code reads `Database.defaultDbPath`.
type TestDbGuard =
    static member EnsureInitialized =
        if
            AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase)
            && Environment.GetEnvironmentVariable("SPACEKIDS_ALLOW_LIVE_DB") <> "1"
        then
            let dir =
                AppDomain.CurrentDomain.GetAssemblies()
                |> Array.tryFind (fun asm ->
                    let name = asm.GetName().Name
                    name.StartsWith("SpaceKids.", StringComparison.Ordinal)
                    && name.EndsWith(".Tests", StringComparison.Ordinal))
                |> Option.map (fun asm -> Path.GetDirectoryName(asm.Location))
                |> Option.defaultWith (fun () -> Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location))

            Environment.SetEnvironmentVariable(
                "SPACEKIDS_DB_PATH",
                Path.Combine(dir, ".test-spacekids.db")
            )
            |> ignore

            Environment.SetEnvironmentVariable(
                "SPACEKIDS_BACKUPS_DIR",
                Path.Combine(dir, ".test-backups")
            )
            |> ignore