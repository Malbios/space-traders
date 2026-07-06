# TODO

See `docs/05-agent-handoff.md` for full context on each of these.

## Milestone 1: Foundation — done

- [x] Real SQLite schema (§12): full 12-table set, `schema_versions`-tracked migrations.
- [x] WAL mode + `busy_timeout` pragma.
- [x] Migrations (hand-rolled `.sql` + `MigrationRunner.fs`).
- [x] Hourly `VACUUM INTO` backup task with retention.
- [x] Retired the Milestone 0 spike's `spike_workspaces` table / `Persistence.fs`;
      `WorkspaceRemoting.fs` now backed by the real `workspaces` table.
- [x] CI build+test workflow.

## Milestone 2: Real data, no Blockly yet — done

- [x] Token flow (paste an existing SpaceTraders token — see docs/decisions.md).
- [x] Read agent, ships, contracts, waypoints, markets from the real API.
- [x] Minimal single-lane request queue stub (§13 non-negotiable from day one).
- [x] Grew `SpaceKids.FakeSpaceTraders` to cover the endpoints consumed so far; pointed
      integration tests at it.
- [x] Verified against the real live SpaceTraders API, not just the fake.

## Milestone 3: Blockly in German (full integration) — done

- [x] Authored the block catalog (`docs/04-block-catalog.md`) with German labels and DSL
      instruction shapes together (§7).
- [x] Built out the toolbox from the catalog (6 categories).
- [x] Added all primitive German blocks planned for the first release (20 new custom
      blocks; the rest turned out to be stock Blockly blocks already available).
- [x] Save/restore workspace JSON from SQLite (mechanism already proven in Milestone 0).
- [x] Highlight the selected block during a fake/simulated run.

## Milestone 4: DSL and validation — done

- [x] Defined DSL types: custom-block collection, `callCustomBlock` instruction shape,
      `resultTarget` (§10).
- [x] Compiled Blockly workspace JSON into the DSL, including expression linearization
      (hoist effectful value blocks into instructions writing frame-local temporaries;
      enforce "inline arguments are pure") (§10).
- [x] Validated the DSL: scope checks, custom-block structural mismatch check,
      transitive-closure completeness, cycle detection (§9, §11).
- [x] Returned German validation errors.

## Milestone 5: Request queue — done

- [x] Enriched the Milestone 2 queue stub into a real priority queue (`BackgroundService`
      worker draining a lock-protected pending list), not a rewrite of its call sites.
- [x] Added priority levels and aging capped at priority 2 (§13).
- [x] Added 429 handling (real `Retry-After`, bounded retry).
- [x] Added retry logic split into definite vs ambiguous failure classes (§13):
      `HttpRequestException` retried, post-send `TaskCanceledException` surfaced as
      `AmbiguousFailure`, never auto-retried.
- [x] Added request history (`priority`/`attempt` columns on `request_queue_events`).
- [x] Added queue status UI ("Warteschlange" section, manual refresh).
- [x] Added server-reset detection (§13) — confirmed live reset cadence is weekly;
      German copy avoids hardcoding a date.
- [x] Added the API-unreachable state with German messaging, distinct from resets (§13);
      exercised both via the fake's fault injection (`POST /_fault/mode`).

## Milestone 6: Runner on the pure scheduler core — done

- [x] Built the pure `step` core (`SpaceKids.Core/Scheduler/`) with `Clock` abstraction,
      `SchedulerEvent` cases, and the stack-of-path-positioned-frames `JobState` shape
      (§14) — one frame deep for now, driven by a simple in-memory foreground loop
      (`JobRunner.fs`).
- [x] Unit tested the core with zero DB/network/real time (fake clock, fabricated
      `ApiResponseReceived` events).
- [x] Added ship selection, navigation, orbit and dock, extraction, market buy and sell
      (6 real POST endpoints on `SpaceTradersClient`, verified field-by-field against
      the live OpenAPI spec).
- [x] Added per-action reconciliation logic for ambiguous-failure retries, per-ship
      signals only (§13); tested each against the fake's `drop-after-processing` fault
      (unit-level fabricated reconciliation + integration-level real HTTP path).
- [x] Added German activity logs.
- [x] Added step mode (`JobService.step`), driving the same core one event at a time,
      alongside run mode (`JobService.run`).

## Milestone 7: Persistent background jobs — done

