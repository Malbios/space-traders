# Decisions

Record of deliberate, hard-to-reverse calls. Update this file when a decision changes;
don't relitigate a listed decision without recording why.

## Runtime: net10.0, not net8.0

Bolero's NuGet packages ship a `lib/net8.0` binary but no `net9.0`/`net10.0` target. All
projects are pinned to **net10.0** (not net8.0), for a reason specific to this dev
environment: only the .NET 10 SDK/runtime is installed here (no net8.0 shared runtime).
A net10.0 project can still reference net8.0-targeted NuGet packages (Bolero,
Bolero.Server, etc.) — ordinary .NET binary/TFM compatibility, not a special case.

Revisit if Bolero ever publishes a net10.0-targeted build, or if this moves to an
environment with the net8.0 runtime actually installed — net8.0 remains the "intended"
target per Bolero's own docs.

## Hosting model: classic Blazor WebAssembly, not the .NET 8+ unified render-mode model

**This took most of a session to pin down — read this before touching `Startup.fs`,
`Index.fs`, or the Client's `Startup.fs` bootstrap.**

The stock `dotnet new bolero-app` template wires the server (`AddRazorComponents()`
`.AddInteractiveWebAssemblyComponents()` + `MapRazorComponents<Index.Page>()
.AddInteractiveWebAssemblyRenderMode()`) for the .NET 8+ "Blazor Web App" unified
render-mode model, using `boleroScript` to emit the bootstrap tag. On this project's
Bolero version (Server 0.24.39, Templates 0.24.18 — the templates package is stale
relative to the library), that combination never serves `_framework/blazor.web.js`
(the script this hosting model needs): reproduced identically on a completely untouched
fresh scaffold, on both net8.0 and net10.0, with every combination of
`UseStaticFiles`/`MapStaticAssets`/catch-all-vs-exact routes tried. Root cause
(confirmed via the generated `staticwebassets.build.endpoints.json` manifest): the
composed `blazor.web.js` asset is sourced from the `Microsoft.AspNetCore.App.Internal.Assets`
package, brought in as an **implicit** dependency of the Client project by the .NET SDK
— and it never appeared in this project's restored dependency graph, unlike a plain
`dotnet new blazor --interactivity WebAssembly` scaffold where it does. The exact SDK
condition that gates this implicit reference was not identified (`StaticWebAssetProjectMode`
was ruled out — it evaluates to `Default` in both cases) — likely something specific to
how `Microsoft.NET.Sdk.BlazorWebAssembly` decides a project participates in the unified
model, which the Bolero template's Client project apparently doesn't trigger.

**Decision: stop fighting it. Use the classic (pre-.NET8) Blazor WebAssembly hosted
model instead**, which needs none of this:

```txt
Server (Startup.fs):
  AddControllersWithViews()   — MapFallbackToBolero renders the page via IHtmlHelper,
                                 which needs MVC's view-rendering services registered
                                 even though this app has no controllers/views.
  AddBoleroComponents(), AddBoleroRemoting<...>()
  app.UseBlazorFrameworkFiles()   — serves the Client's wwwroot output
                                     (blazor.webassembly.js, the WASM payload) at
                                     _framework/* — a real physical file, unlike
                                     blazor.web.js.
  app.UseStaticFiles() / UseRouting() / UseAuthorization()
  app.MapBoleroRemoting()
  app.MapFallbackToBolero(Index.page)

Server (Index.fs):
  Plain `div { attr.id "main" }` — no `comp<MyApp> { attr.renderMode ... }` marker
    (that's unified-model-only).
  Do NOT use `boleroScript` — it hardcodes `_framework/blazor.web.js` regardless of
    hosting model. Write the bootstrap tag by hand instead:
    `script { attr.src "_framework/blazor.webassembly.js" }`.

Client (Startup.fs):
  `builder.RootComponents.Add<Main.MyApp>("#main")` before `builder.Build()` — this is
    what mounts the app into the div in classic mode (the unified model doesn't need
    this; it discovers the root via the server-rendered marker instead).
```

