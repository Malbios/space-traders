module SpaceKids.Core.Scheduler.Step

open System
open SpaceKids.Core.Dsl

// --- Instruction-tree lookup helpers -----------------------------------------------
// A `blockId` is unique across the whole compiled program (assigned from Blockly
// block ids by the compiler), so a `BodyRef` can always be resolved by searching for
// the owning instruction, without needing to also track parent pointers.

let private blockIdOf (instr: Instruction) : string =
    match instr with
    | ApiAction(bid, _, _) -> bid
    | InfoRead(bid, _, _, _) -> bid
    | ShowMessage(bid, _) -> bid
    | Wait(bid, _) -> bid
    | SetVariable(bid, _, _) -> bid
    | ChangeVariable(bid, _, _) -> bid
    | ListSet(bid, _, _, _, _) -> bid
    | If(bid, _, _) -> bid
    | Repeat(bid, _, _) -> bid
    | WhileUntil(bid, _, _, _) -> bid
    | ForEach(bid, _, _, _) -> bid
    | WithShip(bid, _, _, _) -> bid
    | Parallel(bid, _) -> bid
    | CallCustomBlock(bid, _, _, _) -> bid
    | Break bid -> bid
    | Continue bid -> bid
    | ExitShipScope bid -> bid

let rec private findInstructionByBlockId (instructions: Instruction list) (blockId: string) : Instruction option =
    instructions
    |> List.tryPick (fun instr ->
        if blockIdOf instr = blockId then
            Some instr
        else
            match instr with
            | If(_, branches, elseBranch) ->
                match branches |> List.tryPick (fun (_, body) -> findInstructionByBlockId body blockId) with
                | Some found -> Some found
                | None -> elseBranch |> Option.bind (fun eb -> findInstructionByBlockId eb blockId)
            | Repeat(_, _, body) -> findInstructionByBlockId body blockId
            | WhileUntil(_, _, _, body) -> findInstructionByBlockId body blockId
            | ForEach(_, _, _, body) -> findInstructionByBlockId body blockId
            | WithShip(_, _, body, elseBranch) ->
                match findInstructionByBlockId body blockId with
                | Some found -> Some found
                | None -> elseBranch |> Option.bind (fun eb -> findInstructionByBlockId eb blockId)
            | Parallel(_, branches) -> branches |> List.tryPick (fun body -> findInstructionByBlockId body blockId)
            | _ -> None)

let private resolveBody (program: CompiledProgram) (bodyRef: BodyRef) : Instruction list =
    match bodyRef with
    | MainBody -> program.instructions
    | ThenBranch(ifBlockId, branchIndex) ->
        match findInstructionByBlockId program.instructions ifBlockId with
        | Some(If(_, branches, _)) -> snd branches.[branchIndex]
        | _ -> failwith $"If-Block {ifBlockId} nicht gefunden."
    | ElseBranch ifBlockId ->
        match findInstructionByBlockId program.instructions ifBlockId with
        | Some(If(_, _, Some elseBranch)) -> elseBranch
        | _ -> failwith $"Else-Zweig von {ifBlockId} nicht gefunden."
    | RepeatBody blockId ->
        match findInstructionByBlockId program.instructions blockId with
        | Some(Repeat(_, _, body)) -> body
        | _ -> failwith $"Wiederhole-Block {blockId} nicht gefunden."
    | WhileUntilBody blockId ->
        match findInstructionByBlockId program.instructions blockId with
        | Some(WhileUntil(_, _, _, body)) -> body
        | _ -> failwith $"Solange/Bis-Block {blockId} nicht gefunden."
    | ForEachBody blockId ->
        match findInstructionByBlockId program.instructions blockId with
        | Some(ForEach(_, _, _, body)) -> body
        | _ -> failwith $"Für-jedes-Element-Block {blockId} nicht gefunden."
    | WithShipBody blockId ->
        match findInstructionByBlockId program.instructions blockId with
        | Some(WithShip(_, _, body, _)) -> body
        | _ -> failwith $"Mit-Schiff-Block {blockId} nicht gefunden."
    | WithShipElseBody blockId ->
        match findInstructionByBlockId program.instructions blockId with
        | Some(WithShip(_, _, _, Some elseBranch)) -> elseBranch
        | _ -> failwith $"Sonst-Zweig von Mit-Schiff-Block {blockId} nicht gefunden."
    | ParallelBranchBody(blockId, branchIndex) ->
        match findInstructionByBlockId program.instructions blockId with
        | Some(Parallel(_, branches)) -> branches.[branchIndex]
        | _ -> failwith $"Parallel-Zweig {branchIndex} von {blockId} nicht gefunden."
    | CustomBlockBody customBlockId ->
        match program.customBlocks.TryFind customBlockId with
        | Some cb -> cb.instructions
        | None -> failwith $"Eigener Block {customBlockId} nicht gefunden."

let private forEachVariableName (program: CompiledProgram) (bodyRef: BodyRef) : string =
    match bodyRef with
    | ForEachBody bid ->
        match findInstructionByBlockId program.instructions bid with
        | Some(ForEach(_, variable, _, _)) -> variable
        | _ -> failwith $"Für-jedes-Element-Block {bid} nicht gefunden."
    | _ -> failwith "Erwartete einen Für-jedes-Element-Block."

// --- Position/frame helpers ---------------------------------------------------------

let private currentInstructionOf (program: CompiledProgram) (frame: Frame) : Instruction option =
    match frame.position with
    | [] -> None
    | pe :: _ ->
        let body = resolveBody program pe.bodyRef
        if pe.index >= 0 && pe.index < List.length body then Some body.[pe.index] else None

/// Advances the top position by one, walking back up through finished loop
/// bodies/branches as needed. Loop constructs are handled per §14's shape:
/// - `Repeat`/`ForEach`, once genuinely exhausted, pop *and* advance the parent past
///   the owning instruction (they never repeat again).
/// - `WhileUntil`'s body pops *without* advancing the parent — the parent's position
///   still points at the same `WhileUntil` instruction, so the next walk naturally
///   re-evaluates its condition, exactly matching real while-loop semantics.
let rec private advancePosition (program: CompiledProgram) (frame: Frame) : Frame =
    match frame.position with
    | [] -> frame
    | top :: rest ->
        let body = resolveBody program top.bodyRef
        let nextIndex = top.index + 1

        if nextIndex < List.length body then
            { frame with
                position = { top with index = nextIndex } :: rest }
        else
            exhaustLoopEntry program top rest frame

/// The per-loop-kind decision made once a loop's body is treated as exhausted: re-enter
/// (decrementing `RepeatState`/advancing `ForEachState`'s cursor) if iterations remain,
/// or pop and advance the parent past the loop otherwise. Shared by `advancePosition`
/// (a body genuinely ran off the end) and `Continue` (forced early, as if it had).
and private exhaustLoopEntry
    (program: CompiledProgram)
    (top: PathEntry)
    (rest: PathEntry list)
    (frame: Frame)
    : Frame =
    match top.bodyRef, top.loopState with
    | RepeatBody _, Some(RepeatState remaining) when remaining > 1 ->
        { frame with
            position = { top with index = 0; loopState = Some(RepeatState(remaining - 1)) } :: rest }
    | ForEachBody _, Some(ForEachState(items, idx)) when idx + 1 < List.length items ->
        let newIdx = idx + 1
        let varName = forEachVariableName program top.bodyRef

        { frame with
            position = { top with index = 0; loopState = Some(ForEachState(items, newIdx)) } :: rest
            locals = frame.locals.Add(varName, items.[newIdx]) }
    | WhileUntilBody _, _ -> { frame with position = rest }
    | _ ->
        match rest with
        | [] -> { frame with position = [] }
        | _ -> advancePosition program { frame with position = rest }

/// Finds the nearest enclosing loop `PathEntry` (head = deepest), if any, plus
/// everything below it in the position stack — used by `Break`/`Continue` to discard
/// any nested `If`-branch entries above the loop they act on.
let rec private findEnclosingLoop (position: PathEntry list) : (PathEntry * PathEntry list) option =
    match position with
    | [] -> None
    | top :: rest ->
        match top.bodyRef with
        | RepeatBody _
        | WhileUntilBody _
        | ForEachBody _ -> Some(top, rest)
        | _ -> findEnclosingLoop rest

/// Exits the nearest enclosing loop unconditionally, regardless of iterations left —
/// the same "loop exhausted, pop and advance the parent" behavior `exhaustLoopEntry`'s
/// fallback branch already has. `Validator.fs`'s `checkFlowStatements` should have
/// already ruled out "no enclosing loop"; if it somehow still happens, leave the frame
/// untouched rather than crash a running job.
let private breakLoop (program: CompiledProgram) (frame: Frame) : Frame =
    match findEnclosingLoop frame.position with
    | None -> frame
    | Some(_, parentRest) ->
        match parentRest with
        | [] -> { frame with position = [] }
        | _ -> advancePosition program { frame with position = parentRest }

