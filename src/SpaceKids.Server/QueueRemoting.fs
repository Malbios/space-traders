module SpaceKids.Server.QueueRemoting

open Bolero.Remoting
open Bolero.Remoting.Server
open SpaceKids.Client.Main

/// Server-side implementation of QueueService (Milestone 5, §13/§19): read-only view
/// onto RequestQueue's state, refreshed manually by the client (no push infrastructure).
type QueueRemoteHandler(ctx: IRemoteContext) =
    inherit RemoteHandler<QueueService>()

    let dbPath = Persistence.Database.defaultDbPath

    override this.Handler =
        {
            getStatus =
                fun () ->
                    async {
                        let! status = RequestQueue.getStatus dbPath
                        return
                            {
                                pendingCount = status.pendingCount
                                serverResetDetected = status.serverResetDetected
                                unreachableSince = status.unreachableSince
                                recentEvents =
                                    status.recentEvents
                                    |> List.map (fun e ->
                                        {
                                            requestedAt = e.requestedAt
                                            endpoint = e.endpoint
                                            status = e.status
                                            priority = e.priority
                                            attempt = e.attempt
                                        })
                            }
                    }
        }
