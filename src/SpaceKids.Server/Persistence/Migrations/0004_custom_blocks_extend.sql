-- Milestone 9/Part B (plan.md §9): first real writes to custom_blocks/
-- custom_block_versions, provisional since Milestone 1. §9's persistence list also
-- names description/updated_at, which 0001_initial.sql's placeholder shape didn't
-- carry yet.

ALTER TABLE custom_blocks ADD COLUMN description TEXT;
ALTER TABLE custom_blocks ADD COLUMN updated_at TEXT NOT NULL DEFAULT '';
