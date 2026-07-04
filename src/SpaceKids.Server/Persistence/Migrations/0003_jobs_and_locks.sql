-- Milestone 7 (plan.md §14): persisted background jobs + ship locks.
--
-- jobs.state is the coarse status tag (Running/WaitingForArrival/Paused/Completed/...)
-- mirroring JobRemoting's toDto mapping — cheap to query for the dashboard without
-- deserializing jobs.execution_state_json per row, same reasoning as current_block_id.
-- jobs.execution_state_json holds just the mutable execution state (status, stack,
-- lastKnownShip, log, pause/cancel-pending flags); the compiled program itself lives
-- in the programs row referenced by program_id (already NOT NULL in 0001_initial.sql).

ALTER TABLE jobs ADD COLUMN assigned_ship_symbol TEXT;
ALTER TABLE jobs ADD COLUMN current_block_id TEXT;
ALTER TABLE jobs ADD COLUMN next_wake_at TEXT;
ALTER TABLE jobs ADD COLUMN last_error TEXT;

ALTER TABLE ship_locks ADD COLUMN lease_expires_at TEXT NOT NULL DEFAULT '';
