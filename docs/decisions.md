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

## Milestone 1: persistence foundation (§12)

**Migrations:** hand-rolled numbered `.sql` files under
`SpaceKids.Server/Persistence/Migrations/`, embedded as resources and applied by
`MigrationRunner.fs` (tracked in a `schema_versions` table, idempotent — safe to call on
every startup). No EF Core/DbUp dependency added — the raw `Microsoft.Data.Sqlite` style
already used by the spike code was kept.

**Schema scope:** `0001_initial.sql` creates the full 12-table set from §12 in one
migration (`agents, api_tokens, workspaces, programs, custom_blocks,
custom_block_versions, jobs, job_logs, ship_locks, api_cache, request_queue_events,
schema_versions`), not just `workspaces`. Only `workspaces` and `schema_versions` are
load-bearing today — the rest sit empty until their own milestone starts writing to
them, and their exact columns are provisional until then. Chosen over growing the schema
one migration per milestone: the shape is fixed once, per the plan, rather than
revisited repeatedly.

**WAL + busy_timeout:** `MigrationRunner.run` sets `PRAGMA journal_mode=WAL` once,
outside any transaction (SQLite silently refuses to change `journal_mode` inside one).
`Database.openConnection` sets `PRAGMA busy_timeout=5000` on every connection, since
busy_timeout — unlike journal_mode — isn't persisted in the database file. Per §12, this
is expected to be sufficient for this project's actual concurrency level; escalate to a
single-writer-owner pattern only if `SQLITE_BUSY` failures actually show up.

**Backups:** `Persistence/Backup.fs`'s `BackupService` (a `BackgroundService`) runs
`VACUUM INTO` immediately on start, then hourly, then once more in `StopAsync` on clean
shutdown, pruning to the last 7 backup files by filename (UTC timestamp, so
lexicographic sort is chronological). VACUUM INTO — not a plain file copy — because WAL
mode means a live file copy isn't a consistent snapshot.

**Spike retirement:** Milestone 0's `spike_workspaces` table and root `Persistence.fs`
are gone. The Milestone 0 spike page (`SpaceKids.Client/Main.fs`) is unchanged and still
works — `WorkspaceRemoting.fs` now backs it with `Persistence/WorkspaceRepository.fs`
against the real `workspaces` table instead.

**Test note:** `Microsoft.Data.Sqlite` pools native connections by default, which keeps a
database file locked on Windows even after every `SqliteConnection` in a test is
disposed. Tests that delete their temp `.db` file in a `finally` block must call
`SqliteConnection.ClearAllPools()` first (see `tests/SpaceKids.Server.Tests/Tests.fs`).

## Milestone 2: real data, no Blockly yet (§19)

**Token flow: paste an existing token, not self-registration.** Confirmed with the user
rather than guessed. `docs.spacetraders.io`'s prose pages (quickstart, API authorization)
are JS-rendered — WebFetch only ever returned navigation shells across several attempts,
never real content — so the current `/register` request/response contract couldn't be
verified safely. `api.spacetraders.io/v2` itself (a plain JSON status endpoint) and
`api.spacetraders.io/v2/documentation/json` (the OpenAPI spec) are *not* JS-rendered and
fetched fine — that's how the `Agent`/`Ship`/`Contract`/`Waypoint`/`Market` field shapes
below were verified. If self-registration is wanted later, get the current `/register`
contract from the user or a real response example rather than re-attempting the docs
site.

**Field shapes:** `SpaceKids.SpaceTraders/Types.fs` defines minimal subsets of the real
schemas (verified field-by-field against the OpenAPI spec) — e.g. `Ship` only carries
`symbol`, `registration.role`, `nav.{systemSymbol,waypointSymbol,status}`,
`fuel.{current,capacity}`, not the full nav/crew/frame/reactor/engine/modules/mounts/
cargo/cooldown shape. Deserialized with `System.Text.Json` +
`PropertyNameCaseInsensitive = true` — no extra JSON library, and unmapped extra fields
in the real API's response are silently ignored, which is exactly what's wanted for a
deliberately partial subset.

**`DataEnvelope<'a>` must be public, not `internal`.** System.Text.Json's default
reflection-based converter only uses a type's constructor if the constructor itself is
accessible (public here); an `internal` record's implicit constructor made deserialization
throw `NotSupportedException` at runtime, not compile time — caught by the integration
tests immediately. If a future envelope/DTO type needs to stay non-public, it would need
a `[<JsonConstructor>]`-annotated public constructor instead.

**Market waypoint assumption:** the market fetched is always the agent's own
headquarters waypoint, not discovered via `Waypoint.traits`. True for most starting
waypoints in this game; a real limitation if a given account's HQ isn't a marketplace.
Waypoint-trait-based market discovery is a natural follow-up, not required for
Milestone 2's "done when."

**Request queue stub (§13):** `SpaceKids.Server/RequestQueue.fs` is a single static
`SemaphoreSlim(1,1)` gate wrapping every SpaceTraders call, logging one row per call to
`request_queue_events` (endpoint name, status, and — on failure — the exception message
as `response_metadata_json`). No priorities, no backoff, no aging — that's Milestone 5.
The important thing locked in now: there is no ad hoc HTTP path anywhere that bypasses
this, so nothing needs rewiring when the real queue lands.

**`SpaceKids.FakeSpaceTraders` testing setup:** F# `[<EntryPoint>] let main` doesn't
generate a public `Program` class the way C# top-level statements do, so
`WebApplicationFactory<T>` (used by `SpaceKids.IntegrationTests`) has nothing to target
by default. Fixed with the standard workaround: a marker `type Program() = class end` in
`Program.fs`, with the actual endpoint wiring factored into `App.configureApp` so both
the marker-carrying entry point and tests can use it. The fake's endpoints are mounted
at bare paths (`/my/agent`, not `/v2/my/agent`) — `WebApplicationFactory`'s default
`HttpClient` base address (`http://localhost/`) has no `/v2` segment to match, and adding
one would only be cosmetic since the client's relative paths resolve against whatever
base address it's given either way.

