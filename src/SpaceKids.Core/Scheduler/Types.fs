namespace SpaceKids.Core.Scheduler

open System
open SpaceKids.Core.Dsl

/// The pure scheduler core (§14, Milestone 6). This namespace never touches a real
/// SpaceTraders client, HTTP, SQLite, or the wall clock directly — everything it needs
/// is passed in (`Clock`) or produced as data (`Effect`) for a shell to execute.
/// `SpaceKids.Core` stays framework/infrastructure-free (§14), so this deliberately
/// does not reference `SpaceKids.SpaceTraders` — `ShipSnapshot`/`ApiResult` are the
/// scheduler's own minimal shapes; the server-side shell (`JobRunner.fs`) maps the
/// real API's richer types onto these at the boundary.

/// Real clock in production, fake/controllable clock in tests (§14).
type Clock = { now: unit -> DateTimeOffset }

type JobId = string

/// A loop's runtime bookkeeping, persisted per `PathEntry` so a repeat counter or
/// foreach cursor survives a restart mid-loop (a Milestone 7 concern; the shape is
/// fixed here so it isn't restructured later).
type LoopState =
    | RepeatState of remaining: int
    | ForEachState of items: Value list * index: int

/// Which instruction list a `PathEntry` points into, addressed via the instruction's
/// own `blockId` the DSL already carries — no separate addressing scheme needed.
type BodyRef =
    | MainBody
    | ThenBranch of ifBlockId: string * branchIndex: int
    | ElseBranch of ifBlockId: string
    | RepeatBody of blockId: string
    | WhileUntilBody of blockId: string
    | ForEachBody of blockId: string
    | CustomBlockBody of customBlockId: string

/// A frame's position is a path into nested instruction lists, not a flat index
/// (§14 — programs contain nested loops/conditionals). Head = current/deepest entry.
type PathEntry =
    { bodyRef: BodyRef
      index: int
      loopState: LoopState option }

/// One active custom-block call (§9d/§14). The bottom frame (`scope = "main"`) has
/// `returnTarget = None`; every frame pushed by a `CallCustomBlock` carries the
/// caller's `resultTarget` here, so popping it (§9d, Milestone 9) knows which local
/// in the newly-restored top frame to write the callee's `returnExpr` result into —
/// the caller's own position is left unadvanced at the call site for exactly this,
/// the same way `AwaitingApiResponse` leaves the caller's position pointing at the
/// action until its result comes back.
type Frame =
    { scope: string
      position: PathEntry list
      locals: Map<string, Value>
      returnTarget: string option }

/// A ship-state snapshot as the scheduler needs it for reconciliation (§13) —
/// deliberately independent of `SpaceKids.SpaceTraders.Ship` (see the namespace note
/// above). `cargoInventory` maps trade-good symbol to units. `navArrival`/
/// `cooldownExpiration` are ISO-8601 timestamps, parsed only at comparison sites.
type ShipSnapshot =
    { navStatus: string
      navWaypoint: string
      navArrival: string option
      cargoUnits: int
      cargoInventory: Map<string, int>
      cooldownExpiration: string option
      /// Milestone 9 (Part A) — ship-local reconciliation signal for `refuel`.
      fuelCurrent: int }

/// All 11 catalog actions (Milestone 6 added the first 6; Milestone 9/Part A adds
/// survey/deliverContract/acceptContract/purchaseShip/refuel).
type QueuedAction =
    | DoNavigate of destination: string
    | DoOrbit
    | DoDock
    | DoExtract
    | DoBuy of tradeSymbol: string * units: int
    | DoSell of tradeSymbol: string * units: int
    | DoSurvey
    | DoDeliverContract of contractId: string * tradeSymbol: string * units: int
    | DoAcceptContract of contractId: string
    | DoPurchaseShip of shipType: string * waypointSymbol: string
    | DoRefuel

