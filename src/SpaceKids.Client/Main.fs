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
        systems: StarSystem list
        selectedSystemSymbol: string
        waypoints: Waypoint list
        markets: Market list
    }

type GalaxySyncStatusDto =
    {
        phase: string
        systemsLoaded: int
        systemsTotal: int option
        isStale: bool
        error: string option
    }

/// Galaxie-tab snapshot — served from SQLite cache immediately; background sync
/// fills in paginated systems without blocking this RPC.
type GalaxyCatalogSnapshot =
    {
        systems: StarSystem list
        selectedSystemSymbol: string
        waypoints: Waypoint list
        markets: Market list
        sync: GalaxySyncStatusDto
    }

type FactionsSnapshot =
    {
        factions: Faction list
        reputations: (string * int) list
    }

type AgentService =
    {
        submitToken: string -> Async<Result<DashboardState, string>>
        loadDashboard: unit -> Async<DashboardState option>
        /// Ship/contract/agent only — skips the paginated galaxy catalog (`ListSystems`
        /// and friends) so pilot-completion refreshes don't burn rate limit.
        refreshDashboard: unit -> Async<DashboardState option>
        /// Cached galaxy snapshot (stale-while-revalidate) — fast read, background sync.
        getGalaxyCatalog: unit -> Async<Result<GalaxyCatalogSnapshot, string>>
        reloadGalaxy: unit -> Async<Result<unit, string>>
        reloadSystem: string -> Async<Result<GalaxyCatalogSnapshot, string>>
        loadFactions: unit -> Async<Result<FactionsSnapshot, string>>
        loadSystemWaypoints: string -> Async<Result<Waypoint list, string>>
        loadPublicAgents: unit -> Async<Result<Agent list, string>>
        /// Entity inspector (visual-map feature): loaded lazily (a button, not
        /// automatic) when the player opens a waypoint that has the matching
        /// trait — `None` if the waypoint turns out not to have one after all.
        getWaypointMarket: string -> Async<Market option>
        getWaypointShipyard: string -> Async<Shipyard option>
        acceptContract: string -> Async<Result<unit, string>>
        fulfillContract: string -> Async<Result<unit, string>>
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
        requestJson: string option
        responseJson: string option
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
        getPollIntervalSeconds: unit -> Async<int>
        setPollIntervalSeconds: int -> Async<unit>
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

type JobBranchStatusDto =
    {
        shipSymbol: string option
        status: string
        statusDetail: string option
        depth: int
    }

/// Milestone 7 (§15): one row of the pilot dashboard.
type JobSummaryDto =
    {
        jobId: string
        /// Saved/named multiple-program library: which program this pilot is
        /// flying — per-program watch mode filters pilots by this.
        programId: string
        /// `None` for a ship-agnostic job (§14 follow-up) — one that never
        /// references a ship-scoped block, so it was never assigned one.
        shipSymbol: string option
        status: string
        statusDetail: string option
        branchStatuses: JobBranchStatusDto list
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
        shipSymbol: string option
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
        startJob: string * string * string option * string -> Async<Result<string, string>>
        step: string -> Async<JobStatusDto option>
        run: string -> Async<JobStatusDto option>
        getStatus: string -> Async<JobStatusDto option>
        pause: string -> Async<unit>
        resume: string -> Async<unit>
        cancel: string -> Async<unit>
        /// Clears a finished pilot card from the live dashboard (`listJobs`) —
        /// purely local server memory, no bearing on `listHistory` below.
        dismiss: string -> Async<unit>
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
    | ContractsTab
    | FactionsTab
    | AgentsTab
    | SettingsTab

type ProgramSubTab =
    | ProgramsSubTab
    | CustomBlocksSubTab

type Model =
    {
        activeTab: Tab
        activeProgramSubTab: ProgramSubTab
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
        galaxySync: GalaxySyncStatusDto option
        galaxyInitialLoad: bool
        galaxyReloading: bool
        systemReloading: bool
        galaxyCatalogError: string option
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
        /// "now" itself. Pilot polling uses `RefreshDashboard` (no galaxy catalog).
        mapTickCount: int
        /// Visual system map zoom/pan (Galaxie tab) — client-local view state,
        /// reset to the auto-fit default on `ResetMapView`. `mapZoom` scales the
        /// SVG's `viewBox` window (>= 1.0 — no need to zoom out past the
        /// auto-fit-everything default); `mapPanX`/`mapPanY` shift that window's
        /// center, in the same coordinate space `scaleMapPoint` already scales
        /// waypoints into. `mapDragging` is true while the mouse button is held
        /// down over the map (drag-to-pan in progress).
        mapZoom: float
        mapPanX: float
        mapPanY: float
        mapDragging: bool
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
        /// Settings tab: the configured healthy-state baseline (in seconds) for
        /// `MapTick`'s `LoadPilots` polling — the floor that `pilotsPollBackoffTicks`
        /// multiplies past while the server is down. Persisted server-side
        /// (see `SettingsService`), loaded once at `Init`.
        pollIntervalSeconds: int
        /// Settings tab: `"light"`/`"dark"`, a per-browser display preference —
        /// unlike locale/poll-interval this is stored in `localStorage`, not the
        /// server, since it has no bearing on shared/server-side state.
        theme: string
        /// Settings tab: `"off"`/`"info"`/`"trace"`, gates the routine-activity
        /// trace logging in `LoadPilots`/`WatchTick` (both are cheap, frequent,
        /// local-only polls whose per-tick console noise should be silent by
        /// default). Stored in `localStorage`, same reasoning as `theme`.
        logLevel: string
        /// Contracts tab: fulfilled contracts are collapsed under a history
        /// section by default so the active ones aren't buried.
        contractsHistoryExpanded: bool
        /// Contracts tab: the contract id currently being accepted, if any —
        /// disables its Accept button for the round trip (same in-flight-guard
        /// role as `startingJob`, to avoid a repeat of the stuck-Start-button bug).
        acceptingContractId: string option
        /// Contracts tab: the contract id currently being fulfilled, if any.
        fulfillingContractId: string option
        contractActionError: string option
        contractsRefreshing: bool
        factionsSnapshot: FactionsSnapshot option
        factionsLoading: bool
        factionsError: string option
        publicAgents: Agent list option
        publicAgentsLoading: bool
        publicAgentsError: string option
        galaxyMapZoom: float
        galaxyMapPanX: float
        galaxyMapPanY: float
        galaxyDragging: bool
        galaxyMapDragMoved: bool
        systemWaypointsLoading: bool
        systemWaypointsError: string option
        pendingGalaxySystem: string option
    }

let private terminalPilotStatuses = [ "Completed"; "Failed"; "Cancelled" ]

/// Milestone 13/Part D (plan.md §15's "Pilot Max" idea): no name field exists
/// anywhere in the real SpaceTraders API data, so a display name has to be
/// invented rather than read from anything. One shared pool for both locales —
/// picking 24 distinct names for zero functional gain isn't worth it.
/// `internal` (not `private`) so `SpaceKids.Client.Tests` can unit-test this and the
/// other pure helpers below directly — see the `InternalsVisibleTo` attribute in
/// `Startup.fs`. Found in review: this file previously had zero test coverage of its
/// non-trivial pure logic (map math, this hash) despite none of it needing Blazor.
let internal pilotNamePool =
    [| "Max"; "Lina"; "Tom"; "Mia"; "Ben"; "Ella"; "Finn"; "Nora"; "Leo"; "Zoe"; "Emil"; "Ida" |]

/// A stable hash of `key` (not `System.String.GetHashCode`, which is randomized per
/// process in .NET) so the same pilot always shows the same name across restarts and
/// re-runs — continuity, not randomness. Keyed by ship symbol normally, or by job id
/// for a ship-agnostic job (§14 follow-up) that was never assigned one — either way,
/// a stable key yields a stable, flavorful name.
let internal pilotName (key: string) : string =
    let hash = key |> Seq.sumBy int
    pilotNamePool.[hash % pilotNamePool.Length]

let initModel =
    {
        activeTab = ProgrammierenTab
        activeProgramSubTab = ProgramsSubTab
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
        galaxySync = None
        galaxyInitialLoad = false
        galaxyReloading = false
        systemReloading = false
        galaxyCatalogError = None
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
        mapZoom = 1.0
        mapPanX = 0.0
        mapPanY = 0.0
        mapDragging = false
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
        pollIntervalSeconds = 1
        theme = "light"
        logLevel = "off"
        contractsHistoryExpanded = false
        acceptingContractId = None
        fulfillingContractId = None
        contractActionError = None
        contractsRefreshing = false
        factionsSnapshot = None
        factionsLoading = false
        factionsError = None
        publicAgents = None
        publicAgentsLoading = false
        publicAgentsError = None
        galaxyMapZoom = 1.0
        galaxyMapPanX = 0.0
        galaxyMapPanY = 0.0
        galaxyDragging = false
        galaxyMapDragMoved = false
        systemWaypointsLoading = false
        systemWaypointsError = None
        pendingGalaxySystem = None
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
    /// Carries the value actually committed to the Blockly workspace via JS — not
    /// re-derived from `model.readOnly` at completion time, which could have drifted
    /// (e.g. a concurrent watch-mode lock change) between dispatch and completion.
    | ReadOnlyToggled of bool
    | PublishSignature
    | Published of string
    | SimulateRun
    | Simulated
    | TokenInputChanged of string
    | SubmitToken
    | TokenSubmitted of Result<DashboardState, string>
    | LoadDashboard
    | RefreshDashboard
    | RefreshContracts
    | LoadGalaxyCatalog
    | PollGalaxyCatalog
    | GalaxyCatalogLoaded of Result<GalaxyCatalogSnapshot, string>
    | ReloadGalaxy
    | ReloadGalaxyDone of Result<unit, string>
    | ReloadSystem
    | ReloadSystemDone of Result<GalaxyCatalogSnapshot, string>
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
    | DismissPilot of string
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
    /// `jobId` is the one this response was requested *for* — checked against
    /// `model.watchedJobId` before applying, so a slow response for a pilot the
    /// player has since stopped watching can't overwrite a newer, faster watch.
    | WatchStatusLoaded of jobId: string * JobStatusDto option
    | InspectShip of string
    | InspectWaypoint of string
    | CloseInspector
    | LoadWaypointMarket of string
    /// `waypointSymbol` is the one this response was requested for — same
    /// stale-response guard as `WatchStatusLoaded`, keyed by waypoint instead of job.
    | WaypointMarketLoaded of waypointSymbol: string * Market option
    | LoadWaypointShipyard of string
    | WaypointShipyardLoaded of waypointSymbol: string * Shipyard option
    | MapTick
    | MapWheel of deltaY: float
    | MapDragStart
    | MapDragMove of movementX: float * movementY: float
    | MapDragEnd
    | ResetMapView
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
    | SwitchProgramSubTab of ProgramSubTab
    | LoadPollInterval
    | PollIntervalLoaded of int
    | SetPollInterval of int
    | PollIntervalSet
    | LoadTheme
    | ThemeLoaded of string
    | SetTheme of string
    | ThemeSet
    | LoadLogLevel
    | LogLevelLoaded of string
    | SetLogLevel of string
    | LogLevelSet
    /// Shared failure path for every load that previously had none at all (see
    /// `LoadDashboard`'s `DashboardLoadFailed` for the pattern this mirrors) — a
    /// down SpaceKids server should be visible and not spammed, not silent.
    | RemoteCallFailed
    | AcceptContractClicked of contractId: string
    | FulfillContractClicked of contractId: string
    | ContractActionCompleted of Result<unit, string>
    | ToggleContractsHistory
    | LoadFactions
    | FactionsLoaded of Result<FactionsSnapshot, string>
    | LoadPublicAgents
    | PublicAgentsLoaded of Result<Agent list, string>
    | SelectGalaxySystem of systemSymbol: string
    | GalaxyMapClick of offsetX: float * offsetY: float * svgSize: float
    | SystemWaypointsLoaded of Result<Waypoint list, string>
    | GalaxyWheel of deltaY: float
    | GalaxyDragStart
    | GalaxyDragMove of dx: float * dy: float
    | GalaxyDragEnd
    | ResetGalaxyMapView

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
      blockHighlighted: string
      readOnlyToggled: string
      signaturePublished: string
      simulating: string
      simulationDone: string
      pleaseOpenProgramFirst: string
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

      loadingEllipsis: string
      tokenPlaceholder: string
      login: string
      loginInSettingsHint: string
      settingsTokenLabel: string
      pilotLabel: string -> string
      balance: int64 -> string
      headquarters: string -> string
      shipsHeading: string
      shipLine: string * string * string * string -> string
      contractsHeading: string
      refreshContracts: string
      contractLine: string * string * bool * bool -> string
      tabContracts: string
      contractsActiveHeading: string
      contractsHistoryHeading: string
      contractsHistoryToggleShow: string
      contractsHistoryToggleHide: string
      contractDeliverLine: string * string * int * int -> string
      contractPaymentLine: int * int -> string
      contractDeadlineLine: string -> string
      acceptContractButton: string
      fulfillContractButton: string
      contractAcceptedLabel: string
      contractPendingLabel: string
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
      shipTypeNameLine: string -> string
      pricesHiddenHint: string
      shipNotFound: string -> string
      waypointNotFound: string -> string
      selectMapEntityHint: string

      galaxyMapHeading: string
      systemMapHeading: string
      resetMapView: string
      resetGalaxyMapView: string
      agentsHeading: string
      refreshAgents: string
      agentPublicLine: string * string * int64 * int -> string
      refreshGalaxy: string
      refreshSystem: string
      galaxyMapPartialRender: int * int * int -> string
      galaxySyncProgress: int * int option -> string
      galaxySyncStale: string

      queueHeading: string
      refresh: string
      queueNotLoadedYet: string
      pendingRequests: int -> string
      serverResetDetected: string
      apiUnreachableSince: string -> string
      recentEventsHeading: string
      eventLine: string * string * string * int * int -> string
      requestLabel: string
      responseLabel: string
      notCaptured: string

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
      /// Ship-agnostic job (§14 follow-up) variant of `pilotNameLine` — no ship to
      /// mention, so a different sentence shape rather than an empty/dangling ship.
      pilotNameLineNoShip: string -> string
      /// Fallback label for `historyLine`'s ship slot when a ship-agnostic job
      /// (§14 follow-up) was never assigned one.
      noShipLabel: string
      pilotStatusLine: string -> string
      lastLogLine: string -> string
      resume: string
      pause: string
      stop: string
      dismiss: string
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
      tabProgramsSub: string
      tabCustomBlocksSub: string
      tabPiloten: string
      tabGalaxie: string
      tabFactions: string
      tabAgents: string
      tabSettings: string
      factionsHeading: string
      refreshFactions: string
      factionHeadquartersLine: string -> string
      factionRecruitingLabel: string
      factionNotRecruitingLabel: string
      factionReputationLine: int -> string
      factionTraitsHeading: string
      factionTraitLine: string * string -> string
      serverUnreachableMessage: string
      settingsLocaleLabel: string
      settingsPollIntervalLabel: string
      settingsThemeLabel: string
      settingsThemeLight: string
      settingsThemeDark: string
      settingsLogLevelLabel: string }

let private stringsDe: Strings =
    { workshopLoaded = "Werkstatt geladen."
      savedToDb = "In SQLite gespeichert."
      nothingToLoad = "Nichts zum Laden."
      blockHighlighted = "Block hervorgehoben (falls vorhanden)."
      readOnlyToggled = "Lesemodus umgeschaltet."
      signaturePublished = "Signatur an Programm-Werkstatt übergeben."
      simulating = "Simuliere Ausführung..."
      simulationDone = "Simulation beendet."
      pleaseOpenProgramFirst = "Bitte zuerst ein Programm öffnen."
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

      loadingEllipsis = "Lädt..."
      tokenPlaceholder = "SpaceTraders-Token einfügen"
      login = "Anmelden"
      loginInSettingsHint = "Bitte im Reiter „Einstellungen“ anmelden."
      settingsTokenLabel = "SpaceTraders-Token"
      pilotLabel = fun symbol -> $"Pilot: {symbol}"
      balance = fun credits -> $"Kontostand: {credits} Credits"
      headquarters = fun hq -> $"Hauptquartier: {hq}"
      shipsHeading = "Schiffe"
      shipLine = fun (symbol, role, status, waypoint) -> $"{symbol} — {role} — {status} bei {waypoint}"
      contractsHeading = "Aufträge"
      refreshContracts = "Aufträge aktualisieren"
      contractLine = fun (id, ``type``, accepted, fulfilled) -> $"{id} ({``type``}) — angenommen: {accepted}, erfüllt: {fulfilled}"
      tabContracts = "Aufträge"
      contractsActiveHeading = "Aktuelle Aufträge"
      contractsHistoryHeading = "Verlauf"
      contractsHistoryToggleShow = "Verlauf anzeigen"
      contractsHistoryToggleHide = "Verlauf verbergen"
      contractDeliverLine =
        fun (tradeSymbol, destination, unitsFulfilled, unitsRequired) ->
            $"{tradeSymbol} nach {destination}: {unitsFulfilled} / {unitsRequired}"
      contractPaymentLine =
        fun (onAccepted, onFulfilled) -> $"Bezahlung: {onAccepted} Credits bei Annahme, {onFulfilled} Credits bei Erfüllung"
      contractDeadlineLine = fun deadline -> $"Frist: {deadline}"
      acceptContractButton = "Auftrag annehmen"
      fulfillContractButton = "Auftrag abschließen"
      contractAcceptedLabel = "Angenommen"
      contractPendingLabel = "Noch nicht angenommen"
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
      shipTypeNameLine = fun ``type`` -> ``type``
      pricesHiddenHint = "Preise werden nur angezeigt, wenn eines deiner Schiffe hier ist."
      shipNotFound = fun symbol -> $"Schiff {symbol} nicht gefunden."
      waypointNotFound = fun symbol -> $"Wegpunkt {symbol} nicht gefunden."
      selectMapEntityHint = "Klicke ein Schiff oder einen Wegpunkt auf der Karte an, um Details zu sehen."

      galaxyMapHeading = "Galaxiekarte"
      systemMapHeading = "Systemkarte"
      resetMapView = "Ansicht zurücksetzen"
      resetGalaxyMapView = "Galaxie zurücksetzen"
      agentsHeading = "Agenten"
      refreshAgents = "Agenten aktualisieren"
      agentPublicLine = fun (symbol, hq, credits, ships) -> $"{symbol} — HQ {hq}, {credits} Credits, {ships} Schiffe"
      refreshGalaxy = "Galaxie aktualisieren"
      refreshSystem = "System aktualisieren"
      galaxyMapPartialRender = fun (rendered, visible, total) ->
          $"{rendered} von {visible} sichtbaren Systemen ({total} gesamt) — reinzoomen für Details"
      galaxySyncProgress = fun (loaded, total) ->
          match total with
          | Some t -> $"Galaxie wird geladen: {loaded}/{t}"
          | None -> $"Galaxie wird geladen: {loaded}…"
      galaxySyncStale = "Zwischengespeicherte Galaxiedaten (Aktualisierung läuft im Hintergrund)."

      queueHeading = "Warteschlange"
      refresh = "Daten aktualisieren"
      queueNotLoadedYet = "Noch nicht geladen."
      pendingRequests = fun n -> $"Wartende Anfragen: {n}"
      serverResetDetected = "Der Spielserver wurde zurückgesetzt. Ein neuer Kapitän muss erstellt werden."
      apiUnreachableSince =
          fun since ->
              $"Die Raumfunkzentrale ist gerade nicht erreichbar (seit {since}). Deine Piloten machen weiter, sobald sie wieder Funkkontakt haben."
      recentEventsHeading = "Letzte Ereignisse"
      eventLine =
          fun (time, endpoint, status, priority, attempt) -> $"{time} {endpoint} — {status} (Priorität {priority}, Versuch {attempt})"
      requestLabel = "Anfrage"
      responseLabel = "Antwort"
      notCaptured = "(nicht erfasst)"

      pilotStatus =
          fun status ->
              match status with
              | "Running" -> "Führt Programm aus"
              | "AwaitingApiResponse" -> "Wartet auf Bestätigung"
              | "WaitingForArrival" -> "Unterwegs"
              | "WaitingForCooldown" -> "Wartet auf Abklingzeit"
              | "WaitingForShipLock" -> "Wartet auf ein anderes Programm"
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
      pilotNameLineNoShip = fun name -> $"🤖 Pilot {name} führt ein Programm aus"
      noShipLabel = "kein Schiff"
      pilotStatusLine = fun status -> $"Status: {status}"
      lastLogLine = fun line -> $"Zuletzt: {line}"
      resume = "Fortsetzen"
      pause = "Pause"
      stop = "Stoppen"
      dismiss = "Entfernen"
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
      tabProgramsSub = "Programme"
      tabCustomBlocksSub = "Eigene Blöcke"
      tabPiloten = "Piloten"
      tabGalaxie = "Galaxie"
      tabFactions = "Fraktionen"
      tabAgents = "Agenten"
      tabSettings = "Einstellungen"
      factionsHeading = "Fraktionen"
      refreshFactions = "Fraktionen aktualisieren"
      factionHeadquartersLine = fun hq -> $"Hauptquartier: {hq}"
      factionRecruitingLabel = "Rekrutiert neue Agenten"
      factionNotRecruitingLabel = "Rekrutiert derzeit nicht"
      factionReputationLine = fun rep -> $"Ruf: {rep}"
      factionTraitsHeading = "Eigenschaften"
      factionTraitLine = fun (name, description) -> $"{name} — {description}"
      serverUnreachableMessage = "Verbindung zum Server unterbrochen — versuche es weiter..."
      settingsLocaleLabel = "Sprache"
      settingsPollIntervalLabel = "Abfrage-Intervall"
      settingsThemeLabel = "Design"
      settingsThemeLight = "Hell"
      settingsThemeDark = "Dunkel"
      settingsLogLevelLabel = "Protokollstufe" }

let private stringsEn: Strings =
    { workshopLoaded = "Workshop loaded."
      savedToDb = "Saved to SQLite."
      nothingToLoad = "Nothing to load."
      blockHighlighted = "Block highlighted (if any)."
      readOnlyToggled = "Read-only mode toggled."
      signaturePublished = "Signature handed off to the program workshop."
      simulating = "Simulating run..."
      simulationDone = "Simulation finished."
      pleaseOpenProgramFirst = "Please open a program first."
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

      loadingEllipsis = "Loading..."
      tokenPlaceholder = "Paste SpaceTraders token"
      login = "Log in"
      loginInSettingsHint = "Please log in from the Settings tab."
      settingsTokenLabel = "SpaceTraders token"
      pilotLabel = fun symbol -> $"Pilot: {symbol}"
      balance = fun credits -> $"Balance: {credits} credits"
      headquarters = fun hq -> $"Headquarters: {hq}"
      shipsHeading = "Ships"
      shipLine = fun (symbol, role, status, waypoint) -> $"{symbol} — {role} — {status} at {waypoint}"
      contractsHeading = "Contracts"
      refreshContracts = "Reload contracts"
      contractLine = fun (id, ``type``, accepted, fulfilled) -> $"{id} ({``type``}) — accepted: {accepted}, fulfilled: {fulfilled}"
      tabContracts = "Contracts"
      contractsActiveHeading = "Active contracts"
      contractsHistoryHeading = "History"
      contractsHistoryToggleShow = "Show history"
      contractsHistoryToggleHide = "Hide history"
      contractDeliverLine =
        fun (tradeSymbol, destination, unitsFulfilled, unitsRequired) ->
            $"{tradeSymbol} to {destination}: {unitsFulfilled} / {unitsRequired}"
      contractPaymentLine =
        fun (onAccepted, onFulfilled) -> $"Payment: {onAccepted} credits on accept, {onFulfilled} credits on fulfillment"
      contractDeadlineLine = fun deadline -> $"Deadline: {deadline}"
      acceptContractButton = "Accept contract"
      fulfillContractButton = "Fulfill contract"
      contractAcceptedLabel = "Accepted"
      contractPendingLabel = "Not yet accepted"
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
      shipTypeNameLine = fun ``type`` -> ``type``
      pricesHiddenHint = "Prices are only shown when one of your ships is here."
      shipNotFound = fun symbol -> $"Ship {symbol} not found."
      waypointNotFound = fun symbol -> $"Waypoint {symbol} not found."
      selectMapEntityHint = "Click a ship or waypoint on the map to see its details."

      galaxyMapHeading = "Galaxy map"
      systemMapHeading = "System map"
      resetMapView = "Reset view"
      resetGalaxyMapView = "Reset galaxy view"
      agentsHeading = "Agents"
      refreshAgents = "Refresh agents"
      agentPublicLine = fun (symbol, hq, credits, ships) -> $"{symbol} — HQ {hq}, {credits} credits, {ships} ships"
      refreshGalaxy = "Reload galaxy"
      refreshSystem = "Reload system"
      galaxyMapPartialRender = fun (rendered, visible, total) ->
          $"Showing {rendered} of {visible} visible systems ({total} total) — zoom in for detail"
      galaxySyncProgress = fun (loaded, total) ->
          match total with
          | Some t -> $"Loading galaxy: {loaded}/{t}"
          | None -> $"Loading galaxy: {loaded}…"
      galaxySyncStale = "Showing cached galaxy data (refresh running in the background)."

      queueHeading = "Queue"
      refresh = "Update data"
      queueNotLoadedYet = "Not loaded yet."
      pendingRequests = fun n -> $"Pending requests: {n}"
      serverResetDetected = "The game server was reset. A new captain must be created."
      apiUnreachableSince =
          fun since -> $"Mission control is currently unreachable (since {since}). Your pilots will continue once contact is restored."
      recentEventsHeading = "Recent events"
      eventLine = fun (time, endpoint, status, priority, attempt) -> $"{time} {endpoint} — {status} (priority {priority}, attempt {attempt})"
      requestLabel = "Request"
      responseLabel = "Response"
      notCaptured = "(not captured)"

      pilotStatus =
          fun status ->
              match status with
              | "Running" -> "Running program"
              | "AwaitingApiResponse" -> "Awaiting confirmation"
              | "WaitingForArrival" -> "En route"
              | "WaitingForCooldown" -> "Waiting for cooldown"
              | "WaitingForShipLock" -> "Waiting for another program"
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
      pilotNameLineNoShip = fun name -> $"🤖 Pilot {name} is running a program"
      noShipLabel = "no ship"
      pilotStatusLine = fun status -> $"Status: {status}"
      lastLogLine = fun line -> $"Last: {line}"
      resume = "Resume"
      pause = "Pause"
      stop = "Stop"
      dismiss = "Dismiss"
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
      tabProgramsSub = "Programs"
      tabCustomBlocksSub = "Custom blocks"
      tabPiloten = "Pilots"
      tabGalaxie = "Galaxy"
      tabFactions = "Factions"
      tabAgents = "Agents"
      tabSettings = "Settings"
      factionsHeading = "Factions"
      refreshFactions = "Refresh factions"
      factionHeadquartersLine = fun hq -> $"Headquarters: {hq}"
      factionRecruitingLabel = "Recruiting new agents"
      factionNotRecruitingLabel = "Not currently recruiting"
      factionReputationLine = fun rep -> $"Reputation: {rep}"
      factionTraitsHeading = "Traits"
      factionTraitLine = fun (name, description) -> $"{name} — {description}"
      serverUnreachableMessage = "Lost connection to the server — still retrying..."
      settingsLocaleLabel = "Language"
      settingsPollIntervalLabel = "Poll interval"
      settingsThemeLabel = "Theme"
      settingsThemeLight = "Light"
      settingsThemeDark = "Dark"
      settingsLogLevelLabel = "Log level" }

let private stringsFor (locale: string) : Strings = if locale = "en" then stringsEn else stringsDe

let private applyGalaxySnapshot (state: DashboardState) (catalog: GalaxyCatalogSnapshot) : DashboardState =
    let waypoints =
        if catalog.selectedSystemSymbol = state.selectedSystemSymbol then
            catalog.waypoints
        else
            state.waypoints

    let markets =
        if catalog.markets.IsEmpty then state.markets else catalog.markets

    { state with
        systems = catalog.systems
        selectedSystemSymbol =
            if catalog.waypoints.Length > 0 then
                catalog.selectedSystemSymbol
            else
                state.selectedSystemSymbol
        waypoints = waypoints
        markets = markets }

let internal mapViewSize = 400.0
let internal mapPadding = 30.0

/// Guards against a degenerate single-point (or empty) range, which would
/// otherwise divide by zero when scaling.
let internal computeMapBounds (waypoints: Waypoint list) : float * float * float * float =
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
let internal scaleMapPoint (minX, maxX, minY, maxY) (x: float) (y: float) : float * float =
    let scale = min ((mapViewSize - 2.0 * mapPadding) / (maxX - minX)) ((mapViewSize - 2.0 * mapPadding) / (maxY - minY))
    let sx = mapPadding + (x - minX) * scale
    let sy = mapViewSize - (mapPadding + (y - minY) * scale)
    (sx, sy)

let internal computeGalaxyBounds (systems: StarSystem list) : float * float * float * float =
    match systems with
    | [] -> (0.0, 1.0, 0.0, 1.0)
    | _ ->
        let xs = systems |> List.map (fun s -> float s.x)
        let ys = systems |> List.map (fun s -> float s.y)
        let minX, maxX = List.min xs, List.max xs
        let minY, maxY = List.min ys, List.max ys
        let minX, maxX = if minX = maxX then minX - 1.0, maxX + 1.0 else minX, maxX
        let minY, maxY = if minY = maxY then minY - 1.0, maxY + 1.0 else minY, maxY
        (minX, maxX, minY, maxY)

/// Cap rendered galaxy-map DOM nodes so zoom/pan stays responsive with a full
/// universe catalog in memory.
let internal galaxyMapMaxNodes = 500

let internal galaxyMapViewMargin = 12.0

type GalaxyMapNode =
    { system: StarSystem
      sx: float
      sy: float }

let internal buildGalaxyMapNodes (systems: StarSystem list) (bounds: float * float * float * float) : GalaxyMapNode list =
    systems
    |> List.map (fun system ->
        let sx, sy = scaleMapPoint bounds (float system.x) (float system.y)
        { system = system; sx = sx; sy = sy })

let internal galaxyMapNodeBudget (zoom: float) : int =
    if zoom >= 3.0 then 2500
    elif zoom >= 2.0 then 1200
    else galaxyMapMaxNodes

let internal filterGalaxyMapNodes
    (nodes: GalaxyMapNode list)
    (viewX: float)
    (viewY: float)
    (viewSize: float)
    (maxNodes: int)
    (alwaysInclude: string list)
    : GalaxyMapNode list * int =
    let margin = galaxyMapViewMargin

    let inView (n: GalaxyMapNode) =
        n.sx >= viewX - margin
        && n.sx <= viewX + viewSize + margin
        && n.sy >= viewY - margin
        && n.sy <= viewY + viewSize + margin

    let visible = nodes |> List.filter inView
    let total = visible.Length
    let alwaysSet = Set.ofList alwaysInclude

    let mustKeep =
        visible |> List.filter (fun n -> alwaysSet.Contains n.system.symbol)

    let pool =
        visible
        |> List.filter (fun n -> not (alwaysSet.Contains n.system.symbol))
        |> List.sortBy (fun n -> n.system.symbol)

    let budget = max 0 (maxNodes - mustKeep.Length)

    let sampled =
        if pool.Length <= budget then
            pool
        else
            let stride = (float pool.Length / float budget) |> ceil |> int |> max 1

            pool
            |> List.indexed
            |> List.filter (fun (i, _) -> i % stride = 0)
            |> List.map snd
            |> List.truncate budget

    let rendered =
        (mustKeep @ sampled) |> List.distinctBy (fun n -> n.system.symbol)

    rendered, total

let internal svgPointFromClick (viewX: float) (viewY: float) (viewSize: float) (offsetX: float) (offsetY: float) (svgSize: float) =
    let svgX = viewX + offsetX / svgSize * viewSize
    let svgY = viewY + offsetY / svgSize * viewSize
    (svgX, svgY)

let internal galaxyMapShowLabels (zoom: float) (visibleCount: int) =
    zoom >= 2.5 && visibleCount <= 80

let internal galaxyMapHitRadius (zoom: float) =
    max 6.0 (14.0 / zoom)

let internal pickGalaxyMapNodeAt (nodes: GalaxyMapNode list) (svgX: float) (svgY: float) (hitRadius: float) : string option =
    nodes
    |> List.choose (fun n ->
        let dx = n.sx - svgX
        let dy = n.sy - svgY
        let distSq = dx * dx + dy * dy

        if distSq <= hitRadius * hitRadius then
            Some(n, distSq)
        else
            None)
    |> List.sortBy snd
    |> List.tryHead
    |> Option.map (fun (n, _) -> n.system.symbol)

let private galaxyPollDelayCmd =
    Cmd.OfAsync.perform (fun () -> Async.Sleep 1000) () (fun () -> PollGalaxyCatalog)

let private galaxyFollowUpCmds (sync: GalaxySyncStatusDto) : Cmd<Message> =
    if sync.phase = "syncing" then galaxyPollDelayCmd else Cmd.none

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
    | Loaded -> model, Cmd.none

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
        model, Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.setReadOnly" [| box model.containerId; box next |]) () (fun () -> ReadOnlyToggled next)
    | ReadOnlyToggled next ->
        { model with readOnly = next; status = s.readOnlyToggled }, Cmd.none

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
        // Full catalog load (paginated `ListSystems` + HQ waypoints/market): page
        // init, manual "Daten aktualisieren", and token submit — not pilot polling.
        model,
        Cmd.OfAsync.either (fun () -> agentRemote.loadDashboard ()) () DashboardLoaded (fun ex -> DashboardLoadFailed ex.Message)
    | RefreshDashboard ->
        // Volatile slice only (agent/ships/contracts) — `PilotsLoaded` after a job
        // finishes, contract accept/fulfill. Galaxy fields are merged from cache.
        model,
        Cmd.OfAsync.either (fun () -> agentRemote.refreshDashboard ()) () DashboardLoaded (fun ex -> DashboardLoadFailed ex.Message)
    | RefreshContracts ->
        { model with contractsRefreshing = true; contractActionError = None },
        Cmd.OfAsync.either (fun () -> agentRemote.refreshDashboard ()) () DashboardLoaded (fun ex -> DashboardLoadFailed ex.Message)
    | LoadGalaxyCatalog ->
        { model with galaxyInitialLoad = true; galaxyCatalogError = None },
        Cmd.OfAsync.either (fun () -> agentRemote.getGalaxyCatalog ()) () GalaxyCatalogLoaded (fun ex -> GalaxyCatalogLoaded(Error ex.Message))
    | PollGalaxyCatalog ->
        model,
        Cmd.OfAsync.either (fun () -> agentRemote.getGalaxyCatalog ()) () GalaxyCatalogLoaded (fun ex -> GalaxyCatalogLoaded(Error ex.Message))
    | GalaxyCatalogLoaded(Ok catalog) ->
        let mergedDashboard =
            match model.dashboard with
            | Some existing -> Some(applyGalaxySnapshot existing catalog)
            | None -> None

        let syncError =
            catalog.sync.error |> Option.map (fun msg -> $"Galaxie-Sync: {msg}")

        { model with
            dashboard = mergedDashboard
            galaxySync = Some catalog.sync
            galaxyInitialLoad = false
            galaxyReloading = false
            galaxyCatalogError = syncError },
        galaxyFollowUpCmds catalog.sync
    | GalaxyCatalogLoaded(Error message) ->
        { model with
            galaxyInitialLoad = false
            galaxyReloading = false
            galaxyCatalogError = Some message },
        Cmd.none
    | ReloadGalaxy ->
        { model with galaxyReloading = true; galaxyCatalogError = None },
        Cmd.OfAsync.either (fun () -> agentRemote.reloadGalaxy ()) () ReloadGalaxyDone (fun ex -> ReloadGalaxyDone(Error ex.Message))
    | ReloadGalaxyDone(Ok()) ->
        { model with galaxyReloading = false },
        Cmd.ofMsg PollGalaxyCatalog
    | ReloadGalaxyDone(Error message) ->
        { model with galaxyReloading = false; galaxyCatalogError = Some message }, Cmd.none
    | ReloadSystem ->
        match model.dashboard with
        | Some state ->
            { model with systemReloading = true; galaxyCatalogError = None },
            Cmd.OfAsync.either
                (fun () -> agentRemote.reloadSystem state.selectedSystemSymbol)
                ()
                ReloadSystemDone
                (fun ex -> ReloadSystemDone(Error ex.Message))
        | None -> model, Cmd.none
    | ReloadSystemDone(Ok catalog) ->
        let mergedDashboard =
            match model.dashboard with
            | Some existing -> Some(applyGalaxySnapshot existing catalog)
            | None -> None

        { model with
            dashboard = mergedDashboard
            galaxySync = Some catalog.sync
            systemReloading = false
            galaxyCatalogError = None },
        galaxyFollowUpCmds catalog.sync
    | ReloadSystemDone(Error message) ->
        { model with systemReloading = false; galaxyCatalogError = Some message }, Cmd.none
    | DashboardLoaded stateOpt ->
        let merged =
            match stateOpt, model.dashboard with
            | Some incoming, Some existing when incoming.systems.IsEmpty ->
                Some
                    { incoming with
                        systems = existing.systems
                        waypoints = existing.waypoints
                        markets = existing.markets
                        selectedSystemSymbol = existing.selectedSystemSymbol }
            | _ -> stateOpt

        { model with dashboard = merged; contractsRefreshing = false }, Cmd.none
    | DashboardLoadFailed message ->
        { model with dashboardError = Some message; contractsRefreshing = false }, Cmd.none
    | LoadQueueStatus ->
        model, Cmd.OfAsync.either (fun () -> queueRemote.getStatus ()) () QueueStatusLoaded (fun _ -> RemoteCallFailed)
    | QueueStatusLoaded status ->
        { model with queueStatus = Some status; serverUnreachable = false }, Cmd.none

    | SelectShip symbol ->
        { model with selectedShipSymbol = if symbol = "" then None else Some symbol }, Cmd.none
    | StartProgram ->
        match model.currentProgramId with
        | None -> { model with status = s.pleaseOpenProgramFirst }, Cmd.none
        | Some programId ->
            // No ship-required client-side gate (§14 follow-up): a ship-agnostic
            // program should start fine with `model.selectedShipSymbol = None`. If
            // the program actually needs one, the server's own upfront check
            // (`JobRemoting.fs`) rejects the start and its message surfaces below via
            // `ProgramStartResult(Error _)` — no separate client-side message needed.
            // `Cmd.OfAsync.either` (not `.perform`): if `serializeWorkspace` throws
            // (e.g. the Blockly workspace hasn't finished mounting yet — `OpenProgram`
            // only dispatches its init asynchronously, not synchronously), the
            // exception must not vanish silently, leaving `startingJob` stuck `true`
            // and the Start button permanently disabled (found in review).
            { model with startingJob = true; pilotError = None },
            Cmd.OfAsync.either
                (fun () ->
                    async {
                        let! workspaceJson = call<string> js "spaceKids.serializeWorkspace" [| box model.containerId |]
                        return! jobRemote.startJob (model.tokenInput, programId, model.selectedShipSymbol, workspaceJson)
                    })
                ()
                ProgramStartResult
                (fun ex -> ProgramStartResult(Error ex.Message))
    | ProgramStartResult(Ok _) ->
        { model with startingJob = false }, Cmd.ofMsg LoadPilots
    | ProgramStartResult(Error message) ->
        { model with startingJob = false; pilotError = Some message }, Cmd.none
    | LoadPilots ->
        if model.logLevel = "trace" then
            System.Console.WriteLine("[trace] LoadPilots poll")
        model, Cmd.OfAsync.either (fun () -> jobRemote.listJobs ()) () PilotsLoaded (fun _ -> RemoteCallFailed)
    | PilotsLoaded pilots ->
        if model.logLevel = "trace" then
            System.Console.WriteLine($"[trace] PilotsLoaded: {pilots.Length} pilot(s)")
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
        Cmd.batch [ readOnlyCmd; if justCompleted then Cmd.ofMsg RefreshDashboard else Cmd.none ]
    | WatchModeReadOnlySet value ->
        { model with readOnly = value }, Cmd.none
    | PausePilot jobId ->
        model, Cmd.OfAsync.perform (fun () -> jobRemote.pause jobId) () (fun () -> PilotActionDone)
    | ResumePilot jobId ->
        model, Cmd.OfAsync.perform (fun () -> jobRemote.resume jobId) () (fun () -> PilotActionDone)
    | CancelPilot jobId ->
        model, Cmd.OfAsync.perform (fun () -> jobRemote.cancel jobId) () (fun () -> PilotActionDone)
    | DismissPilot jobId ->
        model, Cmd.OfAsync.perform (fun () -> jobRemote.dismiss jobId) () (fun () -> PilotActionDone)
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
        | Some jobId ->
            if model.logLevel = "trace" then
                System.Console.WriteLine($"[trace] WatchTick poll (jobId={jobId})")
            model, Cmd.OfAsync.perform (fun () -> jobRemote.getStatus jobId) () (fun dto -> WatchStatusLoaded(jobId, dto))
    | WatchStatusLoaded(requestedJobId, dtoOpt) when model.watchedJobId <> Some requestedJobId ->
        // Stale response for a pilot we're no longer watching (switched, or stopped,
        // while this request was in flight) — discard it rather than overwriting a
        // newer, faster watch's highlighted frames or resetting its polling loop.
        model, Cmd.none
    | WatchStatusLoaded(_, None) ->
        { model with watchedJobId = None; watchedFrames = [] }, Cmd.none
    | WatchStatusLoaded(_, Some dto) ->
        if model.logLevel = "trace" then
            System.Console.WriteLine($"[trace] WatchStatusLoaded: status={dto.status}")

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
        model,
        Cmd.OfAsync.perform
            (fun () -> agentRemote.getWaypointMarket waypointSymbol)
            ()
            (fun market -> WaypointMarketLoaded(waypointSymbol, market))
    | WaypointMarketLoaded(requestedSymbol, _) when model.inspecting <> Some(InspectedWaypoint requestedSymbol) ->
        // Stale response for a waypoint the player has since stopped inspecting.
        model, Cmd.none
    | WaypointMarketLoaded(_, market) -> { model with waypointMarket = market }, Cmd.none
    | LoadWaypointShipyard waypointSymbol ->
        model,
        Cmd.OfAsync.perform
            (fun () -> agentRemote.getWaypointShipyard waypointSymbol)
            ()
            (fun shipyard -> WaypointShipyardLoaded(waypointSymbol, shipyard))
    | WaypointShipyardLoaded(requestedSymbol, _) when model.inspecting <> Some(InspectedWaypoint requestedSymbol) ->
        model, Cmd.none
    | WaypointShipyardLoaded(_, shipyard) -> { model with waypointShipyard = shipyard }, Cmd.none

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
        // 30, on each failure; `PilotsLoaded` resets it to 1 on success). The
        // configured `pollIntervalSeconds` (Settings tab) is the healthy-state
        // floor; the backoff multiplier only ever pushes the effective cadence
        // *above* that floor while the server is unreachable.
        if ticksSinceLast >= max model.pollIntervalSeconds model.pilotsPollBackoffTicks then
            { model with mapTickCount = count; pilotsPollTicksSinceLast = 0 },
            Cmd.batch [ Cmd.ofMsg LoadPilots; nextTickCmd ]
        else
            { model with mapTickCount = count; pilotsPollTicksSinceLast = ticksSinceLast }, nextTickCmd

    | MapWheel deltaY ->
        let factor = if deltaY < 0.0 then 1.15 else 1.0 / 1.15
        { model with mapZoom = model.mapZoom * factor |> max 1.0 |> min 8.0 }, Cmd.none
    | MapDragStart -> { model with mapDragging = true }, Cmd.none
    | MapDragMove(dx, dy) ->
        if model.mapDragging then
            { model with
                mapPanX = model.mapPanX - dx / model.mapZoom
                mapPanY = model.mapPanY - dy / model.mapZoom },
            Cmd.none
        else
            model, Cmd.none
    | MapDragEnd -> { model with mapDragging = false }, Cmd.none
    | ResetMapView -> { model with mapZoom = 1.0; mapPanX = 0.0; mapPanY = 0.0 }, Cmd.none

    | GalaxyWheel deltaY ->
        let factor = if deltaY < 0.0 then 1.15 else 1.0 / 1.15
        { model with galaxyMapZoom = model.galaxyMapZoom * factor |> max 1.0 |> min 8.0 }, Cmd.none
    | GalaxyDragStart -> { model with galaxyDragging = true; galaxyMapDragMoved = false }, Cmd.none
    | GalaxyDragMove(dx, dy) ->
        if model.galaxyDragging then
            { model with
                galaxyMapPanX = model.galaxyMapPanX - dx / model.galaxyMapZoom
                galaxyMapPanY = model.galaxyMapPanY - dy / model.galaxyMapZoom
                galaxyMapDragMoved = model.galaxyMapDragMoved || abs dx + abs dy > 2.0 },
            Cmd.none
        else
            model, Cmd.none
    | GalaxyDragEnd -> { model with galaxyDragging = false }, Cmd.none
    | ResetGalaxyMapView ->
        { model with galaxyMapZoom = 1.0; galaxyMapPanX = 0.0; galaxyMapPanY = 0.0; galaxyMapDragMoved = false },
        Cmd.none
    | GalaxyMapClick(offsetX, offsetY, svgSize) ->
        if model.galaxyMapDragMoved then
            { model with galaxyMapDragMoved = false }, Cmd.none
        else
            match model.dashboard with
            | None -> model, Cmd.none
            | Some state ->
                let bounds = computeGalaxyBounds state.systems
                let viewSize = mapViewSize / model.galaxyMapZoom
                let viewX = (mapViewSize - viewSize) / 2.0 + model.galaxyMapPanX
                let viewY = (mapViewSize - viewSize) / 2.0 + model.galaxyMapPanY
                let nodes = buildGalaxyMapNodes state.systems bounds
                let alwaysInclude =
                    [ state.selectedSystemSymbol; Waypoint.systemSymbolOf state.agent.headquarters ]
                    |> List.distinct

                let rendered, _ =
                    filterGalaxyMapNodes nodes viewX viewY viewSize (galaxyMapNodeBudget model.galaxyMapZoom) alwaysInclude

                let svgX, svgY = svgPointFromClick viewX viewY viewSize offsetX offsetY svgSize

                match pickGalaxyMapNodeAt rendered svgX svgY (galaxyMapHitRadius model.galaxyMapZoom) with
                | Some symbol -> model, Cmd.ofMsg (SelectGalaxySystem symbol)
                | None -> model, Cmd.none

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

    | LoadPollInterval ->
        model,
        Cmd.OfAsync.either (fun () -> settingsRemote.getPollIntervalSeconds ()) () PollIntervalLoaded (fun _ -> RemoteCallFailed)
    | PollIntervalLoaded seconds ->
        { model with pollIntervalSeconds = seconds; serverUnreachable = false }, Cmd.none
    | SetPollInterval seconds ->
        { model with pollIntervalSeconds = seconds },
        Cmd.OfAsync.perform (fun () -> settingsRemote.setPollIntervalSeconds seconds) () (fun () -> PollIntervalSet)
    | PollIntervalSet -> model, Cmd.none

    | LoadTheme ->
        model, Cmd.OfAsync.perform (fun () -> call<string> js "spaceKids.getTheme" [||]) () ThemeLoaded
    | ThemeLoaded theme ->
        { model with theme = theme },
        Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.setTheme" [| box theme |]) () (fun () -> ThemeSet)
    | SetTheme theme ->
        { model with theme = theme },
        Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.setTheme" [| box theme |]) () (fun () -> ThemeSet)
    | ThemeSet -> model, Cmd.none

    | LoadLogLevel ->
        model, Cmd.OfAsync.perform (fun () -> call<string> js "spaceKids.getLogLevel" [||]) () LogLevelLoaded
    | LogLevelLoaded logLevel ->
        { model with logLevel = logLevel },
        Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.setLogLevel" [| box logLevel |]) () (fun () -> LogLevelSet)
    | SetLogLevel logLevel ->
        { model with logLevel = logLevel },
        Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.setLogLevel" [| box logLevel |]) () (fun () -> LogLevelSet)
    | LogLevelSet -> model, Cmd.none

    | SwitchProgramSubTab subTab ->
        { model with activeProgramSubTab = subTab }, Cmd.none
    | SwitchTab tab ->
        let lazyLoadCmds =
            [
                if tab = FactionsTab && model.factionsSnapshot.IsNone && model.dashboard.IsSome then
                    Some(LoadFactions)
                else
                    None
                if tab = AgentsTab && model.publicAgents.IsNone && model.dashboard.IsSome then
                    Some(LoadPublicAgents)
                else
                    None
                if tab = GalaxieTab && model.dashboard.IsSome then Some LoadGalaxyCatalog else None
                if tab = ContractsTab && model.dashboard.IsSome then Some RefreshDashboard else None ]
            |> List.choose id
            |> List.map Cmd.ofMsg

        { model with activeTab = tab }, Cmd.batch lazyLoadCmds
    | RemoteCallFailed ->
        { model with
            serverUnreachable = true
            pilotsPollBackoffTicks = min 30 (max 2 (model.pilotsPollBackoffTicks * 2)) },
        Cmd.none

    | AcceptContractClicked contractId ->
        { model with acceptingContractId = Some contractId; contractActionError = None },
        Cmd.OfAsync.either
            (fun () -> agentRemote.acceptContract contractId)
            ()
            ContractActionCompleted
            (fun ex -> ContractActionCompleted(Error ex.Message))
    | FulfillContractClicked contractId ->
        { model with fulfillingContractId = Some contractId; contractActionError = None },
        Cmd.OfAsync.either
            (fun () -> agentRemote.fulfillContract contractId)
            ()
            ContractActionCompleted
            (fun ex -> ContractActionCompleted(Error ex.Message))
    | ContractActionCompleted(Ok()) ->
        { model with acceptingContractId = None; fulfillingContractId = None }, Cmd.ofMsg RefreshDashboard
    | ContractActionCompleted(Error message) ->
        { model with acceptingContractId = None; fulfillingContractId = None; contractActionError = Some message }, Cmd.none
    | ToggleContractsHistory -> { model with contractsHistoryExpanded = not model.contractsHistoryExpanded }, Cmd.none

    | LoadFactions ->
        match model.dashboard with
        | None -> model, Cmd.none
        | Some _ ->
            { model with factionsLoading = true; factionsError = None },
            Cmd.OfAsync.either
                (fun () -> agentRemote.loadFactions ())
                ()
                FactionsLoaded
                (fun ex -> FactionsLoaded(Error ex.Message))
    | FactionsLoaded(Ok snapshot) ->
        { model with factionsSnapshot = Some snapshot; factionsLoading = false; factionsError = None }, Cmd.none
    | FactionsLoaded(Error message) ->
        { model with factionsLoading = false; factionsError = Some message }, Cmd.none

    | LoadPublicAgents ->
        match model.dashboard with
        | None -> model, Cmd.none
        | Some _ ->
            { model with publicAgentsLoading = true; publicAgentsError = None },
            Cmd.OfAsync.either
                (fun () -> agentRemote.loadPublicAgents ())
                ()
                PublicAgentsLoaded
                (fun ex -> PublicAgentsLoaded(Error ex.Message))
    | PublicAgentsLoaded(Ok agents) ->
        { model with publicAgents = Some agents; publicAgentsLoading = false; publicAgentsError = None }, Cmd.none
    | PublicAgentsLoaded(Error message) ->
        { model with publicAgentsLoading = false; publicAgentsError = Some message }, Cmd.none

    | SelectGalaxySystem systemSymbol ->
        match model.dashboard with
        | None -> model, Cmd.none
        | Some _ ->
            { model with
                pendingGalaxySystem = Some systemSymbol
                systemWaypointsLoading = true
                systemWaypointsError = None
                inspecting = None
                waypointMarket = None
                waypointShipyard = None
                mapZoom = 1.0
                mapPanX = 0.0
                mapPanY = 0.0 },
            Cmd.OfAsync.either
                (fun () -> agentRemote.loadSystemWaypoints systemSymbol)
                ()
                SystemWaypointsLoaded
                (fun ex -> SystemWaypointsLoaded(Error ex.Message))
    | SystemWaypointsLoaded(Ok waypoints) ->
        match model.dashboard, model.pendingGalaxySystem with
        | Some state, Some systemSymbol ->
            { model with
                dashboard =
                    Some
                        { state with
                            waypoints = waypoints
                            selectedSystemSymbol = systemSymbol }
                pendingGalaxySystem = None
                systemWaypointsLoading = false
                systemWaypointsError = None },
            Cmd.none
        | _ ->
            { model with systemWaypointsLoading = false }, Cmd.none
    | SystemWaypointsLoaded(Error message) ->
        { model with
            pendingGalaxySystem = None
            systemWaypointsLoading = false
            systemWaypointsError = Some message },
        Cmd.none

let private reputationFor (reputations: (string * int) list) (symbol: string) : int option =
    reputations |> List.tryFind (fun (s, _) -> s = symbol) |> Option.map snd

let private viewDashboard (s: Strings) (state: DashboardState) dispatch =
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
                match market.tradeGoods with
                | Some tradeGoods when not tradeGoods.IsEmpty ->
                    ul {
                        for good in tradeGoods do
                            li { strings.tradeGoodLine (good.symbol, good.purchasePrice, good.sellPrice) }
                    }
                | _ ->
                    // The real API only includes priced `tradeGoods` when one of the
                    // player's own ships is at this market — fall back to the
                    // always-visible export/import/exchange names (no price) instead
                    // of silently showing nothing.
                    let names = market.exports @ market.imports @ market.exchange

                    if not names.IsEmpty then
                        p { strings.pricesHiddenHint }
                        ul { for good in names do li { strings.exportLine good.name } }

        if hasTrait "SHIPYARD" then
            h4 { strings.shipyardHeading }
            match model.waypointShipyard with
            | None -> button { on.click (fun _ -> dispatch (LoadWaypointShipyard waypoint.symbol)); strings.loadShipyard }
            | Some shipyard ->
                if not shipyard.ships.IsEmpty then
                    ul {
                        for entry in shipyard.ships do
                            li { strings.shipyardTypeLine (entry.``type``, entry.purchasePrice) }
                    }
                elif not shipyard.shipTypes.IsEmpty then
                    // Same reasoning as the market fallback above: priced `ships` only
                    // shows up when one of the player's own ships is docked here.
                    p { strings.pricesHiddenHint }
                    ul { for t in shipyard.shipTypes do li { strings.shipTypeNameLine t.``type`` } }
    }

let private viewInspector (state: DashboardState) model dispatch =
    let s = stringsFor model.locale

    match model.inspecting with
    | None -> p { s.selectMapEntityHint }
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

/// Contracts tab: splits contracts into the ones still worth acting on and the
/// completed ones, which get collapsed into a history section so they don't
/// bury the active ones. Order within each partition is preserved.
let internal partitionContracts (contracts: Contract list) : Contract list * Contract list =
    contracts |> List.partition (fun c -> not c.fulfilled)

let private systemColor (systemType: string) : string =
    match systemType with
    | "NEBULA" -> "#9b59b6"
    | "RED_STAR" -> "#e74c3c"
    | "ORANGE_STAR" -> "#e67e22"
    | "BLUE_STAR" -> "#3498db"
    | "YOUNG_STAR" -> "#f1c40f"
    | "WHITE_DWARF" -> "#ecf0f1"
    | "BLACK_HOLE" -> "#2c3e50"
    | _ -> "#7f8c8d"

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
let internal interpolatedShipPosition (waypoints: Waypoint list) (ship: Ship) : (float * float) option =
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

let private viewGalaxyMap (s: Strings) (model: Model) (state: DashboardState) dispatch =
    let bounds = computeGalaxyBounds state.systems
    let viewSize = mapViewSize / model.galaxyMapZoom
    let viewX = (mapViewSize - viewSize) / 2.0 + model.galaxyMapPanX
    let viewY = (mapViewSize - viewSize) / 2.0 + model.galaxyMapPanY
    let nodes = buildGalaxyMapNodes state.systems bounds

    let alwaysInclude =
        [ state.selectedSystemSymbol; Waypoint.systemSymbolOf state.agent.headquarters ]
        |> List.distinct

    let rendered, visibleInView =
        filterGalaxyMapNodes nodes viewX viewY viewSize (galaxyMapNodeBudget model.galaxyMapZoom) alwaysInclude

    let showLabels = galaxyMapShowLabels model.galaxyMapZoom visibleInView

    div {
        h2 { s.galaxyMapHeading }
        svg {
            "viewBox" => $"{viewX} {viewY} {viewSize} {viewSize}"
            attr.style
                "width: 100%; max-width: 400px; height: 400px; border: 1px solid #ccc; cursor: grab; touch-action: none"
            on.wheel (fun e -> dispatch (GalaxyWheel e.DeltaY))
            on.mousedown (fun _ -> dispatch GalaxyDragStart)
            on.mousemove (fun e -> dispatch (GalaxyDragMove(e.MovementX, e.MovementY)))
            on.mouseup (fun _ -> dispatch GalaxyDragEnd)
            on.mouseout (fun _ -> dispatch GalaxyDragEnd)
            on.click (fun e -> dispatch (GalaxyMapClick(e.OffsetX, e.OffsetY, mapViewSize)))

            for node in rendered do
                let selected = node.system.symbol = state.selectedSystemSymbol

                elt "circle" {
                    "cx" => string node.sx
                    "cy" => string node.sy
                    "r" => (if selected then "9" else "5")
                    "fill" => systemColor node.system.``type``
                    "stroke" => (if selected then "#000000" else "none")
                    "stroke-width" => (if selected then "2" else "0")
                }

                if showLabels || selected then
                    elt "text" {
                        "x" => string node.sx
                        "y" => string (node.sy + 16.0)
                        "font-size" => "8"
                        "text-anchor" => "middle"
                        "fill" => "currentColor"
                        "pointer-events" => "none"
                        node.system.symbol
                    }
        }
        if rendered.Length < visibleInView || visibleInView < state.systems.Length then
            p {
                attr.style "font-size: 0.8em; opacity: 0.75; margin-top: 0.2rem"
                s.galaxyMapPartialRender(rendered.Length, visibleInView, state.systems.Length)
            }
        button {
            attr.style "font-size: 0.8em; padding: 0.2em 0.6em; margin-top: 0.3rem"
            attr.disabled model.galaxyReloading
            on.click (fun _ -> dispatch ReloadGalaxy)
            s.refreshGalaxy
        }
        match model.galaxySync with
        | Some sync when sync.phase = "syncing" || model.galaxyReloading ->
            let pct =
                match sync.systemsTotal with
                | Some total when total > 0 -> min 100.0 (float sync.systemsLoaded / float total * 100.0)
                | _ -> min 95.0 (float sync.systemsLoaded / 1.0)

            p { s.galaxySyncProgress(sync.systemsLoaded, sync.systemsTotal) }

            div {
                attr.style "width: 100%; max-width: 400px; height: 0.5rem; background: #e0e0e0; border-radius: 0.25rem; margin-top: 0.2rem"
                div {
                    attr.style (sprintf "width: %.1f%%; height: 100%%; background: #4a90d9; border-radius: 0.25rem; transition: width 0.3s" pct)
                    ()
                }
            }
        | Some sync when sync.isStale ->
            p { attr.style "font-size: 0.85em; opacity: 0.8"; s.galaxySyncStale }
        | _ when model.galaxyInitialLoad ->
            p { s.loadingEllipsis }
        | _ -> ()
        match model.galaxyCatalogError with
        | Some err -> p { s.errorPrefix err }
        | None -> ()
        button {
            attr.style "font-size: 0.8em; padding: 0.2em 0.6em; margin-top: 0.3rem"
            on.click (fun _ -> dispatch ResetGalaxyMapView)
            s.resetGalaxyMapView
        }
    }

let private viewSystemMap (s: Strings) (model: Model) (state: DashboardState) dispatch =
    let bounds = computeMapBounds state.waypoints
    let shipsHere = state.ships |> List.filter (fun ship -> ship.nav.systemSymbol = state.selectedSystemSymbol)

    // `viewSize`/`viewX`/`viewY` define which sub-rectangle of the fixed
    // `mapViewSize x mapViewSize` coordinate space `scaleMapPoint` draws into is
    // actually visible -- `mapZoom`/`mapPanX`/`mapPanY` never touch how waypoints/
    // ships are themselves scaled into that space, only this window onto it.
    let viewSize = mapViewSize / model.mapZoom
    let viewX = (mapViewSize - viewSize) / 2.0 + model.mapPanX
    let viewY = (mapViewSize - viewSize) / 2.0 + model.mapPanY

    div {
        h2 { $"{s.systemMapHeading} ({state.selectedSystemSymbol})" }
        if model.systemWaypointsLoading then
            p { s.loadingEllipsis }
        match model.systemWaypointsError with
        | Some err -> p { attr.style "color: #cc0000"; s.errorPrefix err }
        | None -> ()
        svg {
            "viewBox" => $"{viewX} {viewY} {viewSize} {viewSize}"
            attr.style
                "width: 100%; max-width: 400px; height: 400px; border: 1px solid #ccc; cursor: grab; touch-action: none"
            on.wheel (fun e -> dispatch (MapWheel e.DeltaY))
            on.mousedown (fun _ -> dispatch MapDragStart)
            on.mousemove (fun e -> dispatch (MapDragMove(e.MovementX, e.MovementY)))
            on.mouseup (fun _ -> dispatch MapDragEnd)
            on.mouseout (fun _ -> dispatch MapDragEnd)

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
                        "fill" => "currentColor"
                        waypoint.symbol
                    }
                }

            for ship in shipsHere do
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
                            "fill" => "currentColor"
                            ship.symbol
                        }
                    }
        }
        button {
            attr.style "font-size: 0.8em; padding: 0.2em 0.6em; margin-top: 0.3rem"
            on.click (fun _ -> dispatch ResetMapView)
            s.resetMapView
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

                        li {
                            elt "details" {
                                elt "summary" { s.eventLine (requestedAtText, evt.endpoint, evt.status, evt.priority, evt.attempt) }
                                p { attr.style "font-weight: bold; margin-bottom: 0.2rem"; s.requestLabel }
                                elt "pre" {
                                    attr.style "white-space: pre-wrap; word-break: break-all; background: rgba(128,128,128,0.1); padding: 0.4rem; margin: 0 0 0.5rem 0"
                                    evt.requestJson |> Option.defaultValue s.notCaptured
                                }
                                p { attr.style "font-weight: bold; margin-bottom: 0.2rem"; s.responseLabel }
                                elt "pre" {
                                    attr.style "white-space: pre-wrap; word-break: break-all; background: rgba(128,128,128,0.1); padding: 0.4rem"
                                    evt.responseJson |> Option.defaultValue s.notCaptured
                                }
                            }
                        }
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
                    // No ship-selected gate here (§14 follow-up) — a ship-agnostic
                    // program is meant to start with no ship picked; the server
                    // rejects a ship-requiring program with no ship instead.
                    attr.disabled (model.currentProgramId.IsNone || model.startingJob)
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
                    p {
                        match pilot.shipSymbol with
                        | Some ship -> s.pilotNameLine (pilotName ship, ship)
                        | None -> s.pilotNameLineNoShip (pilotName pilot.jobId)
                    }
                    p { s.pilotStatusLine (s.pilotStatus pilot.status) }
                    match pilot.statusDetail with
                    | Some detail -> p { detail }
                    | None -> ()
                    if not pilot.branchStatuses.IsEmpty then
                        ul {
                            attr.style "margin: 0.25rem 0 0.5rem 1rem; padding-left: 1rem"
                            for branch in pilot.branchStatuses do
                                let label = branch.shipSymbol |> Option.defaultValue (pilotName pilot.jobId)
                                li {
                                    attr.style $"margin-left: {branch.depth}rem"
                                    text $"{label}: {s.pilotStatus branch.status}"
                                    match branch.statusDetail with
                                    | Some detail -> text $" ({detail})"
                                    | None -> ()
                                }
                        }
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
                    else
                        button { on.click (fun _ -> dispatch (DismissPilot pilot.jobId)); s.dismiss }

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
                    let label = pilot.shipSymbol |> Option.defaultValue (pilotName pilot.jobId)
                    li {
                        match pilot.lastLogLine with
                        | Some line -> s.logbookLine (label, line)
                        | None -> s.logbookLine (label, s.pilotStatus pilot.status)
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
                        s.historyLine (
                            entry.programName,
                            entry.shipSymbol |> Option.defaultValue s.noShipLabel,
                            s.pilotStatus entry.status,
                            entry.finishedAt
                        )
                    }
            }
    }