**Verified live, not just against the fake:** with a real user-provided SpaceTraders
token (never written to any tracked file — the app persists it only in the gitignored
local `spacekids.db`), a real `submitToken` call round-tripped against
`https://api.spacetraders.io/v2/` and returned real agent/ship/waypoint/market data, with
all 5 calls correctly logged in `request_queue_events`. An earlier version of
`AgentRemoting.fs` called `GET /my/agent` twice per `submitToken` (once to validate the
token, once again inside the shared data-loading helper) — caught during this live
verification and fixed by threading the already-fetched `Agent` through instead of
re-fetching it, since every avoidable call against a rate-limited third-party API is
worth avoiding.

## Milestone 3: Blockly in German, full integration (§19)

**Scope-reducing finding: most of §6's 14 "programming" blocks are stock Blockly, not
new custom blocks.** Only the 20 SpaceTraders-specific action/information blocks needed
new `Blockly.Blocks[type]` definitions (`SpaceKids.Client/Blockly/blocks-catalog.ts`).
`Wenn`/`Wenn sonst` are the *same* stock block (`controls_if` — "sonst" is its own
built-in mutator, not a separate type); `Wiederhole` → `controls_repeat_ext`,
`Wiederhole bis` → `controls_whileUntil`, `Für jedes Element` → `controls_forEach`,
`Vergleiche Werte` → `logic_compare`, `Rechne` → `math_arithmetic`,
`Erstelle/Füge-zu/Hole-aus Liste` → `lists_create_with`/`lists_setIndex`/
`lists_getIndex`, `Setze`/`Ändere Variable` → `variables_set`/`math_change`. All are
already registered by the existing `import "blockly/blocks"` and already German via the
existing `blockly/msg/de` locale — no new registration code, just toolbox references.
`Zeige Nachricht`/`Warte` are the Milestone 0 spike's `sk_show_message`/`sk_wait`,
reused unchanged.

**Variables use Blockly's dynamic `{kind: "variables", custom: "VARIABLE"}` toolbox
category** (auto-generates the "create variable" button plus `variables_get`/
`variables_set` blocks matching declared variables) rather than manually listing
`variables_set`. `math_change` ("Ändere Variable") isn't part of that dynamic category
in Blockly core, so it's listed explicitly in the "Programmierung" category instead.

**One toolbox for both workspaces:** `buildCatalogToolbox(callerBlockTypes)` replaced
`buildTrivialToolbox` with the same signature, so it serves both the main "Programm"
workspace and the "Blockwerkstatt" mutator-spike workspace identically — a custom
block's body should have the same primitives available as the main program, and this
avoids inventing a second toolbox variant for no reason. The Milestone 0 Part C mutator
spike (`sk_custom_block_def`, `publishCustomBlockSignature`) is completely untouched;
custom-block *calling* is still Milestone 9 scope.

**"Simulate run" is not DSL execution.** `simulateRun` (`blockly-host.ts`) walks
`getTopBlocks(true)[0]` and its `.getNextBlock()` chain, highlighting each with a 700ms
pause via the existing `highlightBlock`/`clearHighlight`. It has no notion of branches,
loops, or the DSL at all — it proves highlighting works across the full catalog ahead of
Milestone 4's real compiler/interpreter, per the milestone's own "fake/simulated run"
wording.

**Inputs are plain value sockets, not typed ones.** Every catalog block's input (e.g.
`navigate`'s `destination`) accepts any value block for now — dedicated typed sockets
(Schiff/Wegpunkt/Ware/...) are Milestone 9 scope, consistent with the existing note on
the Milestone 0 mutator spike above.

Verified in a real browser (Playwright): all 6 toolbox categories render with correct
German labels (Aktionen, Informationen, Programmierung, Variablen, Eigener Block, Eigene
Blöcke), a `navigate` block drags onto the canvas with its `Wegpunkt` input socket
visible, Speichern → reload → Laden round-trips it through the real `workspaces` table,
"Simuliere Ausführung" completes without error, and the Milestone 2 dashboard section
(unrelated to this milestone) still renders correctly on the same page. Zero console
errors. `tsc --noEmit` (the `typecheck` npm script, not wired into the build — esbuild
doesn't type-check) run explicitly to catch type errors the bundler would silently
swallow.

## Milestone 4: DSL and validation (§10/§11, §19)

**No UI/Server wiring — `SpaceKids.Core/Dsl/` is a pure compiler+validator library,
verified via unit tests only.** This milestone's own bullet list has no button/endpoint
item; Milestone 6+ is where a runner/interpreter UI naturally lands. If a "Kompilieren"
button is wanted sooner, that's a small addition on top of `Compiler.compileWorkspace`.

**Blockly 13.1.0's serialized JSON shape was verified empirically**, not guessed: built
a small real program via Playwright against the running dev server and read back
`window.spaceKids.serializeWorkspace`'s actual output. A block is `{type, id, x, y}`
plus `inputs`/`next`/`fields`/`extraState` only when populated; value/statement inputs
and `next` always nest their target under a `"block"` key (`BlocklyJson.fs`). Exact
per-block-type `extraState` shapes (e.g. `controls_if`'s `elseIfCount`/`hasElse`) were
reasoned from Blockly's documented JSON-mutator migration rather than independently
re-verified per type — worth spot-checking against a real workspace if a specific
control block misbehaves.

**Custom-block calls use a placeholder convention, not the Milestone 0 spike's
`sk_call_<id>` naming.** The compiler recognizes a `callCustomBlock` block type with
`extraState: {"customBlockId": "..."}`, whose value inputs are named exactly like the
signature's parameter names — good enough for Milestone 4's own testing needs
(`Compiler.resolveCustomBlockCall`, tested with an in-memory `Map`-backed lookup) without
coupling this real compiler to the throwaway spike's naming scheme, which Milestone 9
will redesign anyway when it builds the actual Blockwerkstatt UI. Real caller blocks on
a real canvas don't exist yet — that's Milestone 9.

**Custom-block call arguments are compiled leniently, not strictly.** A regular catalog
block's missing required input is a genuine compile-time structural error (`compileInput`),
but a custom-block call's missing argument is *not* — §11 assigns "call arguments match
the signature (arity and types)" to the *validator*, not the compiler, so
`compileCustomBlockCall` uses a separate lenient helper (`compileInputOptional`) that
just omits the key rather than erroring, letting `Validator.validate` produce one focused
arity/type message instead of the compiler's generic "Eingabe fehlt".

**Type-checking custom-block call arguments is a shallow, literal-only heuristic.**
`Expr` carries no static type information, so `Validator`'s arg-type check only catches
the case where a literal's own kind obviously doesn't match the declared input type
(e.g. a string literal passed where "Zahl" is expected) — it can't (and doesn't try to)
type-check a variable/temp/accessor reference. A real type system is out of scope for
this milestone; revisit if this proves too weak once Milestone 9's real typed inputs
(Schiff/Wegpunkt/Ware/...) are in play.

**Scope check is existence-based, not ordering-based.** §11 says "variables exist in the
scope where they are used" — `Validator.checkScope` collects every declared name across
a whole instruction list first, then checks references against that full set, rather
than enforcing "declared before used" at any particular point. A forward reference
inside the same scope is accepted; this matches the plan's own wording and keeps the
check simple.

**No `System.Text.Json` (de)serializer for the DSL types themselves yet** — `Expr`/
`Instruction`/`CompiledProgram` structurally mirror §10's JSON example (same field
names in spirit) but nothing persists this JSON until a later milestone actually reads/
writes `programs.compiled_dsl_json`. Writing a serializer now would be speculative.

