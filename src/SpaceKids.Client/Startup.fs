namespace SpaceKids.Client

open Microsoft.AspNetCore.Components.WebAssembly.Hosting
open Bolero.Remoting.Client

// Lets `SpaceKids.Client.Tests` unit-test the pure, dependency-free helpers in
// `Main.fs` (map math, pilot-name hashing) directly via `internal` rather than
// `private` — no Blazor rendering/component-testing framework needed for those.
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SpaceKids.Client.Tests")>]
do ()

module Program =

    [<EntryPoint>]
    let Main args =
        let builder = WebAssemblyHostBuilder.CreateDefault(args)
        builder.RootComponents.Add<Main.MyApp>("#main")
        builder.Services.AddBoleroRemoting(builder.HostEnvironment) |> ignore
        builder.Build().RunAsync() |> ignore
        0
