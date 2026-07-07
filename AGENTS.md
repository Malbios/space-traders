# SpaceKids / SpaceTraders — Agent Instructions

## Workspace

**Always work in `C:\dev\space-traders`.** This is the canonical repo.

Do **not** use Grok-managed worktrees under `~/.grok/worktrees/`. If the session cwd points elsewhere, `cd` to `C:\dev\space-traders` before building, testing, or editing.

## Goal completion / stop hook

Fire the stop hook (`update_goal` with `completed: true`, or any equivalent completion signal) **only when the user's entire request is fully finished** — not when an intermediate sub-task completes while overall work is still in progress.

- Sub-task milestones (sorting done, one block added, tests green on a slice, etc.): log progress if helpful, but do **not** mark the goal completed.
- Final handoff: fire the stop hook once, after all parts of the request are done and verified.

## Sub-agents (spawned via spawn_subagent / Task)

If you are a **sub-agent** (child session spawned by the parent agent):

- **Never** call `update_goal` with `completed: true` (the tool is removed from sub-agent profiles in `.grok/agents/`).
- **Never** fire stop hooks or any equivalent completion signal.
- Return results to the parent; only the **top-level agent** handling the user's message may mark the goal complete.

When spawning sub-agents, the parent should include in each prompt: *Do not call `update_goal` or mark the goal complete.*