/// Per-action pre-call baseline, captured from `JobState.lastKnownShip` — data
/// already in hand (§13: "costs nothing, it's bookkeeping of data already in hand"),
/// never a fresh API call. `AcceptContractBaseline`/`FleetBaseline` are the two
/// exceptions (Milestone 9/Part A) — neither action has a ship-local signal, so per
/// §13's explicit allowance they reconcile via a contract/fleet fetch instead.
type ActionBaseline =
    | NavigateBaseline of intendedWaypoint: string
    | DockOrbitBaseline of expectedStatus: string
    | CargoBaseline of unitsBefore: int
    | ExtractBaseline of cooldownExpirationBefore: string option * unitsBefore: int
    | SurveyBaseline of cooldownExpirationBefore: string option
    | AcceptContractBaseline of contractId: string
    | FleetBaseline of shipCountBefore: int
    | FuelBaseline of unitsBefore: int

/// Mirrors §14's job statuses, minus the DB-only ones — there is no separate
/// "queued" state in the pure core, only "waiting for an effect's result". Carrying
/// the original `QueuedAction` alongside the baseline lets a reconciliation retry
/// reissue the exact same call without reconstructing it from scratch.
///
/// `Paused`/`Cancelled` (Milestone 7, §14/§15) are only ever entered from an
/// interruptible status (`Running`/`WaitingForArrival`/`WaitingForCooldown`) — never
/// directly from `AwaitingApiResponse`/`Reconciling`, so a pause/cancel request can
/// never abandon an in-flight non-idempotent action or its reconciliation (see
/// `JobState.pausePending`/`cancelPending`).
type JobStatus =
    | Running
    | AwaitingApiResponse of attempt: int * action: QueuedAction * baseline: ActionBaseline
    | WaitingForArrival of until: DateTimeOffset
    | WaitingForCooldown of until: DateTimeOffset
    | Reconciling of attempt: int * action: QueuedAction * baseline: ActionBaseline
    /// Milestone 9/Part B (§8/§14): an in-flight info-read block (getFuel, getMarket,
    /// ...). Simpler than `AwaitingApiResponse` — a GET is always safe to retry, so
    /// there is no `Reconciling` hop, ever; an `ApiAmbiguous` response just re-emits
    /// the same `QueueInfoRead` with `attempt + 1`.
    | AwaitingInfoResponse of attempt: int * infoType: string * args: Map<string, string> * resultTarget: string
    | Paused of resuming: JobStatus
    | Cancelled
    | Completed
    | Failed of message: string

type JobState =
    { jobId: JobId
      /// Saved/named multiple-program library: the program-definition id this job
      /// is flying (not the per-run `programs` snapshot row id) — per-program watch
      /// mode filters pilots by this, not by "any pilot anywhere."
      programId: string
      program: CompiledProgram
      /// `None` for a ship-agnostic job (§14 follow-up: a program that never
      /// references a ship-scoped block, e.g. only scans waypoints or purchases a
      /// ship, doesn't need one and never takes a `ship_locks` lease) — a player may
      /// still pick a spare ship for one anyway, harmlessly ignored.
      shipSymbol: string option
      status: JobStatus
      /// Head = top frame; length 1 for the whole of Milestone 6 (no real
      /// custom-block calls yet).
      stack: Frame list
      /// The "last time it looked" baseline source (§13) — updated from every
      /// successful/reconciled response, never from a dedicated extra call.
      lastKnownShip: ShipSnapshot option
      /// Milestone 9/Part A — fleet-wide ship count "last known" bookkeeping,
      /// analogous to `lastKnownShip` but scope-wide rather than per-ship; the
      /// `FleetBaseline` source for `purchaseShip` (no per-ship signal exists for it).
      /// Populated at job start (a `ListShips` call, same effort as the initial ship
      /// snapshot) and refreshed on every successful purchase/fleet reconciliation.
      lastKnownFleetShipCount: int option
      /// German activity log, newest-first.
      log: string list
      /// Set by a `PauseRequested`/`CancelRequested` event received while
      /// `AwaitingApiResponse`/`Reconciling` (Milestone 7) — applied at the next
      /// point the job would otherwise settle into an interruptible status, so the
      /// in-flight action/reconciliation is never abandoned mid-flight.
      pausePending: bool
      cancelPending: bool }

