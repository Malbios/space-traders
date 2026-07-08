# Persist the active tab across page reloads via Bolero routing

Status: planned, not yet implemented.

## Context

The app currently has no URL routing at all: `MyApp` (`src/SpaceKids.Client/Main.fs`,
`type MyApp() = inherit ProgramComponent<Model, Message>()`) just runs a plain Elmish
program. Which tab is showing lives only in `Model.activeTab: Tab` (defined at line
~308), and `SwitchTab` (line ~2467) is the only thing that ever changes it — reloading
the page always resets to the hardcoded default (`ProgrammierenTab`, from `initModel`
at line ~523). The user wants the active tab to survive a reload.

Bolero has first-class support for exactly this via `Router.infer`/
`Program.withRouterInfer`, bound to an F# union type decorated with `[<EndPoint "...">]`
per case, plus a `getEndPoint: model -> 'ep` and `makeMessage: 'ep -> msg` pair. The
server already serves any unmatched path through the SPA fallback
(`app.MapFallbackToBolero(Index.page)` in `src/SpaceKids.Server/Startup.fs:72`), so
adding real URL paths like `/piloten`, `/maerkte`, etc. needs no server-side routing
changes — the classic Blazor WASM hosted model already handles this correctly.

The `Tab` union (line 308-317) already models exactly what needs to be in the URL,
1:1, with no associated data, so the plan reuses `Tab` itself as the router's endpoint
type (attaching `[<EndPoint>]` directly to its cases) and reuses the existing
`SwitchTab of Tab` message as the router's `makeMessage` — no parallel "Page" type or
translation layer needed.

**Important wrinkle found during investigation**: `SwitchTab`'s lazy-load logic (lines
2467-2486) only fires data-loading commands (`LoadFactions`, `LoadPublicAgents`,
`LoadGalaxyCatalog`, `RefreshDashboard`) when `model.dashboard.IsSome`. On a cold
reload straight into e.g. `/maerkte`, the router dispatches `SwitchTab MarketsTab`
immediately on startup — before `RefreshDashboard`'s async response has come back, so
`model.dashboard` is still `None` at that moment. Today `SwitchTab` is only ever
triggered by a user's click *after* the dashboard is already loaded, so this gap has
never mattered before; with URL-driven navigation it will, silently leaving those tabs
looking empty after a direct reload. `DashboardLoaded` (line 1870) currently just
merges the dashboard and returns `Cmd.none` — it needs to re-run the same lazy-load
check for whatever tab is currently active once the dashboard becomes available.

## Plan

1. **`src/SpaceKids.Client/Main.fs` — make `Tab` a routable union.** Add `open Bolero`
   (for `EndPointAttribute`) and decorate each case with a plain, locale-independent
   ASCII slug derived from the case name (not the localized `Strings` labels, which
   are user-facing and vary by locale/theme — tying URLs to those would break when the
   locale changes):
   ```fsharp
   type Tab =
       | [<EndPoint "/programmieren">] ProgrammierenTab
       | [<EndPoint "/piloten">] PilotenTab
       | [<EndPoint "/galaxie">] GalaxieTab
       | [<EndPoint "/contracts">] ContractsTab
       | [<EndPoint "/factions">] FactionsTab
       | [<EndPoint "/agents">] AgentsTab
       | [<EndPoint "/markets">] MarketsTab
       | [<EndPoint "/shipyards">] ShipyardsTab
       | [<EndPoint "/settings">] SettingsTab
   ```

2. **Extract the lazy-load decision so both `SwitchTab` and `DashboardLoaded` can use
   it.** Pull the `lazyLoadCmds` computation out of the `SwitchTab` arm (lines
   2468-2484) into a small module-level function, e.g.:
   ```fsharp
   let private lazyLoadCmdsFor (tab: Tab) (model: Model) : Cmd<Message> =
       [ if tab = FactionsTab && model.factionsSnapshot.IsNone && model.dashboard.IsSome then
             Some LoadFactions else None
         if tab = AgentsTab && model.publicAgents.IsNone && model.dashboard.IsSome then
             Some LoadPublicAgents else None
         if (tab = GalaxieTab || tab = MarketsTab || tab = ShipyardsTab) && model.dashboard.IsSome then
             Some LoadGalaxyCatalog else None
         if tab = ContractsTab && model.dashboard.IsSome then Some RefreshDashboard else None ]
       |> List.choose id
       |> List.map Cmd.ofMsg
       |> Cmd.batch
   ```
   `SwitchTab tab` becomes `{ model with activeTab = tab }, lazyLoadCmdsFor tab model`
   (unchanged behavior, just refactored to reuse). Then in `DashboardLoaded`'s arm,
   after computing `merged`, additionally fire `lazyLoadCmdsFor model.activeTab
   { model with dashboard = merged }` batched with the existing `Cmd.none` — so
   whichever tab the router landed on gets its data loaded as soon as the dashboard
   finishes loading, not just on a subsequent manual tab click.

3. **Wire up the router in `MyApp.Program`** (line ~3798): add
   `|> Program.withRouterInfer SwitchTab (fun model -> model.activeTab)` to the
   `Program.mkProgram ... |> ...` pipeline. No `notFound` handling needed —
   `Router.infer` already defaults to "stay on the initial page" for an unrecognized
   URL (confirmed in Bolero's own doc comments: `notFound`/`NotFound`, "If None, don't
   send a message and stay on the initial page"), which here means it just keeps
   `initModel`'s default `ProgrammierenTab`.

4. **Tab buttons need no change.** `tabButton` (line ~3382-3392) already dispatches
   `SwitchTab tab` via `on.click`; once the router is attached, Bolero automatically
   pushes the corresponding URL onto browser history whenever the model transitions to
   a different `getEndPoint` result. Clicking tabs will start updating the address bar
   for free.

## Verification

- `dotnet build` — must succeed (adding `[<EndPoint>]` attributes and the router
  wiring are compile-time changes only).
- `dotnet test` — full suite must stay green; `UpdateTests.fs`'s existing `SwitchTab`
  tests should still pass unchanged since the refactor in step 2 preserves behavior.
- Manual check via the `run` skill: launch the real server on an isolated test DB,
  click into a few tabs (e.g. Markets, Factions), confirm the address bar updates,
  then hard-reload the page and confirm it lands back on the same tab *and* that
  tab's data actually loads (the `DashboardLoaded` reconciliation from step 2 is what
  this specifically checks).
