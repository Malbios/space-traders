-- Milestone 1 foundation schema (plan.md §12).
--
-- Most tables here stay empty until their own milestone starts writing to
-- them (agents/api_tokens: Milestone 2; programs/custom_blocks/
-- custom_block_versions: Milestone 3/9; jobs/job_logs/ship_locks:
-- Milestone 5+; api_cache/request_queue_events: Milestone 2/5). Their exact
-- columns are provisional and may be revisited when that milestone lands —
-- only `workspaces` and `schema_versions` are load-bearing today.
--
-- WAL mode is set by MigrationRunner outside of any transaction (SQLite
-- silently refuses `PRAGMA journal_mode` changes inside one), not here.

CREATE TABLE schema_versions (
    version     INTEGER PRIMARY KEY,
    applied_at  TEXT NOT NULL
);

CREATE TABLE agents (
    id          TEXT PRIMARY KEY,
    symbol      TEXT NOT NULL UNIQUE,
    created_at  TEXT NOT NULL
);

CREATE TABLE api_tokens (
    agent_id    TEXT PRIMARY KEY REFERENCES agents(id),
    token       TEXT NOT NULL,
    created_at  TEXT NOT NULL
);

CREATE TABLE workspaces (
    id              TEXT PRIMARY KEY,
    workspace_json  TEXT NOT NULL,
    updated_at      TEXT NOT NULL
);

CREATE TABLE programs (
    id                  TEXT PRIMARY KEY,
    agent_id            TEXT REFERENCES agents(id),
    workspace_id        TEXT REFERENCES workspaces(id),
    name                TEXT NOT NULL,
    compiled_dsl_json   TEXT,
    created_at          TEXT NOT NULL,
    updated_at          TEXT NOT NULL
);

CREATE TABLE custom_blocks (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    created_at  TEXT NOT NULL
);

CREATE TABLE custom_block_versions (
    id                  TEXT PRIMARY KEY,
    custom_block_id     TEXT NOT NULL REFERENCES custom_blocks(id),
    version             INTEGER NOT NULL,
    definition_json     TEXT NOT NULL,
    compiled_body_json  TEXT,
    created_at          TEXT NOT NULL
);

CREATE TABLE jobs (
    id                      TEXT PRIMARY KEY,
    program_id              TEXT NOT NULL REFERENCES programs(id),
    state                   TEXT NOT NULL,
    execution_state_json    TEXT,
    created_at              TEXT NOT NULL,
    updated_at              TEXT NOT NULL
);

CREATE TABLE job_logs (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id      TEXT NOT NULL REFERENCES jobs(id),
    logged_at   TEXT NOT NULL,
    message     TEXT NOT NULL
);

CREATE TABLE ship_locks (
    ship_symbol TEXT PRIMARY KEY,
    job_id      TEXT REFERENCES jobs(id),
    locked_at   TEXT NOT NULL
);

CREATE TABLE api_cache (
    cache_key       TEXT PRIMARY KEY,
    response_json   TEXT NOT NULL,
    fetched_at      TEXT NOT NULL
);

CREATE TABLE request_queue_events (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
    requested_at                TEXT NOT NULL,
    endpoint                    TEXT NOT NULL,
    status                      TEXT NOT NULL,
    response_metadata_json      TEXT
);
