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

## Later milestones

See `plan.md` §19 for the full milestone list (7 through 10).
