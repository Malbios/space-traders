module SpaceKids.Client.Main

/// Milestone 0 spike page: proves the Blockly<->Bolero seam described in
/// plan.md §3a end to end (create/save/reload/highlight/read-only), nothing
/// more. Real toolbox, routing, and dashboards come in later milestones.
open Elmish
open Bolero
open Bolero.Html
open Bolero.Remoting
open Bolero.Remoting.Client
open Microsoft.JSInterop
open SpaceKids.SpaceTraders

/// Remote service for the Milestone 0 spike only (§12 Persistence lands the real
/// `workspaces` table etc. in Milestone 1) — proves workspace JSON round-trips through
/// SQLite, not just browser memory.
type WorkspaceService =
    {
        save: string * string -> Async<unit>
        load: string -> Async<string option>
    }

    interface IRemoteService with
        member this.BasePath = "/spike-workspaces"

/// Milestone 2 (§19): the shared client/server remoting contract for real SpaceTraders
/// data. Every field here is read through the server's request queue stub (§13).
type DashboardState =
    {
        agent: Agent
        ships: Ship list
        contracts: Contract list
        waypoints: Waypoint list
        markets: Market list
    }

type AgentService =
    {
        submitToken: string -> Async<Result<DashboardState, string>>
        loadDashboard: unit -> Async<DashboardState option>
        /// Entity inspector (visual-map feature): loaded lazily (a button, not
        /// automatic) when the player opens a waypoint that has the matching
        /// trait — `None` if the waypoint turns out not to have one after all.
        getWaypointMarket: string -> Async<Market option>
        getWaypointShipyard: string -> Async<Shipyard option>
    }

    interface IRemoteService with
        member this.BasePath = "/agent"

/// Milestone 5 (§13/§19): observability into the server's request queue — priority,
/// aging, retry classification, server-reset/API-unreachable state.
type QueueEventDto =
    {
        requestedAt: System.DateTime
        endpoint: string
        status: string
        priority: int
        attempt: int
    }

type QueueStatusDto =
    {
        pendingCount: int
        serverResetDetected: bool
        unreachableSince: System.DateTime option
        recentEvents: QueueEventDto list
    }

type QueueService =
    {
        getStatus: unit -> Async<QueueStatusDto>
    }

    interface IRemoteService with
        member this.BasePath = "/queue"

/// Milestone 6/7 (§14/§15/§19): runs a compiled program against one real ship,
/// persisted server-side (`JobRunner.fs`/`JobScheduler.fs`) so it survives restarts
/// and keeps making progress in the background.
type JobStatusDto =
    {
        status: string
        statusDetail: string option
        log: string list
        /// Milestone 9/Part E (§9d/§14): one (scope, blockId) pair per call-stack
        /// frame, deepest-first — index 0 is the block actually executing right now
        /// (possibly inside a custom block's own workshop, identified by `scope`),
        /// the rest are the calling blocks still "innen aktiv" further up the
        /// program (`scope = "main"` for the program's own frame). `blockId` is
        /// `None` for a frame whose position is already exhausted (e.g. it's
        /// suspended on its own last instruction's real-world wait) — the frame is
        /// still genuinely on the stack, just with nothing left to highlight.
        blockIdPerFrame: (string * string option) list
    }

/// Milestone 7 (§15): one row of the pilot dashboard.
type JobSummaryDto =
    {
        jobId: string
        /// Saved/named multiple-program library: which program this pilot is
        /// flying — per-program watch mode filters pilots by this.
        programId: string
        shipSymbol: string
        status: string
        statusDetail: string option
        lastLogLine: string option
    }

type JobService =
    {
        /// token, programId, shipSymbol, current workspace JSON -> new job id, or a
        /// German rejection message (e.g. the ship is already flown by another
        /// program). `programId` (saved/named multiple-program library) is what
        /// `JobState.programId` gets tagged with — per-program watch mode filters
        /// pilots by it.
        startJob: string * string * string * string -> Async<Result<string, string>>
        step: string -> Async<JobStatusDto option>
        run: string -> Async<JobStatusDto option>
        getStatus: string -> Async<JobStatusDto option>
        pause: string -> Async<unit>
        resume: string -> Async<unit>
        cancel: string -> Async<unit>
        listJobs: unit -> Async<JobSummaryDto list>
    }

    interface IRemoteService with
        member this.BasePath = "/job"

/// Milestone 9/Part D (§9b/§9c): the Blockwerkstatt's block library + save/rename/
/// delete, backed by `SpaceKids.Server.Persistence.CustomBlockRepository`.
type CustomBlockSummaryDto =
    {
        id: string
        name: string
        version: int
    }

type CustomBlockService =
    {
        list: unit -> Async<CustomBlockSummaryDto list>
        /// name -> new custom block id.
        create: string -> Async<string>
        /// id -> the workshop's stored workspace JSON, if any version has been saved yet.
        loadDefinition: string -> Async<string option>
        /// id, current workshop workspace JSON -> new version number, or a German
        /// compile/validation error.
        save: string * string -> Async<Result<int, string>>
        rename: string * string -> Async<unit>
        /// id -> Ok, or a German refusal message listing what still uses it (§9c).
        delete: string -> Async<Result<unit, string>>
    }

    interface IRemoteService with
        member this.BasePath = "/customblock"

/// Saved/named multiple-program library (§15/§19): a program library mirroring
/// the custom-block library's own shape (`CustomBlockService` above) closely —
/// same kind of named, listable, renameable, deletable Blockly-workspace-backed
/// entity.
type ProgramSummaryDto = { id: string; name: string }

type ProgramService =
    {
        list: unit -> Async<ProgramSummaryDto list>
        /// name -> new program id.
        create: string -> Async<string>
        /// id -> the program's stored workspace JSON (always Some — `create`
        /// seeds a blank one), and any German structural-mismatch warnings
        /// (§9) comparing its last-run compiled snapshot against currently-live
        /// custom-block definitions — empty if it's never been run, or nothing
        /// referenced has changed.
        loadDefinition: string -> Async<(string option * string list)>
        rename: string * string -> Async<unit>
        /// id -> Ok, or a German refusal message if a pilot is actively flying it.
        delete: string -> Async<Result<unit, string>>
    }

    interface IRemoteService with
        member this.BasePath = "/program"

/// Entity inspector (visual-map feature): which entity's detail panel is open, if
/// any — a ship's own detail view links to its current waypoint, and a
/// waypoint's detail view links to every ship currently there, so this is a
/// simple selection, not a navigation stack (opening a new entity always
/// replaces whichever was open).
type InspectedEntity =
    | InspectedShip of shipSymbol: string
    | InspectedWaypoint of waypointSymbol: string

