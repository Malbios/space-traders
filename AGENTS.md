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

## Build and test verification

Before handoff or commit when changing **Server**, **Client** remoting, or any code that spans multiple projects:

1. Run the **full solution**: `dotnet test SpaceKids.slnx` (or at minimum `dotnet build SpaceKids.slnx`).
2. Chain commands with `&&` so a failed build stops the pipeline — never use `;` between build and test.
3. Do not report "tests green" from a scoped run alone (e.g. `--filter SpaceKids.Core.Tests`) when Server or cross-project code changed.
4. Scoped test runs are fine for tight iteration, but the full solution must pass before you finish.