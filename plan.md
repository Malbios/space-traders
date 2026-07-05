# SpaceTraders Visual Programming Game — Build Plan (v2)

## 0. Changes from the previous plan

This revision resolves eleven issues found in review. Each is a decision, recorded here so a future session doesn't relitigate them without noticing.

1. **Custom-block workspace model redesigned (§9).** The previous plan assumed Blockly's native procedure system would provide reusable blocks across programs. It doesn't: Blockly procedures are strictly per-workspace, their parameters are untyped, and in recent Blockly versions the procedure blocks live partly in a separate plugin. The old decision — "definitions live inline on the shared canvas" — cannot support the stated requirement of reusing blocks across programs. **New model: each custom block is edited in its own dedicated workspace (the "Blockwerkstatt"); caller blocks are generated from stored signatures and injected into every program's toolbox.** This is the one judgment call in this revision that reverses an explicit prior decision — veto it early if it's wrong, because §9, §10, §12, and Milestones 0/3/9 all build on it. A side effect: the previously deferred "go in / go out" drill-down navigation now largely falls out for free, and the previously flagged "no way to browse/clean up the block library" gap gets a natural home (the workshop list).
2. **Effectful expressions are linearized at compile time (§10).** Value blocks that perform API calls (or call custom blocks) are hoisted into their own instructions writing to frame-local temporaries. Inline expression arguments must be pure. This keeps the scheduler's `step` function and job persistence simple — a job never needs to snapshot itself halfway through evaluating an expression.
3. **A frame's execution position is a path, not an index (§14).** Nested loops and conditionals mean position within a frame is a stack of (body, index) entries, and loop counters/iterator positions are persisted state. Spelled out before Milestone 7 so nobody implements a flat instruction pointer.
4. **Variable scoping: editor and runtime now agree by construction (§8, §9).** The per-block workshop model gives each custom block its own Blockly workspace, hence its own variable set — Blockly's workspace-global variables now align exactly with the runtime's frame-local scopes. Remaining static checks are listed in §11.
5. **Watch mode for running programs (§15).** Opening a program that has an active job opens read-only. Editing requires pausing/stopping the job. This prevents the block-highlighting UI from targeting a workspace whose block IDs no longer match the running compiled snapshot.
6. **The pure scheduler core moves to Milestone 6.** The old plan built a foreground runner in M6 and then the real scheduler in M7 — exactly the stub-then-rewrite pattern the plan avoids elsewhere. Now M6 builds the pure `step` core driven by a foreground loop; M7 adds only persistence, locks, and wake-ups.
7. **Reconciliation relies on per-ship signals (§13).** Credits are agent-global and race with other pilots in fleet mode; credits deltas are corroborating evidence only. Cargo deltas, ship state, and per-ship transaction records are the primary signals. Insufficient-funds contention between pilots is handled as an ordinary German runtime error (§11).
8. **The TypeScript/Blockly build toolchain is an explicit Milestone 0 deliverable (§3b).** npm, bundler, pinned Blockly version, wiring into the .NET build. Previously unmentioned despite five `.ts` files in the repo layout.
9. **A fake SpaceTraders server is part of the plan (§13a).** SpaceTraders is a community API with real downtime and no sandbox. An in-process fake makes integration tests deterministic, keeps development possible offline, and is the only practical way to exercise the ambiguous-failure reconciliation paths on demand. API downtime gets its own child-facing German state, distinct from server resets.
10. **SQLite backups from Milestone 1 (§12).** Months of a child's programs in one file on one disk. Periodic consistent snapshots via `VACUUM INTO`.
11. **Smaller fixes:** queue aging caps at priority 2 so fresh interactive requests always win (§13); `api_cache` invalidation is explicitly deferred rather than silently absent (§12); Milestone 0's spike now covers the mutator/typed-input risk instead of leaving it untested until Milestone 9; mission completion detection is specified as API-state deltas (§16).

## 1. Product goal

Build a German-language, Scratch-inspired visual programming environment for a 10-year-old to control ships in SpaceTraders.

The child should be able to create programs by connecting blocks, run those programs against the real SpaceTraders API, watch ships work in the background, and gradually build reusable automation blocks.

The game should teach programming concepts through play:

* sequences
* variables
* conditions
* loops
* data records
* functions
* reusable abstractions
* asynchronous work
* managing several independent processes

The game must not hide the game's strategic problems behind "smart" blocks. Players solve those problems themselves by combining primitive API actions and programming constructs.

## 2. Product principles

```txt
German is the visual language.
English is the implementation language.
Blocks represent primitive API actions.
Strategy emerges from programs, not convenience blocks.
Programs are persistent background jobs.
All API traffic passes through a global queue.
The child never needs to manage API rate limits.
Custom reusable blocks are a first-class feature.
Custom blocks live in their own workshop, not inside any one program.
Inline expressions are pure; anything effectful is its own instruction.
A running program is watched, not edited.
```

## 3. Technology choice

Use:

```txt
F# + Bolero + Blockly + ASP.NET Core + SQLite
```

Everything except the Blockly integration is squarely in Bolero's comfort zone — plain Elmish UI, dashboards, cards, logs. The one real risk is JS interop with a large, stateful, event-heavy library like Blockly, since Bolero's interop story is a thin F# face over raw Blazor `IJSRuntime`/`HtmlRef`.

There is some prior art worth knowing about, though not a perfect match: `SoftwareDriven.Blockly.Blazor` (NuGet) wraps Blockly in a Blazor Razor component, and public writeups exist showing the JSInterop wiring for Blockly inside Blazor. Neither is Bolero/F#/Elmish-specific, and the public example targets Blazor **Server** (SignalR round-trips) rather than WASM (in-process, cheaper calls), so real adaptation work remains — but Milestone 0 should start by reading that package's interop code as a reference before writing the seam from scratch, not by assuming a blank slate.

This risk is manageable but must be *isolated*, not woven through the app — see §3a and Milestone 0.

Do not switch to Fable/React or a plain TS client. That trades a contained, well-understood interop risk for losing F# end-to-end and the client/server type-sharing (DSL types, validation) the whole plan depends on. Not a good trade.

### Client

* Bolero application
* German UI
* Blockly workspaces (program view and Blockwerkstatt) embedded through JavaScript interop
* Fleet dashboard
* Background job dashboard
* Mission control screen
* German activity log

### Server

* ASP.NET Core hosted by F#
* SpaceTraders API client
* token handling
* global request queue
* rate-limit handling
* program runner
* background job scheduler
* SQLite persistence
* program and custom-block validation

### Why Blockly

Blockly provides:

* drag-and-drop blocks
* toolbox categories
* German localization
* custom block definitions
* mutators for dynamic inputs
* workspace serialization
* block IDs for execution highlighting
* mature browser interaction behaviour

Blockly does **not** provide, and this plan builds itself (see §9):

* typed procedure parameters (native procedure params are plain untyped variables)
* reuse of a definition across workspaces/programs
* any cross-workspace synchronization of definitions and callers

Do not build a custom drag-and-drop *editor* from scratch — Blockly remains the editor. But do not assume Blockly's native procedure blocks carry the custom-block feature either; §9 defines what is built on top. Verify at Milestone 0, against the exact pinned Blockly version, where the procedure blocks live (core vs the `@blockly/block-shareable-procedures` plugin split introduced around Blockly v10) — this plan uses its own definition/caller blocks precisely so that churn in Blockly's procedure internals doesn't matter, but the spike should confirm nothing else moved.

### 3a. The Blockly↔Bolero seam

Treat the Blockly integration as a deliberately isolated boundary, not idiomatic Bolero code.

```txt
One hand-written TypeScript module owns all Blockly instances completely.
It exposes a small surface only:
  initWorkspace(containerId: string, toolboxJson: string,
                options: { readOnly: boolean }): void
  destroyWorkspace(containerId: string): void
  registerBlockDefinitions(definitionsJson: string): void
      // German block types, including generated custom-block callers
  updateToolbox(containerId: string, toolboxJson: string): void
      // called when the custom-block library changes
  loadWorkspace(containerId: string, json: string): void
  serializeWorkspace(containerId: string): string
  onWorkspaceChanged(containerId: string,
                     callback: (json: string) => void): void
  setReadOnly(containerId: string, readOnly: boolean): void
      // watch mode, §15
  highlightBlock(containerId: string, blockId: string): void
  clearHighlight(containerId: string): void
Elmish never touches a Blockly object reference.
All communication crosses the boundary as JSON strings via IJSRuntime.
```