type Model =
    {
        containerId: string
        workshopContainerId: string
        lastBlockId: string option
        readOnly: bool
        status: string
        publishedCustomBlockId: string option
        tokenInput: string
        dashboard: DashboardState option
        dashboardLoading: bool
        dashboardError: string option
        queueStatus: QueueStatusDto option
        selectedShipSymbol: string option
        startingJob: bool
        pilotError: string option
        pilots: JobSummaryDto list
        /// Milestone 7 (§15): true whenever any pilot is non-terminal — the shared
        /// workspace is forced read-only while this holds, and the manual
        /// read-only toggle is disabled (watch mode, §3a/§15).
        watchModeLocked: bool
        /// Milestone 9/Part D (§9b/§9c): the block library — every custom block
        /// currently saved, regardless of which one (if any) is open in the workshop.
        customBlocks: CustomBlockSummaryDto list
        newCustomBlockName: string
        openCustomBlockId: string option
        renameNameInput: string
        workshopStatus: string
        /// Milestone 9/Part E (§9d/§14): the pilot currently being watched (highlight
        /// polling), if any — independent of `watchModeLocked`, which just governs
        /// read-only/edit locking for *any* active pilot.
        watchedJobId: string option
        watchedFrames: (string * string option) list
        /// Entity inspector (visual-map feature): the currently open detail
        /// panel, if any, and any lazily-loaded market/shipyard data for
        /// whichever waypoint is open (cleared whenever a *different* entity is
        /// opened, so stale data never bleeds into a new panel).
        inspecting: InspectedEntity option
        waypointMarket: Market option
        waypointShipyard: Shipyard option
        /// Visual system map: bumped every second by a self-rescheduling
        /// `MapTick` (matching `WatchTick`'s own pattern) purely to force a
        /// re-render, so an in-transit ship's interpolated position stays
        /// current against `DateTimeOffset.UtcNow` without needing to store
        /// "now" itself. Every 5th tick also reloads the dashboard.
        mapTickCount: int
        /// Saved/named multiple-program library: every saved program, which one
        /// (if any) is open in the editor — `containerId` is derived from this
        /// directly (the program's own id, already a valid DOM element id and
        /// already the exact `workspaces` row key `WorkspaceService`'s existing
        /// save/load already uses, so no separate DOM-id-vs-DB-key mapping is
        /// needed) — and any structural-mismatch warnings (§9) from opening it.
        programs: ProgramSummaryDto list
        currentProgramId: string option
        newProgramName: string
        renameProgramInput: string
        programStatus: string
        staleWarnings: string list
    }

let private terminalPilotStatuses = [ "Completed"; "Failed"; "Cancelled" ]

let initModel =
    {
        containerId = ""
        workshopContainerId = "blockly-workshop-spike"
        lastBlockId = None
        readOnly = false
        status = "Bereit."
        publishedCustomBlockId = None
        tokenInput = ""
        dashboard = None
        dashboardLoading = false
        dashboardError = None
        queueStatus = None
        selectedShipSymbol = None
        startingJob = false
        pilotError = None
        pilots = []
        watchModeLocked = false
        customBlocks = []
        newCustomBlockName = ""
        openCustomBlockId = None
        renameNameInput = ""
        workshopStatus = ""
        watchedJobId = None
        watchedFrames = []
        inspecting = None
        waypointMarket = None
        waypointShipyard = None
        mapTickCount = 0
        programs = []
        currentProgramId = None
        newProgramName = ""
        renameProgramInput = ""
        programStatus = ""
        staleWarnings = []
    }

type Message =
    | Init
    | Inited
    | Save
    | Saved
    | Load
    | LoadedFromDb of string option
    | Loaded
    | HighlightFirstBlock
    | Highlighted
    | ToggleReadOnly
    | ReadOnlyToggled
    | PublishSignature
    | Published of string
    | SimulateRun
    | Simulated
    | TokenInputChanged of string
    | SubmitToken
    | TokenSubmitted of Result<DashboardState, string>
    | LoadDashboard
    | DashboardLoaded of DashboardState option
    | LoadQueueStatus
    | QueueStatusLoaded of QueueStatusDto
    | SelectShip of string
    | StartProgram
    | ProgramStartResult of Result<string, string>
    | LoadPilots
    | PilotsLoaded of JobSummaryDto list
    | WatchModeReadOnlySet of bool
    | PausePilot of string
    | ResumePilot of string
    | CancelPilot of string
    | PilotActionDone
    | LoadCustomBlocks
    | CustomBlocksLoaded of CustomBlockSummaryDto list
    | NewCustomBlockNameChanged of string
    | CreateCustomBlock
    | CustomBlockCreated of string
    | OpenCustomBlock of string * string
    | CustomBlockDefinitionLoaded of string option
    | RenameNameInputChanged of string
    | RenameCustomBlock of string
    | CustomBlockRenamed
    | DeleteCustomBlock of string
    | CustomBlockDeleteResult of string * Result<unit, string>
    | SaveWorkshop
    | WorkshopSaved of Result<int, string>
    | WatchPilot of string
    | StopWatching
    | WatchTick
    | WatchStatusLoaded of JobStatusDto option
    | InspectShip of string
    | InspectWaypoint of string
    | CloseInspector
    | LoadWaypointMarket of string
    | WaypointMarketLoaded of Market option
    | LoadWaypointShipyard of string
    | WaypointShipyardLoaded of Shipyard option
    | MapTick
    | LoadPrograms
    | ProgramsLoaded of ProgramSummaryDto list
    | NewProgramNameChanged of string
    | CreateProgram
    | ProgramCreated of string
    | OpenProgram of string
    | ProgramDefinitionLoaded of string option * string list
    | CloseProgram
    | RenameProgramInputChanged of string
    | RenameProgram of string
    | ProgramRenamed
    | DeleteProgram of string
    | ProgramDeleteResult of string * Result<unit, string>

let private callVoid (js: IJSRuntime) (identifier: string) (args: obj[]) : Async<unit> =
    js.InvokeVoidAsync(identifier, args).AsTask() |> Async.AwaitTask

let private call<'a> (js: IJSRuntime) (identifier: string) (args: obj[]) : Async<'a> =
    js.InvokeAsync<'a>(identifier, args).AsTask() |> Async.AwaitTask

