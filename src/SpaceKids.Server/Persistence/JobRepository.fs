module SpaceKids.Server.Persistence.JobRepository

open System

/// Milestone 7 (§12/§14): the first real writes to `jobs` — `execution_state_json`
/// carries the full serialized `JobState` (see `JobStateJson.fs`); `state`/
/// `current_block_id`/`next_wake_at`/`last_error` are denormalized columns, cheap to
/// query for the dashboard without deserializing the JSON blob per row.
type JobRow =
    { id: string
      state: string
      executionStateJson: string
      assignedShipSymbol: string }

/// Job statuses the scheduler no longer needs to act on — matches §14's job model
/// (a job here has genuinely finished, one way or another).
let terminalStates = [ "Completed"; "Failed"; "Cancelled" ]

let insert
    (dbPath: string)
    (jobId: string)
    (programId: string)
    (shipSymbol: string)
    (state: string)
    (executionStateJson: string)
    (currentBlockId: string option)
    : Async<unit> =
    async {
        let now = DateTime.UtcNow.ToString("o")
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            INSERT INTO jobs
                (id, program_id, state, execution_state_json, created_at, updated_at,
                 assigned_ship_symbol, current_block_id, next_wake_at, last_error)
            VALUES
                ($id, $programId, $state, $json, $now, $now,
                 $shipSymbol, $currentBlockId, NULL, NULL);
            """

        cmd.Parameters.AddWithValue("$id", jobId) |> ignore
        cmd.Parameters.AddWithValue("$programId", programId) |> ignore
        cmd.Parameters.AddWithValue("$state", state) |> ignore
        cmd.Parameters.AddWithValue("$json", executionStateJson) |> ignore
        cmd.Parameters.AddWithValue("$now", now) |> ignore
        cmd.Parameters.AddWithValue("$shipSymbol", shipSymbol) |> ignore

        cmd.Parameters.AddWithValue(
            "$currentBlockId",
            currentBlockId |> Option.map box |> Option.defaultValue (box DBNull.Value)
        )
        |> ignore

        cmd.ExecuteNonQuery() |> ignore
    }

/// Called after every `step` — write-through, not batched, matching this app's
/// single-user/small-writes scale (§12: WAL + busy_timeout is enough here).
let update
    (dbPath: string)
    (jobId: string)
    (state: string)
    (executionStateJson: string)
    (currentBlockId: string option)
    (nextWakeAt: DateTimeOffset option)
    (lastError: string option)
    : Async<unit> =
    async {
        let now = DateTime.UtcNow.ToString("o")
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            UPDATE jobs
            SET state = $state,
                execution_state_json = $json,
                current_block_id = $currentBlockId,
                next_wake_at = $nextWakeAt,
                last_error = $lastError,
                updated_at = $now
            WHERE id = $id;
            """

        cmd.Parameters.AddWithValue("$id", jobId) |> ignore
        cmd.Parameters.AddWithValue("$state", state) |> ignore
        cmd.Parameters.AddWithValue("$json", executionStateJson) |> ignore

        cmd.Parameters.AddWithValue(
            "$currentBlockId",
            currentBlockId |> Option.map box |> Option.defaultValue (box DBNull.Value)
        )
        |> ignore

        cmd.Parameters.AddWithValue(
            "$nextWakeAt",
            nextWakeAt |> Option.map (fun d -> box (d.ToString("o"))) |> Option.defaultValue (box DBNull.Value)
        )
        |> ignore

        cmd.Parameters.AddWithValue(
            "$lastError",
            lastError |> Option.map box |> Option.defaultValue (box DBNull.Value)
        )
        |> ignore

        cmd.Parameters.AddWithValue("$now", now) |> ignore
        cmd.ExecuteNonQuery() |> ignore
    }

let private readRow (reader: Microsoft.Data.Sqlite.SqliteDataReader) : JobRow =
    { id = reader.GetString(0)
      state = reader.GetString(1)
      executionStateJson = reader.GetString(2)
      assignedShipSymbol = reader.GetString(3) }

let loadById (dbPath: string) (jobId: string) : Async<JobRow option> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT id, state, execution_state_json, assigned_ship_symbol FROM jobs WHERE id = $id;"
        cmd.Parameters.AddWithValue("$id", jobId) |> ignore
        use reader = cmd.ExecuteReader()
        return if reader.Read() then Some(readRow reader) else None
    }

/// Loaded once at scheduler startup (§14: "load jobs in active or waiting states") —
/// everything not already `Completed`/`Failed`/`Cancelled`.
let loadNonTerminal (dbPath: string) : Async<JobRow list> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            $"""
            SELECT id, state, execution_state_json, assigned_ship_symbol
            FROM jobs
            WHERE state NOT IN ({terminalStates |> List.map (sprintf "'%s'") |> String.concat ", "});
            """

        use reader = cmd.ExecuteReader()
        return [ while reader.Read() do yield readRow reader ]
    }

/// Milestone 13/Part C: one row of the job history browser. `jobs` never deletes
/// rows, so a terminal job's history is already sitting in the table — only the
/// in-memory dashboard (`JobRunner.fs`'s `ConcurrentDictionary`) forgets it after a
/// restart. `programName` comes from a join through `programs.workspace_id` to
/// `program_definitions.name` (same join `ProgramRepository.delete`'s own refusal
/// check already uses) rather than showing a raw program/workspace id.
type JobHistoryRow =
    { jobId: string
      programName: string
      shipSymbol: string
      state: string
      updatedAt: DateTime }

/// Most-recent-50 terminal jobs, newest first — no real pagination needed at this
/// app's scale.
let listHistory (dbPath: string) : Async<JobHistoryRow list> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            $"""
            SELECT j.id, pd.name, j.assigned_ship_symbol, j.state, j.updated_at
            FROM jobs j
            JOIN programs p ON p.id = j.program_id
            JOIN program_definitions pd ON pd.id = p.workspace_id
            WHERE j.state IN ({terminalStates |> List.map (sprintf "'%s'") |> String.concat ", "})
            ORDER BY j.updated_at DESC
            LIMIT 50;
            """

        use reader = cmd.ExecuteReader()

        return
            [ while reader.Read() do
                  yield
                      { jobId = reader.GetString(0)
                        programName = reader.GetString(1)
                        shipSymbol = reader.GetString(2)
                        state = reader.GetString(3)
                        updatedAt =
                            DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind) } ]
    }