- [x] Persisted jobs and execution state (`jobs`/`programs`/`ship_locks` tables, a
      real first use since Milestone 1) — the same `JobState`/`Step.step` shape
      Milestone 6 built, unchanged beyond adding pause/resume/cancel.
- [x] Added the real scheduler shell (`JobScheduler.fs`): resumes every non-terminal
      job on startup (ambiguous-failure recovery for anything mid-call, clock-skew
      catch-up for anything waiting), then polls due jobs and refreshes ship-lock
      leases every tick.
- [x] Added ship locks (§14): check-on-acquire lease reclaim plus a low-frequency
      sweep, both reusing the same orphan-pause path.
- [x] Resume-safe restart, proven via a fresh `JobScheduler.resumeAll` call against
      the same on-disk database, including a job persisted mid-wait resuming with
      its position intact.
- [x] Added pause/resume/cancel to the pure scheduler core (deferred while an
      action/reconciliation is in flight, never abandoning it) and wired them into
      the pilot dashboard.
- [x] Added watch mode: the shared workspace goes read-only while any pilot is
      active (global, not per-program — no saved/named programs yet; made
      per-program in Milestone 11).
- [x] Added the pilot dashboard (multiple concurrent jobs, one per ship).

## Milestone 9: Finish the block catalog — done

- [x] Part A — the 5 remaining action blocks (survey, deliverContract, acceptContract,
      purchaseShip, refuel): new `SpaceTradersClient` methods verified against the
      live OpenAPI spec, new fake endpoints, new reconciliation paths for the two
      actions with no ship-local signal (`acceptContract` via a contract fetch,
      `purchaseShip` via a fleet-count fetch).
- [x] Part B — the 9 information blocks + the §8 data model: `Value.VRecord`, a real
      `Eval.Accessor`, a new info-read scheduler path (no reconciliation needed — a
      GET is always safe to retry), `JobRunner.runInfoRead`'s record conversion, and
      26 new accessor blocks (a 7th "Zugriffe" toolbox category).
- [x] Found and fixed a real pre-existing bug (Milestone 6) via live verification: an
      action's start-log message always landed after its result due to effect
      ordering.
- [x] All 20 SpaceTraders catalog blocks now actually run — 86 tests total, all green.

## Milestone 9: Custom reusable blocks (§9) — done

(This is plan.md's actual, numbered Milestone 9 — the section above, "finish the
block catalog," was informally called that at the time too; see `docs/decisions.md`
for the naming note.)

- [x] Part A — real call-stack execution: `JobState.stack` push/pop on
      `CallCustomBlock`, argument binding, `returnExpr` evaluated into the caller's
      `resultTarget`, a suspending call keeps the caller frame on the stack.
- [x] Part B — `CustomBlockRepository.fs` (append-only versioning, delete-usage
      refusal), `JobRemoting.fs`'s `lookup` wired to real persistence.
- [x] Part C — the real typed-input mutator (6 types), one generic `callCustomBlock`
      caller block type, one generic `sk_param_get` getter, structured-record
      outputs (`Expr.RecordLiteral`, `sk_build_record`, dynamic accessor blocks).
- [x] Part D — the Blockwerkstatt UI: block library (create/open/rename/delete) +
      workshop view, wired to a new `CustomBlockRemoting.fs`.
- [x] Part E — `Step.blockIdPerFrame`, an "innen aktiv" indicator + "Block öffnen"
      affordance on the program view while a call is in flight, a per-pilot
      watch/poll loop.
- [x] Found and fixed a real bug during Part E's own live verification: a stack
      frame whose position had already advanced past its own last instruction (a
      common case, not an edge one — several existing suspend paths do this
      deliberately) was silently dropped, making "innen aktiv" undetectable in
      exactly that case.
- [x] 101 tests total, all green; live Playwright verification after Parts C, D, E.

## Milestone 10: Fleet mode (§13/§14/§15) — done

Most of §19's bullets ("run several jobs," "show several pilots," "pause, resume,
stop") were already satisfied by Milestone 7 — no new work needed there.

- [x] Part A — queue priority differentiation: `JobRunner.fs`'s queue calls thread
      a real `priority: int` instead of a single hardcoded tier;
      `JobScheduler.tickOnce`'s background sweep now uses `backgroundPriority` (3),
      distinct from a player's own interactive step/run (1).
- [x] Part B — a fleet-level "Logbuch" panel in `Main.fs` aggregating every active
      pilot's last activity line (reuses existing `JobSummaryDto.lastLogLine`, no
      schema/remoting change).
