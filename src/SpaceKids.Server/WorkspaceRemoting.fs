module SpaceKids.Server.WorkspaceRemoting

open Bolero.Remoting
open Bolero.Remoting.Server
open SpaceKids.Client.Main

/// Server-side implementation of WorkspaceService (§3a save/load), now backed by the
/// real `workspaces` table (§12) instead of Milestone 0's spike table.
type WorkspaceRemoteHandler(ctx: IRemoteContext) =
    inherit RemoteHandler<WorkspaceService>()

    override this.Handler =
        {
            save = fun (containerId, json) -> Persistence.WorkspaceRepository.save Persistence.Database.defaultDbPath containerId json
            load = fun containerId -> Persistence.WorkspaceRepository.load Persistence.Database.defaultDbPath containerId
        }
