module SpaceKids.Server.Persistence.JobStateJson

open System.Text.Json
open System.Text.Json.Serialization
open SpaceKids.Core.Dsl
open SpaceKids.Core.Scheduler

/// Milestone 7 (§14): `JobState`/`CompiledProgram` are trees of F# DUs/records
/// (`JobStatus`, `Frame`, `PathEntry`, `BodyRef`, `LoopState`, `Value`, `Expr`,
/// `Instruction`, ...) — hand-rolling encode/decode for all of them is a lot of
/// mechanical, bug-prone code for something `FSharp.SystemTextJson`'s
/// `JsonFSharpConverter` already solves generically. Kept out of
/// `SpaceKids.Core.fsproj` deliberately, so Core stays free of any serialization
/// library dependency (same reasoning as Core not referencing `SpaceTraders`, see
/// `docs/decisions.md`) — only the persistent shell needs to serialize anything.
let private options =
    let o = JsonSerializerOptions()
    o.Converters.Add(JsonFSharpConverter())
    o

/// Serializes the full `JobState` (including its `program`) into
/// `jobs.execution_state_json` — simpler than splitting the program out into a
/// separate join on every resume, and compiled programs are small. `startJob` still
/// writes a `programs` row (`serializeProgram`) alongside this, for the same reason
/// `programs`/`program_version` exist in §12: future watch-mode version-mismatch
/// checks, not because resume depends on it.
let serializeJobState (job: JobState) : string =
    JsonSerializer.Serialize(job, options)

let deserializeJobState (json: string) : JobState =
    JsonSerializer.Deserialize<JobState>(json, options)

let serializeProgram (program: CompiledProgram) : string =
    JsonSerializer.Serialize(program, options)

let deserializeProgram (json: string) : CompiledProgram =
    JsonSerializer.Deserialize<CompiledProgram>(json, options)

/// Milestone 9/Part B: a single custom block's own compiled body, stored per-version
/// in `custom_block_versions.compiled_body_json` — unlike `CompiledProgram`, this
/// carries no `customBlocks` closure map of its own (it's just the one block).
let serializeCustomBlock (block: CompiledCustomBlock) : string =
    JsonSerializer.Serialize(block, options)

let deserializeCustomBlock (json: string) : CompiledCustomBlock =
    JsonSerializer.Deserialize<CompiledCustomBlock>(json, options)