- [x] Part C — an integration test proving two ships trading concurrently, one
      mid-`Reconciling` from an ambiguous failure while the other completes a real
      credits-changing trade, don't cross-contaminate each other's outcome.
- [x] Dropped "insufficient-credits friendly error": checked the real SpaceTraders
      OpenAPI spec before writing any code — credits can go negative in the real
      game, no such error exists to translate. plan.md's own bullet was written on
      an unverified assumption; corrected there too.
- [x] 103 tests total, all green; live Playwright verification after Part B.

## Entity inspector + visual system map — done

plan.md's "later idea" from Milestone 9 (a static system map), redirected by the
user mid-planning into a real drill-down inspector: click a ship, see all its
details, open the waypoint it's at, see traits and every ship there, load
market/shipyard on demand.

- [x] Part A — `Waypoint` gained `traits: WaypointTrait list` (free — the real
      `ListWaypoints` call already returns this); the fake's fixture gained
      plausible trait data (HQ: MARKETPLACE + SHIPYARD; asteroid field: a
      mining trait only).
- [x] Part B — `AgentService` gained lazy `getWaypointMarket`/
      `getWaypointShipyard`; promoted the duplicated `SYSTEM-WAYPOINT` symbol
      helper to a single shared `Waypoint.systemSymbolOf`; the fake's
      market/shipyard endpoints now 404 for a waypoint without the matching
      trait instead of answering unconditionally.
- [x] Part C — the inspector UI: `InspectedEntity` selection state, a ship
      panel (every `Ship` field) and a waypoint panel (traits, ships present,
      gated market/shipyard buttons), full cross-navigation between them.
- [x] Part D — `viewSystemMap`: pure F#/Bolero.Html SVG (no JS interop needed),
      waypoints as colored circles, ships as triangles (real elapsed-time
      interpolation for an in-transit ship), both clickable into the same
      inspector, auto-refreshing via a `MapTick` reusing the `WatchTick`
      pattern.
- [x] Found and fixed two real bugs while writing Part B's own tests: the
      fake's market/shipyard endpoints didn't 404 for the wrong waypoint at
      all (so the "no market here" path was unexercisable); and the new
      404-detection missed the case where `RequestQueue.enqueue`'s exception
      arrives wrapped in a single-inner `AggregateException` (the same
      Async<->Task interop quirk `JobRunner.fs`'s `classifyException` already
      has to account for).
- [x] 107 tests total, all green; TypeScript typecheck clean; live Playwright
      verification of the full drill-down chain and the map's click/auto-move
      behavior.

## Milestone 11: Saved/named multiple-program library — done

Replaces the one hardcoded shared Blockly workspace (`"blockly-spike"`) with a
real, listable, renameable, deletable collection of programs — closing two
gaps this had been blocking: per-program watch mode, and a real call site for
`Validator.revalidateAgainstCurrentDefinitions` (built in Milestone 9, never
called).

- [x] Part A — `program_definitions` table (new migration, 1:1 with its own
      `workspaces` row by shared id, mirroring `custom_blocks`/
      `custom_block_versions`); `ProgramRepository` create/rename/list/delete
      (delete refused only while a currently non-terminal job actually flies
      the program — a completed/cancelled job's history doesn't block it).
- [x] Part B — `JobState`/`JobSummaryDto` gained `programId`, sourced from the
      existing `workspaceId` parameter `JobRunner.startJob` already received
      (no new parameter needed).
- [x] Part C — `Validator.revalidateAgainstCurrentDefinitions`'s real call
      site: `ProgramRemoting.fs`'s `loadDefinition` compares a reopened
      program's last compiled snapshot against live custom-block definitions,
      surfaced as a dismissible warning banner (not blocking the load).
- [x] Part D — `ProgramService`/`ProgramRemoting.fs` (mirrors
      `CustomBlockService`'s shape); a "Programme" library UI (in-page view
      switch, no real Bolero routing yet, matching the custom-block library's
      own list/workshop toggle) with per-program container switching —
      `model.containerId` *is* the open program's own database id, so the
      pre-existing Speichern/Laden messages needed zero changes.
- [x] Part E — per-program watch mode: `PilotsLoaded`'s lock computation now
      filters pilots by the currently-open program's id, so a pilot flying a
      *different* program no longer locks this one. Found and fixed a real
      gap during live verification: opening a different program didn't
      re-trigger the lock recomputation on its own, so it kept showing the
      previously-open program's stale lock state until `OpenProgram` was
      changed to dispatch `LoadPilots` itself.
