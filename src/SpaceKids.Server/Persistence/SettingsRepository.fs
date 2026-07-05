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