**Nesting-depth limit:** custom-block call chains are capped at 20 levels
(`Validator.maxCustomBlockDepth`) — an arbitrary but reasonable "sane maximum," per §11's
own wording; no specific number is mandated by the plan.

Verified via `dotnet test`: 10 new Core.Tests covering a simple action-only compile, the
exact §10 hoisting example (info block inside `Setze Variable` → `InfoRead` + `$t1` +
`SetVariable` referencing `TempRef "$t1"`), nested `controls_repeat_ext`/`sk_wait`,
rejecting an unknown block type and a missing start block, an out-of-scope variable
reference, `resolveCustomBlockCall`'s transitive closure and cycle detection (against an
in-memory fake lookup, not real Blockly JSON for the call site), a custom-block call
missing a required argument, and `revalidateAgainstCurrentDefinitions` catching a
signature that changed after the original compile.

## Milestone 5: request queue (§13/§19)

**Verified live before designing this, not guessed:** hit the real API directly (bad
token) to confirm actual header names (`Retry-After`, `X-RateLimit-Type`,
`X-RateLimit-Limit-Burst`, `X-RateLimit-Limit-Per-Second`, `X-RateLimit-Remaining`,
`X-RateLimit-Reset`) and the real error-body shape (`{"error":{"code":4100,"message":
"...","requestId":"..."}}`, HTTP 401). Confirmed current reset cadence from the base
URL's live status JSON (already fetched in Milestone 2): weekly — German copy says "in
der Regel einmal pro Woche" rather than hardcoding a date that will go stale.

**Scope boundary carried over from §13's own text, not a judgment call made here:** full
per-action-type reconciliation (comparing a pre-call baseline to decide whether an
ambiguous action already happened) is explicitly deferred — §13 says to "budget this as
real per-instruction design work in Milestone 6, not a side effect of the queue's retry
logic." This milestone's job stops at *classifying* a failure as definite/rate-limited/
ambiguous/reset/possibly-an-outage; `AmbiguousFailure` is surfaced to the caller and
nothing further acts on it yet.

**Still exactly one physical call in flight at a time, by design, not as a leftover
simplification.** The real API is rate-limited per token regardless of how many logical
actions are queued, so the priority queue reorders *what* runs next but never adds
concurrency — `RequestQueue.Worker` (a `BackgroundService`, same shape as
`Persistence/Backup.fs`) awaits each dispatched item to completion (including any
in-line retry backoff) before picking the next one.

**Retry classification per §13, implemented as nested exception matches inside
`enqueue`'s recursive `attempt` function:**
- `SpaceTradersRateLimitException` (new, `SpaceKids.SpaceTraders/Client.fs`, raised
  specifically on HTTP 429, reading the real `Retry-After` header) → sleep, bounded
  retry.
- `HttpRequestException` (never reached the server) → bounded retry with exponential
  backoff; once exhausted, treated as a possible outage (below), not a caller-visible
  failure.
- `TaskCanceledException` *after* the request was sent (a client-side timeout) → the
  ambiguous case §13 draws the line at. Never auto-retried — raised to the caller as
  `AmbiguousFailure`.
- `SpaceTradersApiException(401, _)` → server-reset handling (below), not a retry.
- `SpaceTradersApiException(statusCode >= 500, _)` → bounded retry, then the same
  possible-outage handling as exhausted `HttpRequestException`.

**Server-reset detection:** on 401, `agents.server_reset_detected` is set for the most
recently saved agent (persisted, so it survives a restart) and the `Worker` pauses
dispatch entirely (checked first, ahead of the unreachable check) until
`RequestQueue.clearServerReset()` is called — wired into `AgentRemoting.fs`'s
`submitToken` success path, since accepting a fresh token is what a reset actually
requires the child to do.

**API-unreachable state, distinct from a reset:** repeated 5xx or `HttpRequestException`
failures (retries exhausted) don't fail the caller — the item is silently re-added to the
pending list and `unreachableSinceFlag` is set. The `Worker` then probes on a growing
backoff (5s → capped at 60s) instead of hammering; a successful call clears the flag and
resumes normal dispatch. No new retry rules were invented for what was already queued
during the outage — it just sits in `pending` like anything else.

**Fault injection in `SpaceKids.FakeSpaceTraders` (§13a, explicitly deferred out of
Milestone 2, built now):** `POST /_fault/mode` sets a mutable global mode consulted by
all 5 GET endpoints before responding. `unreachable` returns 503 rather than literally
severing the TCP connection — an in-process `WebApplicationFactory`-hosted fake can't do
that, and §13 treats "connection failures / 5xx across the board" the same way anyway, so
503 is the faithful proxy. `drop-after-processing` sleeps 30s, longer than any sane
client `HttpClient.Timeout`, so it surfaces client-side as exactly the post-send
`TaskCanceledException` the ambiguous-failure path needs to be tested against.

**Test-only seams added to `RequestQueue.fs`, not present in any production code path:**
- `resetForTests()` — clears the module's process-wide mutable state (pending list,
  server-reset flag, unreachable-since flag, max-attempts override) between tests, since
  the queue is a deliberate singleton (one real app, one queue) and xunit runs this
  file's tests in the same class/collection (sequentially, not in parallel).