- [x] Found and fixed a real bug during live verification unrelated to this
      milestone's own logic: a leftover `ship_locks` row from local dev
      testing, referencing a job serialized before `programId` existed,
      crashed the whole server on startup (`JobScheduler`'s orphan sweep threw
      an unhandled `JsonException` deserializing it) — cleared the stale row,
      not a code fix, since this is pre-existing dev-only data.
- [x] 116 tests total, all green; TypeScript typecheck clean; live Playwright
      verification via a scripted `playwright` driver (no interactive browser
      tool was available this session) covering create/open/edit/save/
      reopen/rename/delete, and two open programs proving per-program watch
      mode's isolation.

## Milestone 12: Bilingual support (German/English) — done

A second, English-speaking child needed a real runtime-switchable second
language, not just a dev convenience — the app was previously German-only by
deliberate design (`plan.md` §4).

- [x] Part A — `app_settings` (new table, single row) +
      `SettingsRepository`/`SettingsRemoting.fs`/`SettingsService`; a
      Deutsch/English switcher in the UI, persisted server-side.
- [x] Part B — decoupled the DSL's `VRecord` contract from display language:
      `Compiler.fs`'s `ACCESSOR_BLOCKS` map and `JobRunner.fs`'s record
      builders now use canonical English keys (e.g. `"CargoCapacity"`)
      instead of the German words they used to double as.
- [x] Part C — `blocks-catalog.ts`/`blocks.ts`/`toolbox-de.ts` read a shared
      `locale-state.ts` flag live inside each block's own `init()`;
      `blockly-host.ts`'s new `setLocale` entry point re-renders every open
      workspace (destroy + reinit from its own serialized JSON) — block
      *types* are stable identifiers, so a saved program is never
      invalidated by a language switch.
- [x] Part D — `Main.fs`'s ~45 UI strings became a `Strings` record (`de`/
      `en` values) — a missing translation is a compile error, not a silent
      runtime gap.
- [x] Part E — server-side error messages (`Validator.fs`, a couple of
      `JobRunner.fs` literals, `ProgramRepository.delete`'s refusal message)
      translated by the stored locale. `Compiler.fs`'s own compile-time
      errors were a deliberate, documented exception at the time — closed in
      Milestone 13/Part A.
- [x] Found and fixed a real bug during Part A's own live verification: the
      persisted locale setting loaded into the model at startup but was
      never applied to the Blockly JS side, so a freshly loaded page kept
      rendering new blocks in German regardless of the saved setting.
- [x] 119 tests total, all green (3 new for Part E's German/English message
      parity); TypeScript typecheck clean; live Playwright verification via
      a scripted `playwright` driver (no interactive browser tool available
      this session) after Parts A, C, and D — the switcher persists across
      reload, a program's catalog/accessor block labels and toolbox
      category names switch language with its serialized block types
      provably unchanged, and representative UI text throughout the page
      switches with zero console/page errors.

## Milestone 13: compiler translation, block type-checking, job history, pilot flavor — done

Four independent, previously-flagged known limitations, bundled into one
milestone since each was small-to-medium alone; parts shipped/verified one at
a time.

- [x] Part A — `Compiler.fs`'s own compile-time errors (missing input,
      unknown block type, cycle/not-found, etc.) are now locale-aware too,
      closing the one deliberate exception Milestone 12/Part E left open.
- [x] Part B — every catalog/primitive/accessor block input and output now
      carries a real Blockly `.setCheck` type (`"Number"`/`"String"`/
      `"Boolean"`/`"List"`, or a synthetic record-shape check like
      `"ShipRecord"`/`"MarketRecord"`), so Blockly itself refuses a
      mismatched connection at edit time. `Validator.fs`'s existing
      literal-only server-side check is unchanged — a complementary backstop,
      not replaced.
- [x] Part C — a new "Verlauf"/"History" section reads the most-recent-50
      terminal jobs straight from the persisted `jobs` table
      (`JobRepository.listHistory`), so a finished run stays visible even
      after the server process restarts (unlike `JobRunner.fs`'s in-memory
      dashboard, which still forgets terminal jobs on restart exactly as
      before).
- [x] Part D — pilot cards show a stable, deterministic name per ship (a
      char-sum hash into a small shared name pool, not `GetHashCode`, which
      is per-process-randomized) — no name field exists in the real
      SpaceTraders API data, so this is invented, not read from anywhere.
