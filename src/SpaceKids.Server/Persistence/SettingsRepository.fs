module SpaceKids.Server.Persistence.SettingsRepository

open Microsoft.Data.Sqlite

/// Ensures the single `app_settings` row exists. Values are spelled out explicitly
/// rather than left to the columns' own schema-level defaults: `poll_interval_seconds`'s
/// schema default is still `5` (`0007_poll_interval.sql`) even though
/// `0008_poll_interval_default_1s.sql` established `1` as the actually-intended
/// default for existing rows — SQLite has no `ALTER COLUMN ... SET DEFAULT`, so that
/// migration could only fix rows already present, not the column's own default clause.
/// `ON CONFLICT DO NOTHING` makes this safe under concurrent first-callers (a live
/// request's `getLocale` racing a scheduler tick's `getPollIntervalSeconds` right
/// after startup, say) — a plain `INSERT` with no conflict clause could throw on the
/// loser of that race, uncaught, which (found in review) would crash the whole host
/// since nothing catches infrastructure exceptions at that level.
let private ensureSettingsRow (conn: SqliteConnection) : unit =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "INSERT INTO app_settings (id, locale, poll_interval_seconds) VALUES (1, 'de', 1) ON CONFLICT(id) DO NOTHING;"
    cmd.ExecuteNonQuery() |> ignore

/// Milestone 12 (bilingual support): a single process-wide setting, not attached to any
/// user/profile (there is none — see `AgentRepository.fs`). `"de"`/`"en"` only; the
/// default row is created on first read rather than by a migration seed, so existing
/// databases upgrade for free.
let getLocale (dbPath: string) : Async<string> =
    async {
        use conn = Database.openConnection dbPath
        ensureSettingsRow conn
        use selectCmd = conn.CreateCommand()
        selectCmd.CommandText <- "SELECT locale FROM app_settings WHERE id = 1;"
        return selectCmd.ExecuteScalar() :?> string
    }

let setLocale (dbPath: string) (locale: string) : Async<unit> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            """
            INSERT INTO app_settings (id, locale) VALUES (1, $locale)
            ON CONFLICT(id) DO UPDATE SET locale = excluded.locale;
            """
        cmd.Parameters.AddWithValue("$locale", locale) |> ignore
        cmd.ExecuteNonQuery() |> ignore
    }

/// The baseline (healthy-state) cadence, in seconds, for the pilots-list poll
/// (see `MapTick` in `Main.fs`) — same single-row settings table as locale.
let getPollIntervalSeconds (dbPath: string) : Async<int> =
    async {
        use conn = Database.openConnection dbPath
        ensureSettingsRow conn
        use selectCmd = conn.CreateCommand()
        selectCmd.CommandText <- "SELECT poll_interval_seconds FROM app_settings WHERE id = 1;"
        return System.Convert.ToInt32(selectCmd.ExecuteScalar())
    }

let setPollIntervalSeconds (dbPath: string) (seconds: int) : Async<unit> =
    async {
        use conn = Database.openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            """
            INSERT INTO app_settings (id, poll_interval_seconds) VALUES (1, $seconds)
            ON CONFLICT(id) DO UPDATE SET poll_interval_seconds = excluded.poll_interval_seconds;
            """
        cmd.Parameters.AddWithValue("$seconds", seconds) |> ignore
        cmd.ExecuteNonQuery() |> ignore
    }