If this boundary turns out to be painful in practice, the blast radius is one TS module and one Elmish message pair — not the client architecture.

Blockly itself never executes anything — it's purely an editor. Its responsibility ends the moment a workspace is serialized to JSON. Execution, and deciding which block to highlight at any given moment, is entirely our own scheduler's job (see §14); `highlightBlock` is a dumb primitive that draws a box around whatever ID it's told to.

Event granularity matters. `onWorkspaceChanged` must not fire on every Blockly internal event — a drag alone fires many. Fire only on meaningful events (block create, block delete, block change, and move-*end*, not continuous move), and prove in Milestone 0 that this filtering actually holds, not just that save/load works on a static workspace.

### 3b. JS/TS build toolchain

The seam is TypeScript and Blockly is an npm package; neither builds itself inside a .NET solution. Decide and wire this in Milestone 0, before any seam code exists:

```txt
package.json lives in SpaceKids.Client, with Blockly pinned to an
  exact version (no ^ range). The pinned version is recorded in
  docs/decisions.md; upgrading it is a deliberate decision, re-run
  the Milestone 0 spike checklist when it happens.
esbuild bundles the TS seam (entry: blockly-host.ts) plus Blockly
  plus the German locale into a single wwwroot/js/blockly-host.js.
  esbuild over Vite/webpack: no dev-server needs, one command, fast,
  trivially scriptable from MSBuild.
TypeScript in strict mode; the seam is small enough that this is free.
The dotnet build invokes the bundle step via an MSBuild target (or a
  documented npm script CI runs before dotnet build) so a fresh
  checkout builds with the standard commands and CI cannot forget it.
```

This is mundane, and it is exactly the kind of thing that otherwise eats the first two days of Milestone 0 unplanned.

## 4. Language strategy

The child-facing experience is German.

| Area                   | Language |
| ----------------------- | -------- |
| UI labels               | German   |
| Blockly built-in text   | German   |
| Custom block labels     | German   |
| Tooltips                | German   |
| Mission text            | German   |
| Activity logs           | German   |
| Error messages          | German   |
| Code                    | English  |
| F# types                | English  |
| API client              | English  |
| Internal DSL            | English  |
| JSON fields             | English  |
| Tests                   | English  |
| Database schema         | English  |

Example:

```txt
Visual block:
Fliege mit Schiff A nach X1-DF55-A1

Internal instruction:
navigate(shipId: "SHIP-A", destination: "X1-DF55-A1")
```

Configure Blockly's German locale before creating any workspace. Note that the locale only covers Blockly's built-in UI text; every custom block ships its own German label and tooltip (§7).

```ts
import * as Blockly from "blockly";
import * as De from "blockly/msg/de";

Blockly.setLocale(De);
```

## 5. German terminology

Create a stable glossary in `docs/06-localization.md`.

| Internal/API term | German visual term |
| ------------------ | ------------------- |
| agent               | Kapitän             |
| ship                | Schiff              |
| fleet               | Flotte              |
| waypoint            | Wegpunkt            |
| system              | Sternensystem       |
| contract            | Auftrag             |
| cargo               | Fracht              |
| credits             | Credits             |
| orbit               | Umlaufbahn          |
| dock                | Andocken            |
| navigate            | Fliegen             |
| extract             | Abbauen             |
| trade               | Handeln             |
| buy                 | Kaufen              |
| sell                | Verkaufen           |
| deliver             | Liefern             |
| survey              | Scannen             |
| fuel                | Treibstoff          |
| cooldown            | Abklingzeit         |
| asteroid field      | Asteroidenfeld      |
| marketplace         | Marktplatz          |
| shipyard            | Werft               |
| custom block        | Eigener Block       |
| block workshop      | Blockwerkstatt      |

Use short, concrete German sentences:

```txt
Das Schiff fliegt zum Wegpunkt.
Das Schiff wartet auf Treibstoff.
Die Fracht ist voll.
Der Auftrag braucht noch 8 Einheiten Kupfer.
```

## 6. Block philosophy

Blocks should correspond to direct SpaceTraders API operations, direct API reads, or ordinary programming concepts.

A block may perform necessary mechanical waiting caused by API state, such as waiting for travel or cooldown completion. It must not make strategic decisions on the player's behalf.

### Allowed API action blocks

```txt
Fliege zu Wegpunkt
Gehe in Umlaufbahn
Docke an
Baue Rohstoffe ab
Scanne Asteroidenfeld
Kaufe Ware
Verkaufe Ware
Liefere Fracht
Nimm Auftrag an
Kaufe Schiff
Tanke auf
```

### Allowed API information blocks

```txt
Hole Schiffsinformationen
Hole Flotteninformationen
Hole Wegpunkte
Hole Marktdaten
Hole Werftdaten
Hole Auftragsdaten
Hole Fracht
Hole Treibstoff
Hole Credits
```

Information blocks are value blocks: they plug into variable assignments, conditions, and other inputs. Because they perform API calls, the compiler hoists them into their own instructions (§10) — but the child never sees that; on the canvas they compose like any other value.

### Allowed programming blocks

```txt
Wenn
Wenn sonst
Wiederhole
Wiederhole bis
Für jedes Element
Setze Variable
Ändere Variable
Vergleiche Werte
Rechne
Erstelle Liste
Füge zu Liste hinzu
Hole Element aus Liste
Zeige Nachricht
Warte
```

### Do not add strategy blocks

```txt
Finde besten Handelsweg
Finde nächsten Markt mit Eisen
Kaufe billig und verkaufe teuer
Erfülle Auftrag automatisch
Optimiere Treibstoffroute
Wähle bestes Schiff
```

The player should build these behaviours from data retrieval, variables, loops, comparisons, and primitive action blocks.

## 7. Blockly block design

Each block needs:

```txt
German label
German tooltip
Blockly type identifier
Input fields
Internal DSL instruction
Validation rules
Required ship state
German success log
German failure log
```

Example:

```md
## Fliege zu Wegpunkt

German label: Fliege zu Wegpunkt
Tooltip: Fliegt das ausgewählte Schiff zu einem Wegpunkt.
Blockly type: navigate
DSL instruction: navigate(destination)
Inputs: destination
Requirements: selected ship, enough fuel
Success log: Das Schiff fliegt zu {destination}.
Failure log: Das Schiff hat nicht genug Treibstoff.
```

Keep block identifiers and DSL names English.

```txt
Blockly type: navigate
DSL type: navigate
German display name: Fliege zu Wegpunkt
```

Block definitions and their DSL instruction shapes are two views of the same catalog entry. Author them together in `docs/04-block-catalog.md` — the catalog is written once and consumed twice, by the toolbox build (Milestone 3) and by the compiler/validator (Milestone 4) — rather than defining blocks in M3 and inventing their DSL mapping separately in M4.

## 8. Variables, lists, and data

To allow real problem-solving, expose data without turning every API response into a complicated object tree.

Use friendly structured records.

Example market record:

```txt
Markt
- Wegpunkt
- Handelswaren
- Kaufpreise
- Verkaufspreise
```

Example ship record:

```txt
Schiff
- Name
- Wegpunkt
- Status
- Treibstoff
- Fracht
- Frachtkapazität
```

Use accessor blocks:

```txt
Wegpunkt aus Schiff
Treibstoff aus Schiff
Fracht aus Schiff
Preis aus Handelsware
Name aus Ware
```

This enables programs such as:

```txt
Für jeden Markt in Märkte
  Wenn Markt verkauft Eisen
    ...
```

without requiring raw JSON manipulation.

### Variable scope

Runtime scoping is frame-local (§14): a custom-block call sees only its own parameters and its own variables; nothing leaks in or out except through the return value.

The workshop model (§9) makes the *editor* agree with this by construction: the main program is one Blockly workspace with its own variable set, and each custom block's workshop is another workspace with its own variable set. Blockly's workspace-global variable dropdowns therefore show exactly the variables that will exist in the corresponding runtime scope. There is no way for the child to reference, from the main program, a variable that only exists inside a custom block — the dropdown simply doesn't contain it. No scope-aware dropdown filtering or extra interop is needed; the remaining static checks (a parameter reference matching the signature, and so on) are listed in §11.