- [x] 122 tests total, all green (1 new Server test for `listHistory`); Parts
      B and D needed no new automated tests (verified live instead, per the
      plan). `npm run typecheck` clean. Live Playwright verification: Part B
      — a hand-built mismatched connection (`getMarket` → `shipFuel`)
      throws `"Connection checks failed"` from inside Blockly's own
      deserialization (a hard refusal, not a silent drop), a correctly-typed
      connection still loads normally; Part C — a finished run survived an
      actual `SpaceKids.Server` process restart; Part D — the same ship
      showed the same name across a page reload.

## Backlog / known gaps (not yet scheduled)

Surfaced while building an example "scan all shipyards for a mining drone"
program with a user, not yet planned into a milestone.

- [x] `getShipyard`/`getMarket` still hard-fail the whole job with
      `ApiFailed` on a 404, but a program no longer needs to risk it —
      `waypointHasShipyard`/`waypointHasMarket` accessor blocks expose the
      waypoint's already-fetched trait data, so a "check every waypoint"
      program can filter safely before ever calling `getShipyard`/
      `getMarket`. Verified live with a real forEach-over-every-waypoint
      program.
- [x] No `true`/`false` (`logic_boolean`) block in the toolbox — added
      (`Compiler.fs`: `Literal(BoolLit ...)`, plus a `toolbox-de.ts` entry).
- [x] Audited the rest of the stock-Blockly/programming category for other
      obviously-missing block kinds — added `logic_operation` (AND/OR) and
      `logic_negate` (NOT), both fully wired (`Types.fs`'s `Expr`, `Eval.fs`
      short-circuiting, `Compiler.fs`, toolbox). See
      `docs/04-block-catalog.md`'s "Additional stock blocks" section.
- [ ] `controls_flow_statements` (break/continue) — found during the above
      audit and a real, concretely-felt gap (no way to stop a `forEach`
      early once a "found" condition is hit, today faked with a boolean
      flag variable checked at the top of every loop body). Not a quick
      toolbox add: needs loop-exit semantics threaded through `Step.fs`'s
      `ForEach`/`WhileUntil`/`Repeat` handling in the pure scheduler core
      (no `Break`/`Continue` `Instruction` case exists today), so it
      deserves its own planned pass rather than being folded into a
      "missing block" audit.
- [ ] Make the system map (`viewSystemMap`, Galaxie tab) zoomable — currently
      a fixed 400x400 SVG with no pan/zoom controls.
- [ ] Ship selection is mandatory to run a program in the Piloten tab, even
      for programs that never touch any ship (e.g. a shipyard-scanning/
      ship-purchasing program). `JobState`'s `shipSymbol` is a required
      field, `startJob` always takes an exclusive `ship_locks` lease for it
      (`JobRunner.fs:700`), and the whole pilot dashboard/watch mode is
      keyed by ship — there's no "job with no ship" concept anywhere today.
      Making ship-agnostic programs skip this would mean: an optional
      `shipSymbol` on `JobState`, ship-lock acquire/release skipped when
      absent, a `ship_locks`/migration change, reworking the pilot
      dashboard's per-ship grouping, and the compiler/validator knowing
      whether a given program references any ship-scoped block at all.
      Cross-cutting, needs a real design pass, not a quick fix. Workaround
      today: just pick any spare ship — a ship-agnostic program ignores it
      harmlessly.
- [x] Dark mode now covers buttons/inputs/selects (CSS) and the Blockly
      workspace (a real dark `Blockly.Theme` wired through `setTheme`).
- [x] `controls_forEach`'s LIST input now has a `.setCheck("List")`
      constraint (`registerStockBlockChecks`), refusing a record wired in
      directly at edit time instead of only failing at runtime.
- [x] Finished pilot cards can now be dismissed from the live Piloten
      dashboard (`JobRunner.dismiss`) without touching persisted History.
- [x] Checked every other stock control-block socket live: `controls_if`'s
      IF0/`controls_whileUntil`'s BOOL already check `"Boolean"`,
      `controls_repeat_ext`'s TIMES already checks `"Number"` — none of
      these were actually broken. Only `logic_compare`'s A/B operands had
      no check; now restricted to `["String","Number","Boolean"]`.

## Later milestones

Milestone 8 ("first missions") was removed from the roadmap entirely — not deferred,
gone. See `docs/decisions.md` for the removal rationale. See `plan.md` §19 for the
remaining milestone list.
