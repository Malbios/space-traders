module SpaceKids.Server.Persistence.SettingsRepository

/// Milestone 12 (bilingual support): a single process-wide setting, not attached to any
/// user/profile (there is none — see `AgentRepository.fs`). `"de"`/`"en"` only; the
/// default row is created on first read rather than by a migration seed, so existing
/// databases upgrade for free.
let getLocale (dbPath: string) : Async<string> =
    async {
        use conn = Database.openConnection dbPath
        use selectCmd = conn.CreateCommand()
        selectCmd.CommandText <- "SELECT locale FROM app_settings WHERE id = 1;"
        let result = selectCmd.ExecuteScalar()

        match result with
        | null ->
            use insertCmd = conn.CreateCommand()
            insertCmd.CommandText <- "INSERT INTO app_settings (id, locale) VALUES (1, 'de');"
            insertCmd.ExecuteNonQuery() |> ignore
            return "de"
        | value -> return value :?> string
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
        use selectCmd = conn.CreateCommand()
        selectCmd.CommandText <- "SELECT poll_interval_seconds FROM app_settings WHERE id = 1;"
        let result = selectCmd.ExecuteScalar()

        match result with
        | null ->
            use insertCmd = conn.CreateCommand()
            insertCmd.CommandText <- "INSERT INTO app_settings (id, poll_interval_seconds) VALUES (1, 1);"
            insertCmd.ExecuteNonQuery() |> ignore
            return 1
        | value -> return System.Convert.ToInt32(value)
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