- `dispatchNextForTests()` — pops and runs the single most-urgent pending item
  synchronously from the test's point of view, so ordering/aging/retry tests can control
  exactly when a dispatch happens instead of racing a live `Worker` thread.
- `setMaxAttemptsForTests(n)` — lowers the retry bound (default 5) so a test that drives
  retries to exhaustion doesn't have to sum several real backoff delays.

These exist purely to make the retry/aging/outage logic deterministically testable
without spinning up the full `Worker` background loop for every case; one test
(`a higher-priority item dispatches before a lower-priority one queued first`, plus the
existing FakeSpaceTraders fixture tests) still proves the real client code end-to-end.

**Queue status UI:** a new `QueueService` Bolero remote contract
(`QueueRemoting.fs`/`Main.fs`) exposes `getStatus() : Async<QueueStatusDto>` — pending
count, server-reset/unreachable state, last 20 events. Backing UI is a manual-refresh
"Warteschlange" section (matches this app's existing manual-refresh pattern; no new
push/real-time infrastructure). Verified in a real browser (Playwright): the section
renders on load and the "Aktualisieren" button re-fetches.

Verified via `dotnet test`: 8 new IntegrationTests covering priority ordering over aging
order, aging itself (a long-waiting low-priority item catches up to a newer
higher-priority one), automatic 429 retry, automatic definite-failure retry (a synthetic
`HttpRequestException`, since an in-process fake can't produce a real connection
failure), `drop-after-processing` surfacing as `AmbiguousFailure` without retry, a 401
setting `server_reset_detected` and being clearable, and repeated 5xx marking the queue
unreachable without failing the caller, then recovering once the fault clears — all
alongside the pre-existing FakeSpaceTraders fixture tests, unchanged and still green.

## Milestone 6: runner on the pure scheduler core (§14/§19)

**Scope boundaries agreed before designing, not relitigated mid-implementation:**
fake-only verification (no live SpaceTraders calls — real ship actions burn fuel,
trigger cooldowns, spend/earn real credits); the UI section landed on the existing
single combined page (`Main.fs`), same pattern every prior milestone used; exactly 6
actions in scope (navigate, orbit, dock, extract, buyGood, sellGood) — not
purchaseShip/refuel/acceptContract/deliverContract, not the 9 info-read blocks, not
custom-block calls (Milestone 9). A compiled program that references one of those
fails the job with a clear German message rather than crashing or silently no-op'ing.

**`SpaceKids.Core/Scheduler/` is deliberately independent of `SpaceKids.SpaceTraders`.**
`SpaceKids.SpaceTraders` already depends on `SpaceKids.Core` (an existing, if
previously unused, project reference), so `Core` referencing `SpaceTraders` back would
be circular. More importantly, §14 frames `Core` as the framework/infrastructure-free
domain layer — it shouldn't know about a specific API client's response shapes at all.
`ShipSnapshot`/`ApiResult` are the scheduler's own minimal shapes (nav status/waypoint,
cargo units/inventory, cooldown expiration — all it needs for reconciliation); the
server-side shell (`JobRunner.fs`) maps the real API's richer `Ship`/`NavigateResult`/
`ExtractResult`/`TradeResult` types onto these at the boundary. `SpaceKids.Core/Dsl/`
also gained `Value.fs`/`Eval.fs` (a small runtime value type and pure-expression
evaluator) — the DSL never needed a runtime representation before `step` had to resolve
literals/variables/arithmetic while walking a program.

**`step` is a trampolined "walk every free transition" loop, not one instruction per
call** — exactly per §14's own reasoning: a deeply nested call chain would otherwise
need one persisted scheduler tick per push/pop with no real progress to show for it.
`SetVariable`/`ChangeVariable`/`If`/`Repeat`/`WhileUntil`/`ForEach`/`ShowMessage`/pure
expression evaluation are all "free" (no effect); only an `ApiAction` (one of the 6),
a `Wait` block, or program completion stops the walk. `WhileUntil`'s loop body pops
*without* advancing the parent PathEntry — the parent's position still points at the
same `WhileUntil` instruction, so the next walk naturally re-evaluates its condition,
matching real while-loop semantics; `Repeat`/`ForEach`, once genuinely exhausted, pop
*and* advance the parent (they never repeat again).

**Ambiguous-failure reconciliation is two explicit `JobStatus` hops, not one opaque
case** — `AwaitingApiResponse -> Reconciling -> (already happened, advance) |
(not yet, AwaitingApiResponse(attempt+1))` — matching §13's own 3-step recipe
(refresh state, compare against baseline, only then retry) as literal, testable state
transitions instead of a black box. Per-action `reconcile` rules match §13's table
exactly: navigate compares `nav.waypointSymbol`; dock/orbit compare `nav.status`;
buy/sell compare cargo-unit delta (credits never consulted — §13 is explicit that
credits are agent-global and only corroborating); extract requires **both** a newer
cooldown expiration **and** a cargo delta (conjunctive, not either/or — tested
explicitly, since either signal alone is satisfied by an unrelated action).

**Known limitation, not a gap:** buy/sell reconciliation can't consult "the ship's
transaction records where available" (§13's own phrasing anticipates this) — the real
API's `GET /my/ships/{shipSymbol}` doesn't return transaction history, only current
ship state. Cargo-unit delta is the only signal actually available from that endpoint.

**A real bug caught by the integration tests, not by inspection:** the first cut of
`Step.fs`'s success handlers for `navigate`/`extract` set `WaitingForArrival`/
`WaitingForCooldown` *without* first advancing the job's position past the instruction
— meaning resuming from the wait re-executed the same `ApiAction` a second time. Fixed
by calling `advanceJobPosition` before entering the wait state, in both the direct
success path and the reconciliation "already happened, still waiting" branches.