/// Skips straight to the nearest enclosing loop's next iteration (or exits it, if the
/// current one was its last) by forcing `exhaustLoopEntry`'s decision immediately,
/// instead of waiting for the body to run off the end naturally.
let private continueLoop (program: CompiledProgram) (frame: Frame) : Frame =
    match findEnclosingLoop frame.position with
    | None -> frame
    | Some(top, rest) -> exhaustLoopEntry program top rest frame

let private pushBody (bodyRef: BodyRef) (loopState: LoopState option) (frame: Frame) : Frame =
    { frame with
        position =
            { bodyRef = bodyRef
              index = 0
              loopState = loopState }
            :: frame.position }

let private mapTopFrame (f: Frame -> Frame) (job: JobState) : JobState =
    match job.stack with
    | top :: rest -> { job with stack = f top :: rest }
    | [] -> job

let private currentInstruction (job: JobState) : Instruction option =
    match job.stack with
    | [] -> None
    | top :: _ -> currentInstructionOf job.program top

let private advanceJobPosition (job: JobState) : JobState =
    mapTopFrame (advancePosition job.program) job

let private setLocal (name: string) (value: Value) (job: JobState) : JobState =
    mapTopFrame (fun fr -> { fr with locals = fr.locals.Add(name, value) }) job

let private pushBodyJob (bodyRef: BodyRef) (loopState: LoopState option) (job: JobState) : JobState =
    mapTopFrame (pushBody bodyRef loopState) job

let private currentLocals (job: JobState) : Map<string, Value> =
    match job.stack with
    | [] -> Map.empty
    | top :: _ -> top.locals

let private currentShipSymbol (job: JobState) : string option =
    match job.currentShipStack with
    | (sym, _) :: _ -> Some sym
    | [] -> job.shipSymbol

let private isTerminalStatus (status: JobStatus) : bool =
    match status with
    | Completed
    | Failed _
    | Cancelled -> true
    | _ -> false

let private mapBranchEffect (branchId: string) (effect: Effect) : Effect option =
    match effect with
    | QueueApiCall(jobId, shipSymbol, action, attemptNumber) ->
        Some(QueueBranchApiCall(jobId, branchId, shipSymbol, action, attemptNumber))
    | ReconcileShipState(jobId, shipSymbol, attemptNumber) ->
        Some(ReconcileBranchShipState(jobId, branchId, shipSymbol, attemptNumber))
    | ReconcileContractState(jobId, contractId, attemptNumber, field) ->
        Some(ReconcileBranchContractState(jobId, branchId, contractId, attemptNumber, field))
    | ReconcileFleetState(jobId, attemptNumber) ->
        Some(ReconcileBranchFleetState(jobId, branchId, attemptNumber))
    | ReconcileContractsState(jobId, attemptNumber) ->
        Some(ReconcileBranchContractsState(jobId, branchId, attemptNumber))
    | QueueInfoRead(jobId, shipSymbol, infoType, args, attemptNumber, resultTarget) ->
        Some(QueueBranchInfoRead(jobId, branchId, shipSymbol, infoType, args, attemptNumber, resultTarget))
    | AcquireShipScope(jobId, blockId, shipSymbol, hasElse) ->
        Some(AcquireBranchShipScope(jobId, branchId, blockId, shipSymbol, hasElse))
    | ReleaseShipScope _ -> Some effect
    | LogMessage _
    | StartWait _ -> Some effect
    | JobCompleted _
    | JobFailed _
    | JobCancelled _ -> None
    | QueueBranchApiCall _
    | ReconcileBranchShipState _
    | ReconcileBranchContractState _
    | ReconcileBranchFleetState _
    | ReconcileBranchContractsState _
    | QueueBranchInfoRead _
    | AcquireBranchShipScope _ -> Some effect

let private mapBranchEffects (branchId: string) (effects: Effect list) : Effect list =
    effects |> List.choose (mapBranchEffect branchId)

// --- Call stack (§9d/§14, Milestone 9) ----------------------------------------------

let private pushFrame (frame: Frame) (job: JobState) : JobState = { job with stack = frame :: job.stack }

/// Pops the top frame, unless it's the only one left — the bottom (`"main"`) frame
/// is never popped this way; its position emptying means the whole job is
/// `Completed`, handled separately where this is called.
let private popFrame (job: JobState) : (Frame * JobState) option =
    match job.stack with
    | top :: (_ :: _ as rest) -> Some(top, { job with stack = rest })
    | _ -> None

// --- Reconciliation (§13) -----------------------------------------------------------

/// Decides, from a fresh ship snapshot, whether the ambiguous action already
/// happened. Per-ship signals only — credits are never consulted (§13: "never as the
/// deciding signal").
let private reconcile (baseline: ActionBaseline) (current: ShipSnapshot) : bool =
    match baseline with
    | NavigateBaseline intended -> current.navWaypoint = intended
    | DockOrbitBaseline expected -> current.navStatus = expected
    | FlightModeBaseline intended -> current.flightMode = intended
    | CargoBaseline unitsBefore -> current.cargoUnits <> unitsBefore
    | ExtractBaseline(cooldownBefore, unitsBefore) ->
        let cooldownAdvanced =
            match cooldownBefore, current.cooldownExpiration with
            | Some before, Some after -> DateTimeOffset.Parse(after) > DateTimeOffset.Parse(before)
            | None, Some _ -> true
            | _, None -> false

        cooldownAdvanced && current.cargoUnits <> unitsBefore
    | SurveyBaseline cooldownBefore ->
        match cooldownBefore, current.cooldownExpiration with
        | Some before, Some after -> DateTimeOffset.Parse(after) > DateTimeOffset.Parse(before)
        | None, Some _ -> true
        | _, None -> false
    | FuelBaseline unitsBefore -> current.fuelCurrent <> unitsBefore
    // `AcceptContractBaseline`/`FleetBaseline`/`ContractsCountBaseline` never reach
    // this function — they reconcile via contract/fleet fetches, handled directly in
    // `handleApiResponse` (no `ShipSnapshot` signal exists for any of them).
    | AcceptContractBaseline _
    | FulfillContractBaseline _
    | FleetBaseline _
    | ContractsCountBaseline _ -> failwith "AcceptContractBaseline/FulfillContractBaseline/FleetBaseline/ContractsCountBaseline reconcile via contract/fleet fetch, not ship state."

// --- Pause/resume/cancel (§14/§15, Milestone 7) -------------------------------------