/// What the shell reports back for an in-flight API call or a reconciliation fetch.
type ApiResult =
    | NavigateOk of navStatus: string * navWaypoint: string * arrival: string
    | NavResultOk of navStatus: string * navWaypoint: string
    | ExtractOk of
        cooldownExpiration: string *
        cargoUnits: int *
        cargoInventory: Map<string, int> *
        yieldSymbol: string *
        yieldUnits: int
    | TradeOk of
        cargoUnits: int *
        cargoInventory: Map<string, int> *
        transactionType: string *
        tradeSymbol: string *
        units: int *
        totalPrice: int
    | ReconciliationShip of ShipSnapshot
    | SurveyOk of cooldownExpiration: string
    | DeliverOk of cargoUnits: int * cargoInventory: Map<string, int> * contractFulfilled: bool
    | AcceptContractOk of accepted: bool
    | PurchaseShipOk of newShipSymbol: string * fleetShipCount: int
    | RefuelOk of fuelCurrent: int
    /// Milestone 9/Part A — the two new reconciliation-fetch kinds for actions with
    /// no ship-local signal (`acceptContract`/`purchaseShip`).
    | ReconciliationContract of accepted: bool
    | ReconciliationFleet of shipCount: int
    /// Milestone 9/Part B — an info-read block's result, already converted to a
    /// `Value` (VRecord/VList/VNumber per §8) by the shell.
    | InfoOk of Value
    /// `RequestQueue.AmbiguousFailure` surfaced (§13: never auto-retried by the queue).
    | ApiAmbiguous of message: string
    /// Any other terminal exception surfaced (e.g. `RequestQueue.ServerResetDetected`).
    | ApiFailed of message: string

type SchedulerEvent =
    | WakeTick
    | ApiResponseReceived of jobId: JobId * attemptNumber: int * result: ApiResult
    /// Milestone 7 (§14/§15): player-initiated pilot-card controls. Deferred rather
    /// than applied immediately while an action/reconciliation is in flight — see
    /// `JobState.pausePending`/`cancelPending`.
    | PauseRequested
    | ResumeRequested
    | CancelRequested

type WaitReason =
    | ArrivalWait
    | CooldownWait

/// What `step` wants to happen outside the pure core. The shell executes these and,
/// for `QueueApiCall`/`ReconcileShipState`, eventually feeds an `ApiResponseReceived`
/// event back in. No direct DB/HTTP/sleep call ever appears in `Step.fs` — only these
/// values.
type Effect =
    | QueueApiCall of jobId: JobId * shipSymbol: string option * action: QueuedAction * attemptNumber: int
    | ReconcileShipState of jobId: JobId * shipSymbol: string option * attemptNumber: int
    /// Milestone 9/Part A — reconciliation via a contract/fleet fetch rather than a
    /// ship fetch, for `acceptContract`/`purchaseShip` (see `ActionBaseline`).
    | ReconcileContractState of jobId: JobId * contractId: string * attemptNumber: int
    | ReconcileFleetState of jobId: JobId * attemptNumber: int
    /// Milestone 9/Part B (§8/§14) — an info-read block's fetch. Always safe to
    /// retry (a GET), so unlike `QueueApiCall` there is no baseline to carry.
    | QueueInfoRead of
        jobId: JobId *
        shipSymbol: string option *
        infoType: string *
        args: Map<string, string> *
        attemptNumber: int *
        resultTarget: string
    | StartWait of jobId: JobId * until: DateTimeOffset * reason: WaitReason
    | LogMessage of jobId: JobId * germanText: string
    | JobCompleted of jobId: JobId
    | JobFailed of jobId: JobId * germanText: string
    /// Milestone 7 (§14/§15): the player stopped the job — the shell releases its
    /// ship lock, same as `JobCompleted`/`JobFailed`.
    | JobCancelled of jobId: JobId
