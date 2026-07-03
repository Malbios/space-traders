module SpaceKids.Server.WorkspaceRemoting

open Bolero.Remoting
open Bolero.Remoting.Server
open SpaceKids.Client.Main

/// Server-side implementation of the Milestone 0 spike's WorkspaceService (§3a save/load
/// proven against real SQLite, not just browser memory). Superseded by the real
/// workspaces table and API surface in Milestone 1+ (§12).
type WorkspaceRemoteHandler(ctx: IRemoteContext) =
    inherit RemoteHandler<WorkspaceService>()

    override this.Handler =
        {
            save = fun (containerId, json) -> Persistence.saveWorkspace containerId json
            load = fun containerId -> Persistence.loadWorkspace containerId
        }