## 9. Custom reusable blocks

Custom blocks are a core feature, not a later convenience feature. A player should be able to define a new block, build its logic, give it inputs, and reuse it — like a component — anywhere else, including in other programs and inside other custom blocks.

### 9a. Why not Blockly's native procedures

The previous plan intended to lean on Blockly's built-in procedure system. Review showed that system doesn't cover the requirements:

```txt
Native procedure parameters are untyped — they are ordinary workspace
  variables. Typed Schiff/Wegpunkt/Ware inputs require custom mutator
  work either way.
Native procedures are strictly per-workspace. A definition and its
  auto-generated caller exist only inside one workspace's JSON. There
  is no native way for program B to call a block defined in program A.
The procedure blocks have been partially split out of Blockly core
  into a plugin in recent versions, making "built-in" version-dependent.
```

Since typed inputs already force custom blocks, and cross-program reuse already forces our own definition storage, the plan builds its own small definition/caller pair and uses Blockly purely as the editor for them. This is *less* dependent on Blockly internals than the previous plan, not more.

### 9b. The workspace model: one workshop per block

**Decision (reverses the previous plan's "definitions live inline on the shared canvas"):**

```txt
A program is one Blockly workspace containing only program logic.
  No custom-block definitions ever appear on a program's canvas.
Each custom block has its own definition workspace — its Blockwerkstatt —
  containing exactly one definition shell block with the block's body
  built inside it and a return-value socket.
The canonical definition (name, signature, workspace JSON, compiled
  body) lives in the custom_blocks tables (§12). No program's workspace
  JSON contains or duplicates it.
Caller blocks are generated from the stored signature and injected into
  the "Eigene Blöcke" toolbox category of every program workspace and
  every other block's workshop (blocks can call blocks). A caller block
  in a workspace serializes as just its type id + customBlockId + its
  argument connections — the definition is resolved at compile time.
```

Why this model:

```txt
It is the only coherent home for cross-program reuse: one definition,
  many callers, no synchronization of duplicated definitions across
  workspace JSONs.
It aligns editor variable scope with runtime frame scope for free (§8).
It gives each block a natural editing surface — opening a block's
  workshop IS the "go in" drill-down the previous plan deferred; closing
  it is "go out." What was a deferred polish item mostly falls out of
  the architecture. (Fancier camera transitions remain future polish.)
It gives the block library a management surface: the workshop list view
  shows all custom blocks, supports rename and delete, and answers the
  previously flagged "how do you browse/clean up blocks" gap.
```

Rejected alternatives, for the record: keeping definitions inline per-program and syncing copies across workspaces (permanent synchronization bug farm); dropping cross-program reuse from v1 (contradicts the definition of success and Mission 5's teaching goal).

### 9c. Creation and editing UX

```txt
No name required upfront. A new custom block starts with a default,
  editable name; rename it anytime in the workshop.
Creating a block opens its (empty) workshop. Existing block chains can
  be moved in via ordinary copy/cut-paste between workspaces — grabbing
  the top block of a chain picks up everything snapped beneath it.
Inputs (parameters) can be added, removed, renamed, or retyped at any
  time via the definition block's mutator UI (the gear icon). Typed and
  dynamic, using the input types listed below. This mutator is our own,
  built in the TS seam — flagged as interop work and spiked in
  Milestone 0, not discovered in Milestone 9.
Parameter getter blocks ("Eingabe: Schiff") are generated from the
  signature and appear in the workshop's toolbox.
Output is dynamic, inferred from whatever gets plugged into the
  definition block's return-value socket — a plain value, or one of the
  structured records described under Outputs below.
When a signature changes (input added/renamed/retyped, output shape
  changed), the server regenerates the caller definition and pushes a
  toolbox update (§3a updateToolbox) to any open workspaces. Programs
  already using the old shape are handled by the mismatch check below —
  a clear German message, never a silent break.
Deleting a block that is still used by any program or other block is
  refused, with a German message listing where it's used.
```

Example:

```txt
Eigener Block: Baue Erz ab

Eingaben:
- Schiff
- Asteroidenfeld
- Anzahl

Inhalt:
- Fliege zu Asteroidenfeld
- Wiederhole Anzahl Mal
  - Baue Rohstoffe ab
```

### 9d. Execution model

Custom block calls execute as real function calls, with a call stack — not inlined/macro-expanded at compile time. This was chosen over inlining for several reasons: it avoids duplicating a block's compiled instructions at every call site, it's the more honest representation of what a function actually is (one shared piece of logic, not a copy-paste macro, matching this project's own stated goal of teaching functions as a real concept), it leaves the door open to recursion later without an architecture change (even though recursion stays disallowed for now — see §11), and it keeps compilation local to each block rather than needing to resolve a whole call graph into one flat artifact at compile time.

This is a genuine structural commitment for the scheduler (§14): `JobState` needs to represent a stack of execution positions — one frame per active custom-block call — rather than a single current instruction. See §14 for how this is built in from Milestone 7 onward, specifically to avoid restructuring it later once Milestone 9 introduces actual calls.

### Inputs

Fully typed, dynamic inputs via our own mutator in the TS seam:

```txt
Schiff
Wegpunkt
Ware
Anzahl
Preisgrenze
Liste
```

### Outputs

Multiple independent output sockets are not modeled. Multiple outputs are one structured result record, returned via the definition block's return-value socket.

Example:

```txt
Eigener Block: Prüfe Markt

Eingabe:
- Wegpunkt

Ergebnis:
Marktinfo
```

`Marktinfo` can contain:

```txt
Wegpunkt
Handelswaren
Kaufpreise
Verkaufspreise
```

Provide accessor blocks:

```txt
Wegpunkt aus Marktinfo
Handelswaren aus Marktinfo
Kaufpreis aus Marktinfo
Verkaufspreis aus Marktinfo
```

### Highlighting during custom-block calls

With definitions in their own workshops rather than on the program canvas, "highlight the currently running block" is view-dependent:

```txt
Program view: while execution is inside a custom-block call, the
  caller block is highlighted with an "innen aktiv" state, plus a
  "Block öffnen" affordance.
Workshop view: opening the running block's workshop (read-only while
  the job runs — §15) highlights the actual current block inside it,
  i.e. the top of the call stack (§14).
Nested calls follow the same rule one level at a time: each view
  highlights the deepest position visible in that view.
```

### Custom block persistence

Versioning is deferred, but not to zero. Full version pinning (existing programs keep the version they were saved with, explicit upgrade required) would be premature structure for a single-user tool, so it stays out of the first release. But "deferred" needs a floor: for a child specifically, a program silently running against a changed custom block with different inputs, with no explanation, is a bad failure mode — confusing rather than just occasionally inconvenient. Minimum safety net, no versioning required: on load or validation, if a referenced custom block's current definition doesn't structurally match what the program expects (different inputs, different output shape), refuse with a clear German message rather than running it anyway. That's a validation check, not a version-resolution system, and it belongs in Milestone 4/9.

This check needs something concrete to diff against, which isn't automatic once full versioning is out of scope: at compile time, since a whole program compiles in one pass, every call site referencing a given custom block was necessarily compiled against the same version of it — so the signature snapshot (input names/types, output shape) only needs to be recorded once per custom block within the compiled program (see §10's `customBlocks[id].signature`), not duplicated at every call site. The mismatch check then compares that one frozen snapshot to the block's current live definition, rather than trying to compare two version numbers with nothing behind them.

Persist a version *number* from day one regardless (cheap, and keeps the schema forward-compatible) but do not build resolution/upgrade logic beyond the mismatch check above unless it actually bites you.

Persist:

```txt
name
description
input definitions
output type
Blockly definition workspace JSON   (the workshop)
compiled DSL body
version              (stored, not yet enforced beyond the mismatch check)
created_at
updated_at
```

## 10. Internal DSL

Blockly must not generate JavaScript and must never call SpaceTraders directly.

Blockly serializes the workspace. The client compiles it into a small JSON DSL. The F# server validates and executes the DSL.

Custom blocks compile once into their own keyed collection within the DSL, not inlined at every use (see §9d). That collection must be the full transitive closure of everything the program can reach — every block called directly, and every block called by those blocks, recursively — not just the blocks dragged directly into the main program. Without this, both the cycle-detection check (§11) and the runtime call stack (§14) would have nothing to look up the moment a nested call goes more than one level deep. Because caller blocks in workspace JSON carry only a `customBlockId` (§9b), the compiler resolves each referenced block's current stored definition at compile time and freezes its signature snapshot into the program.

### Expression linearization

The rule that keeps execution and persistence simple:

```txt
An instruction's inline arguments must be pure.
Anything effectful is hoisted into its own instruction that writes a
frame-local temporary.
```

Concretely:

```txt
Pure value blocks — accessors (Wegpunkt aus Schiff), arithmetic
  (Rechne), comparisons (Vergleiche Werte), list reads, literals,
  variable/parameter references — may remain as inline expression
  trees in an instruction's arguments. They are effect-free and
  evaluate atomically inside a single step call; a job is never
  persisted mid-expression.
Effectful value blocks — every API information block (Hole Marktdaten,
  Hole Schiffsinformationen, ...) and every custom-block call used as
  a value — are hoisted by the compiler into their own top-level
  instruction with a resultTarget, in left-to-right evaluation order.
  The original expression position becomes a reference to the
  temporary.
Temporaries are ordinary frame-local variables with reserved names
  ($t1, $t2, ...), invisible in the editor, scoped and persisted
  exactly like any other frame variable (§14).
```

So the child's block

```txt
Setze Variable Markt auf (Hole Marktdaten von (Wegpunkt aus Schiff))
```

compiles to

```json
[
  { "blockId": "b7", "type": "getMarket",
    "waypoint": { "expr": "accessor", "field": "waypoint",
                  "of": { "ref": "variable", "name": "currentShip" } },
    "resultTarget": "$t1" },
  { "blockId": "b8", "type": "setVariable", "name": "Markt",
    "value": { "ref": "variable", "name": "$t1" } }
]
```

A call site becomes a `callCustomBlock` instruction carrying the arguments to bind for that call, and — when the block returns a value — a `resultTarget`. Because a whole program compiles in one pass, every call site referencing a given custom block was necessarily compiled against the same version of it — so the signature it was compiled against only needs to be recorded once per custom block, not duplicated at every call site:

```json
{
  "version": 1,
  "programId": "mine-and-sell",
  "customBlocks": {
    "baue-erz-ab": {
      "signature": {
        "inputs": [
          { "name": "Schiff", "type": "Schiff" },
          { "name": "Asteroidenfeld", "type": "Wegpunkt" },
          { "name": "Anzahl", "type": "Zahl" }
        ],
        "output": null
      },
      "instructions": [
        { "blockId": "b12", "type": "navigate",
          "destination": { "ref": "param", "name": "Asteroidenfeld" } },
        {
          "blockId": "b13",
          "type": "repeat",
          "count": { "ref": "param", "name": "Anzahl" },
          "body": [ { "blockId": "b14", "type": "extract" } ]
        }
      ]
    }
  },
  "instructions": [
    { "blockId": "b1", "type": "selectShip", "shipId": "SHIP-1" },
    {
      "blockId": "b2",
      "type": "callCustomBlock",
      "customBlockId": "baue-erz-ab",
      "arguments": {
        "Schiff": { "ref": "variable", "name": "currentShip" },
        "Asteroidenfeld": { "literal": "X1-DF55-A1" },
        "Anzahl": { "literal": 3 }
      }
    },
    { "blockId": "b3", "type": "navigate", "destination": { "literal": "X1-DF55-A2" } },
    { "blockId": "b4", "type": "dock" },
    { "blockId": "b5", "type": "sellAllCargo" }
  ]
}
```

The DSL should include:

```txt
blockId
instruction type
arguments (pure inline expressions or refs only)
resultTarget where the instruction produces a value
source workspace version
custom block versions
custom block signature snapshots, one per referenced custom block
  (see §9) — not duplicated per call site
program version
```

The `blockId` allows the UI to highlight the currently running block. With custom blocks executing as real calls, "currently running" is always the blockId at the top frame's current position in the job's execution call stack (see §14) — a direct lookup once that stack exists, not something requiring separate bookkeeping.

## 11. Validation

Validate programs before execution and again while executing.

### Static validation

Check:

```txt
Required inputs exist.
Block connections are valid.
Variables exist in the scope where they are used — the main program's
  variables for main-program blocks, a custom block's own variables and
  parameters for blocks inside its workshop. The workshop model makes
  cross-scope references impossible to author (§8), so this check is a
  compiler invariant rather than a child-facing error path, but it
  stays: hand-edited or migrated workspace JSON must not slip through.
Parameter references match the block's signature.
Custom block references resolve to existing blocks.
Custom block call arguments match the signature (arity and types).
The compiled custom-block collection is the full transitive closure.
Custom block references contain no cycles (a block calling itself,
  directly or through another custom block) and stay within a sane
  maximum nesting depth. The call-stack execution model (§9d) is
  structurally capable of recursion, but it stays disallowed by this
  check for the first release regardless.
Signature snapshots match current definitions (§9 mismatch check).
Only allowed block types are present.
Program has a start block.
```

### Runtime validation

Check:

```txt
A ship is selected when required.
The ship is in the correct state.
The ship has enough fuel.
The ship has cargo space.
The agent has enough credits for a purchase — and treat "not enough"
  as an ordinary, expected German runtime error, not an anomaly:
  credits are shared across all pilots, so two jobs can both pass a
  pre-check and one purchase can still fail. The job reports
  "Nicht genug Credits für den Kauf." and continues or pauses per its
  program; nothing about this path is exceptional.
The requested waypoint exists.
The market supports the requested trade good.
The job still owns the selected ship.
```

All errors must be translated into short German messages.

## 12. Persistence

Use SQLite as the durable store.

Use:

```txt
SQLite + EF Core or Dapper
```

Do not use loose JSON files as the primary persistence model. JSON is still appropriate inside SQLite for flexible payloads.

### Core tables

```txt
agents
api_tokens
workspaces
programs
custom_blocks
custom_block_versions
jobs
job_logs
ship_locks
api_cache
request_queue_events
schema_versions
```

### Important data

```txt
workspaces.workspace_json                    (program logic only — no
                                              custom-block definitions,
                                              see §9b)
programs.compiled_dsl_json
custom_block_versions.definition_json        (the workshop workspace)
custom_block_versions.compiled_body_json
jobs.execution_state_json
api_cache.response_json
request_queue_events.response_metadata_json
```

`api_cache` has no invalidation policy in the first release — deliberately. Entries carry a `fetched_at` timestamp; consumers decide per read whether staleness matters (dashboards tolerate it, reconciliation always bypasses the cache). Revisit only if stale data produces a real, observed problem.

### Write concurrency

The web request pipeline and the background scheduler both write to the same SQLite file from different threads, which raises the possibility of `database is locked` errors under EF Core. This isn't deadlock territory — SQLite doesn't have the multi-resource lock-ordering problems that cause real deadlocks; the worst case is one writer waiting briefly for another, surfacing as a quick exception, not a silent hang.

Start with the simplest option: enable WAL mode and set a `busy_timeout` pragma, so a writer that can't get the lock immediately waits briefly and retries instead of failing outright. For the actual concurrency level here — one user, a handful of background jobs, small and fast writes — this is very likely sufficient on its own, and it's far less machinery than a hand-rolled single-writer channel.

Only escalate to a single-writer-owner pattern (a channel/mailbox that funnels writes through one dedicated loop) if `SQLITE_BUSY` failures actually show up during development — don't build it preemptively. If it does become necessary, scope it narrowly: the only tables genuinely written by both the web layer and the scheduler are the job- and ship-lock-related ones (`jobs`, `ship_locks`, `job_logs`). Workspaces, programs, and custom blocks are written exclusively by the web layer already, so they need no coordination at all. Funneling only the shared tables through a single writer is far simpler than routing all persistence through one channel.

If this project ever needed real multi-writer concurrency at meaningfully higher scale — unlikely for a single-user hobby tool — a server-based database like Postgres gives proper MVCC without any of this reasoning. Not worth the operational overhead (a running DB process, connection management) here, but worth knowing as the standard escape hatch if that assumption ever changes.

Decide on WAL + busy_timeout in Milestone 1; treat anything beyond that as something to reach for only if it's actually needed.

WAL reads are always a consistent snapshot — a reader never sees a half-written or torn value, and never sees an in-progress write's uncommitted changes. The only tradeoff is staleness: a read that started a moment before a concurrent write commits won't reflect that write until the next read. That's normal for any concurrent system — the fleet dashboard just picks it up on the next poll — and isn't something WAL makes worse. The default rollback-journal mode doesn't avoid this either; it just blocks the reader until the writer finishes, which costs responsiveness without buying extra correctness.

### Backups

Months of a child's programs end up in one file on one disk; losing "Baue Erz ab" to a disk failure is the worst possible outcome for this project's actual user. From Milestone 1:

```txt
A background task snapshots the database periodically (e.g. hourly
  while the server runs, plus on clean shutdown) into a backups/
  folder with simple retention (keep the last N dailies).
Because the database runs in WAL mode, a plain file copy of a live
  database is not a consistent snapshot. Use VACUUM INTO 'backup.db'
  (or SQLite's online backup API) — both produce a consistent copy
  while the app keeps running.
Optionally point the backups folder at synced storage, or run
  Litestream, but the one-hour VACUUM INTO task is the floor.
```

### Jobs must survive restarts

When the server starts:

1. Load jobs in active or waiting states.
2. Revalidate ownership and state.
3. Resume eligible jobs.
4. Mark unsafe jobs as paused with a German explanation.
5. Never silently rerun an uncertain API action.

## 13. Global API request queue

All SpaceTraders traffic must pass through a single server-side request pipeline.

```txt
Blockly program
  -> DSL runner
    -> SpaceTraders command
      -> global request queue
        -> rate-limited HTTP client
          -> SpaceTraders API
```

No browser-side API calls. No bypasses from individual block handlers.

### Queue responsibilities

```txt
Serialize or throttle outbound calls.
Read rate-limit headers.
Handle 429 responses.
Retry after the server-provided delay.
Apply exponential backoff for transient failures.
Prioritize interactive actions over background jobs.
Record queue events.
Expose queue status to the UI.
Prevent duplicate non-idempotent actions.
```

### Suggested priority levels

```txt
1. Player pressing step/run
2. Program state checks needed for active foreground job
3. Background job action
4. Background polling
5. Cache refresh
```

Strict priority ordering alone risks starving background jobs during a long interactive session (a child stepping through a program for ten minutes shouldn't stall every pilot the whole time). Use aging: track how long each queued item has waited, and bump its effective priority by one tier for every fixed interval it's been waiting (e.g. every 5 seconds), **capped at priority 2, not 1**. Priority 1 stays reserved for the child's own fresh interactive actions — otherwise a backlog of aged background polls can queue-jump en masse and make "step" feel laggy at exactly the moment the child is watching. Aged background work reaching tier 2 still overtakes everything except a button press, which keeps pilots visibly working without needing a separate reserved-capacity mechanism.

### Retry safety

Every queued command should include:

```txt
job ID
program ID
block ID
attempt number      (combined with job ID, this is the dispatch key
                      described in §14 — no separate correlation ID
                      needed)
idempotency classification
```

There must be exactly one owner of the retry decision for a given attempt, and the queue must never let two physical HTTP calls be in flight for the same logical action at once. Concretely, this means splitting failures into two classes:

```txt
Definite failure (never reached the server): DNS failure,
connection refused, request aborted before send. SpaceTraders never
received it — safe to retry immediately, no ambiguity, no
reconciliation needed.

Ambiguous failure (may have reached the server): timeout after the
request was sent, connection reset mid-response. SpaceTraders might
have processed it — do not fire a second, competing request.
```

For the ambiguous case:

1. Refresh ship or contract state (the one reconciliation call).
2. Determine whether the intended action already happened, by comparing against the pre-call baseline.
3. Only then issue a fresh attempt, if reconciliation shows it's still needed.

Because attempt N+1 is never issued while attempt N might still be physically in flight, there's no scenario where a stale response from an abandoned attempt shows up later needing to be discarded — that scenario only arises from a design that lets a retry race the original call, which this explicitly rules out.

This is not one generic mechanism. SpaceTraders has no idempotency-key support, so step 2 above is bespoke reconciliation logic per action type, not something the queue solves once and reuses. Each check needs a baseline captured *before* the call was attempted, not just an inspection of current state after the failure — "check cargo delta" only means something relative to a known starting point, and for extract specifically, the cooldown timestamp needs to be newer than the one recorded before this attempt, not just present.

**Reconciliation must rely on per-ship signals.** Credits are agent-global: once fleet mode runs several pilots, another job can legitimately change the credits balance between this job's baseline and its reconciliation read, so a credits delta can neither confirm nor rule out anything on its own. Cargo, ship state, cooldowns, and the transaction records SpaceTraders returns per ship are the primary evidence; credits are corroborating at best:

```txt
navigate   -> ship nav status/location against the intended destination
buy/sell   -> this ship's cargo delta against the pre-call snapshot,
              plus the ship's transaction records where available;
              credits delta only as corroboration, never as the
              deciding signal
extract    -> cooldown timestamp newer than the pre-call snapshot,
              and this ship's cargo delta
dock/orbit -> ship status
```

Budget this as real per-instruction design work in Milestone 6, not a side effect of the queue's retry logic.

This doesn't mean extra API calls on every action. The baseline is whatever ship or cargo state the job already has on hand from the last time it looked — the previous instruction's response, or the job's last known state — so recording it costs nothing, it's bookkeeping of data already in hand. Reconciliation itself only runs on the rare ambiguous-failure path, and even then it costs exactly one extra API call — "get current ship state" — compared against the baseline already held. Not one per action, and not one per successful call; the normal path never triggers it.

### UI behaviour

The child sees:

```txt
Das Programm wartet kurz auf die Raumfunkzentrale.
```

The child does not see raw rate-limit details unless an advanced view is enabled.

### SpaceTraders server resets

SpaceTraders periodically wipes its game servers, invalidating agents and tokens. This must be detected explicitly, not left to surface as a generic failure:

```txt
Detect via: 401 on any call, or agent-not-found on startup reconciliation.
On detection:
  Pause all active jobs.
  Set a clear German explanation: "Der Spielserver wurde zurückgesetzt.
  Ein neuer Kapitän muss erstellt werden."
  Offer a re-registration flow.
  Never let the queue keep retrying against a dead agent/token.
```

Add a `server_reset_detected` flag to the agent record so the scheduler can short-circuit cheaply instead of discovering this per-job.

Check the actual current cadence before writing any UI copy about timing. SpaceTraders documents server resets at `docs.spacetraders.io/resources/server-resets`; the historical alpha-era cadence (weekly) is not something to assume is still accurate. Read that page directly during Milestone 5 rather than hardcoding an assumption about frequency.

A reset invalidates more than the token. The whole universe is regenerated — ship symbols, waypoint symbols, and market data all change. How much this actually bites depends on what "full programs" end up looking like, which isn't fully settled yet. If most values are piped rather than typed as literals — get the location of ship X, find the nearest Y, fly there — most waypoint data becomes disposable and gets recomputed live after a reset, sidestepping the problem almost entirely. Custom blocks (§9), with their typed Schiff/Wegpunkt parameters, are exactly the natural vehicle for this piped style rather than literals baked into a program. The most likely place a literal reference still has to exist is selecting which physical ship a job controls, since a job needs to start somewhere — though even that might end up data-driven rather than a raw hardcoded ship symbol.

Regardless of how much this ends up mattering in practice, the game should still explain in German if a reset makes a saved program stop working, rather than let it fail silently with no context.

### API downtime (distinct from resets)

SpaceTraders is a community-run service; outages happen and there is no sandbox. Downtime must not surface as job failures or masquerade as a reset:

```txt
Detect via: connection failures / 5xx across the board while the
  token is otherwise believed valid (as opposed to 401/agent-not-found,
  which signals a reset).
On detection:
  The queue enters a paused "unreachable" state with backoff probing —
  it does not fail queued commands, and jobs simply remain in their
  waiting states.
  The child sees: "Die Raumfunkzentrale ist gerade nicht erreichbar.
  Deine Piloten machen weiter, sobald sie wieder Funkkontakt haben."
  On recovery, the queue drains normally; ambiguous in-flight attempts
  from the moment of the outage go through the standard reconciliation
  path — downtime creates no new retry rules.
```

### 13a. Fake SpaceTraders server

Add a small in-process fake implementing only the endpoints this project uses (`SpaceKids.FakeSpaceTraders`):

```txt
An ASP.NET Core app/test host speaking the same JSON shapes as the
  real API: a handful of ships, waypoints, markets, one contract;
  deterministic travel and cooldown timings driven by the same Clock
  abstraction the scheduler uses (§14).
Grown endpoint-by-endpoint alongside the real API client from
  Milestone 2 — never ahead of what's actually consumed.
Fault injection hooks: drop a request after processing it (the
  ambiguous-failure case), return 429, return 5xx, simulate a reset
  (401 + fresh universe), go unreachable.
```

What it buys, in order of importance: the ambiguous-failure reconciliation paths (§13) become deterministically testable — they are nearly impossible to trigger on demand against the real API, and they're the most correctness-critical code in the project; integration tests stop depending on a third-party service's uptime and rate limits; development continues during outages; and a demo/offline mode exists if the real server is down when the child wants to play. It is explicitly *not* a game simulator — no economy, no other agents; just enough state for the endpoints to answer coherently.

## 14. Background jobs

A running program is a persistent background job.

The initial rule should be:

```txt
One job controls one ship.
```

Later, programs may coordinate multiple ships, but that should not be part of the first release.

### Job model

```txt
id
name
status
workspace_id
program_id
program_version
assigned_ship_id
current_block_id       (denormalized blockId at the top frame's current
                         position, kept in sync whenever
                         execution_state_json changes — cheap to query
                         for dashboards showing many jobs at once,
                         without deserializing JSON per row)
execution_state_json   (the call stack of execution frames — see
                         Execution state shape below)
created_at
updated_at
last_error
next_wake_at
```

### Job statuses

```txt
running
queued_for_api
waiting_for_arrival
waiting_for_cooldown
waiting_for_condition
paused
failed
completed
cancelled
```

### Execution state shape

Because custom blocks execute as real calls (§9d), a job's execution position is a call stack — a list of frames, one per active custom-block call, pushed on `callCustomBlock` and popped when that block's instructions finish. And because programs contain nested loops and conditionals, **a frame's own position is a path, not a single index**:

```txt
Frame = {
  scope        : "main" | customBlockId
  position     : nonempty list of PathEntry — the path from the frame's
                 top-level instruction list down into nested bodies
  locals       : the frame's variables — parameters (bound at call
                 time), child-created variables, compiler temporaries
                 ($t1, ...; §10), plus the returnTarget for value calls
}

PathEntry = {
  bodyRef      : which instruction list (top level | then-branch of b7
                 | body of b13 | ...)
  index        : current position within that list
  loopState    : present when bodyRef is a loop body —
                 remaining/target count for Wiederhole,
                 current list position for Für jedes Element,
                 all persisted; a repeat counter must survive a restart
                 mid-loop
}
```

The top frame's deepest PathEntry identifies the currently running blockId — that is what `current_block_id` denormalizes and what the UI highlights (§9, §15).

Each frame carries its own isolated local variable scope, matching real function-call semantics: a call's frame starts with only its own parameters bound, nothing from the caller's variables carries in automatically, and nothing set inside the call leaks back out or is visible anywhere else. This is consistent with why call-stack execution was chosen over inlining in the first place (§9d) — a function that silently shares mutable state with whoever calls it isn't really a function, just a macro with extra steps. The only way a value crosses from a call back into the caller is through the block's return value, delivered into the caller frame's recorded `resultTarget` (§10). The workshop model (§9b) means the editor already presents exactly this scoping, so the runtime isn't enforcing anything the child couldn't see.

Build the stack shape, the path-based position, and per-frame locals in Milestone 7 from day one, even though the stack only ever holds one frame until Milestone 9 introduces actual calls. Restructuring a flat instruction pointer into a stack — or retrofitting isolated scoping onto a shared variable store, or bolting loop counters onto an index — later would be exactly the kind of avoidable rework this plan guards against elsewhere.

### Job scheduler

The scheduler should:

1. Find jobs ready to continue.
2. Acquire a ship lock.
3. Execute one safe instruction or state transition.
4. Persist the updated job state.
5. Release the ship lock when appropriate.
6. Schedule the next wake-up.
7. Send UI updates.

Do not hold an in-memory thread while a ship travels or cools down. Persist `next_wake_at` and resume later.

### Testable scheduler core

Structure the scheduler as a pure core with a thin imperative shell. **The pure core is built in Milestone 6** and driven there by a simple foreground loop for step/run mode; Milestone 7 adds the persistent shell around the same core. This is deliberate: the old sequencing built a throwaway foreground runner in M6 and a "real" scheduler in M7 — the same stub-then-rewrite pattern this plan avoids with the request queue. Navigation and extraction already require waiting-for-arrival/cooldown logic in M6; that logic belongs in the core once, not twice.

```txt
Pure core:
  step : Clock -> JobState -> SchedulerEvent -> (JobState * Effect list)
  No direct DB access, no direct HTTP calls, no direct sleep/wait.
  Effect list describes what should happen (queue this API call,
  persist this state, log this German message) without doing it.

  JobState's execution position is the call stack of path-positioned
  frames described above.

  A single call to step isn't limited to exactly one DSL instruction.
  Pushing a frame, popping a frame, advancing a loop counter, entering
  a conditional branch, and evaluating pure inline expressions (§10)
  don't require waiting on anything external, so step should walk
  through as many of these "free" transitions as it can in one call,
  only returning once it either produces an effect that needs to
  happen outside the pure core (an API call, a wait) or the job
  completes. Otherwise a deeply nested call chain would need one
  persisted scheduler tick per push/pop with no real progress to show
  for it. The linearization rule guarantees step never needs to pause
  mid-expression: effects only occur at instruction boundaries.

  SchedulerEvent is not one shape. A job waiting on a timer
  (cooldown, travel) resumes on a wake tick; a job waiting on an
  in-flight API call resumes when that response arrives. These need
  distinct cases up front, sketched before implementation:

    type SchedulerEvent =
        | WakeTick
        | ApiResponseReceived of jobId: JobId * attemptNumber: int * result: ApiResult

Imperative shell (Milestone 7; a minimal foreground loop stands in
  for it during Milestone 6):
  Reads jobs due to wake, calls step, executes the returned effects,
  persists results.

  When a job's effect queues an API call, the shell needs to route
  the eventual response back to the right job. Since each job only
  ever has one instruction in flight at a time, and §13's retry
  discipline guarantees the queue never issues a second physical
  call for that instruction while the first might still resolve,
  jobId alone is sufficient as the dispatch key — a simple jobId ->
  pending attempt table. There's no stale-response case to guard
  against, because there's never more than one live call per job to
  begin with. Attempt number is still worth recording alongside it,
  purely for logging and the idempotency classification already
  listed in §13's retry-safety fields.

Clock:
  Inject a Clock abstraction (real clock in production,
  fake/controllable clock in tests and in the fake SpaceTraders
  server, §13a) instead of calling DateTime.Now /
  DateTimeOffset.UtcNow directly inside scheduling logic.
```

This lets the scheduler's state-transition logic be unit tested with zero DB, zero network, and zero real waiting — advance the fake clock, assert the resulting state and effects. Fits F# well and avoids the usual pain of testing background schedulers only through slow, flaky end-to-end tests.

### Ship locks

A ship should normally belong to only one active job.

```txt
ship_id
job_id
locked_at
lease_expires_at
```

If a player starts a second program for the same ship:

```txt
Schiff A wird bereits von Pilot Max gesteuert.
```

Lease reclaim: `lease_expires_at` must have an owner. Use check-on-acquire — when acquiring a lock, if the existing lock's lease is expired, reclaim it and pause the orphaned job with a German explanation — plus a low-frequency sweep so an expired lease with no competing acquirer still gets its job paused visibly rather than lingering. Define this before Milestone 7 ships.

Clock-skew catch-up on resume: if the server was down for an extended period, some jobs' `next_wake_at` may be far in the past. On resume, process these immediately rather than assuming "due" only means "due a few seconds ago" — an implicit assumption that will otherwise produce subtle bugs the first time the machine is off overnight.

## 15. Background-job UI

Present jobs as robot pilots.

```txt
🤖 Pilot Max steuert Schiff A
🤖 Pilot Lina wartet auf Ankunft
🤖 Pilot Tom verkauft Fracht
```

### Main dashboard

```txt
Mission Control
- Credits
- Aktive Mission
- API-Warteschlange

Flotte
- Ships and their current state

Aktive Piloten
- Running background jobs

Pausierte Programme
- Jobs requiring attention

Logbuch
- Recent German activity messages
```

### Pilot card

Each card shows:

```txt
Pilot name
Program name
Assigned ship
Current block
Current action
Status
Last log entry
Pause
Fortsetzen
Stoppen
Programm öffnen
```

### Visual status examples

```txt
🤖 Pilot Max
Schiff: AURORA-1
Macht gerade: Fliege zu X1-DF55-A1
Status: Unterwegs

🤖 Pilot Lina
Schiff: MINER-2
Macht gerade: Baue Rohstoffe ab
Status: Wartet auf Abklingzeit
```

### Watch mode: a running program is watched, not edited

Jobs execute a compiled snapshot (`compiled_dsl_json` + `program_version`), but the highlight UI targets the *workspace*. If the child could edit the workspace while a job runs against the old snapshot, block IDs would stop matching and highlighting would silently break — or worse, mislead.

```txt
Opening a program that has an active (running/waiting/queued) job
  opens the workspace read-only (§3a setReadOnly), with the pilot's
  live status shown alongside: "Pilot Max fliegt gerade dieses
  Programm. Zum Bearbeiten musst du das Programm anhalten."
A prominent control offers pause/stop right there; the moment no
  active job references the program, the workspace unlocks for
  editing.
The same rule applies to a custom block's workshop while any running
  job's compiled snapshot includes that block.
Highlighting inside custom-block calls follows §9: the program view
  marks the caller as "innen aktiv"; opening the (read-only) workshop
  shows the inner highlight.
```

This is also the simpler model for a child: watching and building are visibly different modes, and a pilot never has the rug pulled out from under it.

### Program view

When opening a job, highlight:

```txt
Current block (top frame's current position, see §14)
Last completed block
Waiting block
Failed block
```

The activity log should explain the state:

```txt
Das Schiff ist unterwegs und kommt in 42 Sekunden an.
Das Schiff wartet noch 18 Sekunden, bevor es wieder abbauen kann.
Der Marktplatz verkauft diese Ware nicht.
```

## 17. Repository structure

```txt
/src
  /SpaceKids.Client
    package.json           # Blockly pinned exact; esbuild; see §3b
    tsconfig.json
    /Blockly
      blockly-host.ts      # the seam (§3a) — sole owner of Blockly
      blocks.ts            # German block definitions incl. generated
                           # custom-block callers and the definition
                           # shell block + typed-input mutator
      toolbox-de.ts
      workspace-serialization.ts
    /i18n
      de.ts
      en.ts
    /Pages
    /Components
    /FleetDashboard
    /JobDashboard
    /MissionControl
    /Blockwerkstatt        # workshop view + block library list (§9)

  /SpaceKids.Server
    /Api
    /Auth
    /Persistence           # incl. backup task (§12)
    /RequestQueue
    /RateLimiting
    /ProgramRunner
    /BackgroundJobs
    /ShipLocks
    /Caching

  /SpaceKids.Core
    /Domain
    /Dsl                   # incl. expression linearization (§10)
    /Validation
    /Blocks
    /CustomBlocks
    /Missions
    /Localization
    /Jobs
    /Scheduling            # pure scheduler core lives here, framework-free

  /SpaceKids.SpaceTraders
    /Client
    /Models
    /Mapping

  /SpaceKids.FakeSpaceTraders   # in-process fake API (§13a)

/tests
  /SpaceKids.Core.Tests
  /SpaceKids.Server.Tests
  /SpaceKids.IntegrationTests   # run against FakeSpaceTraders

/docs
  00-project-map.md
  01-gameplay-notes.md
  02-api-notes.md
  03-dsl-spec.md
  04-block-catalog.md
  05-agent-handoff.md
  06-localization.md
  07-missions.md
  08-persistence.md
  09-rate-limiting.md
  10-background-jobs.md
  11-custom-blocks.md
  12-testing.md
  decisions.md

/prompts
  codex-next-step.md
  grok-next-step.md

README.md
TODO.md
```

## 18. Documentation and agent handoff

Before beginning a coding session, read:

```txt
README.md
TODO.md
docs/00-project-map.md
docs/05-agent-handoff.md
docs/decisions.md
```

At the end of every session, update:

```txt
docs/05-agent-handoff.md
TODO.md
docs/decisions.md
```

### Required handoff format

```md
# Current state

## Working
What works now.

## Changed this session
Files changed and why.

## Known issues
Broken, incomplete, or uncertain behaviour.

## Next tasks
1. One small concrete task.
2. One small concrete task.
3. One small concrete task.

## Commands
Commands for build, test, database reset, and local run.

## Important constraints
Rules future agents must not violate.
```

### Required constraints in every handoff

```txt
German child-facing UI; English internals.
Primitive API blocks only.
No direct browser-to-SpaceTraders calls.
All API calls use the global request queue.
Jobs must persist safely.
Never blindly retry uncertain non-idempotent API actions; never
  issue a second physical call while the first might still resolve.
Reconciliation decisions use per-ship signals; credits deltas are
  corroborating evidence only.
The Blockly instances are owned entirely by the TS seam — Elmish only
  ever sees JSON strings crossing that boundary.
Custom-block definitions live in the Blockwerkstatt and the
  custom_blocks tables — never inline in a program's workspace JSON.
Inline DSL expressions are pure; every effectful value is its own
  instruction with a resultTarget.
Custom blocks execute as real function calls with a call stack, not
  inlined at compile time. JobState is stack-based with path positions
  and per-frame locals from Milestone 7 onward, even before
  Milestone 9 introduces actual calls.
A program with an active job is read-only (watch mode); editing
  requires pausing or stopping the job.
Custom block versioning is intentionally not fully enforced yet —
  only a structural mismatch check exists. Do not build full
  upgrade/pinning logic without discussing it first.
The pinned Blockly version is a recorded decision; do not bump it
  casually.
Integration tests run against SpaceKids.FakeSpaceTraders, not the
  live API.
```

## 19. Milestones

This sequencing front-loads a toolchain-plus-interop spike (including the mutator risk that previously sat untested until Milestone 9) and a visible slice of real data before deeper investment. It also moves the pure scheduler core into the first runner milestone so nothing built in M6 is rewritten in M7.

### Milestone 0: Toolchain and Blockly↔Bolero interop spike

Before committing to the rest of the plan, de-risk the genuinely uncertain integrations — both of them.

Part A — toolchain (§3b):

* Set up package.json with Blockly pinned to an exact version; record the version and, at that version, where the procedure blocks live (core vs plugin) in `docs/decisions.md`.
* Wire esbuild bundling of the TS seam into the .NET build so a fresh checkout builds with standard commands.

Part B — seam basics (§3a):

* Read the JSInterop wiring in `SoftwareDriven.Blockly.Blazor` (NuGet) and any other public Blockly-in-Blazor writeups as reference before writing the seam from scratch.
* Minimal Bolero app with a single page.
* Embed Blockly via the isolated TS seam.
* Load a trivial German toolbox (two or three blocks).
* Serialize the workspace to JSON and save it to SQLite.
* Reload it into a fresh Blockly instance.
* Highlight one block by ID from F#.
* Confirm `onWorkspaceChanged` fires on meaningful events only, not on every drag frame.
* Toggle read-only mode from F# (watch mode primitive).

Part C — custom-block mechanics mini-spike (§9):

* One hand-rolled definition shell block with a working mutator that adds/removes one *typed* input.
* Generate a caller block from a stored signature and inject it into a *second* workspace's toolbox via `updateToolbox`.
* Change the signature; regenerate the caller; confirm the second workspace picks it up.

Done when:

```txt
A trivial German block program can be created, saved, reloaded, and a
block can be highlighted from Elmish — and a typed-input definition in
one workspace produces a working, regenerable caller in another.
```

If this fights the framework hard — several failed approaches, fighting Blazor's interop model at every step — stop and reconsider the client stack before investing further. Part C is deliberately in scope here so that Milestone 9 no longer carries a separate, untested interop risk: what remains for M9 is breadth (more input types, the workshop UI, library management), not a new category of unknown.

### Milestone 1: Foundation

* Create F# solution.
* Create Bolero client and server.
* Initialize SQLite (decide on WAL mode / busy_timeout here — see §12).
* Add migrations.
* Add the periodic `VACUUM INTO` backup task with retention (§12).
* Create project documentation.
* Add CI build and test command (including the JS bundle step).

Done when:

```txt
The app starts, the database initializes, backups appear, and tests run.
```

### Milestone 2: Real data, no Blockly yet

* Implement token flow.
* Read agent, ships, contracts, waypoints, and markets.
* Add server-side API proxy.
* Route every call through a minimal single-lane queue stub from day one — no priorities, no backoff sophistication yet, just "one request at a time, logged." Section 2's core principle ("all API traffic passes through a global queue") is non-negotiable even at this early stage; the alternative is an ad hoc HTTP path now that gets rewired through the real queue in Milestone 5, which is avoidable rework.
* Start `SpaceKids.FakeSpaceTraders` (§13a) with exactly the endpoints the client consumes so far; point the first integration tests at it.
* Show it on a plain, unstyled Bolero page.

Done when:

```txt
The dashboard shows real SpaceTraders data for a real agent, every
call already passes through the queue stub, and the same client code
runs green against the fake in tests. This is the first genuinely
rewarding milestone and validates the API client and auth flow early,
independent of the Blockly risk.
```

### Milestone 3: Blockly in German (full integration)

* Author the block catalog (`docs/04-block-catalog.md`) with German labels *and* DSL instruction shapes together (§7) — the catalog feeds both this milestone and the next.
* Build out the toolbox from the catalog.
* Add all primitive German blocks planned for the first release (custom-block callers come in Milestone 9).
* Save and restore workspace JSON from SQLite (already proven in Milestone 0).
* Highlight the selected block during a fake/simulated run.

Done when:

```txt
A player can create, save, and reopen a full German block program.
```

### Milestone 4: DSL and validation

* Define DSL types, including the custom-block collection, the `callCustomBlock` instruction shape, and `resultTarget` (§10).
* Compile Blockly workspace into DSL, including expression linearization: hoist effectful value blocks into instructions writing frame-local temporaries; enforce the "inline arguments are pure" invariant in the compiler (§10).
* Validate DSL, including scope checks, the custom-block structural mismatch check, transitive-closure completeness, and cycle detection (§9, §11).
* Return German validation errors.

Done when:

```txt
Invalid programs are rejected before they can run, and a program using
an information block inside an expression compiles to flat instructions
with temps.
```

### Milestone 5: Request queue

* Enrich the queue stub from Milestone 2 — this is growth, not a rewrite.
* Add priority levels and aging capped at priority 2 (§13).
* Add 429 handling.
* Add retry logic split into definite vs ambiguous failure classes (§13).
* Add request history.
* Add queue status UI.
* Add server-reset detection (§13), after checking the current reset cadence in SpaceTraders' docs.
* Add the API-unreachable state with German messaging, distinct from resets (§13); exercise both via the fake's fault injection.

Done when:

```txt
All API traffic is queued, retried safely, observable, and resilient
to both a SpaceTraders server reset and plain API downtime — with the
ambiguous-failure and outage paths demonstrated in tests against the
fake, not just reasoned about.
```

### Milestone 6: Runner on the pure scheduler core

* Build the pure `step` core with Clock abstraction, `SchedulerEvent` cases, and the stack-of-path-positioned-frames JobState shape (§14) — one frame deep for now, in memory, driven by a simple foreground loop.
* Unit test the core without DB/network/real time.
* Add ship selection, navigation, orbit and dock, extraction, market buy and sell.
* Add per-action reconciliation logic for ambiguous-failure retries, per-ship signals only (§13); test each against the fake's drop-after-processing fault.
* Add German activity logs.
* Add step mode (drives the same core one event at a time).

Done when:

```txt
A player can run and single-step a primitive program against one real
ship; a network failure mid-action does not risk a duplicate buy,
sell, or extract; and the core that executed it is the same one
Milestone 7 will persist — nothing here is throwaway.
```

### Milestone 7: Persistent background jobs

* Persist jobs and execution state (call stack with path positions, per-frame locals including temps, denormalized current_block_id) — the shape already exists from Milestone 6; this milestone adds the durable shell around it.
* Add the scheduler shell: wake-ups via `next_wake_at`, response routing via the jobId dispatch table (§14).
* Add ship locks, including check-on-acquire lease reclaim plus the low-frequency sweep (§14).
* Resume safe jobs after restart, with clock-skew catch-up (§14) — including a job persisted mid-loop resuming with its counter intact.
* Add watch mode: programs with active jobs open read-only with pause/stop controls (§15).
* Add pilot dashboard.

Done when:

```txt
A program can continue safely while the player uses another screen or
returns later; a job stopped mid-loop overnight resumes correctly; and
a running program cannot be edited out from under its pilot.
```

### Milestone 9: Custom blocks

* Build the Blockwerkstatt view: per-block definition workspace, block library list with rename/delete (delete refuses with a German message while referenced) (§9).
* Extend the Milestone 0 Part C mechanics to the full typed input set; generate parameter getter blocks from signatures.
* Inject generated caller blocks into the "Eigene Blöcke" toolbox category of all program workspaces and other workshops; regenerate and push toolbox updates on signature change.
* Compile custom block bodies once each, with `callCustomBlock` call sites and resultTargets; enforce transitive closure (§10).
* Implement call-stack push/pop in the scheduler core (§9d, §14) — the JobState shape from Milestone 6/7 already supports this.
* Return structured result records via the return-value socket; add accessor blocks.
* Wire caller/workshop highlighting ("innen aktiv" + inner highlight) (§9, §15).
* Persist a version number (not yet enforced beyond the structural mismatch check — §9).
* Reuse blocks across programs.

Done when:

```txt
A child can define "Baue Erz ab" in its own Werkstatt, call it from
two different programs as a real function call, watch execution step
into it, and editing a block that's already in use produces a clear
German message rather than a silent break.
```

### Milestone 10: Fleet mode

* Run several jobs.
* Show several pilots.
* Add pause, resume, stop, and inspect controls.
* Improve queue fairness.
* Add fleet-level logs.
* Verify reconciliation stays correct with concurrent pilots (credits deltas now genuinely race — §13).
* ~~Make insufficient-credits contention a friendly German runtime error.~~ Dropped during Milestone 10: the real SpaceTraders API has no such error — credits are documented as able to go negative if overdrawn, and no error code for an unaffordable purchase exists anywhere in the OpenAPI spec. This bullet was written on an unverified assumption; see `docs/decisions.md`.

Done when:

```txt
Several ships can work independently without conflicting commands,
duplicate trades, or overwhelming the API.
```

### Later idea (not yet scheduled): visual system map

Noted during Milestone 9's block-catalog work, not designed yet. The fleet/pilot
dashboards planned above are text/card-based (§19's own wording — "dashboards,
cards, logs"); nothing here currently gives a graphical view of a system's
waypoints or a ship's position within it. Once the DSL's Wegpunkt/Schiff records
(§8) are flowing through real programs, a simple SVG/canvas rendering of the
current system (waypoints plotted by their x/y, ships overlaid) is a fairly
self-contained addition — likely its own milestone after Custom blocks/Fleet mode,
not squeezed into either. Revisit once there's an actual need driving it (e.g. a
mission or fleet-mode UI that would clearly benefit).

## 20. Definition of success

The project succeeds when the child can:

1. Read and use German visual blocks.
2. Build a program without typing code.
3. Solve trading and contract problems from primitive actions.
4. Use variables, loops, conditions, lists, and data records.
5. Create reusable custom blocks with several inputs in the Blockwerkstatt, as real function calls, and use them across different programs.
6. Run programs in the background.
7. Control several ships through separate pilots.
8. Understand program state through German logs.
9. Return later and safely continue saved programs.
10. Learn programming concepts without being exposed to API rate-limit mechanics.