let update
    (js: IJSRuntime)
    (remote: WorkspaceService)
    (agentRemote: AgentService)
    (queueRemote: QueueService)
    (jobRemote: JobService)
    (customBlockRemote: CustomBlockService)
    (programRemote: ProgramService)
    message
    model
    =
    match message with
    | Init ->
        // The main program container isn't initialized here — no program is open
        // yet at startup (saved/named multiple-program library); `OpenProgram`
        // initializes it on demand, keyed by the program's own id.
        model, Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.initWorkspace" [| box model.workshopContainerId; box false |]) () (fun () -> Inited)
    | Inited ->
        { model with status = "Werkstatt geladen." }, Cmd.none

    | Save ->
        let saveToDb = async {
            let! json = call<string> js "spaceKids.serializeWorkspace" [| box model.containerId |]
            do! remote.save (model.containerId, json)
        }
        model, Cmd.OfAsync.perform (fun () -> saveToDb) () (fun () -> Saved)
    | Saved ->
        { model with status = "In SQLite gespeichert." }, Cmd.none

    | Load ->
        model, Cmd.OfAsync.perform (fun () -> remote.load model.containerId) () LoadedFromDb
    | LoadedFromDb None ->
        { model with status = "Nichts zum Laden." }, Cmd.none
    | LoadedFromDb(Some json) ->
        model, Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.loadWorkspace" [| box model.containerId; box json |]) () (fun () -> Loaded)
    | Loaded ->
        { model with status = "Aus SQLite geladen." }, Cmd.none

    | HighlightFirstBlock ->
        let highlightFirst = async {
            let! idOpt = call<string option> js "spaceKids.firstBlockId" [| box model.containerId |]
            match idOpt with
            | Some id -> do! callVoid js "spaceKids.highlightBlock" [| box model.containerId; box id |]
            | None -> ()
        }
        model, Cmd.OfAsync.perform (fun () -> highlightFirst) () (fun () -> Highlighted)
    | Highlighted ->
        { model with status = "Block hervorgehoben (falls vorhanden)." }, Cmd.none

    | ToggleReadOnly ->
        let next = not model.readOnly
        model, Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.setReadOnly" [| box model.containerId; box next |]) () (fun () -> ReadOnlyToggled)
    | ReadOnlyToggled ->
        { model with readOnly = not model.readOnly; status = "Lesemodus umgeschaltet." }, Cmd.none

    | PublishSignature ->
        let existingId: obj = match model.publishedCustomBlockId with
                               | Some id -> box id
                               | None -> null
        model,
        Cmd.OfAsync.perform
            (fun () -> call<string> js "spaceKids.publishCustomBlockSignature" [| box model.workshopContainerId; box model.containerId; existingId |])
            ()
            Published
    | Published customBlockId ->
        { model with publishedCustomBlockId = Some customBlockId; status = "Signatur an Programm-Werkstatt übergeben." }, Cmd.none

    | SimulateRun ->
        { model with status = "Simuliere Ausführung..." },
        Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.simulateRun" [| box model.containerId |]) () (fun () -> Simulated)
    | Simulated ->
        { model with status = "Simulation beendet." }, Cmd.none

    | TokenInputChanged value ->
        { model with tokenInput = value }, Cmd.none
    | SubmitToken ->
        { model with dashboardLoading = true; dashboardError = None },
        Cmd.OfAsync.perform (fun () -> agentRemote.submitToken model.tokenInput) () TokenSubmitted
    | TokenSubmitted(Ok state) ->
        { model with dashboard = Some state; dashboardLoading = false; dashboardError = None }, Cmd.none
    | TokenSubmitted(Error message) ->
        { model with dashboardLoading = false; dashboardError = Some message }, Cmd.none
    | LoadDashboard ->
        { model with dashboardLoading = true }, Cmd.OfAsync.perform (fun () -> agentRemote.loadDashboard ()) () DashboardLoaded
    | DashboardLoaded stateOpt ->
        { model with dashboard = stateOpt; dashboardLoading = false }, Cmd.none
    | LoadQueueStatus ->
        model, Cmd.OfAsync.perform (fun () -> queueRemote.getStatus ()) () QueueStatusLoaded
    | QueueStatusLoaded status ->
        { model with queueStatus = Some status }, Cmd.none

    | SelectShip symbol ->
        { model with selectedShipSymbol = Some symbol }, Cmd.none
    | StartProgram ->
        match model.currentProgramId, model.selectedShipSymbol with
        | None, _ -> { model with status = "Bitte zuerst ein Programm öffnen." }, Cmd.none
        | Some _, None -> { model with status = "Bitte zuerst ein Schiff auswählen." }, Cmd.none
        | Some programId, Some shipSymbol ->
            { model with startingJob = true; pilotError = None },
            Cmd.OfAsync.perform
                (fun () ->
                    async {
                        let! workspaceJson = call<string> js "spaceKids.serializeWorkspace" [| box model.containerId |]
                        return! jobRemote.startJob (model.tokenInput, programId, shipSymbol, workspaceJson)
                    })
                ()
                ProgramStartResult
    | ProgramStartResult(Ok _) ->
        { model with startingJob = false }, Cmd.ofMsg LoadPilots
    | ProgramStartResult(Error message) ->
        { model with startingJob = false; pilotError = Some message }, Cmd.none
    | LoadPilots ->
        model, Cmd.OfAsync.perform (fun () -> jobRemote.listJobs ()) () PilotsLoaded
    | PilotsLoaded pilots ->
        // Per-program watch mode (§9/§15/Milestone-11-Part-E): a pilot flying a
        // *different* saved program no longer locks the one currently open here —
        // only a pilot actually flying this program does.
        let anyActive =
            match model.currentProgramId with
            | None -> false
            | Some programId ->
                pilots
                |> List.exists (fun p -> p.programId = programId && not (List.contains p.status terminalPilotStatuses))

        let readOnlyCmd =
            if anyActive <> model.watchModeLocked then
                Cmd.OfAsync.perform
                    (fun () -> callVoid js "spaceKids.setReadOnly" [| box model.containerId; box anyActive |])
                    ()
                    (fun () -> WatchModeReadOnlySet anyActive)
            else
                Cmd.none

        { model with pilots = pilots; watchModeLocked = anyActive }, readOnlyCmd
    | WatchModeReadOnlySet value ->
        { model with readOnly = value }, Cmd.none
    | PausePilot jobId ->
        model, Cmd.OfAsync.perform (fun () -> jobRemote.pause jobId) () (fun () -> PilotActionDone)
    | ResumePilot jobId ->
        model, Cmd.OfAsync.perform (fun () -> jobRemote.resume jobId) () (fun () -> PilotActionDone)
    | CancelPilot jobId ->
        model, Cmd.OfAsync.perform (fun () -> jobRemote.cancel jobId) () (fun () -> PilotActionDone)
    | PilotActionDone ->
        model, Cmd.ofMsg LoadPilots

    | LoadCustomBlocks ->
        model, Cmd.OfAsync.perform (fun () -> customBlockRemote.list ()) () CustomBlocksLoaded
    | CustomBlocksLoaded blocks ->
        { model with customBlocks = blocks }, Cmd.none

    | NewCustomBlockNameChanged value ->
        { model with newCustomBlockName = value }, Cmd.none
    | CreateCustomBlock ->
        if model.newCustomBlockName.Trim() = "" then
            model, Cmd.none
        else
            { model with workshopStatus = "Erstelle..." },
            Cmd.OfAsync.perform (fun () -> customBlockRemote.create model.newCustomBlockName) () CustomBlockCreated
    | CustomBlockCreated id ->
        let name = model.newCustomBlockName
        { model with newCustomBlockName = ""; workshopStatus = "Block erstellt." },
        Cmd.batch [ Cmd.ofMsg LoadCustomBlocks; Cmd.ofMsg (OpenCustomBlock(id, name)) ]

    | OpenCustomBlock(id, name) ->
        { model with
            openCustomBlockId = Some id
            renameNameInput = name
            workshopStatus = "Werkstatt wird geladen..." },
        Cmd.OfAsync.perform (fun () -> customBlockRemote.loadDefinition id) () CustomBlockDefinitionLoaded
    | CustomBlockDefinitionLoaded jsonOpt ->
        let json = jsonOpt |> Option.defaultValue """{"blocks":{"languageVersion":0,"blocks":[]}}"""

        { model with workshopStatus = "Werkstatt geladen." },
        Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.loadWorkspace" [| box model.workshopContainerId; box json |]) () (fun () -> Loaded)

    | RenameNameInputChanged value ->
        { model with renameNameInput = value }, Cmd.none
    | RenameCustomBlock id ->
        model, Cmd.OfAsync.perform (fun () -> customBlockRemote.rename (id, model.renameNameInput)) () (fun () -> CustomBlockRenamed)
    | CustomBlockRenamed ->
        { model with workshopStatus = "Umbenannt." }, Cmd.ofMsg LoadCustomBlocks

    | DeleteCustomBlock id ->
        model,
        Cmd.OfAsync.perform
            (fun () ->
                async {
                    let! result = customBlockRemote.delete id
                    return (id, result)
                })
            ()
            CustomBlockDeleteResult
    | CustomBlockDeleteResult(id, Ok()) ->
        let openId = if model.openCustomBlockId = Some id then None else model.openCustomBlockId
        { model with openCustomBlockId = openId; workshopStatus = "Gelöscht." }, Cmd.ofMsg LoadCustomBlocks
    | CustomBlockDeleteResult(_, Error message) ->
        { model with workshopStatus = message }, Cmd.none

    | SaveWorkshop ->
        match model.openCustomBlockId with
        | None -> { model with workshopStatus = "Kein Block zum Speichern geöffnet." }, Cmd.none
        | Some id ->
            { model with workshopStatus = "Speichere..." },
            Cmd.OfAsync.perform
                (fun () ->
                    async {
                        let! json = call<string> js "spaceKids.serializeWorkspace" [| box model.workshopContainerId |]
                        return! customBlockRemote.save (id, json)
                    })
                ()
                WorkshopSaved
    | WorkshopSaved(Ok version) ->
        let publishCmd =
            match model.openCustomBlockId with
            | Some id ->
                Cmd.OfAsync.perform
                    (fun () -> call<string> js "spaceKids.publishCustomBlockSignature" [| box model.workshopContainerId; box model.containerId; box id |])
                    ()
                    Published
            | None -> Cmd.none

        { model with workshopStatus = $"Gespeichert (Version {version})." }, Cmd.batch [ publishCmd; Cmd.ofMsg LoadCustomBlocks ]
    | WorkshopSaved(Error message) ->
        { model with workshopStatus = $"Fehler: {message}" }, Cmd.none

    | WatchPilot jobId ->
        { model with watchedJobId = Some jobId; watchedFrames = [] }, Cmd.ofMsg WatchTick
    | StopWatching ->
        { model with watchedJobId = None; watchedFrames = [] },
        Cmd.batch [
            Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.clearHighlight" [| box model.containerId |]) () (fun () -> Highlighted)
            Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.clearHighlight" [| box model.workshopContainerId |]) () (fun () -> Highlighted)
        ]
    | WatchTick ->
        match model.watchedJobId with
        | None -> model, Cmd.none
        | Some jobId -> model, Cmd.OfAsync.perform (fun () -> jobRemote.getStatus jobId) () WatchStatusLoaded
    | WatchStatusLoaded None ->
        { model with watchedJobId = None; watchedFrames = [] }, Cmd.none
    | WatchStatusLoaded(Some dto) ->
        match model.watchedJobId with
        | None -> model, Cmd.none
        | Some _ ->
            let frames = dto.blockIdPerFrame

            let programHighlightCmd =
                match frames |> List.tryFind (fun (scope, _) -> scope = "main") with
                | Some(_, Some blockId) ->
                    Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.highlightBlock" [| box model.containerId; box blockId |]) () (fun () -> Highlighted)
                | Some(_, None)
                | None -> Cmd.none

            let workshopHighlightCmd =
                match model.openCustomBlockId, frames |> List.tryHead with
                | Some openId, Some(scope, Some blockId) when scope = openId ->
                    Cmd.OfAsync.perform
                        (fun () -> callVoid js "spaceKids.highlightBlock" [| box model.workshopContainerId; box blockId |])
                        ()
                        (fun () -> Highlighted)
                | Some _, _ ->
                    Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.clearHighlight" [| box model.workshopContainerId |]) () (fun () -> Highlighted)
                | None, _ -> Cmd.none

            let terminal = List.contains dto.status terminalPilotStatuses

            let nextTickCmd =
                if terminal then
                    Cmd.none
                else
                    Cmd.OfAsync.perform (fun () -> async { do! Async.Sleep 1000 }) () (fun () -> WatchTick)

            { model with watchedFrames = frames }, Cmd.batch [ programHighlightCmd; workshopHighlightCmd; nextTickCmd ]

    | InspectShip shipSymbol ->
        { model with
            inspecting = Some(InspectedShip shipSymbol)
            waypointMarket = None
            waypointShipyard = None },
        Cmd.none
    | InspectWaypoint waypointSymbol ->
        { model with
            inspecting = Some(InspectedWaypoint waypointSymbol)
            waypointMarket = None
            waypointShipyard = None },
        Cmd.none
    | CloseInspector ->
        { model with inspecting = None; waypointMarket = None; waypointShipyard = None }, Cmd.none
    | LoadWaypointMarket waypointSymbol ->
        model, Cmd.OfAsync.perform (fun () -> agentRemote.getWaypointMarket waypointSymbol) () WaypointMarketLoaded
    | WaypointMarketLoaded market ->
        { model with waypointMarket = market }, Cmd.none
    | LoadWaypointShipyard waypointSymbol ->
        model, Cmd.OfAsync.perform (fun () -> agentRemote.getWaypointShipyard waypointSymbol) () WaypointShipyardLoaded
    | WaypointShipyardLoaded shipyard ->
        { model with waypointShipyard = shipyard }, Cmd.none

    | MapTick ->
        let count = model.mapTickCount + 1

        let reloadCmd = if count % 5 = 0 then Cmd.ofMsg LoadDashboard else Cmd.none

        let nextTickCmd =
            Cmd.OfAsync.perform (fun () -> async { do! Async.Sleep 1000 }) () (fun () -> MapTick)

        { model with mapTickCount = count }, Cmd.batch [ reloadCmd; nextTickCmd ]

    | LoadPrograms ->
        model, Cmd.OfAsync.perform (fun () -> programRemote.list ()) () ProgramsLoaded
    | ProgramsLoaded programs ->
        { model with programs = programs }, Cmd.none

    | NewProgramNameChanged value ->
        { model with newProgramName = value }, Cmd.none
    | CreateProgram ->
        if model.newProgramName.Trim() = "" then
            model, Cmd.none
        else
            { model with programStatus = "Erstelle..." },
            Cmd.OfAsync.perform (fun () -> programRemote.create model.newProgramName) () ProgramCreated
    | ProgramCreated id ->
        { model with newProgramName = ""; programStatus = "Programm erstellt." },
        Cmd.batch [ Cmd.ofMsg LoadPrograms; Cmd.ofMsg (OpenProgram id) ]

    | OpenProgram id ->
        // Switching programs tears down the previously-mounted container first —
        // its Blockly instance would otherwise keep referencing a DOM element this
        // render is about to stop rendering (only one program's container is ever
        // in the page at a time).
        let closeOldCmd =
            match model.currentProgramId with
            | Some oldId when oldId <> id ->
                Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.destroyWorkspace" [| box oldId |]) () (fun () -> Loaded)
            | _ -> Cmd.none

        let name =
            model.programs |> List.tryFind (fun p -> p.id = id) |> Option.map (fun p -> p.name) |> Option.defaultValue ""

        { model with
            currentProgramId = Some id
            containerId = id
            renameProgramInput = name
            staleWarnings = []
            programStatus = "Lädt..." },
        Cmd.batch [
            closeOldCmd
            Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.initWorkspace" [| box id; box false |]) () (fun () -> Inited)
            Cmd.OfAsync.perform (fun () -> programRemote.loadDefinition id) () ProgramDefinitionLoaded
            // Per-program watch mode: `watchModeLocked` is only recomputed on
            // `PilotsLoaded`, which doesn't fire on its own just because a
            // different program was opened — without this, the newly-opened
            // program would keep showing whatever lock state the *previous*
            // program last had.
            Cmd.ofMsg LoadPilots
        ]
    | ProgramDefinitionLoaded(jsonOpt, warnings) ->
        let loadCmd =
            match jsonOpt with
            | Some json ->
                Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.loadWorkspace" [| box model.containerId; box json |]) () (fun () -> Loaded)
            | None -> Cmd.none

        { model with programStatus = "Programm geladen."; staleWarnings = warnings }, loadCmd

    | CloseProgram ->
        match model.currentProgramId with
        | None -> model, Cmd.none
        | Some id ->
            { model with currentProgramId = None; containerId = ""; staleWarnings = [] },
            Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.destroyWorkspace" [| box id |]) () (fun () -> Loaded)

    | RenameProgramInputChanged value ->
        { model with renameProgramInput = value }, Cmd.none
    | RenameProgram id ->
        model, Cmd.OfAsync.perform (fun () -> programRemote.rename (id, model.renameProgramInput)) () (fun () -> ProgramRenamed)
    | ProgramRenamed ->
        { model with programStatus = "Umbenannt." }, Cmd.ofMsg LoadPrograms

    | DeleteProgram id ->
        model,
        Cmd.OfAsync.perform
            (fun () ->
                async {
                    let! result = programRemote.delete id
                    return (id, result)
                })
            ()
            ProgramDeleteResult
    | ProgramDeleteResult(id, Ok()) ->
        let closeCmd = if model.currentProgramId = Some id then Cmd.ofMsg CloseProgram else Cmd.none
        { model with programStatus = "Gelöscht." }, Cmd.batch [ closeCmd; Cmd.ofMsg LoadPrograms ]
    | ProgramDeleteResult(_, Error message) ->
        { model with programStatus = message }, Cmd.none

