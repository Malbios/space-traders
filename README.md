# SpaceKids

A German-language, Scratch-inspired visual programming environment for a child to control
ships in [SpaceTraders](https://spacetraders.io), built with F# + Bolero + Blockly +
ASP.NET Core + SQLite.

See `plan.md` for the full build plan (product goals, architecture, milestones) and
`docs/decisions.md` for hard-to-reverse calls already made and why.

## Prerequisites

- .NET 10 SDK
- Node.js (Blockly TS seam bundles via `dotnet build`; root `npm install` is only needed
  for Playwright browser verification)
- Google Chrome (preferred for `npm run verify:browser`; Playwright bundled Chromium is
  the fallback — install with `npm run playwright:install`)

## Commands

```txt
dotnet build SpaceKids.slnx     Build everything (also bundles the Blockly TS seam)
dotnet test SpaceKids.slnx      Run all tests
dotnet run --project src/SpaceKids.Server   Run the app (http://localhost:5000 by default)
```

### Local dev against the fake API

```pwsh
pwsh scripts/dev.ps1 fake       # terminal 1 — http://localhost:5196
pwsh scripts/dev.ps1 server     # terminal 2 — http://localhost:5290
pwsh scripts/dev.ps1 stop       # free ports 5196 / 5290
```

Paste token `FAKE_TOKEN_1` in Settings when using the fake. The server must use
`SpaceTraders__BaseUrl=http://localhost:5196/` (no `/v2/` suffix) — `dev.ps1 server`
sets this automatically.

### Browser verification (Playwright)

```pwsh
npm install
npm run verify:browser          # needs fake + server running (see above)
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

Every milestone in `plan.md` §19 has shipped, plus post-roadmap work (Flotilla
multi-ship programs, full SpaceTraders API block coverage). See `docs/decisions.md`
for current state, design rationale, and what's been built.