If Bolero.Templates ever republishes with a fixed scaffold (or the implicit-package-reference
gate gets identified), this whole section can be revisited — but don't casually "clean up"
back to `comp<MyApp>`/`boleroScript`/`MapRazorComponents` without re-verifying end to end
in a browser, not just that the build succeeds.

## Client package pin: `Microsoft.AspNetCore.Components.WebAssembly` 10.0.9

Separate, real bug found while chasing the above: Bolero pins its own transitive
dependency on `Microsoft.AspNetCore.Components.WebAssembly` at **8.0.0**, which skews
against the net10.0 WASM runtime/SDK pack (`Microsoft.NET.Sdk.WebAssembly.Pack`, 10.0.9)
actually driving the build. Symptom: the app loads and `blazor.webassembly.js` starts,
but `dotnet.js`'s dynamic module loader throws `Failed to fetch dynamically imported
module: http://localhost:5290/0` and the WASM runtime never boots — a version-skew bug,
not a hosting-model issue. Fixed by adding a direct `<PackageReference
Include="Microsoft.AspNetCore.Components.WebAssembly" Version="10.0.9" />` in
`SpaceKids.Client.fsproj` so NuGet's nearest-wins resolution picks the matching version
instead of Bolero's older pin. Verify this is still necessary (`grep
"microsoft.aspnetcore.components.webassembly/" obj/project.assets.json` should show
10.0.9, not 8.0.0) whenever Bolero or the target TFM changes.

## Blockly version: 13.1.0 (pinned exact)

**Date:** Milestone 0.

Pinned in `src/SpaceKids.Client/package.json` with an exact version (no `^`/`~` range).
Upgrading this is a deliberate decision — re-run the Milestone 0 Part A/C spike checklist
(§3b, §19) when it happens: re-verify where the procedure blocks live and re-run the
mutator/toolbox-regeneration spike below.

### Where do procedure blocks live at 13.1.0? (§3, §9a)

Checked directly against the installed package (`node_modules/blockly`):

- `Blockly.Blocks["procedures_defnoreturn"]`, `procedures_defreturn`,
  `procedures_callnoreturn`, `procedures_callreturn`, etc. are defined in Blockly
  **core** (`blockly/blocks_compressed.js`), not in a separate plugin.
- `@blockly/block-shareable-procedures` still exists as an npm package, but at this
  version its published version number (13.1.0) tracks core's own — it is not needed as
  a dependency for anything this project does. It was **not** installed.
- Conclusion: at 13.1.0 the split described in the plan (procedure blocks partially
  moved into `@blockly/block-shareable-procedures` around Blockly v10) is not the
  relevant risk for this pin. It doesn't matter either way, because per §9a this project
  never uses Blockly's native procedure blocks — its own definition/caller pair
  (`sk_custom_block_def`, `sk_call_<id>`, see `Blockly/blocks.ts`) is built instead,
  specifically so this kind of churn in Blockly's internals is a non-issue.

Note: `blockly/blocks` (the standard block library — `math_number`, `text`, etc.) must
be imported for side effects (`import "blockly/blocks"`) alongside `blockly/core` —
`blockly/core` alone does not register them, and the toolbox references a couple of them
directly (§ Milestone 0 spike below).

## Milestone 0 spike: what was proven

Verified end-to-end in a real browser (Playwright), not just "it builds": create a
block by dragging from the toolbox, save to SQLite, reload the page, load from SQLite
(same block ID reappears), highlight it, toggle read-only. Zero console errors.

Part A (toolchain, §3b): `package.json` + `esbuild.config.mjs` bundle
`Blockly/blockly-host.ts` (+ Blockly + `blockly/msg/de`) into
`wwwroot/js/blockly-host.js` as a single IIFE `<script>` (not an ES module — simpler to
load from a server-rendered page, no import-map concerns). Wired into `dotnet build` via
two MSBuild targets in `SpaceKids.Client.fsproj` (`NpmInstall`, `BuildBlocklyHostBundle`),
both `BeforeTargets="BeforeBuild"`, both using MSBuild `Inputs`/`Outputs` so they no-op on
an unchanged tree. A fresh checkout builds with plain `dotnet build`/`dotnet publish` —
no separate npm step for a developer or CI to remember.

Part B (seam basics, §3a): `Blockly/blockly-host.ts` is the sole module that touches
`Blockly.*`; it exposes `window.spaceKids.*` (initWorkspace, destroyWorkspace,
loadWorkspace, serializeWorkspace, setReadOnly, highlightBlock, clearHighlight, plus two
spike-only helpers: `firstBlockId`, `getChangeLog`) called from F# via
`IJSRuntime.InvokeVoidAsync`/`InvokeAsync`. `SpaceKids.Client/Main.fs` is a throwaway
spike page (Save/Load/Highlight/Read-only buttons over one workspace) — it is not the
real client UI, which starts in Milestone 3. Save/Load round-trip through a real SQLite
file (`SpaceKids.Server/Persistence.fs`, a `spike_workspaces` table) via a Bolero remote
service (`WorkspaceService`/`WorkspaceRemoteHandler`) — superseded by the real
`workspaces` table design (§12) in Milestone 1.

Event filtering: `onWorkspaceChanged`'s listener only reacts to
`BLOCK_CREATE`/`BLOCK_DELETE`/`BLOCK_CHANGE`/`BLOCK_MOVE`; Blockly only ever emits one
`BLOCK_MOVE` per completed drag (not one per pointer-move frame), so no extra "is this
the end of the drag" bookkeeping was needed on top of the event-type filter. Verify with
`window.spaceKids.getChangeLog(containerId)` in the browser console while dragging a
block around versus creating/deleting one.

Part C (custom-block mutator mini-spike, §9): `Blockly/blocks.ts` implements
`sk_custom_block_def` — a definition shell block with a real mutator (gear icon) that
adds/removes one typed input (`Zahl`/Number, via `sk_custom_block_def_mutator_arg`
sub-blocks), using the modern `saveExtraState`/`loadExtraState` + `decompose`/`compose`
hooks (not the legacy XML mutation API). `window.spaceKids.publishCustomBlockSignature
(defContainerId, targetContainerId, customBlockId)` reads the live signature off a
definition block in one workspace, generates/re-registers a caller block type
(`registerCallerBlock` in `blocks.ts`), and pushes it into a *different* workspace's
toolbox via `Workspace.updateToolbox`. Exercised manually with two `<div>` containers on
the spike page: adding an input in the definition workspace and re-publishing
regenerates the caller with the new input present in the second workspace's "Eigene
Blöcke" category.

This mini-spike deliberately only implements one input type (Zahl/Number). The full typed
input set (Schiff, Wegpunkt, Ware, Anzahl, Preisgrenze, Liste) is Milestone 9 breadth work,
not a new category of risk — the mechanics (mutator, signature storage, caller
regeneration, cross-workspace toolbox push) are what needed proving now.

Verification level reached: the 0-input signature path (definition block → publish →
caller appears in the other workspace's toolbox) was verified fully automated
(Playwright), including re-publishing after the def block's name changes. Adding an
input via the gear icon was verified visually (screenshot: the mutator bubble opens
showing an "Eingaben" container with a draggable "Zahl n" block) rather than fully
scripted end-to-end — Playwright's coordinate-based interaction with Blockly's nested
mini-workspace bubble (non-standard class names, its own SVG canvas) turned into a test-
tooling problem, not a functionality question, and wasn't worth fully automating for a
Milestone 0 spike. If this needs re-verifying later (e.g. before Milestone 9), drive it
manually in a browser: place the def block, click its gear icon, drag a "Zahl" arg block
into the popup, close it, click "Signatur an Programm übergeben", and confirm the caller
in "Eigene Blöcke" now has an input socket.