let private viewDashboard model dispatch =
    div {
        h2 { "Echte SpaceTraders-Daten (Milestone 2)" }
        if model.dashboardLoading then
            p { "Lädt..." }
        match model.dashboardError with
        | Some err -> p { $"Fehler: {err}" }
        | None -> ()
        match model.dashboard with
        | None ->
            div {
                input {
                    attr.``type`` "text"
                    attr.placeholder "SpaceTraders-Token einfügen"
                    attr.value model.tokenInput
                    on.change (fun e -> dispatch (TokenInputChanged(string e.Value)))
                }
                button { on.click (fun _ -> dispatch SubmitToken); "Anmelden" }
            }
        | Some state ->
            div {
                h3 { $"Pilot: {state.agent.symbol}" }
                p { $"Kontostand: {state.agent.credits} Credits" }
                p { $"Hauptquartier: {state.agent.headquarters}" }
                h3 { "Schiffe" }
                ul {
                    for ship in state.ships do
                        li {
                            attr.style "cursor: pointer; text-decoration: underline"
                            on.click (fun _ -> dispatch (InspectShip ship.symbol))
                            $"{ship.symbol} — {ship.registration.role} — {ship.nav.status} bei {ship.nav.waypointSymbol}"
                        }
                }
                h3 { "Aufträge" }
                ul {
                    for contract in state.contracts do
                        li { $"{contract.id} ({contract.``type``}) — angenommen: {contract.accepted}, erfüllt: {contract.fulfilled}" }
                }
                h3 { "Wegpunkte" }
                ul {
                    for waypoint in state.waypoints do
                        li {
                            attr.style "cursor: pointer; text-decoration: underline"
                            on.click (fun _ -> dispatch (InspectWaypoint waypoint.symbol))
                            $"{waypoint.symbol} ({waypoint.``type``})"
                        }
                }
                h3 { "Markt" }
                for market in state.markets do
                    div {
                        p { $"Markt bei {market.symbol}" }
                        ul {
                            for good in market.exports do
                                li { $"Export: {good.name}" }
                        }
                    }
            }
    }

