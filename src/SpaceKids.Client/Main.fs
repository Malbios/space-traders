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

/// Milestone 12 (bilingual support): the single process-wide locale preference —
/// `"de"`/`"en"` only, not attached to any user/profile (there is none).
type SettingsService =
    {
        getLocale: unit -> Async<string>
        setLocale: string -> Async<unit>
    }

    interface IRemoteService with
        member this.BasePath = "/settings"

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

/// Milestone 13/Part C: one row of the job history browser — unlike `JobSummaryDto`
/// above (only what's still in `JobRunner.fs`'s in-memory dictionary, so it forgets
/// terminal jobs after a restart), this reads straight from the `jobs` table, which
/// never deletes rows. `programName` is already resolved server-side (joined
/// through `programs.workspace_id` to `program_definitions.name`) so the client
/// never needs a raw program/workspace id.
type JobHistoryDto =
    {
        jobId: string
        programName: string
        shipSymbol: string
        status: string
        finishedAt: System.DateTime
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
        /// Milestone 13/Part C: most-recent-50 terminal jobs, newest first —
        /// survives a server restart since it reads the persisted `jobs` table
        /// directly instead of `JobRunner.fs`'s in-memory dictionary.
        listHistory: unit -> Async<JobHistoryDto list>
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

type Tab =
    | ProgrammierenTab
    | PilotenTab
    | GalaxieTab
    | SystemTab

type Model =
    {
        activeTab: Tab
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
        /// Milestone 13/Part C: most-recent-50 terminal jobs, read straight from
        /// the `jobs` table — unlike `pilots` above (which forgets terminal jobs
        /// once the server process restarts), this survives a restart.
        jobHistory: JobHistoryDto list
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
        /// Milestone 12 (bilingual support): `"de"`/`"en"`, loaded once at `Init`
        /// from the stored server-side setting (see `SettingsService`).
        locale: string
        /// True whenever the most recent load of *any* kind (pilots, queue status,
        /// job history, custom blocks, programs, locale) has failed — the SpaceKids
        /// server itself is unreachable, not the SpaceTraders API. Cleared by any
        /// subsequent successful load.
        serverUnreachable: bool
        /// Backoff for `MapTick`'s `LoadPilots` polling (the one periodic, repeat-
        /// forever call): only dispatched once `pilotsPollTicksSinceLast` reaches
        /// this many ticks. Doubles (capped at 30) on failure, resets to 1 on success
        /// — stops hammering a down server every second.
        pilotsPollBackoffTicks: int
        pilotsPollTicksSinceLast: int
    }

let private terminalPilotStatuses = [ "Completed"; "Failed"; "Cancelled" ]

/// Milestone 13/Part D (plan.md §15's "Pilot Max" idea): no name field exists
/// anywhere in the real SpaceTraders API data, so a display name has to be
/// invented rather than read from anything. One shared pool for both locales —
/// picking 24 distinct names for zero functional gain isn't worth it.
let private pilotNamePool =
    [| "Max"; "Lina"; "Tom"; "Mia"; "Ben"; "Ella"; "Finn"; "Nora"; "Leo"; "Zoe"; "Emil"; "Ida" |]

/// A stable hash of `shipSymbol` (not `System.String.GetHashCode`, which is
/// randomized per process in .NET) so the same ship always shows the same name
/// across restarts and re-runs — continuity, not randomness.
let private pilotName (shipSymbol: string) : string =
    let hash = shipSymbol |> Seq.sumBy int
    pilotNamePool.[hash % pilotNamePool.Length]

let initModel =
    {
        activeTab = ProgrammierenTab
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
        jobHistory = []
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
        locale = "de"
        serverUnreachable = false
        pilotsPollBackoffTicks = 1
        pilotsPollTicksSinceLast = 0
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
    | DashboardLoadFailed of string
    | LoadQueueStatus
    | QueueStatusLoaded of QueueStatusDto
    | SelectShip of string
    | StartProgram
    | ProgramStartResult of Result<string, string>
    | LoadPilots
    | PilotsLoaded of JobSummaryDto list
    | LoadJobHistory
    | JobHistoryLoaded of JobHistoryDto list
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
    | LoadLocale
    | LocaleLoaded of string
    | SetLocale of string
    | LocaleSet
    | SwitchTab of Tab
    /// Shared failure path for every load that previously had none at all (see
    /// `LoadDashboard`'s `DashboardLoadFailed` for the pattern this mirrors) — a
    /// down SpaceKids server should be visible and not spammed, not silent.
    | RemoteCallFailed

let private callVoid (js: IJSRuntime) (identifier: string) (args: obj[]) : Async<unit> =
    js.InvokeVoidAsync(identifier, args).AsTask() |> Async.AwaitTask

let private call<'a> (js: IJSRuntime) (identifier: string) (args: obj[]) : Async<'a> =
    js.InvokeAsync<'a>(identifier, args).AsTask() |> Async.AwaitTask

/// Milestone 12 (bilingual support): every UI string in this file, as a compile-time-
/// checked record rather than a stringly-typed lookup — a locale missing a
/// translation is a compile error, not a silent runtime gap. Interpolated messages
/// are functions instead of plain strings.
type Strings =
    { workshopLoaded: string
      savedToDb: string
      nothingToLoad: string
      loadedFromDb: string
      blockHighlighted: string
      readOnlyToggled: string
      signaturePublished: string
      simulating: string
      simulationDone: string
      pleaseOpenProgramFirst: string
      pleaseSelectShipFirst: string
      creating: string
      blockCreated: string
      workshopLoading: string
      renamed: string
      deleted: string
      noBlockToSave: string
      saving: string
      savedVersion: int -> string
      errorPrefix: string -> string
      programCreated: string
      programLoading: string
      programLoaded: string

      dashboardHeading: string
      loadingEllipsis: string
      tokenPlaceholder: string
      login: string
      pilotLabel: string -> string
      balance: int64 -> string
      headquarters: string -> string
      shipsHeading: string
      shipLine: string * string * string * string -> string
      contractsHeading: string
      contractLine: string * string * bool * bool -> string
      waypointsHeading: string
      waypointLine: string * string -> string
      marketHeading: string
      marketAt: string -> string
      exportLine: string -> string

      shipInspectorTitle: string -> string
      closeButton: string
      roleLine: string -> string
      waypointLabel: string
      statusAndFlightMode: string * string -> string
      inTransitLine: string * string * string -> string
      fuelLine: int * int -> string
      cargoLine: int * int -> string
      noCargo: string
      cooldownLine: int -> string

      waypointInspectorTitle: string -> string
      typeAndPosition: string * int * int -> string
      traitsHeading: string
      noKnownTraits: string
      shipsHereHeading: string
      noShipsHere: string
      loadMarket: string
      tradeGoodLine: string * int * int -> string
      shipyardHeading: string
      loadShipyard: string
      shipyardTypeLine: string * int -> string
      shipNotFound: string -> string
      waypointNotFound: string -> string

      systemMapHeading: string

      queueHeading: string
      refresh: string
      queueNotLoadedYet: string
      pendingRequests: int -> string
      serverResetDetected: string
      apiUnreachableSince: string -> string
      recentEventsHeading: string
      eventLine: string * string * string * int * int -> string

      pilotStatus: string -> string

      jobRunnerHeading: string
      pleaseLoginFirst: string
      shipLabel: string
      chooseOption: string
      start: string
      refreshPilots: string
      pilotsHeading: string
      noPilotsYet: string
      pilotNameLine: string * string -> string
      pilotStatusLine: string -> string
      lastLogLine: string -> string
      resume: string
      pause: string
      stop: string
      stopWatching: string
      watch: string
      innerActiveLine: string -> string
      openBlock: string
      logbookHeading: string
      noActivePilots: string
      logbookLine: string * string -> string
      historyHeading: string
      refreshHistory: string
      noHistoryYet: string
      historyLine: string * string * string * System.DateTime -> string

      customBlocksHeading: string
      newBlockNamePlaceholder: string
      newBlockButton: string
      noCustomBlocksYet: string
      customBlockLine: string * int -> string
      openButton: string
      deleteButton: string
      renameButton: string
      saveWorkshopButton: string

      programsHeading: string
      newProgramNamePlaceholder: string
      newProgramButton: string
      noProgramsYet: string
      closeButton2: string

      noProgramOpen: string
      staleWarningsBanner: string
      saveButton: string
      loadButton: string
      highlightFirstBlockButton: string
      allowEditing: string
      viewOnly: string
      simulateRunButton: string
      watchModeLockedBanner: string
      programHeading: string
      workshopHeading: string
      workshopHint: string
      publishSignatureButton: string

      tabProgrammieren: string
      tabPiloten: string
      tabGalaxie: string
      tabSystem: string
      serverUnreachableMessage: string }

let private stringsDe: Strings =
    { workshopLoaded = "Werkstatt geladen."
      savedToDb = "In SQLite gespeichert."
      nothingToLoad = "Nichts zum Laden."
      loadedFromDb = "Aus SQLite geladen."
      blockHighlighted = "Block hervorgehoben (falls vorhanden)."
      readOnlyToggled = "Lesemodus umgeschaltet."
      signaturePublished = "Signatur an Programm-Werkstatt übergeben."
      simulating = "Simuliere Ausführung..."
      simulationDone = "Simulation beendet."
      pleaseOpenProgramFirst = "Bitte zuerst ein Programm öffnen."
      pleaseSelectShipFirst = "Bitte zuerst ein Schiff auswählen."
      creating = "Erstelle..."
      blockCreated = "Block erstellt."
      workshopLoading = "Werkstatt wird geladen..."
      renamed = "Umbenannt."
      deleted = "Gelöscht."
      noBlockToSave = "Kein Block zum Speichern geöffnet."
      saving = "Speichere..."
      savedVersion = fun v -> $"Gespeichert (Version {v})."
      errorPrefix = fun msg -> $"Fehler: {msg}"
      programCreated = "Programm erstellt."
      programLoading = "Lädt..."
      programLoaded = "Programm geladen."

      dashboardHeading = "Echte SpaceTraders-Daten"
      loadingEllipsis = "Lädt..."
      tokenPlaceholder = "SpaceTraders-Token einfügen"
      login = "Anmelden"
      pilotLabel = fun symbol -> $"Pilot: {symbol}"
      balance = fun credits -> $"Kontostand: {credits} Credits"
      headquarters = fun hq -> $"Hauptquartier: {hq}"
      shipsHeading = "Schiffe"
      shipLine = fun (symbol, role, status, waypoint) -> $"{symbol} — {role} — {status} bei {waypoint}"
      contractsHeading = "Aufträge"
      contractLine = fun (id, ``type``, accepted, fulfilled) -> $"{id} ({``type``}) — angenommen: {accepted}, erfüllt: {fulfilled}"
      waypointsHeading = "Wegpunkte"
      waypointLine = fun (symbol, ``type``) -> $"{symbol} ({``type``})"
      marketHeading = "Markt"
      marketAt = fun symbol -> $"Markt bei {symbol}"
      exportLine = fun name -> $"Export: {name}"

      shipInspectorTitle = fun symbol -> $"Schiff: {symbol}"
      closeButton = "Schließen"
      roleLine = fun role -> $"Rolle: {role}"
      waypointLabel = "Wegpunkt: "
      statusAndFlightMode = fun (status, flightMode) -> $"Status: {status} ({flightMode})"
      inTransitLine = fun (origin, dest, arrival) -> $"Unterwegs von {origin} nach {dest}, Ankunft: {arrival}"
      fuelLine = fun (current, capacity) -> $"Treibstoff: {current} / {capacity}"
      cargoLine = fun (units, capacity) -> $"Fracht: {units} / {capacity}"
      noCargo = "Keine Fracht an Bord."
      cooldownLine = fun seconds -> $"Abklingzeit: noch {seconds}s"

      waypointInspectorTitle = fun symbol -> $"Wegpunkt: {symbol}"
      typeAndPosition = fun (``type``, x, y) -> $"Typ: {``type``} — Position: ({x}, {y})"
      traitsHeading = "Eigenschaften"
      noKnownTraits = "Keine bekannten Eigenschaften."
      shipsHereHeading = "Schiffe hier"
      noShipsHere = "Keine Schiffe an diesem Wegpunkt."
      loadMarket = "Markt laden"
      tradeGoodLine = fun (symbol, buy, sell) -> $"{symbol}: Kaufpreis {buy}, Verkaufspreis {sell}"
      shipyardHeading = "Werft"
      loadShipyard = "Werft laden"
      shipyardTypeLine = fun (``type``, price) -> $"{``type``}: {price} Credits"
      shipNotFound = fun symbol -> $"Schiff {symbol} nicht gefunden."
      waypointNotFound = fun symbol -> $"Wegpunkt {symbol} nicht gefunden."

      systemMapHeading = "Systemkarte"

      queueHeading = "Warteschlange"
      refresh = "Aktualisieren"
      queueNotLoadedYet = "Noch nicht geladen."
      pendingRequests = fun n -> $"Wartende Anfragen: {n}"
      serverResetDetected = "Der Spielserver wurde zurückgesetzt. Ein neuer Kapitän muss erstellt werden."
      apiUnreachableSince =
          fun since ->
              $"Die Raumfunkzentrale ist gerade nicht erreichbar (seit {since}). Deine Piloten machen weiter, sobald sie wieder Funkkontakt haben."
      recentEventsHeading = "Letzte Ereignisse"
      eventLine =
          fun (time, endpoint, status, priority, attempt) -> $"{time} {endpoint} — {status} (Priorität {priority}, Versuch {attempt})"

      pilotStatus =
          fun status ->
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

      jobRunnerHeading = "Programm ausführen"
      pleaseLoginFirst = "Zuerst anmelden, um ein Schiff auszuwählen."
      shipLabel = "Schiff: "
      chooseOption = "-- wählen --"
      start = "Start"
      refreshPilots = "Piloten aktualisieren"
      pilotsHeading = "Piloten"
      noPilotsYet = "Noch kein Pilot aktiv."
      pilotNameLine = fun (name, ship) -> $"🤖 Pilot {name} steuert Schiff {ship}"
      pilotStatusLine = fun status -> $"Status: {status}"
      lastLogLine = fun line -> $"Zuletzt: {line}"
      resume = "Fortsetzen"
      pause = "Pause"
      stop = "Stoppen"
      stopWatching = "Beobachtung stoppen"
      watch = "Beobachten"
      innerActiveLine = fun name -> $"Innen aktiv: \"{name}\" "
      openBlock = "Block öffnen"
      logbookHeading = "Logbuch"
      noActivePilots = "Keine Piloten aktiv."
      logbookLine = fun (symbol, line) -> $"🤖 {symbol}: {line}"
      historyHeading = "Verlauf"
      refreshHistory = "Verlauf aktualisieren"
      noHistoryYet = "Noch kein abgeschlossener Lauf."
      historyLine =
          fun (programName, shipSymbol, status, finishedAt) ->
              $"{programName} — Schiff {shipSymbol} — {status} ({finishedAt.ToLocalTime():g})"

      customBlocksHeading = "Eigene Blöcke"
      newBlockNamePlaceholder = "Name des neuen Blocks"
      newBlockButton = "Neuer Block"
      noCustomBlocksYet = "Noch kein eigener Block gespeichert."
      customBlockLine = fun (name, version) -> $"{name} (Version {version}) "
      openButton = "Öffnen"
      deleteButton = "Löschen"
      renameButton = "Umbenennen"
      saveWorkshopButton = "Workshop speichern"

      programsHeading = "Programme"
      newProgramNamePlaceholder = "Name des neuen Programms"
      newProgramButton = "Neues Programm"
      noProgramsYet = "Noch kein Programm gespeichert."
      closeButton2 = "Schließen"

      noProgramOpen = "Kein Programm geöffnet — wähle oder erstelle eines oben."
      staleWarningsBanner = "Einige eigene Blöcke in diesem Programm haben sich geändert — bitte vor dem Ausführen prüfen."
      saveButton = "Speichern"
      loadButton = "Laden"
      highlightFirstBlockButton = "Ersten Block hervorheben"
      allowEditing = "Bearbeiten erlauben"
      viewOnly = "Nur ansehen"
      simulateRunButton = "Simuliere Ausführung"
      watchModeLockedBanner = "Ein Pilot fliegt gerade ein Programm. Zum Bearbeiten müssen alle Piloten angehalten werden."
      programHeading = "Programm"
      workshopHeading = "Blockwerkstatt (Eigener Block definieren)"
      workshopHint = "Ziehe \"Eigener Block\" auf die Fläche, öffne sein Zahnrad-Menü, füge eine Eingabe hinzu, dann:"
      publishSignatureButton = "Signatur an Programm übergeben"

      tabProgrammieren = "Programmieren"
      tabPiloten = "Piloten"
      tabGalaxie = "Galaxie"
      tabSystem = "System"
      serverUnreachableMessage = "Verbindung zum Server unterbrochen — versuche es weiter..." }

let private stringsEn: Strings =
    { workshopLoaded = "Workshop loaded."
      savedToDb = "Saved to SQLite."
      nothingToLoad = "Nothing to load."
      loadedFromDb = "Loaded from SQLite."
      blockHighlighted = "Block highlighted (if any)."
      readOnlyToggled = "Read-only mode toggled."
      signaturePublished = "Signature handed off to the program workshop."
      simulating = "Simulating run..."
      simulationDone = "Simulation finished."
      pleaseOpenProgramFirst = "Please open a program first."
      pleaseSelectShipFirst = "Please select a ship first."
      creating = "Creating..."
      blockCreated = "Block created."
      workshopLoading = "Loading workshop..."
      renamed = "Renamed."
      deleted = "Deleted."
      noBlockToSave = "No block open to save."
      saving = "Saving..."
      savedVersion = fun v -> $"Saved (version {v})."
      errorPrefix = fun msg -> $"Error: {msg}"
      programCreated = "Program created."
      programLoading = "Loading..."
      programLoaded = "Program loaded."

      dashboardHeading = "Real SpaceTraders data"
      loadingEllipsis = "Loading..."
      tokenPlaceholder = "Paste SpaceTraders token"
      login = "Log in"
      pilotLabel = fun symbol -> $"Pilot: {symbol}"
      balance = fun credits -> $"Balance: {credits} credits"
      headquarters = fun hq -> $"Headquarters: {hq}"
      shipsHeading = "Ships"
      shipLine = fun (symbol, role, status, waypoint) -> $"{symbol} — {role} — {status} at {waypoint}"
      contractsHeading = "Contracts"
      contractLine = fun (id, ``type``, accepted, fulfilled) -> $"{id} ({``type``}) — accepted: {accepted}, fulfilled: {fulfilled}"
      waypointsHeading = "Waypoints"
      waypointLine = fun (symbol, ``type``) -> $"{symbol} ({``type``})"
      marketHeading = "Market"
      marketAt = fun symbol -> $"Market at {symbol}"
      exportLine = fun name -> $"Export: {name}"

      shipInspectorTitle = fun symbol -> $"Ship: {symbol}"
      closeButton = "Close"
      roleLine = fun role -> $"Role: {role}"
      waypointLabel = "Waypoint: "
      statusAndFlightMode = fun (status, flightMode) -> $"Status: {status} ({flightMode})"
      inTransitLine = fun (origin, dest, arrival) -> $"En route from {origin} to {dest}, arrival: {arrival}"
      fuelLine = fun (current, capacity) -> $"Fuel: {current} / {capacity}"
      cargoLine = fun (units, capacity) -> $"Cargo: {units} / {capacity}"
      noCargo = "No cargo on board."
      cooldownLine = fun seconds -> $"Cooldown: {seconds}s remaining"

      waypointInspectorTitle = fun symbol -> $"Waypoint: {symbol}"
      typeAndPosition = fun (``type``, x, y) -> $"Type: {``type``} — Position: ({x}, {y})"
      traitsHeading = "Traits"
      noKnownTraits = "No known traits."
      shipsHereHeading = "Ships here"
      noShipsHere = "No ships at this waypoint."
      loadMarket = "Load market"
      tradeGoodLine = fun (symbol, buy, sell) -> $"{symbol}: buy price {buy}, sell price {sell}"
      shipyardHeading = "Shipyard"
      loadShipyard = "Load shipyard"
      shipyardTypeLine = fun (``type``, price) -> $"{``type``}: {price} credits"
      shipNotFound = fun symbol -> $"Ship {symbol} not found."
      waypointNotFound = fun symbol -> $"Waypoint {symbol} not found."

      systemMapHeading = "System map"

      queueHeading = "Queue"
      refresh = "Refresh"
      queueNotLoadedYet = "Not loaded yet."
      pendingRequests = fun n -> $"Pending requests: {n}"
      serverResetDetected = "The game server was reset. A new captain must be created."
      apiUnreachableSince =
          fun since -> $"Mission control is currently unreachable (since {since}). Your pilots will continue once contact is restored."
      recentEventsHeading = "Recent events"
      eventLine = fun (time, endpoint, status, priority, attempt) -> $"{time} {endpoint} — {status} (priority {priority}, attempt {attempt})"

      pilotStatus =
          fun status ->
              match status with
              | "Running" -> "Running program"
              | "AwaitingApiResponse" -> "Awaiting confirmation"
              | "WaitingForArrival" -> "En route"
              | "WaitingForCooldown" -> "Waiting for cooldown"
              | "Reconciling" -> "Checking last action"
              | "AwaitingInfoResponse" -> "Awaiting information"
              | "Paused" -> "Paused"
              | "Cancelled" -> "Stopped"
              | "Completed" -> "Done"
              | "Failed" -> "Failed"
              | other -> other

      jobRunnerHeading = "Run program"
      pleaseLoginFirst = "Log in first to select a ship."
      shipLabel = "Ship: "
      chooseOption = "-- choose --"
      start = "Start"
      refreshPilots = "Refresh pilots"
      pilotsHeading = "Pilots"
      noPilotsYet = "No pilot active yet."
      pilotNameLine = fun (name, ship) -> $"🤖 Pilot {name} is flying ship {ship}"
      pilotStatusLine = fun status -> $"Status: {status}"
      lastLogLine = fun line -> $"Last: {line}"
      resume = "Resume"
      pause = "Pause"
      stop = "Stop"
      stopWatching = "Stop watching"
      watch = "Watch"
      innerActiveLine = fun name -> $"Active inside: \"{name}\" "
      openBlock = "Open block"
      logbookHeading = "Log"
      noActivePilots = "No pilots active."
      logbookLine = fun (symbol, line) -> $"🤖 {symbol}: {line}"
      historyHeading = "History"
      refreshHistory = "Refresh history"
      noHistoryYet = "No finished run yet."
      historyLine =
          fun (programName, shipSymbol, status, finishedAt) ->
              $"{programName} — ship {shipSymbol} — {status} ({finishedAt.ToLocalTime():g})"

      customBlocksHeading = "Custom blocks"
      newBlockNamePlaceholder = "New block name"
      newBlockButton = "New block"
      noCustomBlocksYet = "No custom block saved yet."
      customBlockLine = fun (name, version) -> $"{name} (version {version}) "
      openButton = "Open"
      deleteButton = "Delete"
      renameButton = "Rename"
      saveWorkshopButton = "Save workshop"

      programsHeading = "Programs"
      newProgramNamePlaceholder = "New program name"
      newProgramButton = "New program"
      noProgramsYet = "No program saved yet."
      closeButton2 = "Close"

      noProgramOpen = "No program open — choose or create one above."
      staleWarningsBanner = "Some custom blocks in this program have changed — please check before running."
      saveButton = "Save"
      loadButton = "Load"
      highlightFirstBlockButton = "Highlight first block"
      allowEditing = "Allow editing"
      viewOnly = "View only"
      simulateRunButton = "Simulate run"
      watchModeLockedBanner = "A pilot is currently flying a program. All pilots must be stopped before editing."
      programHeading = "Program"
      workshopHeading = "Block workshop (define a custom block)"
      workshopHint = "Drag \"Custom block\" onto the canvas, open its gear menu, add an input, then:"
      publishSignatureButton = "Hand signature to program"

      tabProgrammieren = "Code"
      tabPiloten = "Pilots"
      tabGalaxie = "Galaxy"
      tabSystem = "System"
      serverUnreachableMessage = "Lost connection to the server — still retrying..." }

let private stringsFor (locale: string) : Strings = if locale = "en" then stringsEn else stringsDe

let update
    (js: IJSRuntime)
    (remote: WorkspaceService)
    (agentRemote: AgentService)
    (queueRemote: QueueService)
    (jobRemote: JobService)
    (customBlockRemote: CustomBlockService)
    (programRemote: ProgramService)
    (settingsRemote: SettingsService)
    message
    model
    =
    let s = stringsFor model.locale

    match message with
    | Init ->
        // The main program container isn't initialized here — no program is open
        // yet at startup (saved/named multiple-program library); `OpenProgram`
        // initializes it on demand, keyed by the program's own id.
        model, Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.initWorkspace" [| box model.workshopContainerId; box false |]) () (fun () -> Inited)
    | Inited ->
        { model with status = s.workshopLoaded }, Cmd.none

    | Save ->
        let saveToDb = async {
            let! json = call<string> js "spaceKids.serializeWorkspace" [| box model.containerId |]
            do! remote.save (model.containerId, json)
        }
        model, Cmd.OfAsync.perform (fun () -> saveToDb) () (fun () -> Saved)
    | Saved ->
        { model with status = s.savedToDb }, Cmd.none

    | Load ->
        model, Cmd.OfAsync.perform (fun () -> remote.load model.containerId) () LoadedFromDb
    | LoadedFromDb None ->
        { model with status = s.nothingToLoad }, Cmd.none
    | LoadedFromDb(Some json) ->
        model, Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.loadWorkspace" [| box model.containerId; box json |]) () (fun () -> Loaded)
    | Loaded ->
        { model with status = s.loadedFromDb }, Cmd.none

    | HighlightFirstBlock ->
        let highlightFirst = async {
            let! idOpt = call<string option> js "spaceKids.firstBlockId" [| box model.containerId |]
            match idOpt with
            | Some id -> do! callVoid js "spaceKids.highlightBlock" [| box model.containerId; box id |]
            | None -> ()
        }
        model, Cmd.OfAsync.perform (fun () -> highlightFirst) () (fun () -> Highlighted)
    | Highlighted ->
        { model with status = s.blockHighlighted }, Cmd.none

    | ToggleReadOnly ->
        let next = not model.readOnly
        model, Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.setReadOnly" [| box model.containerId; box next |]) () (fun () -> ReadOnlyToggled)
    | ReadOnlyToggled ->
        { model with readOnly = not model.readOnly; status = s.readOnlyToggled }, Cmd.none

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
        { model with publishedCustomBlockId = Some customBlockId; status = s.signaturePublished }, Cmd.none

    | SimulateRun ->
        { model with status = s.simulating },
        Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.simulateRun" [| box model.containerId |]) () (fun () -> Simulated)
    | Simulated ->
        { model with status = s.simulationDone }, Cmd.none

    | TokenInputChanged value ->
        { model with tokenInput = value }, Cmd.none
    | SubmitToken ->
        { model with dashboardLoading = true; dashboardError = None },
        Cmd.OfAsync.either
            (fun () -> agentRemote.submitToken model.tokenInput)
            ()
            TokenSubmitted
            (fun ex -> TokenSubmitted(Error ex.Message))
    | TokenSubmitted(Ok state) ->
        { model with dashboard = Some state; dashboardLoading = false; dashboardError = None }, Cmd.none
    | TokenSubmitted(Error message) ->
        { model with dashboardLoading = false; dashboardError = Some message }, Cmd.none
    | LoadDashboard ->
        // Fires automatically at page load and every few `MapTick`s (a silent
        // background refresh) -- unlike `SubmitToken`, a real user action, this
        // shouldn't flash a loading indicator for something the player never asked
        // for, so `dashboardLoading` is left untouched here. A failure (e.g. a
        // deserialization error on unexpected real-account data) must still surface
        // visibly rather than leaving the page stuck with no error and no result.
        model,
        Cmd.OfAsync.either (fun () -> agentRemote.loadDashboard ()) () DashboardLoaded (fun ex -> DashboardLoadFailed ex.Message)
    | DashboardLoaded stateOpt ->
        { model with dashboard = stateOpt }, Cmd.none
    | DashboardLoadFailed message ->
        { model with dashboardError = Some message }, Cmd.none
    | LoadQueueStatus ->
        model, Cmd.OfAsync.either (fun () -> queueRemote.getStatus ()) () QueueStatusLoaded (fun _ -> RemoteCallFailed)
    | QueueStatusLoaded status ->
        { model with queueStatus = Some status; serverUnreachable = false }, Cmd.none

    | SelectShip symbol ->
        { model with selectedShipSymbol = Some symbol }, Cmd.none
    | StartProgram ->
        match model.currentProgramId, model.selectedShipSymbol with
        | None, _ -> { model with status = s.pleaseOpenProgramFirst }, Cmd.none
        | Some _, None -> { model with status = s.pleaseSelectShipFirst }, Cmd.none
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
        model, Cmd.OfAsync.either (fun () -> jobRemote.listJobs ()) () PilotsLoaded (fun _ -> RemoteCallFailed)
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

        // Event-driven dashboard refresh: a job that just transitioned into a
        // terminal state (matched by jobId against the *previous* poll) means
        // ship/agent state on the real API likely changed -- that's the only time
        // it's worth spending a rate-limited real API refetch.
        let justCompleted =
            pilots
            |> List.exists (fun p ->
                List.contains p.status terminalPilotStatuses
                && model.pilots
                   |> List.exists (fun old -> old.jobId = p.jobId && not (List.contains old.status terminalPilotStatuses)))

        { model with
            pilots = pilots
            watchModeLocked = anyActive
            serverUnreachable = false
            pilotsPollBackoffTicks = 1 },
        Cmd.batch [ readOnlyCmd; if justCompleted then Cmd.ofMsg LoadDashboard else Cmd.none ]
    | WatchModeReadOnlySet value ->
        { model with readOnly = value }, Cmd.none
    | PausePilot jobId ->
        model, Cmd.OfAsync.perform (fun () -> jobRemote.pause jobId) () (fun () -> PilotActionDone)
    | ResumePilot jobId ->
        model, Cmd.OfAsync.perform (fun () -> jobRemote.resume jobId) () (fun () -> PilotActionDone)
    | CancelPilot jobId ->
        model, Cmd.OfAsync.perform (fun () -> jobRemote.cancel jobId) () (fun () -> PilotActionDone)
    | PilotActionDone ->
        model, Cmd.batch [ Cmd.ofMsg LoadPilots; Cmd.ofMsg LoadJobHistory ]
    | LoadJobHistory ->
        model, Cmd.OfAsync.either (fun () -> jobRemote.listHistory ()) () JobHistoryLoaded (fun _ -> RemoteCallFailed)
    | JobHistoryLoaded history ->
        { model with jobHistory = history; serverUnreachable = false }, Cmd.none

    | LoadCustomBlocks ->
        model, Cmd.OfAsync.either (fun () -> customBlockRemote.list ()) () CustomBlocksLoaded (fun _ -> RemoteCallFailed)
    | CustomBlocksLoaded blocks ->
        { model with customBlocks = blocks; serverUnreachable = false }, Cmd.none

    | NewCustomBlockNameChanged value ->
        { model with newCustomBlockName = value }, Cmd.none
    | CreateCustomBlock ->
        if model.newCustomBlockName.Trim() = "" then
            model, Cmd.none
        else
            { model with workshopStatus = s.creating },
            Cmd.OfAsync.perform (fun () -> customBlockRemote.create model.newCustomBlockName) () CustomBlockCreated
    | CustomBlockCreated id ->
        let name = model.newCustomBlockName
        { model with newCustomBlockName = ""; workshopStatus = s.blockCreated },
        Cmd.batch [ Cmd.ofMsg LoadCustomBlocks; Cmd.ofMsg (OpenCustomBlock(id, name)) ]

    | OpenCustomBlock(id, name) ->
        { model with
            openCustomBlockId = Some id
            renameNameInput = name
            workshopStatus = s.workshopLoading },
        Cmd.OfAsync.perform (fun () -> customBlockRemote.loadDefinition id) () CustomBlockDefinitionLoaded
    | CustomBlockDefinitionLoaded jsonOpt ->
        let json = jsonOpt |> Option.defaultValue """{"blocks":{"languageVersion":0,"blocks":[]}}"""

        { model with workshopStatus = s.workshopLoaded },
        Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.loadWorkspace" [| box model.workshopContainerId; box json |]) () (fun () -> Loaded)

    | RenameNameInputChanged value ->
        { model with renameNameInput = value }, Cmd.none
    | RenameCustomBlock id ->
        model, Cmd.OfAsync.perform (fun () -> customBlockRemote.rename (id, model.renameNameInput)) () (fun () -> CustomBlockRenamed)
    | CustomBlockRenamed ->
        { model with workshopStatus = s.renamed }, Cmd.ofMsg LoadCustomBlocks

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
        { model with openCustomBlockId = openId; workshopStatus = s.deleted }, Cmd.ofMsg LoadCustomBlocks
    | CustomBlockDeleteResult(_, Error message) ->
        { model with workshopStatus = message }, Cmd.none

    | SaveWorkshop ->
        match model.openCustomBlockId with
        | None -> { model with workshopStatus = s.noBlockToSave }, Cmd.none
        | Some id ->
            { model with workshopStatus = s.saving },
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

        { model with workshopStatus = s.savedVersion version }, Cmd.batch [ publishCmd; Cmd.ofMsg LoadCustomBlocks ]
    | WorkshopSaved(Error message) ->
        { model with workshopStatus = s.errorPrefix message }, Cmd.none

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
        let ticksSinceLast = model.pilotsPollTicksSinceLast + 1

        let nextTickCmd =
            Cmd.OfAsync.perform (fun () -> async { do! Async.Sleep 1000 }) () (fun () -> MapTick)

        // Job status is a cheap, local, non-rate-limited read (JobRunner's in-process
        // dict) -- polling it every tick costs nothing against the real SpaceTraders
        // API. `PilotsLoaded` uses this to detect a job just finishing and triggers
        // the actual (rate-limited) dashboard refetch only then, instead of blindly
        // re-fetching every few ticks regardless of whether anything changed.
        //
        // `pilotsPollBackoffTicks` guards against a *different* problem: if the
        // SpaceKids server itself is down, this poll would otherwise fire every
        // second forever with no backoff (`RemoteCallFailed` doubles it, capped at
        // 30, on each failure; `PilotsLoaded` resets it to 1 on success).
        if ticksSinceLast >= model.pilotsPollBackoffTicks then
            { model with mapTickCount = count; pilotsPollTicksSinceLast = 0 },
            Cmd.batch [ Cmd.ofMsg LoadPilots; nextTickCmd ]
        else
            { model with mapTickCount = count; pilotsPollTicksSinceLast = ticksSinceLast }, nextTickCmd

    | LoadPrograms ->
        model, Cmd.OfAsync.either (fun () -> programRemote.list ()) () ProgramsLoaded (fun _ -> RemoteCallFailed)
    | ProgramsLoaded programs ->
        { model with programs = programs; serverUnreachable = false }, Cmd.none

    | NewProgramNameChanged value ->
        { model with newProgramName = value }, Cmd.none
    | CreateProgram ->
        if model.newProgramName.Trim() = "" then
            model, Cmd.none
        else
            { model with programStatus = s.creating },
            Cmd.OfAsync.perform (fun () -> programRemote.create model.newProgramName) () ProgramCreated
    | ProgramCreated id ->
        { model with newProgramName = ""; programStatus = s.programCreated },
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
            programStatus = s.programLoading },
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

        { model with programStatus = s.programLoaded; staleWarnings = warnings }, loadCmd

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
        { model with programStatus = s.renamed }, Cmd.ofMsg LoadPrograms

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
        { model with programStatus = s.deleted }, Cmd.batch [ closeCmd; Cmd.ofMsg LoadPrograms ]
    | ProgramDeleteResult(_, Error message) ->
        { model with programStatus = message }, Cmd.none

    | LoadLocale ->
        model, Cmd.OfAsync.either (fun () -> settingsRemote.getLocale ()) () LocaleLoaded (fun _ -> RemoteCallFailed)
    | LocaleLoaded locale ->
        // The persisted setting must reach the JS side too — otherwise a freshly
        // loaded page keeps rendering new blocks in German regardless of what was
        // saved, since `locale-state.ts`'s own `currentLocale` defaults to "de"
        // until something explicitly tells it otherwise.
        { model with locale = locale; serverUnreachable = false },
        Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.setLocale" [| box locale |]) () (fun () -> LocaleSet)
    | SetLocale locale ->
        { model with locale = locale },
        Cmd.batch [
            Cmd.OfAsync.perform (fun () -> settingsRemote.setLocale locale) () (fun () -> LocaleSet)
            // `spaceKids.setLocale` re-renders every currently-open workspace under
            // the new locale itself (recreating each one's blocks from its own
            // serialized JSON) — nothing else needs to destroy/reinit anything here.
            Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.setLocale" [| box locale |]) () (fun () -> LocaleSet)
        ]
    | LocaleSet -> model, Cmd.none

    | SwitchTab tab -> { model with activeTab = tab }, Cmd.none
    | RemoteCallFailed ->
        { model with
            serverUnreachable = true
            pilotsPollBackoffTicks = min 30 (max 2 (model.pilotsPollBackoffTicks * 2)) },
        Cmd.none

let private viewDashboard model dispatch =
    let s = stringsFor model.locale

    div {
        h2 { s.dashboardHeading }
        button { on.click (fun _ -> dispatch LoadDashboard); s.refresh }
        if model.dashboardLoading then
            p { s.loadingEllipsis }
        match model.dashboardError with
        | Some err -> p { s.errorPrefix err }
        | None -> ()
        match model.dashboard with
        | None ->
            div {
                input {
                    attr.``type`` "text"
                    attr.placeholder s.tokenPlaceholder
                    attr.value model.tokenInput
                    on.change (fun e -> dispatch (TokenInputChanged(string e.Value)))
                }
                button { on.click (fun _ -> dispatch SubmitToken); s.login }
            }
        | Some state ->
            div {
                h3 { s.pilotLabel state.agent.symbol }
                p { s.balance state.agent.credits }
                p { s.headquarters state.agent.headquarters }
                h3 { s.shipsHeading }
                ul {
                    for ship in state.ships do
                        li {
                            attr.style "cursor: pointer; text-decoration: underline"
                            on.click (fun _ -> dispatch (InspectShip ship.symbol))
                            s.shipLine (ship.symbol, ship.registration.role, ship.nav.status, ship.nav.waypointSymbol)
                        }
                }
                h3 { s.contractsHeading }
                ul {
                    for contract in state.contracts do
                        li { s.contractLine (contract.id, contract.``type``, contract.accepted, contract.fulfilled) }
                }
                h3 { s.waypointsHeading }
                ul {
                    for waypoint in state.waypoints do
                        li {
                            attr.style "cursor: pointer; text-decoration: underline"
                            on.click (fun _ -> dispatch (InspectWaypoint waypoint.symbol))
                            s.waypointLine (waypoint.symbol, waypoint.``type``)
                        }
                }
                h3 { s.marketHeading }
                for market in state.markets do
                    div {
                        p { s.marketAt market.symbol }
                        ul {
                            for good in market.exports do
                                li { s.exportLine good.name }
                        }
                    }
            }
    }

/// Entity inspector (visual-map feature): every field already on `Ship` — no
/// data gap here, unlike waypoints (see `viewWaypointInspector`).
let private viewShipInspector (s: Strings) (ship: Ship) dispatch =
    div {
        attr.style "border: 1px solid #ccc; border-radius: 4px; padding: 0.5rem; margin: 0.5rem 0"
        h3 { s.shipInspectorTitle ship.symbol }
        button { on.click (fun _ -> dispatch CloseInspector); s.closeButton }
        p { s.roleLine ship.registration.role }
        p {
            s.waypointLabel
            a {
                attr.style "cursor: pointer; text-decoration: underline"
                on.click (fun _ -> dispatch (InspectWaypoint ship.nav.waypointSymbol))
                ship.nav.waypointSymbol
            }
        }
        p { s.statusAndFlightMode (ship.nav.status, ship.nav.flightMode) }
        if ship.nav.status = "IN_TRANSIT" then
            p { s.inTransitLine (ship.nav.route.origin.symbol, ship.nav.route.destination.symbol, ship.nav.route.arrival) }
        p { s.fuelLine (ship.fuel.current, ship.fuel.capacity) }
        p { s.cargoLine (ship.cargo.units, ship.cargo.capacity) }
        if ship.cargo.inventory.IsEmpty then
            p { s.noCargo }
        else
            ul {
                for item in ship.cargo.inventory do
                    li { $"{item.name}: {item.units}" }
            }
        if ship.cooldown.remainingSeconds > 0 then
            p { s.cooldownLine ship.cooldown.remainingSeconds }
    }

/// Entity inspector (visual-map feature): unlike `Ship`, `Waypoint` is thin on
/// its own (§'s "waypoint traits" addition) — traits, ships currently here (from
/// `state.ships`, not the waypoint itself), and lazily-loaded market/shipyard
/// data (gated on the matching trait) fill in "all the details."
let private viewWaypointInspector (strings: Strings) (waypoint: Waypoint) (state: DashboardState) model dispatch =
    let shipsHere = state.ships |> List.filter (fun sh -> sh.nav.waypointSymbol = waypoint.symbol)
    let hasTrait symbol = waypoint.traits |> List.exists (fun t -> t.symbol = symbol)

    div {
        attr.style "border: 1px solid #ccc; border-radius: 4px; padding: 0.5rem; margin: 0.5rem 0"
        h3 { strings.waypointInspectorTitle waypoint.symbol }
        button { on.click (fun _ -> dispatch CloseInspector); strings.closeButton }
        p { strings.typeAndPosition (waypoint.``type``, waypoint.x, waypoint.y) }

        h4 { strings.traitsHeading }
        if waypoint.traits.IsEmpty then
            p { strings.noKnownTraits }
        else
            ul {
                for t in waypoint.traits do
                    li { $"{t.name}: {t.description}" }
            }

        h4 { strings.shipsHereHeading }
        if shipsHere.IsEmpty then
            p { strings.noShipsHere }
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
            h4 { strings.marketHeading }
            match model.waypointMarket with
            | None -> button { on.click (fun _ -> dispatch (LoadWaypointMarket waypoint.symbol)); strings.loadMarket }
            | Some market ->
                ul {
                    for good in market.tradeGoods |> Option.defaultValue [] do
                        li { strings.tradeGoodLine (good.symbol, good.purchasePrice, good.sellPrice) }
                }

        if hasTrait "SHIPYARD" then
            h4 { strings.shipyardHeading }
            match model.waypointShipyard with
            | None -> button { on.click (fun _ -> dispatch (LoadWaypointShipyard waypoint.symbol)); strings.loadShipyard }
            | Some shipyard ->
                ul {
                    for entry in shipyard.ships do
                        li { strings.shipyardTypeLine (entry.``type``, entry.purchasePrice) }
                }
    }

let private viewInspector (state: DashboardState) model dispatch =
    let s = stringsFor model.locale

    match model.inspecting with
    | None -> Node.Empty()
    | Some(InspectedShip shipSymbol) ->
        match state.ships |> List.tryFind (fun sh -> sh.symbol = shipSymbol) with
        | Some ship -> viewShipInspector s ship dispatch
        | None -> div { s.shipNotFound shipSymbol }
    | Some(InspectedWaypoint waypointSymbol) ->
        match state.waypoints |> List.tryFind (fun w -> w.symbol = waypointSymbol) with
        | Some waypoint -> viewWaypointInspector s waypoint state model dispatch
        | None -> div { s.waypointNotFound waypointSymbol }

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

let private viewSystemMap (s: Strings) (state: DashboardState) dispatch =
    let bounds = computeMapBounds state.waypoints

    div {
        h2 { s.systemMapHeading }
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
    let s = stringsFor model.locale

    div {
        h2 { s.queueHeading }
        button { on.click (fun _ -> dispatch LoadQueueStatus); s.refresh }
        match model.queueStatus with
        | None -> p { s.queueNotLoadedYet }
        | Some status ->
            div {
                p { s.pendingRequests status.pendingCount }
                if status.serverResetDetected then
                    p { s.serverResetDetected }
                match status.unreachableSince with
                | Some since ->
                    let sinceText = since.ToString("HH:mm:ss")
                    p { s.apiUnreachableSince sinceText }
                | None -> ()
                h3 { s.recentEventsHeading }
                ul {
                    for evt in status.recentEvents do
                        let requestedAtText = evt.requestedAt.ToString("HH:mm:ss")
                        li { s.eventLine (requestedAtText, evt.endpoint, evt.status, evt.priority, evt.attempt) }
                }
            }
    }

let private viewJobRunner model dispatch =
    let s = stringsFor model.locale

    div {
        h2 { s.jobRunnerHeading }
        match model.dashboard with
        | None -> p { s.pleaseLoginFirst }
        | Some state ->
            div {
                label { s.shipLabel }
                select {
                    on.change (fun e -> dispatch (SelectShip(string e.Value)))
                    option { attr.value ""; s.chooseOption }
                    for ship in state.ships do
                        option { attr.value ship.symbol; ship.symbol }
                }
                button {
                    attr.disabled (model.currentProgramId.IsNone || model.selectedShipSymbol.IsNone || model.startingJob)
                    on.click (fun _ -> dispatch StartProgram)
                    s.start
                }
            }
            if model.currentProgramId.IsNone then
                p { s.pleaseOpenProgramFirst }
        match model.pilotError with
        | Some message -> p { s.errorPrefix message }
        | None -> ()

        h3 { s.pilotsHeading }
        button { on.click (fun _ -> dispatch LoadPilots); s.refreshPilots }
        if model.pilots.IsEmpty then
            p { s.noPilotsYet }
        else
            for pilot in model.pilots do
                let isTerminal = List.contains pilot.status terminalPilotStatuses
                let isPaused = pilot.status = "Paused"

                div {
                    attr.style "border: 1px solid #ccc; border-radius: 4px; padding: 0.5rem; margin: 0.5rem 0"
                    p { s.pilotNameLine (pilotName pilot.shipSymbol, pilot.shipSymbol) }
                    p { s.pilotStatusLine (s.pilotStatus pilot.status) }
                    match pilot.statusDetail with
                    | Some detail -> p { detail }
                    | None -> ()
                    match pilot.lastLogLine with
                    | Some line -> p { s.lastLogLine line }
                    | None -> ()
                    if not isTerminal then
                        if isPaused then
                            button { on.click (fun _ -> dispatch (ResumePilot pilot.jobId)); s.resume }
                        else
                            button { on.click (fun _ -> dispatch (PausePilot pilot.jobId)); s.pause }
                        button { on.click (fun _ -> dispatch (CancelPilot pilot.jobId)); s.stop }
                        if model.watchedJobId = Some pilot.jobId then
                            button { on.click (fun _ -> dispatch StopWatching); s.stopWatching }
                        else
                            button { on.click (fun _ -> dispatch (WatchPilot pilot.jobId)); s.watch }

                    if model.watchedJobId = Some pilot.jobId then
                        match model.watchedFrames with
                        | [] -> ()
                        | frames when frames.Length > 1 ->
                            let innerScope = fst frames.[0]
                            let innerName =
                                model.customBlocks |> List.tryFind (fun b -> b.id = innerScope) |> Option.map (fun b -> b.name) |> Option.defaultValue innerScope

                            p {
                                s.innerActiveLine innerName
                                button { on.click (fun _ -> dispatch (OpenCustomBlock(innerScope, innerName))); s.openBlock }
                            }
                        | _ -> ()
                }

        h3 { s.logbookHeading }
        let activePilots = model.pilots |> List.filter (fun p -> not (List.contains p.status terminalPilotStatuses))

        if activePilots.IsEmpty then
            p { s.noActivePilots }
        else
            ul {
                for pilot in activePilots do
                    li {
                        match pilot.lastLogLine with
                        | Some line -> s.logbookLine (pilot.shipSymbol, line)
                        | None -> s.logbookLine (pilot.shipSymbol, s.pilotStatus pilot.status)
                    }
            }

        h3 { s.historyHeading }
        button { on.click (fun _ -> dispatch LoadJobHistory); s.refreshHistory }
        if model.jobHistory.IsEmpty then
            p { s.noHistoryYet }
        else
            ul {
                for entry in model.jobHistory do
                    li {
                        s.historyLine (entry.programName, entry.shipSymbol, s.pilotStatus entry.status, entry.finishedAt)
                    }
            }
    }

let private viewCustomBlockLibrary model dispatch =
    let s = stringsFor model.locale

    div {
        h2 { s.customBlocksHeading }
        div {
            input {
                attr.``type`` "text"
                attr.placeholder s.newBlockNamePlaceholder
                attr.value model.newCustomBlockName
                on.change (fun e -> dispatch (NewCustomBlockNameChanged(string e.Value)))
            }
            button { on.click (fun _ -> dispatch CreateCustomBlock); s.newBlockButton }
        }
        if model.customBlocks.IsEmpty then
            p { s.noCustomBlocksYet }
        else
            ul {
                for b in model.customBlocks do
                    li {
                        s.customBlockLine (b.name, b.version)
                        button { on.click (fun _ -> dispatch (OpenCustomBlock(b.id, b.name))); s.openButton }
                        button { on.click (fun _ -> dispatch (DeleteCustomBlock b.id)); s.deleteButton }
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
                button { on.click (fun _ -> dispatch (RenameCustomBlock id)); s.renameButton }
                button { on.click (fun _ -> dispatch SaveWorkshop); s.saveWorkshopButton }
            }
        if model.workshopStatus <> "" then
            p { model.workshopStatus }
    }

let private viewProgramLibrary model dispatch =
    let s = stringsFor model.locale

    div {
        h2 { s.programsHeading }
        div {
            input {
                attr.``type`` "text"
                attr.placeholder s.newProgramNamePlaceholder
                attr.value model.newProgramName
                on.change (fun e -> dispatch (NewProgramNameChanged(string e.Value)))
            }
            button { on.click (fun _ -> dispatch CreateProgram); s.newProgramButton }
        }
        if model.programs.IsEmpty then
            p { s.noProgramsYet }
        else
            ul {
                for p in model.programs do
                    li {
                        $"{p.name} "
                        button { on.click (fun _ -> dispatch (OpenProgram p.id)); s.openButton }
                        button { on.click (fun _ -> dispatch (DeleteProgram p.id)); s.deleteButton }
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
                button { on.click (fun _ -> dispatch (RenameProgram id)); s.renameButton }
                button { on.click (fun _ -> dispatch CloseProgram); s.closeButton2 }
            }
        if model.programStatus <> "" then
            p { model.programStatus }
    }

/// CSS-hide inactive tabs rather than omitting them from the tree: two sections
/// (the program workspace and the custom-block workshop, both in `ProgrammierenTab`)
/// host imperative Blockly mounts (`spaceKids.initWorkspace`/`loadWorkspace`, JS
/// interop outside Blazor's diffing) — actually removing/re-adding those DOM nodes
/// on every tab switch would require re-running Blockly's init/load calls each time
/// and risks losing workspace state. Keeping every tab's markup always in the DOM
/// (just hidden) means Blockly's mounted instance is never touched by switching tabs.
let private tabStyle (model: Model) (tab: Tab) = if model.activeTab = tab then "" else "display: none"

let private tabButton (model: Model) dispatch (tab: Tab) (label: string) =
    button {
        attr.style (
            if model.activeTab = tab then
                "font-weight: bold; text-decoration: underline"
            else
                ""
        )
        on.click (fun _ -> dispatch (SwitchTab tab))
        label
    }

let view model dispatch =
    let s = stringsFor model.locale

    div {
        attr.style "font-family: sans-serif; padding: 1rem"
        h1 { "SpaceKids" }
        div {
            button {
                attr.disabled (model.locale = "de")
                on.click (fun _ -> dispatch (SetLocale "de"))
                "Deutsch"
            }
            button {
                attr.disabled (model.locale = "en")
                on.click (fun _ -> dispatch (SetLocale "en"))
                "English"
            }
        }
        p { model.status }
        if model.serverUnreachable then
            p { attr.style "color: #cc0000"; s.serverUnreachableMessage }

        div {
            attr.style "margin-bottom: 1rem; border-bottom: 1px solid #ccc; padding-bottom: 0.5rem"
            tabButton model dispatch ProgrammierenTab s.tabProgrammieren
            tabButton model dispatch PilotenTab s.tabPiloten
            tabButton model dispatch GalaxieTab s.tabGalaxie
            tabButton model dispatch SystemTab s.tabSystem
        }

        div {
            attr.style (tabStyle model ProgrammierenTab)
            viewProgramLibrary model dispatch
            match model.currentProgramId with
            | None -> p { s.noProgramOpen }
            | Some _ ->
                div {
                    if not model.staleWarnings.IsEmpty then
                        div {
                            attr.style "border: 1px solid #cc8800; background: #fff8e6; padding: 0.5rem; margin-bottom: 0.5rem"
                            p { s.staleWarningsBanner }
                            ul {
                                for w in model.staleWarnings do
                                    li { w }
                            }
                        }
                    div {
                        button { on.click (fun _ -> dispatch Save); s.saveButton }
                        button { on.click (fun _ -> dispatch Load); s.loadButton }
                        button { on.click (fun _ -> dispatch HighlightFirstBlock); s.highlightFirstBlockButton }
                        button {
                            attr.disabled model.watchModeLocked
                            on.click (fun _ -> dispatch ToggleReadOnly)
                            if model.readOnly then s.allowEditing else s.viewOnly
                        }
                        button { on.click (fun _ -> dispatch SimulateRun); s.simulateRunButton }
                    }
                    if model.watchModeLocked then
                        p { s.watchModeLockedBanner }
                    h2 { s.programHeading }
                    div {
                        attr.id model.containerId
                        attr.style "height: 360px; width: 100%; border: 1px solid #ccc; margin-top: 0.5rem"
                    }
                }
            viewCustomBlockLibrary model dispatch
            h2 { s.workshopHeading }
            p {
                s.workshopHint
                button { on.click (fun _ -> dispatch PublishSignature); s.publishSignatureButton }
            }
            div {
                attr.id model.workshopContainerId
                attr.style "height: 360px; width: 100%; border: 1px solid #ccc; margin-top: 0.5rem"
            }
        }

        div {
            attr.style (tabStyle model PilotenTab)
            viewJobRunner model dispatch
        }

        div {
            attr.style (tabStyle model GalaxieTab)
            viewDashboard model dispatch
            match model.dashboard with
            | Some state ->
                viewSystemMap s state dispatch
                viewInspector state model dispatch
            | None -> Node.Empty()
        }

        div {
            attr.style (tabStyle model SystemTab)
            viewQueueStatus model dispatch
        }
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
        let settingsRemote = this.Remote<SettingsService>()
        Program.mkProgram
            (fun _ ->
                initModel,
                Cmd.batch [
                    Cmd.ofMsg Init
                    Cmd.ofMsg LoadDashboard
                    Cmd.ofMsg LoadQueueStatus
                    Cmd.ofMsg LoadPilots
                    Cmd.ofMsg LoadJobHistory
                    Cmd.ofMsg LoadCustomBlocks
                    Cmd.ofMsg LoadPrograms
                    Cmd.ofMsg LoadLocale
                    Cmd.ofMsg MapTick
                ])
            (update js remote agentRemote queueRemote jobRemote customBlockRemote programRemote settingsRemote)
            view