**`JobRunner.fs`'s effect loop does not sleep inline for `StartWait`.** The first
version had `applyEffects` sleep-then-recurse synchronously inside a single `stepOnce`
call to resolve every wait a program hit, cascading many effect/tick levels deep for
even a short program. Under heavier concurrent load (the full solution's test suite
running together) this occasionally left a job stuck mid-cascade for reasons that
resisted several rounds of live tracing. §14's own description of the shell — "reads
jobs due to wake, calls step, executes the returned effects" — describes a *polling
loop*, not one call recursively sleeping through every wait it encounters. `StartWait`
is now a no-op in `applyEffects` (the wait is already recorded in `job.status`);
`runToCompletion` (run mode) polls via repeated `stepOnce`/`WakeTick` calls on a short
interval instead. This also makes `stepOnce` (step mode) a genuine single step, matching
"driving the same core one event at a time" more literally than the original design did.

**A second real bug, found the same way:** the fake's extraction cooldown used
`int fixedCooldownSeconds` for `remainingSeconds`/`totalSeconds` — with a sub-1-second
test duration (e.g. `0.3`), `int 0.3 = 0`, so `JobRunner.cooldownExpirationOf`'s
`remainingSeconds > 0` gate (how it decides "is there an active cooldown at all")
always read "no cooldown", making extract's reconciliation permanently blind to its own
mutation regardless of real elapsed time — a genuine duplicate extraction, not a timing
race. Fixed with `ceil`, not `int`, in the fake.

