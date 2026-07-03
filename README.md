# SpaceKids

A German-language, Scratch-inspired visual programming environment for a child to control
ships in [SpaceTraders](https://spacetraders.io), built with F# + Bolero + Blockly +
ASP.NET Core + SQLite.

See `plan.md` for the full build plan (product goals, architecture, milestones) and
`docs/decisions.md` for hard-to-reverse calls already made and why.

## Prerequisites

- .NET 10 SDK
- Node.js (for the Blockly TS seam bundle — wired into `dotnet build` automatically, no
  separate step needed)

## Commands

```txt
dotnet build SpaceKids.slnx     Build everything (also bundles the Blockly TS seam)
dotnet test SpaceKids.slnx      Run all tests
dotnet run --project src/SpaceKids.Server   Run the app (http://localhost:5000 by default)
```

## Repository layout

```txt
src/SpaceKids.Client          Bolero WASM client (German UI, Blockly workspaces)
src/SpaceKids.Server          ASP.NET Core host, persistence, request queue, scheduler
src/SpaceKids.Core            Domain, DSL, validation, scheduling — framework-free
src/SpaceKids.SpaceTraders    SpaceTraders API client
src/SpaceKids.FakeSpaceTraders  In-process fake API for deterministic integration tests
tests/                        Unit and integration tests
docs/                         Design docs, decisions log, catalog, localization glossary
```

## Status

Milestone 0 (toolchain + Blockly↔Bolero interop spike) is complete. See
`docs/05-agent-handoff.md` for current state and next steps.
