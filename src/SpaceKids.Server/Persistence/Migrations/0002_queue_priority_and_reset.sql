-- Milestone 5: request queue priority/aging/retry history and server-reset flag (§13/§19).

ALTER TABLE request_queue_events ADD COLUMN priority INTEGER NOT NULL DEFAULT 1;
ALTER TABLE request_queue_events ADD COLUMN attempt INTEGER NOT NULL DEFAULT 0;

ALTER TABLE agents ADD COLUMN server_reset_detected INTEGER NOT NULL DEFAULT 0;
