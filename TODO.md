# TODO

See `docs/05-agent-handoff.md` for full context on each of these.

## Milestone 1: Foundation

- [ ] Real SQLite schema (§12): `workspaces`, `programs`, `custom_blocks`, `jobs`,
      `ship_locks`, `api_cache`, `request_queue_events`, `schema_versions`.
- [ ] WAL mode + `busy_timeout` pragma.
- [ ] Migrations.
- [ ] Hourly `VACUUM INTO` backup task with retention.
- [ ] Delete the Milestone 0 spike's `spike_workspaces` table / `Persistence.fs` /
      `WorkspaceRemoting.fs` once superseded.
- [ ] CI build+test workflow.

## Milestone 2: Real data, no Blockly yet

- [ ] Token flow.
- [ ] Read agent, ships, contracts, waypoints, markets from the real API.
- [ ] Minimal single-lane request queue stub (§13 non-negotiable from day one).
- [ ] Grow `SpaceKids.FakeSpaceTraders` to cover the endpoints consumed so far; point
      first integration tests at it.

## Later milestones

See `plan.md` §19 for the full milestone list (3 through 10).