/// Entity inspector (visual-map feature): every field already on `Ship` — no
/// data gap here, unlike waypoints (see `viewWaypointInspector`).
let private viewShipInspector (ship: Ship) dispatch =
    div {
        attr.style "border: 1px solid #ccc; border-radius: 4px; padding: 0.5rem; margin: 0.5rem 0"
        h3 { $"Schiff: {ship.symbol}" }
        button { on.click (fun _ -> dispatch CloseInspector); "Schließen" }
        p { $"Rolle: {ship.registration.role}" }
        p {
            "Wegpunkt: "
            a {
                attr.style "cursor: pointer; text-decoration: underline"
                on.click (fun _ -> dispatch (InspectWaypoint ship.nav.waypointSymbol))
                ship.nav.waypointSymbol
            }
        }
        p { $"Status: {ship.nav.status} ({ship.nav.flightMode})" }
        if ship.nav.status = "IN_TRANSIT" then
            p {
                $"Unterwegs von {ship.nav.route.origin.symbol} nach {ship.nav.route.destination.symbol}, Ankunft: {ship.nav.route.arrival}"
            }
        p { $"Treibstoff: {ship.fuel.current} / {ship.fuel.capacity}" }
        p { $"Fracht: {ship.cargo.units} / {ship.cargo.capacity}" }
        if ship.cargo.inventory.IsEmpty then
            p { "Keine Fracht an Bord." }
        else
            ul {
                for item in ship.cargo.inventory do
                    li { $"{item.name}: {item.units}" }
            }
        if ship.cooldown.remainingSeconds > 0 then
            p { $"Abklingzeit: noch {ship.cooldown.remainingSeconds}s" }
    }