**In-process test isolation:** `RequestQueue`, `JobRunner`, and
`SpaceKids.FakeSpaceTraders.App`'s mutable ship/agent/fault-mode/clock state are all
process-wide singletons by design (this app's own single-user/single-process shape).
xUnit parallelizes different test collections (one per module) by default; two test
files manipulating this shared state concurrently produced an unrecoverable hang (one
file's `resetForTests()` clearing a pending item another file's job was still awaiting
— no exception, no timeout, just silence). `SpaceKids.IntegrationTests` now carries an
assembly-level `[<CollectionBehavior(DisableTestParallelization = true)>]`
(`AssemblyInfo.fs`) so every test in that assembly runs strictly sequentially.

**`SpaceKids.FakeSpaceTraders`'s first mutable state.** Every endpoint before this
milestone was read-only (`let` bindings). `ship`/`agent` are now `let mutable`, guarded
by a plain `lock` — including on *reads* (`readShip`/`readAgent`), not just writes,
since a reconciliation `GetShip` request and a `navigate`/`extract`/`sell` mutation
genuinely run on different request threads. `navigate` snaps the stored ship straight
to its destination rather than simulating "still in transit" (§13a: "not a game
simulator") — only the *response*'s `route.arrival` matters, for the shell's wait timer.

**Verified live before any of this:** hit the real OpenAPI spec
(`https://api.spacetraders.io/v2/documentation/json`) to confirm the exact response
shapes for all 6 actions plus `GET /my/ships/{shipSymbol}`, the same rigor as every
prior milestone's client work — not guessed from memory.

Verified in a real browser (Playwright), pointed at a locally-run
`SpaceKids.FakeSpaceTraders` instance (not the live API, per the agreed scope): logged
in with the fake's seeded token, loaded a one-block ("Gehe in Umlaufbahn"/orbit)
program directly via the existing `spaceKids.loadWorkspace` seam, picked the seeded
ship, pressed Start then Einzelschritt, and watched it reach `Status: Completed` with
the correct German activity log — zero console errors. Verified via `dotnet test`: 15
new `SchedulerTests` (pure, fake clock, zero DB/network — free-transition walking, loop
persistence across steps, each action's happy path, wait/resume via `WakeTick`,
reconciliation's both branches per action including the extract conjunctive check, the
stale-attempt guard, and `InfoRead`/`CallCustomBlock` failing cleanly) and 4 new
`JobRunnerTests` (real HTTP through the real `RequestQueue`, against the fake — a
5-action happy-path chain, and reconciliation-without-duplication for navigate/extract/
sellGood specifically), alongside the full pre-existing suite, all green.

## Milestone 7: persistent background jobs (§14/§15/§19)

Replaces Milestone 6's throwaway in-memory `JobRunner` with the real persistent shell
§14 always intended — same `Step.step`, restructured only to add pause/resume/cancel,
which it had no way to express before.

**Pause/cancel are deferred, never immediate, mid-action.** `JobStatus` gained
`Paused of resuming: JobStatus` and `Cancelled`; `JobState` gained `pausePending`/
`cancelPending` flags. A `PauseRequested`/`CancelRequested` event while `Running`/
waiting takes effect immediately; while `AwaitingApiResponse`/`Reconciling` it only
sets a flag. The one place that flag is checked is `settleOrDefer`, called at each of
the 8 places `handleApiResponse` would otherwise continue running or enter a wait —
without it, a pause requested mid-flight would only be noticed *after* the job had
already run arbitrarily far past the point the player asked it to stop (`continueFn`
itself calls `advance`, which keeps walking free transitions). This is the same
"never abandon an in-flight non-idempotent action" invariant §13 already enforces
everywhere else, just extended to a new kind of interruption.

**Restart recovery reuses the exact ambiguous-failure path, not a new one.** A job
found in `AwaitingApiResponse` at scheduler startup has an unknown-outcome call in
flight — the process that made it is gone. Feeding it a synthetic
`ApiResponseReceived(_, _, ApiAmbiguous "Server wurde neu gestartet")` routes it
through the same `Reconciling` transition Milestone 6 already built and tested; a job
found already `Reconciling` just gets its `ReconcileShipState` effect reissued (a GET,
always safe to redo). `WaitingForArrival`/`WaitingForCooldown` need no special
handling at all — the tick loop's plain `until <= now` due-check already treats an
arbitrarily-overdue wait as due now, which *is* the clock-skew catch-up §14 asks for.

**`FSharp.SystemTextJson` over hand-rolled encoders.** `JobState`/`CompiledProgram`
are trees of ~15 F# DU/record types. A `JsonFSharpConverter` handles them generically;
hand-rolling that many encode/decode paths would be mechanical, bug-prone code with no
real benefit over a well-established library. Added only to `SpaceKids.Server`, not
`SpaceKids.Core` — Core stays free of any serialization dependency, matching why it
doesn't reference `SpaceTraders` either. `jobs.execution_state_json` serializes the
*whole* `JobState`, program included, rather than splitting the program out into its
own join on every resume — simpler, and compiled programs are small. `startJob` still
writes a `programs` row alongside it (a real first use of that table), for the same
reason `program_version` exists in §12 — a future watch-mode version-mismatch check —
not because resume depends on it.

**Ship locks: check-on-acquire reclaim, both directions.** `ShipLockRepository.
tryAcquire` handles three cases in one call: no existing lock (acquire), an existing
lock already owned by the same job (a resuming job refreshing its own lease — no
distinction from a fresh acquire), and an existing lock owned by a different job,
live or expired (reject, or reclaim-and-pause the orphan). The low-frequency sweep
(`JobScheduler.sweep`, ~60s) only acts on locks whose job *isn't* one of the process's
own live in-memory jobs — the tick loop's own per-tick lease refresh is what's
supposed to keep a genuinely active job's lease fresh, so a lock still actively held
never reaches the sweep at all; testing the sweep path therefore requires simulating
a job that's genuinely *not* loaded (see `JobRunnerTests.fs`), not just an expired
timestamp on a live one.

**A single global tick lock, not one per job.** `JobRunner.tick`'s
"read job, compute `step`, write result" critical section is guarded by one
process-wide `SemaphoreSlim`, deliberately not released across `applyEffects` (which
can itself recurse back into `tick` after an HTTP round-trip — holding the lock there
would deadlock against itself). Serializing all jobs' ticks through one lock, rather
than a per-job lock table, matches this app's existing "single-lane" philosophy
(`RequestQueue` itself only ever has one physical HTTP call in flight) and this app's
actual scale (one user, a handful of background jobs) — not a bottleneck worth a more
complex design.

**The fake needed real multi-ship state for the first time.** Ship-lock rejection and
lease reclaim are only testable with two *independently* mutable ships (one locked,
one free); Milestone 6's single mutable `ship` value couldn't represent that.
`SpaceKids.FakeSpaceTraders.App` now keys its mutable ship state by symbol (`Map<string,
Ship>`, still guarded by the same lock), seeded with `FAKE-AGENT-1`/`FAKE-AGENT-2`.

**Watch mode is global, not per-program.** There's still only the single Milestone-0
spike workspace — no saved/named multiple programs or routing yet. So "a running
program can't be edited out from under its pilot" (§15) is implemented as: the shared
workspace goes read-only whenever *any* pilot is non-terminal, unlocking once none
are, reusing the `setReadOnly`/`readOnly` plumbing already built in Milestone 0. Full
per-program watch mode needs the program-library UI that doesn't exist yet.

**Four real bugs found while wiring this up, all structural rather than logic slips:**

1. **`jobs.program_id` already existed.** The Milestone 1 schema (`0001_initial.sql`)
   already had a `NOT NULL` `program_id` column — the new migration re-adding it via
   `ALTER TABLE` collided with itself (`duplicate column name`). Also discovered a
   genuine version-number collision with `0002_queue_priority_and_reset.sql` (Milestone
   5) already claiming version 2; this milestone's migration is `0003_jobs_and_locks.sql`.
2. **Insert order vs. the foreign key.** `ship_locks.job_id REFERENCES jobs(id)` means
   the `jobs` row must exist *before* the lock row can reference it. `startJob` now
   inserts the job row first (tentatively `Running`), attempts the lock, and rolls the
   row back to `Cancelled` if the lock turns out to be unavailable — rather than
   acquiring the lock first and having nothing yet to attach it to.
3. **`programs.workspace_id` needs a real `workspaces` row.** Nothing previously
   guaranteed a `workspaces` row existed before a program referenced it — a player
   could start a job before ever clicking "Speichern". `startJob` now saves the
   current workspace JSON as part of starting a job, which is also simply correct
   behavior: the program being run *is* the workspace state to persist.
4. **An empty Blockly workspace crashed the compiler, and no caller had ever hit
   `Validator.validate`.** `BlocklyJson.parseWorkspace` assumed a top-level `"blocks"`
   key always exists; Blockly's own serializer omits it entirely for zero blocks — a
   real, reachable state once starting a job doesn't require having placed any blocks
   yet. Fixed to treat a missing section as an empty block list. Separately,
   `Validator.validate` (§11's "no start block" check, written in Milestone 4) had
   never actually been wired into the running path — `JobRemoting.startJob` called
   only `Compiler.compileWorkspace`. Both surfaced together: starting a job on an
   empty workspace first crashed with a 500, and after the parse fix would otherwise
   have silently "succeeded" with a zero-instruction job that completes instantly and
   confusingly rather than a clear German validation message. Both are now wired in.

Also found: `JobService.startJob` used the client-supplied token directly instead of
looking up the stored one, unlike every other handler here. A fresh page load's
`tokenInput` starts out empty again (it's client-side scratch state, not repopulated
from the persisted agent), so starting a job right after a reload silently sent an
empty token — a 401 that `RequestQueue` classifies exactly like a real server reset,
incorrectly tripping that flag. Fixed to ignore the passed-in token and look up the
stored one, matching `step`/`run`/`pause`/`resume`/`cancel`.

Verified in a real browser (Playwright) against a locally-run `SpaceKids.
FakeSpaceTraders` instance: logged in with the fake's seeded token, injected a real
"Warte 15 Sekunden" block via the existing `spaceKids.loadWorkspace` seam, started it
on a seeded ship, watched the pilot card render and the shared workspace go read-only
(manual toggle disabled) the moment it did, paused it (card showed "Pausiert"),
resumed it (back to "Unterwegs"), stopped it (card showed "Gestoppt"), and confirmed
the workspace unlocked again once no pilot remained active — zero console errors
throughout. Verified via `dotnet test`: 10 new `SchedulerTests` (pause/resume/cancel
immediate and deferred paths, an explicit restart-recovery-framed ambiguous-failure
test, and an arbitrarily-far-in-the-past clock-skew wake) and 5 new
`JobRunnerTests`/`JobScheduler` tests (same-ship rejection, two-ships-concurrently,
restart-resume against a fresh `JobScheduler.resumeAll` call over the same on-disk
database, lease-sweep reclaim, and pause-mid-flight settling into `Paused` without
losing the reconciliation), alongside the full pre-existing suite — 64 tests total,
all green, stable across repeated runs.

## Milestone 9: finish the block catalog — remaining actions, info blocks, accessors (§8/§13/§19)

Milestone 6 only wired 6 of the 11 action blocks into the scheduler core and none of
the 9 information blocks; the rest compiled fine but failed every job with "Dieser
Block wird erst in einem späteren Meilenstein unterstützt." This milestone finishes
the whole 20-block catalog so any program a player builds actually runs, split into
two parts done and verified independently.

### Part A: the 5 remaining actions (survey, deliverContract, acceptContract,
purchaseShip, refuel)

Same shape as Milestone 6's 6 actions (new `QueuedAction`/`ActionBaseline`/`ApiResult`
cases, new `SpaceTradersClient` methods verified field-by-field against the live
OpenAPI spec, new fake endpoints, new `JobRunner.runAction` arms) — except two of the
five have no ship-local reconciliation signal:

- **`acceptContract`** reconciles via a *contract* fetch (`GetContract`) instead of a
  ship fetch — is the contract's `accepted` flag now true? Per §13's own explicit
  allowance ("refresh ship **or contract** state").
- **`purchaseShip`** reconciles via a *fleet* fetch (`ListShips`) — did the ship count
  increase versus a pre-call baseline? The acting ship's own state never changes (a
  new ship is added elsewhere in the fleet), so there's no per-ship signal to reuse.
  This needed a new `JobState.lastKnownFleetShipCount` field (analogous to
  `lastKnownShip`, but fleet-wide) — populated by a `GetAgent` call in
  `JobRemoting.startJob` alongside the existing `GetShip` call, since neither
  `ShipSnapshot` nor anything else already in hand carries a fleet-wide count.
  **Documented limitation:** this can theoretically misfire if another pilot's job
  purchases a *different* ship in the same ambiguous window — the same class of
  simplification as the existing "market is always headquarters" one, not a new
  category of hazard. Fleet-mode concurrency correctness is a later milestone's
  concern generally.

