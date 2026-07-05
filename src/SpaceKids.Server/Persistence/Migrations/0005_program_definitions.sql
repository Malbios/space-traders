-- Saved/named multiple-program library: the parent row for a named, editable
-- program. Deliberately id-aligned with its `workspaces` row (one-to-one) rather
-- than a separate foreign key column, matching how `custom_blocks.id` doubles as
-- `custom_block_versions.custom_block_id` — the program's own editable Blockly
-- JSON keeps living in `workspaces`, overwrite-based (no history needed there);
-- `programs` already captures an immutable compiled snapshot per job run.

CREATE TABLE program_definitions (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);