/// The "name input + create button" row is identical between the custom-block and
/// program libraries below — everything past it (list rendering, the rename row)
/// differs in real ways (an extra "Save workshop" button, different message types)
/// and is left as-is rather than forced into a shared abstraction.
let private viewNameEntryRow (placeholder: string) (value: string) (onChange: string -> Message) (buttonLabel: string) (onCreate: Message) dispatch =
    div {
        input {
            attr.``type`` "text"
            attr.placeholder placeholder
            attr.value value
            on.change (fun e -> dispatch (onChange (string e.Value)))
        }
        button { on.click (fun _ -> dispatch onCreate); buttonLabel }
    }

let private viewCustomBlockLibrary model dispatch =
    let s = stringsFor model.locale

    div {
        h2 { s.customBlocksHeading }
        viewNameEntryRow s.newBlockNamePlaceholder model.newCustomBlockName NewCustomBlockNameChanged s.newBlockButton CreateCustomBlock dispatch
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
        viewNameEntryRow s.newProgramNamePlaceholder model.newProgramName NewProgramNameChanged s.newProgramButton CreateProgram dispatch
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
                button { on.click (fun _ -> dispatch CloseProgram); s.closeButton }
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

let private programSubTabStyle (model: Model) (subTab: ProgramSubTab) =
    if model.activeProgramSubTab = subTab then "" else "display: none"

let private programSubTabButton (model: Model) dispatch (subTab: ProgramSubTab) (label: string) =
    button {
        attr.style (
            if model.activeProgramSubTab = subTab then
                "font-weight: bold; text-decoration: underline; margin-right: 0.75rem"
            else
                "margin-right: 0.75rem"
        )
        on.click (fun _ -> dispatch (SwitchProgramSubTab subTab))
        label
    }

let private pollIntervalPresets = [ 1; 5; 10; 30 ]
let private logLevelPresets = [ "off"; "info"; "trace" ]

let private viewSettings (model: Model) dispatch =
    let s = stringsFor model.locale

    div {
        div {
            attr.style "margin-bottom: 1rem"
            p { attr.style "font-weight: bold"; s.settingsTokenLabel }
            match model.dashboard with
            | Some state -> p { s.pilotLabel state.agent.symbol }
            | None ->
                div {
                    input {
                        attr.``type`` "text"
                        attr.placeholder s.tokenPlaceholder
                        attr.value model.tokenInput
                        on.change (fun e -> dispatch (TokenInputChanged(string e.Value)))
                    }
                    button { on.click (fun _ -> dispatch SubmitToken); s.login }
                    if model.dashboardLoading then
                        p { s.loadingEllipsis }
                    match model.dashboardError with
                    | Some err -> p { s.errorPrefix err }
                    | None -> ()
                }
        }
        div {
            attr.style "margin-bottom: 1rem"
            p { attr.style "font-weight: bold"; s.settingsLocaleLabel }
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
        div {
            attr.style "margin-bottom: 1rem"
            p { attr.style "font-weight: bold"; s.settingsPollIntervalLabel }
            for seconds in pollIntervalPresets do
                button {
                    attr.disabled (model.pollIntervalSeconds = seconds)
                    on.click (fun _ -> dispatch (SetPollInterval seconds))
                    $"{seconds}s"
                }
        }
        div {
            attr.style "margin-bottom: 1rem"
            p { attr.style "font-weight: bold"; s.settingsThemeLabel }
            button {
                attr.disabled (model.theme = "light")
                on.click (fun _ -> dispatch (SetTheme "light"))
                s.settingsThemeLight
            }
            button {
                attr.disabled (model.theme = "dark")
                on.click (fun _ -> dispatch (SetTheme "dark"))
                s.settingsThemeDark
            }
        }
        div {
            attr.style "margin-bottom: 1rem"
            p { attr.style "font-weight: bold"; s.settingsLogLevelLabel }
            for level in logLevelPresets do
                button {
                    attr.disabled (model.logLevel = level)
                    on.click (fun _ -> dispatch (SetLogLevel level))
                    level.Substring(0, 1).ToUpper() + level.Substring(1)
                }
        }
        viewQueueStatus model dispatch
    }

let private viewContracts (s: Strings) (model: Model) (state: DashboardState) dispatch =
    let active, history = partitionContracts state.contracts

    div {
        h2 { s.contractsHeading }
        button {
            attr.disabled model.contractsRefreshing
            on.click (fun _ -> dispatch RefreshContracts)
            s.refreshContracts
        }
        if model.contractsRefreshing then
            p { s.loadingEllipsis }
        match model.contractActionError with
        | Some err -> p { attr.style "color: #cc0000"; s.errorPrefix err }
        | None -> ()

        h3 { s.contractsActiveHeading }
        for contract in active do
            div {
                attr.style "border: 1px solid #ccc; border-radius: 4px; padding: 0.5rem; margin: 0.5rem 0"
                p { attr.style "font-weight: bold"; $"{contract.id} ({contract.``type``})" }
                for good in contract.terms.deliver do
                    p { s.contractDeliverLine (good.tradeSymbol, good.destinationSymbol, good.unitsFulfilled, good.unitsRequired) }
                p { s.contractPaymentLine (contract.terms.payment.onAccepted, contract.terms.payment.onFulfilled) }
                p { s.contractDeadlineLine contract.terms.deadline }
                if contract.accepted then
                    p { s.contractAcceptedLabel }
                    button {
                        attr.disabled (model.fulfillingContractId = Some contract.id)
                        on.click (fun _ -> dispatch (FulfillContractClicked contract.id))
                        s.fulfillContractButton
                    }
                else
                    p { s.contractPendingLabel }
                    button {
                        attr.disabled (model.acceptingContractId = Some contract.id)
                        on.click (fun _ -> dispatch (AcceptContractClicked contract.id))
                        s.acceptContractButton
                    }
            }

        button {
            on.click (fun _ -> dispatch ToggleContractsHistory)
            if model.contractsHistoryExpanded then s.contractsHistoryToggleHide else s.contractsHistoryToggleShow
        }
        if model.contractsHistoryExpanded then
            div {
                h3 { s.contractsHistoryHeading }
                ul {
                    for contract in history do
                        li { s.contractLine (contract.id, contract.``type``, contract.accepted, contract.fulfilled) }
                }
            }
    }

let private viewFactions (s: Strings) (model: Model) dispatch =
    div {
        h2 { s.factionsHeading }
        button {
            attr.disabled model.factionsLoading
            on.click (fun _ -> dispatch LoadFactions)
            s.refreshFactions
        }
        if model.factionsLoading then
            p { s.loadingEllipsis }
        match model.factionsError with
        | Some err -> p { attr.style "color: #cc0000"; s.errorPrefix err }
        | None -> ()
        match model.factionsSnapshot with
        | None -> ()
        | Some snapshot ->
            for faction in snapshot.factions |> List.sortBy (fun f -> f.symbol) do
                div {
                    attr.style "border: 1px solid #ccc; border-radius: 4px; padding: 0.5rem; margin: 0.5rem 0"
                    p { attr.style "font-weight: bold"; $"{faction.symbol} — {faction.name}" }
                    p { faction.description }
                    match faction.headquarters with
                    | Some hq -> p { s.factionHeadquartersLine hq }
                    | None -> ()
                    p {
                        if faction.isRecruiting then
                            s.factionRecruitingLabel
                        else
                            s.factionNotRecruitingLabel
                    }
                    match reputationFor snapshot.reputations faction.symbol with
                    | Some rep -> p { s.factionReputationLine rep }
                    | None -> ()
                    if not faction.traits.IsEmpty then
                        div {
                            p { attr.style "font-weight: bold"; s.factionTraitsHeading }
                            ul {
                                for trait_ in faction.traits do
                                    li { s.factionTraitLine (trait_.name, trait_.description) }
                            }
                        }
                }
    }

let private viewAgents (s: Strings) (model: Model) dispatch =
    div {
        h2 { s.agentsHeading }
        button {
            attr.disabled model.publicAgentsLoading
            on.click (fun _ -> dispatch LoadPublicAgents)
            s.refreshAgents
        }
        if model.publicAgentsLoading then
            p { s.loadingEllipsis }
        match model.publicAgentsError with
        | Some err -> p { attr.style "color: #cc0000"; s.errorPrefix err }
        | None -> ()
        match model.publicAgents with
        | None -> ()
        | Some agents ->
            ul {
                for agent in agents |> List.sortBy (fun a -> a.symbol) do
                    li { s.agentPublicLine (agent.symbol, agent.headquarters, agent.credits, agent.shipCount) }
            }
    }

let view model dispatch =
    let s = stringsFor model.locale

    div {
        attr.style "font-family: sans-serif; padding: 1rem"
        p { model.status }
        if model.serverUnreachable then
            p { attr.style "color: #cc0000"; s.serverUnreachableMessage }

        div {
            attr.style "margin-bottom: 1rem; border-bottom: 1px solid #ccc; padding-bottom: 0.5rem"
            tabButton model dispatch ProgrammierenTab s.tabProgrammieren
            tabButton model dispatch PilotenTab s.tabPiloten
            tabButton model dispatch GalaxieTab s.tabGalaxie
            tabButton model dispatch ContractsTab s.tabContracts
            tabButton model dispatch FactionsTab s.tabFactions
            tabButton model dispatch AgentsTab s.tabAgents
            tabButton model dispatch SettingsTab s.tabSettings
        }

        div {
            attr.style (tabStyle model ProgrammierenTab)
            div {
                attr.style "margin-bottom: 0.75rem"
                programSubTabButton model dispatch ProgramsSubTab s.tabProgramsSub
                programSubTabButton model dispatch CustomBlocksSubTab s.tabCustomBlocksSub
            }
            div {
                attr.style (programSubTabStyle model ProgramsSubTab)
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
                            attr.style "height: 480px; width: 100%; border: 1px solid #ccc; margin-top: 0.5rem"
                        }
                    }
            }
            div {
                attr.style (programSubTabStyle model CustomBlocksSubTab)
                viewCustomBlockLibrary model dispatch
                p {
                    s.workshopHint
                    button { on.click (fun _ -> dispatch PublishSignature); s.publishSignatureButton }
                }
                div {
                    attr.id model.workshopContainerId
                    attr.style "height: 480px; width: 100%; border: 1px solid #ccc; margin-top: 0.5rem"
                }
            }
        }

        div {
            attr.style (tabStyle model PilotenTab)
            viewJobRunner model dispatch
        }

        div {
            attr.style (tabStyle model GalaxieTab)
            match model.dashboard with
            | None -> p { s.loginInSettingsHint }
            | Some state ->
                div {
                    attr.style "display: flex; gap: 1rem; align-items: flex-start; flex-wrap: nowrap; overflow-x: auto"
                    div {
                        attr.style "flex: 0 0 auto; min-width: 280px"
                        viewGalaxyMap s model state dispatch
                    }
                    div {
                        attr.style "flex: 0 0 auto; min-width: 280px"
                        viewSystemMap s model state dispatch
                        button {
                            attr.style "font-size: 0.8em; padding: 0.2em 0.6em; margin-top: 0.3rem"
                            attr.disabled model.systemReloading
                            on.click (fun _ -> dispatch ReloadSystem)
                            s.refreshSystem
                        }
                        if model.systemWaypointsLoading || model.systemReloading then
                            p { s.loadingEllipsis }
                    }
                    div {
                        attr.style "flex: 0 0 300px; min-width: 250px"
                        viewInspector state model dispatch
                    }
                }
        }

        div {
            attr.style (tabStyle model ContractsTab)
            match model.dashboard with
            | None -> p { s.loginInSettingsHint }
            | Some state -> viewContracts s model state dispatch
        }

        div {
            attr.style (tabStyle model FactionsTab)
            match model.dashboard with
            | None -> p { s.loginInSettingsHint }
            | Some _ -> viewFactions s model dispatch
        }

        div {
            attr.style (tabStyle model AgentsTab)
            match model.dashboard with
            | None -> p { s.loginInSettingsHint }
            | Some _ -> viewAgents s model dispatch
        }

        div {
            attr.style (tabStyle model SettingsTab)
            viewSettings model dispatch
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
                    Cmd.ofMsg RefreshDashboard
                    Cmd.ofMsg LoadQueueStatus
                    Cmd.ofMsg LoadPilots
                    Cmd.ofMsg LoadJobHistory
                    Cmd.ofMsg LoadCustomBlocks
                    Cmd.ofMsg LoadPrograms
                    Cmd.ofMsg LoadLocale
                    Cmd.ofMsg LoadPollInterval
                    Cmd.ofMsg LoadTheme
                    Cmd.ofMsg LoadLogLevel
                    Cmd.ofMsg MapTick
                ])
            (update js remote agentRemote queueRemote jobRemote customBlockRemote programRemote settingsRemote)
            view