/// Entity inspector (visual-map feature): unlike `Ship`, `Waypoint` is thin on
/// its own (§'s "waypoint traits" addition) — traits, ships currently here (from
/// `state.ships`, not the waypoint itself), and lazily-loaded market/shipyard
/// data (gated on the matching trait) fill in "all the details."
let private viewWaypointInspector (waypoint: Waypoint) (state: DashboardState) model dispatch =
    let shipsHere = state.ships |> List.filter (fun s -> s.nav.waypointSymbol = waypoint.symbol)
    let hasTrait symbol = waypoint.traits |> List.exists (fun t -> t.symbol = symbol)

    div {
        attr.style "border: 1px solid #ccc; border-radius: 4px; padding: 0.5rem; margin: 0.5rem 0"
        h3 { $"Wegpunkt: {waypoint.symbol}" }
        button { on.click (fun _ -> dispatch CloseInspector); "Schließen" }
        p { $"Typ: {waypoint.``type``} — Position: ({waypoint.x}, {waypoint.y})" }

        h4 { "Eigenschaften" }
        if waypoint.traits.IsEmpty then
            p { "Keine bekannten Eigenschaften." }
        else
            ul {
                for t in waypoint.traits do
                    li { $"{t.name}: {t.description}" }
            }

        h4 { "Schiffe hier" }
        if shipsHere.IsEmpty then
            p { "Keine Schiffe an diesem Wegpunkt." }
        else
            ul {
                for ship in shipsHere do
                    li {
                        attr.style "cursor: pointer; text-decoration: underline"
                        on.click (fun _ -> dispatch (InspectShip ship.symbol))
                        ship.symbol
                    }
            }

        if hasTrait "MARKETPLACE" then
            h4 { "Markt" }
            match model.waypointMarket with
            | None -> button { on.click (fun _ -> dispatch (LoadWaypointMarket waypoint.symbol)); "Markt laden" }
            | Some market ->
                ul {
                    for good in market.tradeGoods do
                        li { $"{good.symbol}: Kaufpreis {good.purchasePrice}, Verkaufspreis {good.sellPrice}" }
                }

        if hasTrait "SHIPYARD" then
            h4 { "Werft" }
            match model.waypointShipyard with
            | None -> button { on.click (fun _ -> dispatch (LoadWaypointShipyard waypoint.symbol)); "Werft laden" }
            | Some shipyard ->
                ul {
                    for entry in shipyard.ships do
                        li { $"{entry.``type``}: {entry.purchasePrice} Credits" }
                }
    }

let private viewInspector (state: DashboardState) model dispatch =
    match model.inspecting with
    | None -> Node.Empty()
    | Some(InspectedShip shipSymbol) ->
        match state.ships |> List.tryFind (fun s -> s.symbol = shipSymbol) with
        | Some ship -> viewShipInspector ship dispatch
        | None -> div { $"Schiff {shipSymbol} nicht gefunden." }
    | Some(InspectedWaypoint waypointSymbol) ->
        match state.waypoints |> List.tryFind (fun w -> w.symbol = waypointSymbol) with
        | Some waypoint -> viewWaypointInspector waypoint state model dispatch
        | None -> div { $"Wegpunkt {waypointSymbol} nicht gefunden." }

// --- Visual system map (§9's "later idea," Milestone 10-adjacent) ------------------
// Pure F#/Bolero.Html — `svg` is a predefined element builder, and `circle`/
// `polygon`/`title`/`text` go through the generic `elt "tagName"`/`"attr" => value`
// escape hatches this file already relies on elsewhere for non-curated HTML. No
// JS/TS interop: unlike Blockly, nothing here owns its own mutable object graph —
// it's just static SVG nodes Bolero already knows how to diff.

let private mapViewSize = 400.0
let private mapPadding = 30.0

/// Guards against a degenerate single-point (or empty) range, which would
/// otherwise divide by zero when scaling.
let private computeMapBounds (waypoints: Waypoint list) : float * float * float * float =
    match waypoints with
    | [] -> (0.0, 1.0, 0.0, 1.0)
    | _ ->
        let xs = waypoints |> List.map (fun w -> float w.x)
        let ys = waypoints |> List.map (fun w -> float w.y)
        let minX, maxX = List.min xs, List.max xs
        let minY, maxY = List.min ys, List.max ys
        let minX, maxX = if minX = maxX then minX - 1.0, maxX + 1.0 else minX, maxX
        let minY, maxY = if minY = maxY then minY - 1.0, maxY + 1.0 else minY, maxY
        (minX, maxX, minY, maxY)

/// Y is flipped (SVG is Y-down) so the schematic reads with north up.
let private scaleMapPoint (minX, maxX, minY, maxY) (x: float) (y: float) : float * float =
    let scale = min ((mapViewSize - 2.0 * mapPadding) / (maxX - minX)) ((mapViewSize - 2.0 * mapPadding) / (maxY - minY))
    let sx = mapPadding + (x - minX) * scale
    let sy = mapViewSize - (mapPadding + (y - minY) * scale)
    (sx, sy)

let private waypointColor (waypointType: string) : string =
    match waypointType with
    | "PLANET" -> "#4a90d9"
    | "GAS_GIANT" -> "#d9a44a"
    | "ASTEROID_FIELD" -> "#8a8a8a"
    | "MOON" -> "#b0b0c0"
    | "ORBITAL_STATION" -> "#6ad98a"
    | "JUMP_GATE" -> "#c04ad9"
    | "FUEL_STATION" -> "#d94a4a"
    | _ -> "#999999"

/// `None` if the ship's position can't be determined at all — either an
/// in-transit ship whose route timestamps don't parse, or a docked/orbiting ship
/// at a waypoint outside the loaded system (the known "dashboard only loads the
/// headquarters system" simplification, same class documented elsewhere).
let private interpolatedShipPosition (waypoints: Waypoint list) (ship: Ship) : (float * float) option =
    if ship.nav.status = "IN_TRANSIT" then
        try
            let departure = System.DateTimeOffset.Parse ship.nav.route.departureTime
            let arrival = System.DateTimeOffset.Parse ship.nav.route.arrival
            let totalSeconds = (arrival - departure).TotalSeconds

            let fraction =
                if totalSeconds <= 0.0 then
                    1.0
                else
                    max 0.0 (min 1.0 ((System.DateTimeOffset.UtcNow - departure).TotalSeconds / totalSeconds))

            let ox, oy = float ship.nav.route.origin.x, float ship.nav.route.origin.y
            let dx, dy = float ship.nav.route.destination.x, float ship.nav.route.destination.y
            Some(ox + (dx - ox) * fraction, oy + (dy - oy) * fraction)
        with _ ->
            None
    else
        waypoints
        |> List.tryFind (fun w -> w.symbol = ship.nav.waypointSymbol)
        |> Option.map (fun w -> float w.x, float w.y)

