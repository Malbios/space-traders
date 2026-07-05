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
        shipSymbol: string
        status: string
        statusDetail: string option
        lastLogLine: string option
    }

type JobService =
    {
        /// token, shipSymbol, current workspace JSON -> new job id, or a German
        /// rejection message (e.g. the ship is already flown by another program).
        startJob: string * string * string -> Async<Result<string, string>>
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
    }

let private terminalPilotStatuses = [ "Completed"; "Failed"; "Cancelled" ]

let initModel =
    {
        containerId = "blockly-spike"
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
    message
    model
    =
    match message with
    | Init ->
        let initBoth = async {
            do! callVoid js "spaceKids.initWorkspace" [| box model.containerId; box model.readOnly |]
            do! callVoid js "spaceKids.initWorkspace" [| box model.workshopContainerId; box false |]
        }
        model, Cmd.OfAsync.perform (fun () -> initBoth) () (fun () -> Inited)
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
        match model.selectedShipSymbol with
        | None -> { model with status = "Bitte zuerst ein Schiff auswählen." }, Cmd.none
        | Some shipSymbol ->
            { model with startingJob = true; pilotError = None },
            Cmd.OfAsync.perform
                (fun () ->
                    async {
                        let! workspaceJson = call<string> js "spaceKids.serializeWorkspace" [| box model.containerId |]
                        return! jobRemote.startJob (model.tokenInput, shipSymbol, workspaceJson)
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
        let anyActive =
            pilots |> List.exists (fun p -> not (List.contains p.status terminalPilotStatuses))

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
                        li { $"{ship.symbol} — {ship.registration.role} — {ship.nav.status} bei {ship.nav.waypointSymbol}" }
                }
                h3 { "Aufträge" }
                ul {
                    for contract in state.contracts do
                        li { $"{contract.id} ({contract.``type``}) — angenommen: {contract.accepted}, erfüllt: {contract.fulfilled}" }
                }
                h3 { "Wegpunkte" }
                ul {
                    for waypoint in state.waypoints do
                        li { $"{waypoint.symbol} ({waypoint.``type``})" }
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
                    attr.disabled (model.selectedShipSymbol.IsNone || model.startingJob)
                    on.click (fun _ -> dispatch StartProgram)
                    "Start"
                }
            }
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

let view model dispatch =
    div {
        attr.style "font-family: sans-serif; padding: 1rem"
        h1 { "SpaceKids – Blockly-Spike (Milestone 0)" }
        p { model.status }
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
        Program.mkProgram
            (fun _ ->
                initModel,
                Cmd.batch [ Cmd.ofMsg Init; Cmd.ofMsg LoadDashboard; Cmd.ofMsg LoadQueueStatus; Cmd.ofMsg LoadPilots; Cmd.ofMsg LoadCustomBlocks ])
            (update js remote agentRemote queueRemote jobRemote customBlockRemote)
            view
