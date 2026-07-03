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

## Milestone 2: Real data, no Blockly yet

- [ ] Token flow.
- [ ] Read agent, ships, contracts, waypoints, markets from the real API.
- [ ] Minimal single-lane request queue stub (§13 non-negotiable from day one).
- [ ] Grow `SpaceKids.FakeSpaceTraders` to cover the endpoints consumed so far; point
      first integration tests at it.

## Later milestones

See `plan.md` §19 for the full milestone list (3 through 10).
