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

/// One active custom-block call (§9d/§14). Milestone 6 only ever has exactly one
/// Frame (`scope = "main"`) — `CallCustomBlock` fails the job cleanly rather than
/// pushing a second frame (real calls are Milestone 9) — but the shape is the real
/// one later milestones build on, not a placeholder.
type Frame =
    { scope: string
      position: PathEntry list
      locals: Map<string, Value> }

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
      cooldownExpiration: string option }

/// One of the 6 in-scope actions (Milestone 6 — navigate/orbit/dock/extract/buy/sell
/// only; not purchaseShip/refuel/acceptContract/deliverContract).
type QueuedAction =
    | DoNavigate of destination: string
    | DoOrbit
    | DoDock
    | DoExtract
    | DoBuy of tradeSymbol: string * units: int
    | DoSell of tradeSymbol: string * units: int

/// Per-action pre-call baseline, captured from `JobState.lastKnownShip` — data
/// already in hand (§13: "costs nothing, it's bookkeeping of data already in hand"),
/// never a fresh API call.
type ActionBaseline =
    | NavigateBaseline of intendedWaypoint: string
    | DockOrbitBaseline of expectedStatus: string
    | CargoBaseline of unitsBefore: int
    | ExtractBaseline of cooldownExpirationBefore: string option * unitsBefore: int

/// Mirrors §14's job statuses, minus the DB-only ones — there is no separate
/// "queued" state in the pure core, only "waiting for an effect's result". Carrying
/// the original `QueuedAction` alongside the baseline lets a reconciliation retry
/// reissue the exact same call without reconstructing it from scratch.
type JobStatus =
    | Running
    | AwaitingApiResponse of attempt: int * action: QueuedAction * baseline: ActionBaseline
    | WaitingForArrival of until: DateTimeOffset
    | WaitingForCooldown of until: DateTimeOffset
    | Reconciling of attempt: int * action: QueuedAction * baseline: ActionBaseline
    | Completed
    | Failed of message: string

type JobState =
    { jobId: JobId
      program: CompiledProgram
      shipSymbol: string
      status: JobStatus
      /// Head = top frame; length 1 for the whole of Milestone 6 (no real
      /// custom-block calls yet).
      stack: Frame list
      /// The "last time it looked" baseline source (§13) — updated from every
      /// successful/reconciled response, never from a dedicated extra call.
      lastKnownShip: ShipSnapshot option
      /// German activity log, newest-first.
      log: string list }

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
    /// `RequestQueue.AmbiguousFailure` surfaced (§13: never auto-retried by the queue).
    | ApiAmbiguous of message: string
    /// Any other terminal exception surfaced (e.g. `RequestQueue.ServerResetDetected`).
    | ApiFailed of message: string

type SchedulerEvent =
    | WakeTick
    | ApiResponseReceived of jobId: JobId * attemptNumber: int * result: ApiResult

type WaitReason =
    | ArrivalWait
    | CooldownWait

/// What `step` wants to happen outside the pure core. The shell executes these and,
/// for `QueueApiCall`/`ReconcileShipState`, eventually feeds an `ApiResponseReceived`
/// event back in. No direct DB/HTTP/sleep call ever appears in `Step.fs` — only these
/// values.
type Effect =
    | QueueApiCall of jobId: JobId * shipSymbol: string * action: QueuedAction * attemptNumber: int
    | ReconcileShipState of jobId: JobId * shipSymbol: string * attemptNumber: int
    | StartWait of jobId: JobId * until: DateTimeOffset * reason: WaitReason
    | LogMessage of jobId: JobId * germanText: string
    | JobCompleted of jobId: JobId
    | JobFailed of jobId: JobId * germanText: string
