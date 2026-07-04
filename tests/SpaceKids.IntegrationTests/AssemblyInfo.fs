module SpaceKids.IntegrationTests.AssemblyInfo

/// Every test file in this assembly touches process-wide singleton state
/// (`RequestQueue`, `JobRunner`, `SpaceKids.FakeSpaceTraders.App`'s mutable
/// ship/agent/fault-mode fields) — by design, matching this app's own
/// single-user/single-process shape (see docs/decisions.md). xUnit parallelizes
/// different test collections (one per module/file) by default; without this,
/// `Tests.fs` and `JobRunnerTests.fs` running concurrently can have one file's
/// `resetForTests()` clear state a job in the other file is still waiting on,
/// producing a hang with no timeout and no exception — not a slow test, an
/// unrecoverable one. Every test in this assembly must run sequentially.
[<assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)>]
do ()