/// After an action's success/reconciled-confirmation settles the job into an
/// interruptible status (`job'.status` already set to `Running`/`WaitingForArrival`/
/// `WaitingForCooldown`), applies any pending pause/cancel request *before* doing
/// anything further — this is the one place that matters: without it, a pause
/// requested mid-`AwaitingApiResponse`/`Reconciling` would only be noticed after the
/// job had already run arbitrarily far past the point the player asked it to stop
/// (`continueFn` may itself call `advance`, which keeps walking free transitions).
/// Never abandons the action that just resolved — its ship/log update is already
/// baked into `job'` either way.
let private settleOrDefer
    (job': JobState)
    (settledEffects: Effect list)
    (continueFn: JobState -> JobState * Effect list)
    : JobState * Effect list =
    if job'.cancelPending then
        { job' with
            status = Cancelled
            cancelPending = false
            pausePending = false
            log = "Programm gestoppt." :: job'.log },
        settledEffects @ [ JobCancelled job'.jobId ]
    elif job'.pausePending then
        { job' with
            status = Paused job'.status
            pausePending = false
            log = "Programm pausiert." :: job'.log },
        settledEffects
    else
        continueFn job'

// --- The pure step core --------------------------------------------------------------

let rec private advance (clock: Clock) (job: JobState) : JobState * Effect list =
    match job.status with
    | Running ->
        match currentInstruction job with
        | None -> completeOrPopFrame clock job
        | Some instr -> advanceInstruction clock job instr
    | _ -> job, []

/// The top frame's position is exhausted (§9d, Milestone 9): if it's the only frame,
/// the whole job is `Completed`; otherwise it's a custom-block call returning —
/// pop it, evaluate its own `returnExpr` against its own (now-discarded) locals, bind
/// the result into the caller's `resultTarget` local (if any), and resume the caller
/// from its call site, which `advanceJobPosition` moves past.
and private completeOrPopFrame (clock: Clock) (job: JobState) : JobState * Effect list =
    match popFrame job with
    | None -> { job with status = Completed }, [ JobCompleted job.jobId ]
    | Some(poppedFrame, job') ->
        let job'' =
            match poppedFrame.returnTarget, job.program.customBlocks.TryFind poppedFrame.scope with
            | Some target, Some cb ->
                match cb.returnExpr with
                | Some expr -> setLocal target (Eval.eval poppedFrame.locals expr) job'
                | None -> job'
            | _ -> job'

        advance clock (job'' |> advanceJobPosition)

and private advanceInstruction (clock: Clock) (job: JobState) (instr: Instruction) : JobState * Effect list =
    let locals = currentLocals job

    match instr with
    | SetVariable(_, name, expr) ->
        let v = Eval.eval locals expr
        advance clock (job |> setLocal name v |> advanceJobPosition)
    | ChangeVariable(_, name, deltaExpr) ->
        let delta = Eval.eval locals deltaExpr |> Eval.asFloat

        let current =
            locals |> Map.tryFind name |> Option.map Eval.asFloat |> Option.defaultValue 0.0

        advance clock (job |> setLocal name (VNumber(current + delta)) |> advanceJobPosition)
    | ListSet(_, name, where, indexOpt, valueExpr) ->
        let items = locals |> Map.find name |> Eval.asList
        let indexValue = indexOpt |> Option.map (Eval.eval locals)
        let idx = Eval.resolveListIndex items where indexValue
        let value = Eval.eval locals valueExpr
        let updated = Eval.setListAt items idx value
        advance clock (job |> setLocal name (VList updated) |> advanceJobPosition)
    | ShowMessage(_, textExpr) ->
        let text = Eval.eval locals textExpr |> Eval.asString
        advance clock ({ job with log = text :: job.log } |> advanceJobPosition)
    | Wait(_, secondsExpr) ->
        let secs = Eval.eval locals secondsExpr |> Eval.asFloat
        let until = clock.now().AddSeconds(secs)
        let job' = job |> advanceJobPosition
        { job' with status = WaitingForArrival until }, [ StartWait(job.jobId, until, ArrivalWait) ]
    | If(_, branches, elseBranch) ->
        let ifBlockId = blockIdOf instr

        match branches |> List.tryFindIndex (fun (cond, _) -> Eval.eval locals cond |> Eval.asBool) with
        | Some branchIndex -> advance clock (job |> pushBodyJob (ThenBranch(ifBlockId, branchIndex)) None)
        | None ->
            match elseBranch with
            | Some _ -> advance clock (job |> pushBodyJob (ElseBranch ifBlockId) None)
            | None -> advance clock (job |> advanceJobPosition)
    | Repeat(blockId, countExpr, _) ->
        let n = Eval.eval locals countExpr |> Eval.asInt

        if n <= 0 then
            advance clock (job |> advanceJobPosition)
        else
            advance clock (job |> pushBodyJob (RepeatBody blockId) (Some(RepeatState n)))
    | WhileUntil(blockId, mode, cond, _) ->
        let truthy = Eval.eval locals cond |> Eval.asBool

        let shouldEnter =
            match mode with
            | While -> truthy
            | Until -> not truthy

        if shouldEnter then
            advance clock (job |> pushBodyJob (WhileUntilBody blockId) None)
        else
            advance clock (job |> advanceJobPosition)
    | ForEach(blockId, variable, listExpr, _) ->
        let items = Eval.eval locals listExpr |> Eval.asList

        match items with
        | [] -> advance clock (job |> advanceJobPosition)
        | first :: _ ->
            let job' =
                job
                |> pushBodyJob (ForEachBody blockId) (Some(ForEachState(items, 0)))
                |> setLocal variable first

            advance clock job'
    | Parallel(blockId, branches) -> startParallel clock job blockId branches
    | WithShip(blockId, shipExpr, _, elseBranch) ->
        let shipSymbol = Eval.eval locals shipExpr |> Eval.asString
        { job with status = WaitingForShipLock(shipSymbol, blockId, elseBranch.IsSome) },
        [ AcquireShipScope(job.jobId, blockId, shipSymbol, elseBranch.IsSome) ]
    | Break _ -> advance clock (job |> mapTopFrame (breakLoop job.program))
    | Continue _ -> advance clock (job |> mapTopFrame (continueLoop job.program))
    | ExitShipScope _ ->
        match job.currentShipStack with
        | (shipSymbol, true) :: rest ->
            let job' =
                { job with
                    currentShipStack = rest
                    dynamicShipLocks = job.dynamicShipLocks |> List.filter ((<>) shipSymbol) }
                |> advanceJobPosition

            let job'', effects = advance clock job'
            job'', ReleaseShipScope(job.jobId, shipSymbol) :: effects
        | (_, false) :: rest ->
            advance clock ({ job with currentShipStack = rest } |> advanceJobPosition)
        | [] -> advance clock (job |> advanceJobPosition)
    | InfoRead(blockId, infoType, args, resultTarget) -> emitInfoRead job blockId infoType args resultTarget
    | CallCustomBlock(_, customBlockId, arguments, resultTarget) ->
        match job.program.customBlocks.TryFind customBlockId with
        | None ->
            let msg = $"Eigener Block \"{customBlockId}\" wurde nicht gefunden."
            { job with status = Failed msg }, [ JobFailed(job.jobId, msg) ]
        | Some cb ->
            let boundArgs = arguments |> Map.map (fun _ expr -> Eval.eval locals expr)

            let calleeFrame =
                { scope = customBlockId
                  position = [ { bodyRef = CustomBlockBody customBlockId; index = 0; loopState = None } ]
                  locals = boundArgs
                  returnTarget = resultTarget }

            // The caller's own position is deliberately left unadvanced — it still
            // points at this `CallCustomBlock` instruction, so `completeOrPopFrame`'s
            // `advanceJobPosition` (once the callee's frame pops) moves past it,
            // exactly like an in-flight `ApiAction`/`InfoRead` leaves its position
            // unadvanced until its result comes back.
            advance clock (job |> pushFrame calleeFrame)
    | ApiAction(blockId, actionType, args) -> emitApiAction job blockId actionType args

and private startParallel
    (clock: Clock)
    (job: JobState)
    (blockId: string)
    (branches: Instruction list list)
    : JobState * Effect list =
    let activeBranches =
        branches
        |> List.mapi (fun idx _ ->
            let branchId = $"{blockId}:{idx}"
            let frame =
                { scope = branchId
                  position = [ { bodyRef = ParallelBranchBody(blockId, idx); index = 0; loopState = None } ]
                  locals = currentLocals job
                  returnTarget = None }

            { branchId = branchId
              job =
                { job with
                    status = Running
                    stack = [ frame ]
                    currentShipStack = job.currentShipStack
                    dynamicShipLocks = []
                    parallelBranches = []
                    pausePending = false
                    cancelPending = false }
              failure = None
              completed = false })

    { (job |> advanceJobPosition) with parallelBranches = activeBranches } |> driveParallelBranches clock

and private settleBranch (branch: ParallelBranch) : ParallelBranch =
    match branch.job.status with
    | Completed -> { branch with completed = true }
    | Failed msg -> { branch with completed = true; failure = Some msg }
    | Cancelled -> { branch with completed = true; failure = Some "Parallel-Zweig wurde gestoppt." }
    | _ -> branch

and private finalizeParallelIfSettled (clock: Clock) (job: JobState) (effects: Effect list) : JobState * Effect list =
    if job.parallelBranches.IsEmpty then
        job, effects
    elif job.parallelBranches |> List.forall (fun b -> b.completed) then
        let failures = job.parallelBranches |> List.choose (fun b -> b.failure)
        let released =
            job.parallelBranches
            |> List.collect (fun b -> b.job.dynamicShipLocks)
            |> List.distinct
            |> List.map (fun sym -> ReleaseShipScope(job.jobId, sym))

        if failures.IsEmpty then
            let job', more = advance clock { job with parallelBranches = [] }
            job', effects @ released @ more
        else
            let msg = "Mindestens ein Parallel-Zweig ist fehlgeschlagen: " + (String.concat "; " failures)
            { job with status = Failed msg; parallelBranches = [] }, effects @ released @ [ JobFailed(job.jobId, msg) ]
    else
        job, effects

and private siblingHoldsShip (branchId: string) (shipSymbol: string) (branches: ParallelBranch list) : bool =
    branches
    |> List.exists (fun branch ->
        branch.branchId <> branchId
        && not branch.completed
        && branch.job.currentShipStack |> List.exists (fun (sym, _) -> sym = shipSymbol))

and private driveBranch
    (clock: Clock)
    (event: SchedulerEvent)
    (parentBranches: ParallelBranch list)
    (branch: ParallelBranch)
    : ParallelBranch * Effect list =
    if branch.completed then
        branch, []
    else
        let stepLeaf branchJob =
            match event with
            | WakeTick ->
                match branchJob.status with
                | Running -> advance clock branchJob
                | WaitingForArrival until when clock.now() >= until -> advance clock { branchJob with status = Running }
                | WaitingForCooldown until when clock.now() >= until -> advance clock { branchJob with status = Running }
                | WaitingForShipLock(shipSymbol, blockId, hasElse) ->
                    branchJob, [ AcquireShipScope(branchJob.jobId, blockId, shipSymbol, hasElse) ]
                | _ -> branchJob, []
            | PauseRequested ->
                match branchJob.status with
                | Running
                | WaitingForArrival _
                | WaitingForCooldown _
                | WaitingForShipLock _ -> { branchJob with status = Paused branchJob.status; log = "Programm pausiert." :: branchJob.log }, []
                | AwaitingApiResponse _
                | Reconciling _
                | AwaitingInfoResponse _ ->
                    { branchJob with
                        pausePending = true
                        log = "Pause angefordert – wird nach Bestätigung der aktuellen Aktion wirksam." :: branchJob.log },
                    []
                | _ -> branchJob, []
            | ResumeRequested ->
                match branchJob.status with
                | Paused Running -> advance clock { branchJob with status = Running }
                | Paused prior -> { branchJob with status = prior }, []
                | _ -> branchJob, []
            | CancelRequested ->
                match branchJob.status with
                | Running
                | WaitingForArrival _
                | WaitingForCooldown _
                | WaitingForShipLock _
                | Paused _ -> { branchJob with status = Cancelled; log = "Programm gestoppt." :: branchJob.log }, [ JobCancelled branchJob.jobId ]
                | AwaitingApiResponse _
                | Reconciling _
                | AwaitingInfoResponse _ ->
                    { branchJob with
                        cancelPending = true
                        log = "Stopp angefordert – wird nach Bestätigung der aktuellen Aktion wirksam." :: branchJob.log },
                    []
                | _ -> branchJob, []
            | _ -> branchJob, []

        let stepBranch branchJob =
            let job', effects = stepLeaf branchJob

            match
                effects
                |> List.tryPick (function
                    | AcquireShipScope(_, blockId, shipSymbol, _) when siblingHoldsShip branch.branchId shipSymbol parentBranches ->
                        Some(blockId, shipSymbol)
                    | _ -> None)
            with
            | Some(blockId, shipSymbol) ->
                let reason = "wird bereits von einem anderen Parallel-Zweig gesteuert"
                let job'', more =
                    match job'.status with
                    | WaitingForShipLock(waitingFor, waitingBlockId, hasElse) when waitingFor = shipSymbol && waitingBlockId = blockId ->
                        if hasElse then
                            let next =
                                { job' with
                                    status = Running
                                    log = $"Schiff {shipSymbol} ist nicht verfügbar: {reason}" :: job'.log }
                                |> advanceJobPosition
                                |> pushBodyJob (WithShipElseBody blockId) None

                            advance clock next
                        else
                            let msg = $"Schiff {shipSymbol} ist nicht verfügbar: {reason}"
                            { job' with status = Failed msg }, [ JobFailed(job'.jobId, msg) ]
                    | _ -> job', []

                { branch with job = job'' } |> settleBranch, mapBranchEffects branch.branchId more
            | None ->
                let effects' = effects |> List.choose (mapBranchEffect branch.branchId)
                { branch with job = job' } |> settleBranch, effects'

        match event with
        | BranchApiResponseReceived(_, branchId, attempt, result) when branchId = branch.branchId ->
            let job', effects = handleApiResponse clock branch.job attempt result
            { branch with job = job' } |> settleBranch, mapBranchEffects branch.branchId effects
        | BranchApiResponseReceived _ when not branch.job.parallelBranches.IsEmpty ->
            let job', effects = driveParallelBranchesWithEvent clock branch.job event
            { branch with job = job' } |> settleBranch, mapBranchEffects branch.branchId effects
        | BranchShipScopeAcquired(_, branchId, blockId, shipSymbol, snapshot) when branchId = branch.branchId ->
            let job', effects =
                match branch.job.status with
                | WaitingForShipLock(waitingFor, waitingBlockId, _) when waitingFor = shipSymbol && waitingBlockId = blockId ->
                    let isPrimary = branch.job.shipSymbol = Some shipSymbol
                    let job' =
                        { branch.job with
                            status = Running
                            currentShipStack = (shipSymbol, not isPrimary) :: branch.job.currentShipStack
                            dynamicShipLocks = if isPrimary then branch.job.dynamicShipLocks else shipSymbol :: branch.job.dynamicShipLocks
                            lastKnownShip = Some snapshot
                            log = $"Steuere jetzt Schiff {shipSymbol}." :: branch.job.log }
                        |> pushBodyJob (WithShipBody blockId) None

                    advance clock job'
                | _ -> branch.job, []

            { branch with job = job' } |> settleBranch, mapBranchEffects branch.branchId effects
        | BranchShipScopeAcquired _ when not branch.job.parallelBranches.IsEmpty ->
            let job', effects = driveParallelBranchesWithEvent clock branch.job event
            { branch with job = job' } |> settleBranch, mapBranchEffects branch.branchId effects
        | BranchShipScopeUnavailable(_, branchId, blockId, shipSymbol, reason) when branchId = branch.branchId ->
            let job', effects =
                match branch.job.status with
                | WaitingForShipLock(waitingFor, waitingBlockId, hasElse) when waitingFor = shipSymbol && waitingBlockId = blockId ->
                    if hasElse then
                        let job' =
                            { branch.job with
                                status = Running
                                log = $"Schiff {shipSymbol} ist nicht verfügbar: {reason}" :: branch.job.log }
                            |> advanceJobPosition
                            |> pushBodyJob (WithShipElseBody blockId) None

                        advance clock job'
                    else
                        let msg = $"Schiff {shipSymbol} ist nicht verfügbar: {reason}"
                        { branch.job with status = Failed msg }, [ JobFailed(branch.job.jobId, msg) ]
                | _ -> branch.job, []

            { branch with job = job' } |> settleBranch, mapBranchEffects branch.branchId effects
        | BranchShipScopeUnavailable _ when not branch.job.parallelBranches.IsEmpty ->
            let job', effects = driveParallelBranchesWithEvent clock branch.job event
            { branch with job = job' } |> settleBranch, mapBranchEffects branch.branchId effects
        | WakeTick -> stepBranch branch.job
        | PauseRequested
        | ResumeRequested
        | CancelRequested -> stepBranch branch.job
        | _ -> branch, []

and private driveParallelBranchesWithEvent (clock: Clock) (job: JobState) (event: SchedulerEvent) : JobState * Effect list =
    let branches, effects =
        (([], []), job.parallelBranches)
        ||> List.fold (fun (branchesAcc, effectsAcc) branch ->
            let branch', effects = driveBranch clock event job.parallelBranches branch
            branchesAcc @ [ branch' ], effectsAcc @ effects)

    finalizeParallelIfSettled clock { job with parallelBranches = branches } effects

and private driveParallelBranches (clock: Clock) (job: JobState) : JobState * Effect list =
    driveParallelBranchesWithEvent clock job WakeTick

/// Stops the free-transition walk for an info-read block (§8/§14, Milestone 9/Part
/// B) — a GET is always safe to retry, so unlike `emitApiAction` there is no baseline
/// to capture, only the evaluated string args to carry through a retry unchanged.
and private emitInfoRead
    (job: JobState)
    (_blockId: string)
    (infoType: string)
    (args: Map<string, Expr>)
    (resultTarget: string)
    : JobState * Effect list =
    let locals = currentLocals job
    let stringArgs = args |> Map.map (fun _ expr -> Eval.eval locals expr |> Eval.asString)

    // Ship-agnostic info types (getFleetInfo/getWaypoints/getMarket/getShipyard/
    // getContracts/getCredits) never read `job.shipSymbol` server-side, but
    // getShipInfo/getCargo/getFuel do — same gate shape as `emitApiAction`'s.
    let shipSymbol = currentShipSymbol job

    if Validator.shipScopedInfoTypes.Contains infoType && shipSymbol.IsNone then
        let msg = "Kein Schiff ausgewählt."
        { job with status = Failed msg }, [ JobFailed(job.jobId, msg) ]
    else
        { job with status = AwaitingInfoResponse(0, infoType, stringArgs, resultTarget) },
        [ QueueInfoRead(job.jobId, shipSymbol, infoType, stringArgs, 0, resultTarget) ]

/// Stops the free-transition walk: captures the pre-call baseline from
/// `job.lastKnownShip` (data already in hand, §13), emits exactly one `QueueApiCall`.
and private emitApiAction
    (job: JobState)
    (blockId: string)
    (actionType: string)
    (args: Map<string, Expr>)
    : JobState * Effect list =
    let locals = currentLocals job

    let requireArg (name: string) =
        match args.TryFind name with
        | Some expr -> Eval.eval locals expr
        | None -> failwith $"Fehlendes Argument \"{name}\" für Block {blockId}."

    let awaiting (baseline: ActionBaseline) (action: QueuedAction) (logText: string) =
        // `LogMessage` must run *before* `QueueApiCall` in this list: the shell
        // (`JobRunner.applyEffects`) applies effects strictly in order, and
        // `QueueApiCall` recursively drives the job all the way to its next
        // settled state (persisting any further log lines) before this function
        // returns — if `LogMessage` ran second, this action's *start* message
        // would always end up prepended on top of whatever the action already
        // logged as its outcome, corrupting "latest log line" displays (a real
        // bug found via live verification, Milestone 9/Part A).
        { job with status = AwaitingApiResponse(0, action, baseline) },
        [ LogMessage(job.jobId, logText); QueueApiCall(job.jobId, currentShipSymbol job, action, 0) ]

    // `acceptContract`/`fulfillContract`/`purchaseShip` never read a ship snapshot (they
    // reconcile via `AcceptContractBaseline`/`FulfillContractBaseline`/`FleetBaseline`
    // instead, §13's explicit allowance) — so unlike every other action type, they must
    // not be gated behind `lastKnownShip`. A ship-agnostic job (§14 follow-up) can reach
    // any of these with `lastKnownShip = None`, and that's fine.
    match actionType with
    | "acceptContract" ->
        let contractId = requireArg "contractId" |> Eval.asString
        awaiting (AcceptContractBaseline contractId) (DoAcceptContract contractId) $"Nehme Auftrag {contractId} an..."
    | "fulfillContract" ->
        let contractId = requireArg "contractId" |> Eval.asString
        awaiting (FulfillContractBaseline contractId) (DoFulfillContract contractId) $"Schließe Auftrag {contractId} ab..."
    | "purchaseShip" ->
        let shipType = requireArg "shipType" |> Eval.asString
        let waypointSymbol = requireArg "waypointSymbol" |> Eval.asString
        let shipCountBefore = job.lastKnownFleetShipCount |> Option.defaultValue 0

        awaiting
            (FleetBaseline shipCountBefore)
            (DoPurchaseShip(shipType, waypointSymbol))
            $"Kaufe ein Schiff vom Typ {shipType}..."
    | shipScopedActionType ->
        match job.lastKnownShip with
        | None ->
            let msg = "Kein Schiff ausgewählt."
            { job with status = Failed msg }, [ JobFailed(job.jobId, msg) ]
        | Some ship ->
            match shipScopedActionType with
            | "navigate" ->
                let dest = requireArg "destination" |> Eval.asString
                awaiting (NavigateBaseline dest) (DoNavigate dest) $"Fliege zu {dest}..."
            | "orbit" -> awaiting (DockOrbitBaseline "IN_ORBIT") DoOrbit "Verlasse den Andockplatz..."
            | "dock" -> awaiting (DockOrbitBaseline "DOCKED") DoDock "Docke an..."
            | "extract" ->
                awaiting (ExtractBaseline(ship.cooldownExpiration, ship.cargoUnits)) DoExtract "Baue Rohstoffe ab..."
            | "buyGood" ->
                let tradeSymbol = requireArg "tradeSymbol" |> Eval.asString
                let units = requireArg "units" |> Eval.asInt
                awaiting (CargoBaseline ship.cargoUnits) (DoBuy(tradeSymbol, units)) $"Kaufe {units}x {tradeSymbol}..."
            | "sellGood" ->
                let tradeSymbol = requireArg "tradeSymbol" |> Eval.asString
                let units = requireArg "units" |> Eval.asInt
                awaiting (CargoBaseline ship.cargoUnits) (DoSell(tradeSymbol, units)) $"Verkaufe {units}x {tradeSymbol}..."
            | "survey" -> awaiting (SurveyBaseline ship.cooldownExpiration) DoSurvey "Führe eine Vermessung durch..."
            | "deliverContract" ->
                let contractId = requireArg "contractId" |> Eval.asString
                let tradeSymbol = requireArg "tradeSymbol" |> Eval.asString
                let units = requireArg "units" |> Eval.asInt

                awaiting
                    (CargoBaseline ship.cargoUnits)
                    (DoDeliverContract(contractId, tradeSymbol, units))
                    $"Liefere {units}x {tradeSymbol} für Auftrag {contractId}..."
            | "refuel" -> awaiting (FuelBaseline ship.fuelCurrent) DoRefuel "Tanke auf..."
            | "negotiateContract" ->
                let contractCountBefore = job.lastKnownContractCount |> Option.defaultValue 0

                awaiting
                    (ContractsCountBaseline contractCountBefore)
                    DoNegotiateContract
                    "Verhandle neuen Auftrag..."
            | "jettison" ->
                let tradeSymbol = requireArg "tradeSymbol" |> Eval.asString
                let units = requireArg "units" |> Eval.asInt
                awaiting (CargoBaseline ship.cargoUnits) (DoJettison(tradeSymbol, units)) $"Wirf {units}x {tradeSymbol} ab..."
            | "jump" ->
                let waypointSymbol = requireArg "waypointSymbol" |> Eval.asString
                awaiting (NavigateBaseline waypointSymbol) (DoJump waypointSymbol) $"Springe zu {waypointSymbol}..."
            | "warp" ->
                let waypointSymbol = requireArg "waypointSymbol" |> Eval.asString
                awaiting (NavigateBaseline waypointSymbol) (DoWarp waypointSymbol) $"Warpe zu {waypointSymbol}..."
            | "transferCargo" ->
                let tradeSymbol = requireArg "tradeSymbol" |> Eval.asString
                let units = requireArg "units" |> Eval.asInt
                let targetShipSymbol = requireArg "targetShipSymbol" |> Eval.asString

                awaiting
                    (CargoBaseline ship.cargoUnits)
                    (DoTransferCargo(tradeSymbol, units, targetShipSymbol))
                    $"Übertrage {units}x {tradeSymbol} auf {targetShipSymbol}..."
            | "siphon" ->
                awaiting (ExtractBaseline(ship.cooldownExpiration, ship.cargoUnits)) DoSiphon "Entnehme Gas..."
            | "scrapShip" ->
                let shipCountBefore = job.lastKnownFleetShipCount |> Option.defaultValue 0
                awaiting (FleetBaseline shipCountBefore) DoScrapShip "Verschrotte Schiff..."
            | "repair" -> awaiting (FuelBaseline ship.fuelCurrent) DoRepair "Repariere Schiff..."
            | "refine" ->
                let produce = requireArg "produce" |> Eval.asString
                awaiting (CargoBaseline ship.cargoUnits) (DoRefine produce) $"Veredle zu {produce}..."
            | "scanShips" -> awaiting (SurveyBaseline ship.cooldownExpiration) DoScanShips "Scanne Schiffe..."
            | "scanSystems" -> awaiting (SurveyBaseline ship.cooldownExpiration) DoScanSystems "Scanne Systeme..."
            | "scanWaypoints" -> awaiting (SurveyBaseline ship.cooldownExpiration) DoScanWaypoints "Scanne Wegpunkte..."
            | "installModule" ->
                let moduleSymbol = requireArg "moduleSymbol" |> Eval.asString
                awaiting (DockOrbitBaseline ship.navStatus) (DoInstallModule moduleSymbol) $"Installiere Modul {moduleSymbol}..."
            | "removeModule" ->
                let moduleSymbol = requireArg "moduleSymbol" |> Eval.asString
                awaiting (DockOrbitBaseline ship.navStatus) (DoRemoveModule moduleSymbol) $"Entferne Modul {moduleSymbol}..."
            | "installMount" ->
                let mountSymbol = requireArg "mountSymbol" |> Eval.asString
                awaiting (DockOrbitBaseline ship.navStatus) (DoInstallMount mountSymbol) $"Installiere Aufsatz {mountSymbol}..."
            | "removeMount" ->
                let mountSymbol = requireArg "mountSymbol" |> Eval.asString
                awaiting (DockOrbitBaseline ship.navStatus) (DoRemoveMount mountSymbol) $"Entferne Aufsatz {mountSymbol}..."
            | "createChart" -> awaiting (SurveyBaseline ship.cooldownExpiration) DoCreateChart "Erstelle Karte..."
            | "extractWithSurvey" ->
                let surveySignature = requireArg "surveySignature" |> Eval.asString

                awaiting
                    (ExtractBaseline(ship.cooldownExpiration, ship.cargoUnits))
                    (DoExtractWithSurvey surveySignature)
                    "Baue mit Vermessung ab..."
            | "supplyConstruction" ->
                let waypointSymbol = requireArg "waypointSymbol" |> Eval.asString
                let tradeSymbol = requireArg "tradeSymbol" |> Eval.asString
                let units = requireArg "units" |> Eval.asInt

                awaiting
                    (CargoBaseline ship.cargoUnits)
                    (DoSupplyConstruction(waypointSymbol, tradeSymbol, units))
                    $"Liefere {units}x {tradeSymbol} für Bau..."
            | "patchShipNav" ->
                let flightMode = requireArg "flightMode" |> Eval.asString
                awaiting (FlightModeBaseline flightMode) (DoPatchShipNav flightMode) $"Setze Flugmodus auf {flightMode}..."
            | other ->
                let msg = $"Unbekannte Aktion: {other}"
                { job with status = Failed msg }, [ JobFailed(job.jobId, msg) ]

/// Handles a response to an in-flight API call or a reconciliation fetch. Ambiguous
/// failures are modeled as two explicit hops matching §13's own 3-step recipe:
/// 1. `ApiAmbiguous` while `AwaitingApiResponse` -> `Reconciling` + one
///    `ReconcileShipState` effect (the shell's job to actually call `GetShip`).
/// 2. `ReconciliationShip` while `Reconciling` -> `reconcile` decides "already
///    happened" (treated like the success path, using the fresh snapshot directly)
///    vs "not yet" (a fresh attempt, same baseline and same original action).
and private handleApiResponse (clock: Clock) (job: JobState) (attemptNumber: int) (result: ApiResult) : JobState * Effect list =
    match job.status, result with
    | AwaitingApiResponse(attempt, action, baseline), ApiAmbiguous _ when attempt = attemptNumber ->
        let reconcileEffect =
            match baseline with
            | AcceptContractBaseline contractId -> ReconcileContractState(job.jobId, contractId, attempt, CheckAccepted)
            | FulfillContractBaseline contractId -> ReconcileContractState(job.jobId, contractId, attempt, CheckFulfilled)
            | FleetBaseline _ -> ReconcileFleetState(job.jobId, attempt)
            | ContractsCountBaseline _ -> ReconcileContractsState(job.jobId, attempt)
            | _ -> ReconcileShipState(job.jobId, currentShipSymbol job, attempt)

        { job with status = Reconciling(attempt, action, baseline) },
        [ reconcileEffect
          LogMessage(job.jobId, "Prüfe, ob die Aktion schon stattgefunden hat...") ]
    | AwaitingApiResponse(attempt, _, _), ApiFailed msg when attempt = attemptNumber ->
        { job with status = Failed msg }, [ JobFailed(job.jobId, msg) ]
    | AwaitingApiResponse(attempt, _, _), NavigateOk(navStatus, navWaypoint, arrival) when attempt = attemptNumber ->
        let ship' =
            { (job.lastKnownShip |> Option.get) with
                navStatus = navStatus
                navWaypoint = navWaypoint
                navArrival = Some arrival }

        let until = DateTimeOffset.Parse arrival

        settleOrDefer
            { (job |> advanceJobPosition) with
                lastKnownShip = Some ship'
                status = WaitingForArrival until
                log = "Unterwegs..." :: job.log }
            [ StartWait(job.jobId, until, ArrivalWait) ]
            (fun j -> j, [ StartWait(job.jobId, until, ArrivalWait) ])
    | AwaitingApiResponse(attempt, _, _), NavResultOk(navStatus, navWaypoint) when attempt = attemptNumber ->
        let ship' =
            { (job.lastKnownShip |> Option.get) with
                navStatus = navStatus
                navWaypoint = navWaypoint }

        let logText = if navStatus = "IN_ORBIT" then "Habe den Andockplatz verlassen." else "Angedockt."

        settleOrDefer
            { job with
                lastKnownShip = Some ship'
                status = Running
                log = logText :: job.log }
            []
            (fun j -> advance clock (advanceJobPosition j))
    | AwaitingApiResponse(attempt, _, _), ExtractOk(cooldownExpiration, cargoUnits, cargoInventory, yieldSymbol, yieldUnits) when
        attempt = attemptNumber
        ->
        let ship' =
            { (job.lastKnownShip |> Option.get) with
                cargoUnits = cargoUnits
                cargoInventory = cargoInventory
                cooldownExpiration = Some cooldownExpiration }

        let until = DateTimeOffset.Parse cooldownExpiration

        settleOrDefer
            { (job |> advanceJobPosition) with
                lastKnownShip = Some ship'
                status = WaitingForCooldown until
                log = $"Abgebaut: {yieldUnits}x {yieldSymbol}." :: job.log }
            [ StartWait(job.jobId, until, CooldownWait) ]
            (fun j -> j, [ StartWait(job.jobId, until, CooldownWait) ])
    | AwaitingApiResponse(attempt, _, _), TradeOk(cargoUnits, cargoInventory, transactionType, tradeSymbol, units, totalPrice) when
        attempt = attemptNumber
        ->
        let ship' =
            { (job.lastKnownShip |> Option.get) with
                cargoUnits = cargoUnits
                cargoInventory = cargoInventory }

        let verb = if transactionType = "SELL" then "Verkauft" else "Gekauft"

        settleOrDefer
            { job with
                lastKnownShip = Some ship'
                status = Running
                log = $"{verb}: {units}x {tradeSymbol} für {totalPrice} Credits." :: job.log }
            []
            (fun j -> advance clock (advanceJobPosition j))
    | AwaitingApiResponse(attempt, _, _), SurveyOk cooldownExpiration when attempt = attemptNumber ->
        let ship' =
            { (job.lastKnownShip |> Option.get) with
                cooldownExpiration = Some cooldownExpiration }

        let until = DateTimeOffset.Parse cooldownExpiration

        settleOrDefer
            { (job |> advanceJobPosition) with
                lastKnownShip = Some ship'
                status = WaitingForCooldown until
                log = "Vermessung abgeschlossen." :: job.log }
            [ StartWait(job.jobId, until, CooldownWait) ]
            (fun j -> j, [ StartWait(job.jobId, until, CooldownWait) ])
    | AwaitingApiResponse(attempt, _, _), DeliverOk(cargoUnits, cargoInventory, contractFulfilled) when attempt = attemptNumber ->
        let ship' =
            { (job.lastKnownShip |> Option.get) with
                cargoUnits = cargoUnits
                cargoInventory = cargoInventory }

        let logText =
            if contractFulfilled then
                "Lieferung abgeschlossen: Auftrag erfüllt."
            else
                "Lieferung abgeschlossen."

        settleOrDefer
            { job with
                lastKnownShip = Some ship'
                status = Running
                log = logText :: job.log }
            []
            (fun j -> advance clock (advanceJobPosition j))
    | AwaitingApiResponse(attempt, _, _), AcceptContractOk accepted when attempt = attemptNumber ->
        let logText =
            if accepted then
                "Auftrag angenommen."
            else
                "Auftrag konnte nicht angenommen werden."

        settleOrDefer { job with status = Running; log = logText :: job.log } [] (fun j -> advance clock (advanceJobPosition j))
    | AwaitingApiResponse(attempt, _, _), FulfillContractOk fulfilled when attempt = attemptNumber ->
        let logText =
            if fulfilled then
                "Auftrag abgeschlossen."
            else
                "Auftrag konnte nicht abgeschlossen werden."

        settleOrDefer { job with status = Running; log = logText :: job.log } [] (fun j -> advance clock (advanceJobPosition j))
    | AwaitingApiResponse(attempt, _, baseline), NegotiateContractOk contractId when attempt = attemptNumber ->
        let contractCountAfter =
            match baseline with
            | ContractsCountBaseline countBefore -> countBefore + 1
            | _ -> (job.lastKnownContractCount |> Option.defaultValue 0) + 1

        settleOrDefer
            { job with
                lastKnownContractCount = Some contractCountAfter
                status = Running
                log = $"Neuer Auftrag verhandelt: {contractId}." :: job.log }
            []
            (fun j -> advance clock (advanceJobPosition j))
    | AwaitingApiResponse(attempt, _, _), PurchaseShipOk(newShipSymbol, fleetShipCount) when attempt = attemptNumber ->
        settleOrDefer
            { job with
                lastKnownFleetShipCount = Some fleetShipCount
                status = Running
                log = $"Neues Schiff gekauft: {newShipSymbol}." :: job.log }
            []
            (fun j -> advance clock (advanceJobPosition j))
    | AwaitingApiResponse(attempt, _, _), RefuelOk fuelCurrent when attempt = attemptNumber ->
        let ship' =
            { (job.lastKnownShip |> Option.get) with
                fuelCurrent = fuelCurrent }

        settleOrDefer
            { job with
                lastKnownShip = Some ship'
                status = Running
                log = "Aufgetankt." :: job.log }
            []
            (fun j -> advance clock (advanceJobPosition j))
    | AwaitingApiResponse(attempt, _, _), ActionOk when attempt = attemptNumber ->
        settleOrDefer { job with status = Running; log = "Erledigt." :: job.log } [] (fun j -> advance clock (advanceJobPosition j))
    | AwaitingApiResponse(attempt, _, _), ScrapOk fleetShipCount when attempt = attemptNumber ->
        settleOrDefer
            { job with
                lastKnownFleetShipCount = Some fleetShipCount
                lastKnownShip = None
                status = Running
                log = "Schiff verschrottet." :: job.log }
            []
            (fun j -> advance clock (advanceJobPosition j))
    | Reconciling(attempt, action, baseline), ReconciliationShip current when attempt = attemptNumber ->
        let job' = { job with lastKnownShip = Some current }

        if reconcile baseline current then
            match baseline with
            | NavigateBaseline _ ->
                match current.navArrival with
                | Some arrival when DateTimeOffset.Parse arrival > clock.now() ->
                    let until = DateTimeOffset.Parse arrival

                    settleOrDefer
                        { (job' |> advanceJobPosition) with
                            status = WaitingForArrival until
                            log = "Unterwegs (bereits bestätigt)." :: job'.log }
                        [ StartWait(job.jobId, until, ArrivalWait) ]
                        (fun j -> j, [ StartWait(job.jobId, until, ArrivalWait) ])
                | _ ->
                    settleOrDefer
                        { job' with
                            status = Running
                            log = "Angekommen (bereits bestätigt)." :: job'.log }
                        []
                        (fun j -> advance clock (advanceJobPosition j))
            | DockOrbitBaseline _ ->
                settleOrDefer
                    { job' with
                        status = Running
                        log = "Bestätigt (bereits erledigt)." :: job'.log }
                    []
                    (fun j -> advance clock (advanceJobPosition j))
            | FlightModeBaseline _ ->
                settleOrDefer
                    { job' with
                        status = Running
                        log = "Bestätigt: Flugmodus wurde bereits gesetzt." :: job'.log }
                    []
                    (fun j -> advance clock (advanceJobPosition j))
            | CargoBaseline _ ->
                settleOrDefer
                    { job' with
                        status = Running
                        log = "Bestätigt: Handel hat bereits stattgefunden." :: job'.log }
                    []
                    (fun j -> advance clock (advanceJobPosition j))
            | ExtractBaseline _ ->
                match current.cooldownExpiration with
                | Some expiration when DateTimeOffset.Parse expiration > clock.now() ->
                    let until = DateTimeOffset.Parse expiration

                    settleOrDefer
                        { (job' |> advanceJobPosition) with
                            status = WaitingForCooldown until
                            log = "Bestätigt: Abbau hat bereits stattgefunden." :: job'.log }
                        [ StartWait(job.jobId, until, CooldownWait) ]
                        (fun j -> j, [ StartWait(job.jobId, until, CooldownWait) ])
                | _ ->
                    settleOrDefer
                        { job' with
                            status = Running
                            log = "Bestätigt: Abbau hat bereits stattgefunden." :: job'.log }
                        []
                        (fun j -> advance clock (advanceJobPosition j))
            | SurveyBaseline _ ->
                match current.cooldownExpiration with
                | Some expiration when DateTimeOffset.Parse expiration > clock.now() ->
                    let until = DateTimeOffset.Parse expiration

                    settleOrDefer
                        { (job' |> advanceJobPosition) with
                            status = WaitingForCooldown until
                            log = "Bestätigt: Vermessung hat bereits stattgefunden." :: job'.log }
                        [ StartWait(job.jobId, until, CooldownWait) ]
                        (fun j -> j, [ StartWait(job.jobId, until, CooldownWait) ])
                | _ ->
                    settleOrDefer
                        { job' with
                            status = Running
                            log = "Bestätigt: Vermessung hat bereits stattgefunden." :: job'.log }
                        []
                        (fun j -> advance clock (advanceJobPosition j))
            | FuelBaseline _ ->
                settleOrDefer
                    { job' with
                        status = Running
                        log = "Bestätigt: Auftanken hat bereits stattgefunden." :: job'.log }
                    []
                    (fun j -> advance clock (advanceJobPosition j))
            | AcceptContractBaseline _
            | FulfillContractBaseline _
            | FleetBaseline _
            | ContractsCountBaseline _ -> failwith "AcceptContractBaseline/FulfillContractBaseline/FleetBaseline/ContractsCountBaseline never pair with ReconciliationShip."
        else
            { job' with status = AwaitingApiResponse(attempt + 1, action, baseline) },
            [ LogMessage(job.jobId, "Aktion war noch nicht erfolgreich, versuche erneut...")
              QueueApiCall(job.jobId, currentShipSymbol job, action, attempt + 1) ]
    | Reconciling(attempt, action, baseline), ReconciliationContract accepted when attempt = attemptNumber ->
        if accepted then
            settleOrDefer
                { job with
                    status = Running
                    log = "Bestätigt: Auftrag wurde bereits angenommen." :: job.log }
                []
                (fun j -> advance clock (advanceJobPosition j))
        else
            { job with status = AwaitingApiResponse(attempt + 1, action, baseline) },
            [ LogMessage(job.jobId, "Aktion war noch nicht erfolgreich, versuche erneut...")
              QueueApiCall(job.jobId, currentShipSymbol job, action, attempt + 1) ]
    | Reconciling(attempt, action, baseline), ReconciliationContractFulfilled fulfilled when attempt = attemptNumber ->
        if fulfilled then
            settleOrDefer
                { job with
                    status = Running
                    log = "Bestätigt: Auftrag wurde bereits abgeschlossen." :: job.log }
                []
                (fun j -> advance clock (advanceJobPosition j))
        else
            { job with status = AwaitingApiResponse(attempt + 1, action, baseline) },
            [ LogMessage(job.jobId, "Aktion war noch nicht erfolgreich, versuche erneut...")
              QueueApiCall(job.jobId, currentShipSymbol job, action, attempt + 1) ]
    | Reconciling(attempt, action, baseline), ReconciliationFleet shipCount when attempt = attemptNumber ->
        match baseline, action with
        | FleetBaseline shipCountBefore, DoPurchaseShip _ when shipCount > shipCountBefore ->
            settleOrDefer
                { job with
                    lastKnownFleetShipCount = Some shipCount
                    status = Running
                    log = "Bestätigt: Schiff wurde bereits gekauft." :: job.log }
                []
                (fun j -> advance clock (advanceJobPosition j))
        | FleetBaseline shipCountBefore, DoScrapShip when shipCount < shipCountBefore ->
            settleOrDefer
                { job with
                    lastKnownFleetShipCount = Some shipCount
                    lastKnownShip = None
                    status = Running
                    log = "Bestätigt: Schiff wurde bereits verschrottet." :: job.log }
                []
                (fun j -> advance clock (advanceJobPosition j))
        | FleetBaseline _, _ ->
            { job with
                lastKnownFleetShipCount = Some shipCount
                status = AwaitingApiResponse(attempt + 1, action, baseline) },
            [ LogMessage(job.jobId, "Aktion war noch nicht erfolgreich, versuche erneut...")
              QueueApiCall(job.jobId, currentShipSymbol job, action, attempt + 1) ]
        | _ ->
            { job with status = AwaitingApiResponse(attempt + 1, action, baseline) },
            [ LogMessage(job.jobId, "Aktion war noch nicht erfolgreich, versuche erneut...")
              QueueApiCall(job.jobId, currentShipSymbol job, action, attempt + 1) ]
    | Reconciling(attempt, action, baseline), ReconciliationContracts contractCount when attempt = attemptNumber ->
        match baseline with
        | ContractsCountBaseline countBefore when contractCount > countBefore ->
            settleOrDefer
                { job with
                    lastKnownContractCount = Some contractCount
                    status = Running
                    log = "Bestätigt: Auftrag wurde bereits verhandelt." :: job.log }
                []
                (fun j -> advance clock (advanceJobPosition j))
        | _ ->
            { job with
                lastKnownContractCount = Some contractCount
                status = AwaitingApiResponse(attempt + 1, action, baseline) },
            [ LogMessage(job.jobId, "Aktion war noch nicht erfolgreich, versuche erneut...")
              QueueApiCall(job.jobId, currentShipSymbol job, action, attempt + 1) ]
    | Reconciling(attempt, _, baseline), ApiAmbiguous _ when attempt = attemptNumber ->
        // The reconciliation fetch (a plain GET) is itself safe to retry indefinitely —
        // unlike the original action, it has no side effects.
        let reconcileEffect =
            match baseline with
            | AcceptContractBaseline contractId -> ReconcileContractState(job.jobId, contractId, attempt, CheckAccepted)
            | FulfillContractBaseline contractId -> ReconcileContractState(job.jobId, contractId, attempt, CheckFulfilled)
            | FleetBaseline _ -> ReconcileFleetState(job.jobId, attempt)
            | ContractsCountBaseline _ -> ReconcileContractsState(job.jobId, attempt)
            | _ -> ReconcileShipState(job.jobId, currentShipSymbol job, attempt)

        job, [ reconcileEffect ]
    | Reconciling(attempt, _, _), ApiFailed msg when attempt = attemptNumber ->
        { job with status = Failed msg }, [ JobFailed(job.jobId, msg) ]
    | AwaitingInfoResponse(attempt, _, _, resultTarget), InfoOk value when attempt = attemptNumber ->
        settleOrDefer
            { (job |> setLocal resultTarget value |> advanceJobPosition) with status = Running }
            []
            (fun j -> advance clock j)
    | AwaitingInfoResponse(attempt, infoType, args, resultTarget), ApiAmbiguous _ when attempt = attemptNumber ->
        // A GET is always safe to retry (§8/§14) — no reconciliation hop, ever;
        // just re-issue the same fetch with the next attempt number.
        { job with status = AwaitingInfoResponse(attempt + 1, infoType, args, resultTarget) },
        [ QueueInfoRead(job.jobId, currentShipSymbol job, infoType, args, attempt + 1, resultTarget) ]
    | AwaitingInfoResponse(attempt, _, _, _), ApiFailed msg when attempt = attemptNumber ->
        { job with status = Failed msg }, [ JobFailed(job.jobId, msg) ]
    | _ -> job, [] // stale attempt number or unrelated status/result pairing — defensive no-op

/// `step : Clock -> JobState -> SchedulerEvent -> (JobState * Effect list)` (§14).
let step (clock: Clock) (job: JobState) (event: SchedulerEvent) : JobState * Effect list =
    if not job.parallelBranches.IsEmpty then
        match event with
        | WakeTick
        | BranchApiResponseReceived _
        | BranchShipScopeAcquired _
        | BranchShipScopeUnavailable _
        | PauseRequested
        | ResumeRequested
        | CancelRequested -> driveParallelBranchesWithEvent clock job event
        | ApiResponseReceived _
        | ShipScopeAcquired _
        | ShipScopeUnavailable _ -> job, []
    else
    match event with
    | WakeTick ->
        match job.status with
        // A freshly-started job (status already `Running`, nothing walked yet) starts
        // its free-transition walk on the first tick, same as resuming from a wait.
        | Running -> advance clock job
        | WaitingForArrival until when clock.now() >= until -> advance clock { job with status = Running }
        | WaitingForCooldown until when clock.now() >= until -> advance clock { job with status = Running }
        | WaitingForShipLock(shipSymbol, blockId, hasElse) ->
            job, [ AcquireShipScope(job.jobId, blockId, shipSymbol, hasElse) ]
        | _ -> job, []
    | ApiResponseReceived(jobId, attemptNumber, result) when jobId = job.jobId ->
        handleApiResponse clock job attemptNumber result
    | ApiResponseReceived _ -> job, []
    | BranchApiResponseReceived _ -> job, []
    | ShipScopeAcquired(jobId, blockId, shipSymbol, snapshot) when jobId = job.jobId ->
        match job.status with
        | WaitingForShipLock(waitingFor, waitingBlockId, _) when waitingFor = shipSymbol && waitingBlockId = blockId ->
            let isPrimary = job.shipSymbol = Some shipSymbol
            let job' =
                { job with
                    status = Running
                    currentShipStack = (shipSymbol, not isPrimary) :: job.currentShipStack
                    dynamicShipLocks = if isPrimary then job.dynamicShipLocks else shipSymbol :: job.dynamicShipLocks
                    lastKnownShip = Some snapshot
                    log = $"Steuere jetzt Schiff {shipSymbol}." :: job.log }
                |> pushBodyJob (WithShipBody blockId) None

            advance clock job'
        | _ -> job, []
    | ShipScopeAcquired _ -> job, []
    | BranchShipScopeAcquired _ -> job, []
    | ShipScopeUnavailable(jobId, blockId, shipSymbol, reason) when jobId = job.jobId ->
        match job.status with
        | WaitingForShipLock(waitingFor, waitingBlockId, hasElse) when waitingFor = shipSymbol && waitingBlockId = blockId ->
            if hasElse then
                let job' =
                    { job with
                        status = Running
                        log = $"Schiff {shipSymbol} ist nicht verfügbar: {reason}" :: job.log }
                    |> advanceJobPosition
                    |> pushBodyJob (WithShipElseBody blockId) None

                advance clock job'
            else
                let msg = $"Schiff {shipSymbol} ist nicht verfügbar: {reason}"
                { job with status = Failed msg }, [ JobFailed(job.jobId, msg) ]
        | _ -> job, []
    | ShipScopeUnavailable _ -> job, []
    | BranchShipScopeUnavailable _ -> job, []
    | PauseRequested ->
        match job.status with
        | Running
        | WaitingForArrival _
        | WaitingForCooldown _
        | WaitingForShipLock _ -> { job with status = Paused job.status; log = "Programm pausiert." :: job.log }, []
        | AwaitingApiResponse _
        | Reconciling _
        | AwaitingInfoResponse _ ->
            if job.pausePending then
                job, []
            else
                { job with
                    pausePending = true
                    log = "Pause angefordert – wird nach Bestätigung der aktuellen Aktion wirksam." :: job.log },
                []
        | Paused _
        | Cancelled
        | Completed
        | Failed _ -> job, []
    | ResumeRequested ->
        match job.status with
        | Paused Running -> advance clock { job with status = Running }
        | Paused prior -> { job with status = prior }, []
        | _ -> job, []
    | CancelRequested ->
        match job.status with
        | Running
        | WaitingForArrival _
        | WaitingForCooldown _
        | WaitingForShipLock _
        | Paused _ ->
            { job with status = Cancelled; log = "Programm gestoppt." :: job.log }, [ JobCancelled job.jobId ]
        | AwaitingApiResponse _
        | Reconciling _
        | AwaitingInfoResponse _ ->
            if job.cancelPending then
                job, []
            else
                { job with
                    cancelPending = true
                    log = "Stopp angefordert – wird nach Bestätigung der aktuellen Aktion wirksam." :: job.log },
                []
        | Cancelled
        | Completed
        | Failed _ -> job, []

/// The top frame's deepest position's blockId (§14) — what a persistent shell
/// denormalizes into `jobs.current_block_id` for cheap dashboard queries without
/// deserializing `execution_state_json` per row.
let currentBlockId (job: JobState) : string option =
    match job.parallelBranches |> List.tryFind (fun b -> not b.completed) with
    | Some branch -> currentInstruction branch.job |> Option.map blockIdOf
    | None -> currentInstruction job |> Option.map blockIdOf

/// One (scope, blockId option) pair per stack frame, deepest-first (§9d/§14,
/// Milestone 9/Part E) — always one entry per frame, even one whose position is
/// already exhausted (e.g. a custom block whose last instruction is a `Wait`/
/// `ApiAction` that advances its own position *before* suspending, per the
/// existing "advance past the block that started the wait" pattern shared with
/// `NavigateOk`/`ExtractOk` — the frame is still genuinely on the stack, just with
/// nothing left to highlight inside it). Dropping such frames (as an earlier,
/// `List.choose`-based version of this function did) would make a call's own
/// "innen aktiv" state undetectable in exactly this common case, since the caller
/// frame would then look like the only frame left. `scope` is `"main"` for the
/// program's own frame, or the custom block's id for a call frame — the client uses
/// it to decide which open Blockly workspace (program vs. a specific workshop) a
/// given entry's `blockId` belongs to.
let blockIdPerFrame (job: JobState) : (string * string option) list =
    if not job.parallelBranches.IsEmpty then
        job.parallelBranches
        |> List.collect (fun branch ->
            branch.job.stack
            |> List.map (fun frame -> frame.scope, currentInstructionOf branch.job.program frame |> Option.map blockIdOf))
    else
        job.stack
        |> List.map (fun frame -> frame.scope, currentInstructionOf job.program frame |> Option.map blockIdOf)
