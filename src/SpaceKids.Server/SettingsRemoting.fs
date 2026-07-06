module SpaceKids.Server.SettingsRemoting

open Bolero.Remoting
open Bolero.Remoting.Server
open SpaceKids.Client.Main

/// Server-side implementation of SettingsService (Milestone 12): the single
/// process-wide locale preference — see `Persistence/SettingsRepository.fs`.
type SettingsRemoteHandler(ctx: IRemoteContext) =
    inherit RemoteHandler<SettingsService>()

    let dbPath = Persistence.Database.defaultDbPath

    override this.Handler =
        {
            getLocale = fun () -> Persistence.SettingsRepository.getLocale dbPath
            setLocale = fun locale -> Persistence.SettingsRepository.setLocale dbPath locale
            getPollIntervalSeconds = fun () -> Persistence.SettingsRepository.getPollIntervalSeconds dbPath
            setPollIntervalSeconds = fun seconds -> Persistence.SettingsRepository.setPollIntervalSeconds dbPath seconds
        }
