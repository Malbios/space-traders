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
      active (global, not per-program — no saved/named programs yet).
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

## Later milestones

Milestone 8 ("first missions") deliberately skipped — explicit user feedback that
guided-mission/pedagogy work isn't the current priority; practical tool capability
(finishing the block catalog, then custom blocks) is. See `plan.md` §19 for the full
milestone list (8, 10) and `docs/decisions.md` for the skip rationale.