let private viewSystemMap (state: DashboardState) dispatch =
    let bounds = computeMapBounds state.waypoints

    div {
        h2 { "Systemkarte" }
        svg {
            "viewBox" => $"0 0 {mapViewSize} {mapViewSize}"
            attr.style "width: 100%; max-width: 400px; height: 400px; border: 1px solid #ccc"

            for waypoint in state.waypoints do
                let sx, sy = scaleMapPoint bounds (float waypoint.x) (float waypoint.y)

                elt "g" {
                    attr.style "cursor: pointer"
                    on.click (fun _ -> dispatch (InspectWaypoint waypoint.symbol))

                    elt "circle" {
                        "cx" => string sx
                        "cy" => string sy
                        "r" => "6"
                        "fill" => waypointColor waypoint.``type``
                    }

                    elt "title" { $"{waypoint.symbol} ({waypoint.``type``})" }

                    elt "text" {
                        "x" => string sx
                        "y" => string (sy + 16.0)
                        "font-size" => "8"
                        "text-anchor" => "middle"
                        waypoint.symbol
                    }
                }

            for ship in state.ships do
                match interpolatedShipPosition state.waypoints ship with
                | None -> ()
                | Some(x, y) ->
                    let sx, sy = scaleMapPoint bounds x y

                    elt "g" {
                        attr.style "cursor: pointer"
                        on.click (fun _ -> dispatch (InspectShip ship.symbol))

                        elt "polygon" {
                            "points" => $"{sx},{sy - 6.0} {sx - 5.0},{sy + 5.0} {sx + 5.0},{sy + 5.0}"
                            "fill" => "#e04040"
                        }

                        elt "title" { $"{ship.symbol} — {ship.nav.status}" }

                        elt "text" {
                            "x" => string sx
                            "y" => string (sy - 8.0)
                            "font-size" => "8"
                            "text-anchor" => "middle"
                            ship.symbol
                        }
                    }
        }
    }

let private viewQueueStatus model dispatch =
    div {
        h2 { "Warteschlange (Milestone 5)" }
        button { on.click (fun _ -> dispatch LoadQueueStatus); "Aktualisieren" }
        match model.queueStatus with
        | None -> p { "Noch nicht geladen." }
        | Some status ->
            div {
                p { $"Wartende Anfragen: {status.pendingCount}" }
                if status.serverResetDetected then
                    p { "Der Spielserver wurde zurückgesetzt. Ein neuer Kapitän muss erstellt werden." }
                match status.unreachableSince with
                | Some since ->
                    let sinceText = since.ToString("HH:mm:ss")
                    p {
                        $"Die Raumfunkzentrale ist gerade nicht erreichbar (seit {sinceText}). Deine Piloten machen weiter, sobald sie wieder Funkkontakt haben."
                    }
                | None -> ()
                h3 { "Letzte Ereignisse" }
                ul {
                    for evt in status.recentEvents do
                        let requestedAtText = evt.requestedAt.ToString("HH:mm:ss")
                        li {
                            $"{requestedAtText} {evt.endpoint} — {evt.status} (Priorität {evt.priority}, Versuch {evt.attempt})"
                        }
                }
            }
    }

/// Milestone 7 (§15): German status text for a pilot card.
let private germanPilotStatus (status: string) : string =
    match status with
    | "Running" -> "Führt Programm aus"
    | "AwaitingApiResponse" -> "Wartet auf Bestätigung"
    | "WaitingForArrival" -> "Unterwegs"
    | "WaitingForCooldown" -> "Wartet auf Abklingzeit"
    | "Reconciling" -> "Prüft die letzte Aktion"
    | "AwaitingInfoResponse" -> "Wartet auf Information"
    | "Paused" -> "Pausiert"
    | "Cancelled" -> "Gestoppt"
    | "Completed" -> "Fertig"
    | "Failed" -> "Fehlgeschlagen"
    | other -> other

let private viewJobRunner model dispatch =
    div {
        h2 { "Programm ausführen (Milestone 7)" }
        match model.dashboard with
        | None -> p { "Zuerst anmelden, um ein Schiff auszuwählen." }
        | Some state ->
            div {
                label { "Schiff: " }
                select {
                    on.change (fun e -> dispatch (SelectShip(string e.Value)))
                    option { attr.value ""; "-- wählen --" }
                    for ship in state.ships do
                        option { attr.value ship.symbol; ship.symbol }
                }
                button {
                    attr.disabled (model.currentProgramId.IsNone || model.selectedShipSymbol.IsNone || model.startingJob)
                    on.click (fun _ -> dispatch StartProgram)
                    "Start"
                }
            }
            if model.currentProgramId.IsNone then
                p { "Bitte zuerst ein Programm öffnen." }
        match model.pilotError with
        | Some message -> p { $"Fehler: {message}" }
        | None -> ()

        h3 { "Piloten" }
        button { on.click (fun _ -> dispatch LoadPilots); "Piloten aktualisieren" }
        if model.pilots.IsEmpty then
            p { "Noch kein Pilot aktiv." }
        else
            for pilot in model.pilots do
                let isTerminal = List.contains pilot.status terminalPilotStatuses
                let isPaused = pilot.status = "Paused"

                div {
                    attr.style "border: 1px solid #ccc; border-radius: 4px; padding: 0.5rem; margin: 0.5rem 0"
                    p { $"🤖 Schiff: {pilot.shipSymbol}" }
                    p { $"Status: {germanPilotStatus pilot.status}" }
                    match pilot.statusDetail with
                    | Some detail -> p { detail }
                    | None -> ()
                    match pilot.lastLogLine with
                    | Some line -> p { $"Zuletzt: {line}" }
                    | None -> ()
                    if not isTerminal then
                        if isPaused then
                            button { on.click (fun _ -> dispatch (ResumePilot pilot.jobId)); "Fortsetzen" }
                        else
                            button { on.click (fun _ -> dispatch (PausePilot pilot.jobId)); "Pause" }
                        button { on.click (fun _ -> dispatch (CancelPilot pilot.jobId)); "Stoppen" }
                        if model.watchedJobId = Some pilot.jobId then
                            button { on.click (fun _ -> dispatch StopWatching); "Beobachtung stoppen" }
                        else
                            button { on.click (fun _ -> dispatch (WatchPilot pilot.jobId)); "Beobachten" }

                    if model.watchedJobId = Some pilot.jobId then
                        match model.watchedFrames with
                        | [] -> ()
                        | frames when frames.Length > 1 ->
                            let innerScope = fst frames.[0]
                            let innerName =
                                model.customBlocks |> List.tryFind (fun b -> b.id = innerScope) |> Option.map (fun b -> b.name) |> Option.defaultValue innerScope

                            p {
                                $"Innen aktiv: \"{innerName}\" "
                                button { on.click (fun _ -> dispatch (OpenCustomBlock(innerScope, innerName))); "Block öffnen" }
                            }
                        | _ -> ()
                }

        h3 { "Logbuch" }
        let activePilots = model.pilots |> List.filter (fun p -> not (List.contains p.status terminalPilotStatuses))

        if activePilots.IsEmpty then
            p { "Keine Piloten aktiv." }
        else
            ul {
                for pilot in activePilots do
                    li {
                        match pilot.lastLogLine with
                        | Some line -> $"🤖 {pilot.shipSymbol}: {line}"
                        | None -> $"🤖 {pilot.shipSymbol}: {germanPilotStatus pilot.status}"
                    }
            }
    }

