module SpaceKids.FakeSpaceTraders.EntryPoint

open Microsoft.AspNetCore.Builder

/// Marker type only — the standard workaround so `WebApplicationFactory<Program>`
/// (IntegrationTests) can locate this assembly's entry point. F# minimal-hosting
/// `[<EntryPoint>] let main` doesn't generate a public `Program` class the way C#
/// top-level statements do.
type Program() =
    class end

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)
    let app = builder.Build()
    App.configureApp app |> ignore
    app.Run()
    0