Two new `Effect`/`ApiResult` case pairs (`ReconcileContractState`/`ReconciliationContract`,
`ReconcileFleetState`/`ReconciliationFleet`) carry these fetches; `handleApiResponse`'s
existing "route to reconciliation on `ApiAmbiguous`" logic picks the right one by
matching on the *baseline* shape, so the 6 existing ship-signal actions are untouched.

**Real OpenAPI-spec surprises, checked before writing any types (not guessed):**
`deliverContract` is `POST /my/contracts/{contractId}/deliver` (contract-scoped, not
ship-scoped, despite reading like a ship action); `purchaseShip` is `POST /my/ships`
(fleet-scoped, no ship symbol in the path at all). Both confirmed against the live
spec before implementation.

### Part B: the 9 info blocks + accessor blocks (§8 data model)

A real gap, not previously built at all — `Value` had no record type, and
`Expr.Accessor` existed in the DSL but nothing compiled into it (`Eval.eval`
deliberately `failwith`ed).

- **`Value` gained `VRecord of Map<string, Value>`** — a "friendly structured record"
  per §8's own instruction ("without turning every API response into a complicated
  object tree"), kept deliberately flat. `Eval.Accessor` now does a real field lookup,
  with a clear German error for both an unknown field and a non-record target.
- **A new scheduler path for info reads, deliberately simpler than actions.** A GET is
  always safe to retry, so info reads need no baseline/reconciliation at all: a new
  `JobStatus.AwaitingInfoResponse` (parallel to `AwaitingApiResponse` but with no
  `Reconciling` hop, ever) and a new `Effect.QueueInfoRead`. On `ApiAmbiguous`, `step`
  just re-emits the same `QueueInfoRead` with `attempt + 1` — no fetch-then-decide hop,
  since there's nothing to decide.
- **`JobRunner.runInfoRead`** maps each of the 9 `infoType`s to the right
  `SpaceTradersClient` call and converts the response into a `Value` (`Schiff`,
  `Fracht`, `Werft`, `Markt`, `Auftrag`, `Wegpunkt` records, per the field tables in
  `docs/04-block-catalog.md`).
- **Two documented simplifications, both following an existing project pattern**
  (the "market is always headquarters" precedent): (1) `getMarket`/`getShipyard`
  only take a waypoint symbol in the catalog (no separate system-symbol input) —
  the system symbol is derived from SpaceTraders' own `SYSTEM-WAYPOINT` naming
  convention (`systemSymbolOfWaypoint`), not asked for separately. (2) Both
  `Market.tradeGoods` (prices) and `Shipyard.ships` (ship prices) are only populated
  by the real API when a ship is physically present at that location — when empty,
  the info-read conversion falls back to the always-visible names with a price of 0
  rather than failing or omitting the field.
- **26 new accessor blocks** (one per reachable field across all 9 record shapes,
  including one-item-of-a-list records like `Ware`/`Handelsware`/`Schiffstyp`), a new
  `ACCESSOR_BLOCKS` array in `blocks-catalog.ts` (value blocks, one `TARGET` input,
  same `registerBlock` pattern as info blocks) and a 7th "Zugriffe" toolbox category.
  `Compiler.fs` gained a matching `ACCESSOR_BLOCKS: Map<string, string>` (block type ->
  field name) compiling each into `Accessor(fieldName, compileInput ... "TARGET")` —
  table-driven exactly like `ACTION_BLOCKS`/`INFO_BLOCKS`, the two maps kept in sync
  manually (documented in `docs/04-block-catalog.md`).

### A real bug found during live verification, not introduced by this milestone

