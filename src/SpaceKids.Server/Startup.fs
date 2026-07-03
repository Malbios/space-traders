module SpaceKids.Server.Program

open System
open Microsoft.AspNetCore
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Bolero
open Bolero.Remoting.Server
open Bolero.Server
open SpaceKids
open SpaceKids.SpaceTraders
open Bolero.Templating.Server

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)

    builder.Services.AddAuthorization()
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie()
    |> ignore
    // MapFallbackToBolero renders the page server-side via IHtmlHelper, which needs the
    // MVC view-rendering services registered even though this app has no controllers/views.
    builder.Services.AddControllersWithViews() |> ignore
    builder.Services.AddBoleroComponents() |> ignore
    builder.Services.AddBoleroRemoting<WorkspaceRemoting.WorkspaceRemoteHandler>() |> ignore
    builder.Services.AddBoleroRemoting<AgentRemoting.AgentRemoteHandler>() |> ignore
    builder.Services.AddHttpClient<SpaceTradersClient>(fun client ->
        client.BaseAddress <- Uri(builder.Configuration["SpaceTraders:BaseUrl"]))
    |> ignore
    builder.Services.AddHostedService<Persistence.Backup.BackupService>() |> ignore
#if DEBUG
    builder.Services.AddHotReload(templateDir = __SOURCE_DIRECTORY__ + "/../SpaceKids.Client") |> ignore
#endif

    Persistence.MigrationRunner.run Persistence.Database.defaultDbPath

    let app = builder.Build()

    if app.Environment.IsDevelopment() then
        app.UseWebAssemblyDebugging()

    // Classic Blazor WebAssembly hosted model (not the .NET 8+ "Blazor Web App" unified
    // render-mode model — see docs/decisions.md for why: the stock Bolero template mixes
    // the two incompatibly, and `_framework/blazor.web.js` never resolves as a result,
    // even after extensive troubleshooting of the NuGet/MSBuild static-assets pipeline).
    // UseBlazorFrameworkFiles serves the Client's wwwroot output (blazor.webassembly.js,
    // the WASM payload) at `_framework/*`; the Client's own WebAssemblyHostBuilder
    // (Startup.fs) mounts SpaceKids.Client.Main.MyApp into the "#main" div from Index.fs.
    app
        .UseBlazorFrameworkFiles()
        .UseAuthentication()
        .UseStaticFiles()
        .UseRouting()
        .UseAuthorization()
    |> ignore

#if DEBUG
    app.UseHotReload()
#endif
    app.MapBoleroRemoting() |> ignore
    app.MapFallbackToBolero(Index.page) |> ignore

    app.Run()
    0