let private viewCustomBlockLibrary model dispatch =
    div {
        h2 { "Eigene Blöcke (Milestone 9)" }
        div {
            input {
                attr.``type`` "text"
                attr.placeholder "Name des neuen Blocks"
                attr.value model.newCustomBlockName
                on.change (fun e -> dispatch (NewCustomBlockNameChanged(string e.Value)))
            }
            button { on.click (fun _ -> dispatch CreateCustomBlock); "Neuer Block" }
        }
        if model.customBlocks.IsEmpty then
            p { "Noch kein eigener Block gespeichert." }
        else
            ul {
                for b in model.customBlocks do
                    li {
                        $"{b.name} (Version {b.version}) "
                        button { on.click (fun _ -> dispatch (OpenCustomBlock(b.id, b.name))); "Öffnen" }
                        button { on.click (fun _ -> dispatch (DeleteCustomBlock b.id)); "Löschen" }
                    }
            }
        match model.openCustomBlockId with
        | None -> ()
        | Some id ->
            div {
                input {
                    attr.``type`` "text"
                    attr.value model.renameNameInput
                    on.change (fun e -> dispatch (RenameNameInputChanged(string e.Value)))
                }
                button { on.click (fun _ -> dispatch (RenameCustomBlock id)); "Umbenennen" }
                button { on.click (fun _ -> dispatch SaveWorkshop); "Workshop speichern" }
            }
        if model.workshopStatus <> "" then
            p { model.workshopStatus }
    }

let private viewProgramLibrary model dispatch =
    div {
        h2 { "Programme" }
        div {
            input {
                attr.``type`` "text"
                attr.placeholder "Name des neuen Programms"
                attr.value model.newProgramName
                on.change (fun e -> dispatch (NewProgramNameChanged(string e.Value)))
            }
            button { on.click (fun _ -> dispatch CreateProgram); "Neues Programm" }
        }
        if model.programs.IsEmpty then
            p { "Noch kein Programm gespeichert." }
        else
            ul {
                for p in model.programs do
                    li {
                        $"{p.name} "
                        button { on.click (fun _ -> dispatch (OpenProgram p.id)); "Öffnen" }
                        button { on.click (fun _ -> dispatch (DeleteProgram p.id)); "Löschen" }
                    }
            }
        match model.currentProgramId with
        | None -> ()
        | Some id ->
            div {
                input {
                    attr.``type`` "text"
                    attr.value model.renameProgramInput
                    on.change (fun e -> dispatch (RenameProgramInputChanged(string e.Value)))
                }
                button { on.click (fun _ -> dispatch (RenameProgram id)); "Umbenennen" }
                button { on.click (fun _ -> dispatch CloseProgram); "Schließen" }
            }
        if model.programStatus <> "" then
            p { model.programStatus }
    }

let view model dispatch =
    div {
        attr.style "font-family: sans-serif; padding: 1rem"
        h1 { "SpaceKids – Blockly-Spike (Milestone 0)" }
        p { model.status }
        viewProgramLibrary model dispatch
        match model.currentProgramId with
        | None -> p { "Kein Programm geöffnet — wähle oder erstelle eines oben." }
        | Some _ ->
            div {
                if not model.staleWarnings.IsEmpty then
                    div {
                        attr.style "border: 1px solid #cc8800; background: #fff8e6; padding: 0.5rem; margin-bottom: 0.5rem"
                        p { "Einige eigene Blöcke in diesem Programm haben sich geändert — bitte vor dem Ausführen prüfen." }
                        ul {
                            for w in model.staleWarnings do
                                li { w }
                        }
                    }
                div {
                    button { on.click (fun _ -> dispatch Save); "Speichern" }
                    button { on.click (fun _ -> dispatch Load); "Laden" }
                    button { on.click (fun _ -> dispatch HighlightFirstBlock); "Ersten Block hervorheben" }
                    button {
                        attr.disabled model.watchModeLocked
                        on.click (fun _ -> dispatch ToggleReadOnly)
                        if model.readOnly then "Bearbeiten erlauben" else "Nur ansehen"
                    }
                    button { on.click (fun _ -> dispatch SimulateRun); "Simuliere Ausführung" }
                }
                if model.watchModeLocked then
                    p {
                        "Ein Pilot fliegt gerade ein Programm. Zum Bearbeiten müssen alle Piloten angehalten werden."
                    }
                h2 { "Programm" }
                div {
                    attr.id model.containerId
                    attr.style "height: 360px; width: 100%; border: 1px solid #ccc; margin-top: 0.5rem"
                }
            }
        viewCustomBlockLibrary model dispatch
        h2 { "Blockwerkstatt (Eigener Block definieren)" }
        p {
            "Ziehe \"Eigener Block\" auf die Fläche, öffne sein Zahnrad-Menü, füge eine Eingabe hinzu, dann:"
            button { on.click (fun _ -> dispatch PublishSignature); "Signatur an Programm übergeben" }
        }
        div {
            attr.id model.workshopContainerId
            attr.style "height: 360px; width: 100%; border: 1px solid #ccc; margin-top: 0.5rem"
        }
        viewDashboard model dispatch
        match model.dashboard with
        | Some state ->
            viewSystemMap state dispatch
            viewInspector state model dispatch
        | None -> Node.Empty()
        viewQueueStatus model dispatch
        viewJobRunner model dispatch
    }

type MyApp() =
    inherit ProgramComponent<Model, Message>()

    override this.Program =
        let js = this.JSRuntime
        let remote = this.Remote<WorkspaceService>()
        let agentRemote = this.Remote<AgentService>()
        let queueRemote = this.Remote<QueueService>()
        let jobRemote = this.Remote<JobService>()
        let customBlockRemote = this.Remote<CustomBlockService>()
        let programRemote = this.Remote<ProgramService>()
        Program.mkProgram
            (fun _ ->
                initModel,
                Cmd.batch [
                    Cmd.ofMsg Init
                    Cmd.ofMsg LoadDashboard
                    Cmd.ofMsg LoadQueueStatus
                    Cmd.ofMsg LoadPilots
                    Cmd.ofMsg LoadCustomBlocks
                    Cmd.ofMsg LoadPrograms
                    Cmd.ofMsg MapTick
                ])
            (update js remote agentRemote queueRemote jobRemote customBlockRemote programRemote)
            view