Every action's `emitApiAction` emitted its effects as `[ QueueApiCall(...);
LogMessage(job.jobId, startText) ]` — `QueueApiCall` first. Since
`JobRunner.applyEffects` applies effects strictly in order, and `QueueApiCall`
recursively drives the job all the way through its *next* settled state (persisting
whatever log line the action's actual outcome produces) before returning, the
`LogMessage` for the action's *start* text always ran afterward and got prepended on
top — silently corrupting "latest log line" displays (the pilot dashboard's
"Zuletzt: ...") for every single action ever run, since Milestone 6. Only surfaced now
because Part A/B's live verification chased a real value (an accessor's resolved
field) through to the dashboard and noticed the wrong string appearing at the end.
Fixed by swapping the order (`LogMessage` before `QueueApiCall`) everywhere the same
pattern appeared, including the three post-reconciliation retry sites.

### Verification

`dotnet test` after Part A (build clean, existing suite plus new tests green) and
again after Part B, not blurred together — 86 tests total (54 `SpaceKids.Core.Tests`,
28 `SpaceKids.IntegrationTests`, 4 `SpaceKids.Server.Tests`), all green. Live browser
check (Playwright) against a locally-run `SpaceKids.FakeSpaceTraders` instance with
the real `SpaceKids.Server` pointed at it via `SpaceTraders:BaseUrl` (not the real
SpaceTraders API): a program using `refuel` (Part A) feeding straight into `Treibstoff
aus Schiff` (`getShipInfo` + accessor, Part B) piped to a show-message block —
completed successfully, correct fuel value displayed, zero console errors.

## Milestone 9: custom reusable blocks (§9)

(Naming note: the section above, "finish the block catalog," was informally called
"Milestone 9" at the time even though it's really Milestone 9-adjacent catalog-
completion work — plan.md's actual, numbered Milestone 9 is this one: real function
calls between custom blocks. The user confirmed proceeding to this real Milestone 9
after that catalog work landed.)

Five parts, each built/tested/verified independently. **Part A** made
`JobState.stack` (a `Frame list`, forward-designed since Milestone 6/7 specifically
for this milestone) actually push/pop on `CallCustomBlock`: arguments bind into a
fresh frame's locals, the callee's `returnExpr` (a new `CompiledCustomBlock` field)
evaluates against its own locals once its position is exhausted, and the result
binds into the caller's `resultTarget`. A call that suspends on an
`ApiAction`/`InfoRead` leaves the caller frame on the stack underneath — a real call
stack, not inlining. **Part B** built `CustomBlockRepository.fs` (append-only
versioning; `findUsages` for delete-refusal, asymmetric by necessity: programs via a
`CompiledProgram.customBlocks` map-key lookup since that closure already exists,
other custom blocks via a substring match on serialized JSON since a lone
`CompiledCustomBlock` has no closure of its own) and finally wired
`JobRemoting.fs`'s `lookup` to real persistence instead of `fun _ -> None`.
`Validator.revalidateAgainstCurrentDefinitions` (the structural-mismatch check) was
deliberately *not* wired into `startJob` — its own doc comment says it only matters
when re-checking an *already-compiled* program later, and compile+run still happen
in the same request here; its real call site arrives with a future saved-program
library, not this one.

**Part C** replaced the Milestone-0 mutator spike with the real thing: all six typed
inputs (Schiff/Wegpunkt/Ware/Anzahl/Preisgrenze/Liste) behind one generic mutator-arg
block with a type dropdown; one generic `callCustomBlock` caller block type (not
`sk_call_<id>` per block) whose shape rebuilds per-instance from a client-side
signature cache; one generic `sk_param_get` getter; structured-record outputs via a
new `Expr.RecordLiteral` DSL case, an `sk_build_record` mutator block, and
dynamically generated `accessor_<customBlockId>_<field>` blocks. **Part D** built the
Blockwerkstatt UI in `Main.fs`/`CustomBlockRemoting.fs`: a block library
(create/open/rename/delete, delete refused inline with a German message listing
usages) and a workshop view; saving derives a fresh signature from the just-edited
JSON (`Compiler.deriveCustomBlockSignature`, the server-side counterpart to the
client's own `readSignature`), persists a new version, and re-publishes the caller
into the main program's toolbox via the existing `publishCustomBlockSignature`
mechanism (no new plumbing needed there).

**Part E** added `Step.blockIdPerFrame : JobState -> (string * string option) list`
— one entry per stack frame, deepest-first, `scope` naming which workshop (or
`"main"` for the program) a frame belongs to. The program view shows an "innen
aktiv" indicator plus a "Block öffnen" button whenever the list has more than one
entry (a call is in flight), driven by a per-pilot "Beobachten" watch/poll loop in
`Main.fs` (`WatchTick`/`WatchStatusLoaded`, polling `JobService.getStatus` once a
second while non-terminal).

### A real bug found during Part E's own live verification

The first version of `blockIdPerFrame` used `List.choose`, silently dropping any
frame whose position was already exhausted. That's a common, not edge, case: several
existing instructions (`Wait`, and the post-action-resolution paths for
`NavigateOk`/`ExtractOk`/etc.) deliberately call `advanceJobPosition` *before*
entering a suspended status — "advance past the block that started the wait, so the
next block runs the moment the wait resolves." When a custom block's *last*
instruction is one of these, its frame's position becomes empty at the exact moment
it suspends — genuinely still on the stack (not yet popped; popping only happens via
`completeOrPopFrame`, which hasn't run since the walk stopped to suspend), but with
nothing left to report. `List.choose` threw that frame away entirely, so a call
suspended in exactly this state looked identical to "no call in progress" — the one
case "innen aktiv" most needed to catch. Fixed by switching to `List.map` returning
`string option` for the blockId (one entry always emitted per frame; `None` just
means "nothing to highlight inside," not "this frame doesn't exist") — the client
derives "is a call in flight" from the list's *length*, never from whether a
particular blockId is present. Found by deliberately testing a custom block whose
one-instruction body was a `Wait`, not an `ApiAction` — the two suspend differently
in this exact respect, and only one of them exposed the bug.

### Verification

`dotnet test --blame-hang-timeout 60s` after every part, 101 tests total (62
`SpaceKids.Core.Tests`, 29 `SpaceKids.IntegrationTests`, 10 `SpaceKids.Server.Tests`),
all green; `npm run typecheck` clean after Part C/D's TypeScript changes. Live
Playwright checks after Parts C, D, and E against a locally-run
`SpaceKids.FakeSpaceTraders` with the real `SpaceKids.Server` pointed at it (env var
`SpaceTraders__BaseUrl` — note: the fake serves its routes at the bare root, e.g.
`/my/agent`, not under a `/v2` prefix the way the real API does, so the env var must
be set to the fake's own root URL, not `.../v2/`, or every call 404s): Part C proved
one `callCustomBlock` type rendering two different signatures side-by-side; Part D
proved create → workshop opens → save persists a version → caller injected into the
main program's toolbox → rename updates the library, end to end through real HTTP;
Part E proved a call into a custom block whose body waits shows "innen aktiv" +
"Block öffnen" while suspended, and both clear once the job completes. Zero console
errors throughout.
