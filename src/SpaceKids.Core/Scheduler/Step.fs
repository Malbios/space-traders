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
    | If(bid, _, _) -> bid
    | Repeat(bid, _, _) -> bid
    | WhileUntil(bid, _, _, _) -> bid
    | ForEach(bid, _, _, _) -> bid
    | CallCustomBlock(bid, _, _, _) -> bid

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

// --- Reconciliation (§13) -----------------------------------------------------------

/// Decides, from a fresh ship snapshot, whether the ambiguous action already
/// happened. Per-ship signals only — credits are never consulted (§13: "never as the
/// deciding signal").
let private reconcile (baseline: ActionBaseline) (current: ShipSnapshot) : bool =
    match baseline with
    | NavigateBaseline intended -> current.navWaypoint = intended
    | DockOrbitBaseline expected -> current.navStatus = expected
    | CargoBaseline unitsBefore -> current.cargoUnits <> unitsBefore
    | ExtractBaseline(cooldownBefore, unitsBefore) ->
        let cooldownAdvanced =
            match cooldownBefore, current.cooldownExpiration with
            | Some before, Some after -> DateTimeOffset.Parse(after) > DateTimeOffset.Parse(before)
            | None, Some _ -> true
            | _, None -> false

        cooldownAdvanced && current.cargoUnits <> unitsBefore

// --- The pure step core --------------------------------------------------------------

let rec private advance (clock: Clock) (job: JobState) : JobState * Effect list =
    match job.status with
    | Running ->
        match currentInstruction job with
        | None -> { job with status = Completed }, [ JobCompleted job.jobId ]
        | Some instr -> advanceInstruction clock job instr
    | _ -> job, []

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
    | InfoRead _
    | CallCustomBlock _ ->
        // Out of scope for Milestone 6 (only the 6 action blocks are implemented) —
        // fail the job with a clear German message rather than crash or silently
        // no-op, so a full-catalog program degrades safely.
        let msg = "Dieser Block wird erst in einem späteren Meilenstein unterstützt."
        { job with status = Failed msg }, [ JobFailed(job.jobId, msg) ]
    | ApiAction(blockId, actionType, args) -> emitApiAction job blockId actionType args

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

    match job.lastKnownShip with
    | None ->
        let msg = "Kein Schiff ausgewählt."
        { job with status = Failed msg }, [ JobFailed(job.jobId, msg) ]
    | Some ship ->
        let awaiting (baseline: ActionBaseline) (action: QueuedAction) (logText: string) =
            { job with status = AwaitingApiResponse(0, action, baseline) },
            [ QueueApiCall(job.jobId, job.shipSymbol, action, 0); LogMessage(job.jobId, logText) ]

        match actionType with
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
    | AwaitingApiResponse(attempt, _, _), ApiAmbiguous _ when attempt = attemptNumber ->
        match job.status with
        | AwaitingApiResponse(_, action, baseline) ->
            { job with status = Reconciling(attempt, action, baseline) },
            [ ReconcileShipState(job.jobId, job.shipSymbol, attempt)
              LogMessage(job.jobId, "Prüfe, ob die Aktion schon stattgefunden hat...") ]
        | _ -> job, []
    | AwaitingApiResponse(attempt, _, _), ApiFailed msg when attempt = attemptNumber ->
        { job with status = Failed msg }, [ JobFailed(job.jobId, msg) ]
    | AwaitingApiResponse(attempt, _, _), NavigateOk(navStatus, navWaypoint, arrival) when attempt = attemptNumber ->
        let ship' =
            { (job.lastKnownShip |> Option.get) with
                navStatus = navStatus
                navWaypoint = navWaypoint
                navArrival = Some arrival }

        let until = DateTimeOffset.Parse arrival

        { (job |> advanceJobPosition) with
            lastKnownShip = Some ship'
            status = WaitingForArrival until
            log = "Unterwegs..." :: job.log },
        [ StartWait(job.jobId, until, ArrivalWait) ]
    | AwaitingApiResponse(attempt, _, _), NavResultOk(navStatus, navWaypoint) when attempt = attemptNumber ->
        let ship' =
            { (job.lastKnownShip |> Option.get) with
                navStatus = navStatus
                navWaypoint = navWaypoint }

        let logText = if navStatus = "IN_ORBIT" then "Habe den Andockplatz verlassen." else "Angedockt."

        advance
            clock
            ({ job with
                lastKnownShip = Some ship'
                status = Running
                log = logText :: job.log }
             |> advanceJobPosition)
    | AwaitingApiResponse(attempt, _, _), ExtractOk(cooldownExpiration, cargoUnits, cargoInventory, yieldSymbol, yieldUnits) when
        attempt = attemptNumber
        ->
        let ship' =
            { (job.lastKnownShip |> Option.get) with
                cargoUnits = cargoUnits
                cargoInventory = cargoInventory
                cooldownExpiration = Some cooldownExpiration }

        let until = DateTimeOffset.Parse cooldownExpiration

        { (job |> advanceJobPosition) with
            lastKnownShip = Some ship'
            status = WaitingForCooldown until
            log = $"Abgebaut: {yieldUnits}x {yieldSymbol}." :: job.log },
        [ StartWait(job.jobId, until, CooldownWait) ]
    | AwaitingApiResponse(attempt, _, _), TradeOk(cargoUnits, cargoInventory, transactionType, tradeSymbol, units, totalPrice) when
        attempt = attemptNumber
        ->
        let ship' =
            { (job.lastKnownShip |> Option.get) with
                cargoUnits = cargoUnits
                cargoInventory = cargoInventory }

        let verb = if transactionType = "SELL" then "Verkauft" else "Gekauft"

        advance
            clock
            ({ job with
                lastKnownShip = Some ship'
                status = Running
                log = $"{verb}: {units}x {tradeSymbol} für {totalPrice} Credits." :: job.log }
             |> advanceJobPosition)
    | Reconciling(attempt, action, baseline), ReconciliationShip current when attempt = attemptNumber ->
        let job' = { job with lastKnownShip = Some current }

        if reconcile baseline current then
            match baseline with
            | NavigateBaseline _ ->
                match current.navArrival with
                | Some arrival when DateTimeOffset.Parse arrival > clock.now() ->
                    { (job' |> advanceJobPosition) with
                        status = WaitingForArrival(DateTimeOffset.Parse arrival)
                        log = "Unterwegs (bereits bestätigt)." :: job'.log },
                    [ StartWait(job.jobId, DateTimeOffset.Parse arrival, ArrivalWait) ]
                | _ ->
                    advance
                        clock
                        ({ job' with
                            status = Running
                            log = "Angekommen (bereits bestätigt)." :: job'.log }
                         |> advanceJobPosition)
            | DockOrbitBaseline _ ->
                advance
                    clock
                    ({ job' with
                        status = Running
                        log = "Bestätigt (bereits erledigt)." :: job'.log }
                     |> advanceJobPosition)
            | CargoBaseline _ ->
                advance
                    clock
                    ({ job' with
                        status = Running
                        log = "Bestätigt: Handel hat bereits stattgefunden." :: job'.log }
                     |> advanceJobPosition)
            | ExtractBaseline _ ->
                match current.cooldownExpiration with
                | Some expiration when DateTimeOffset.Parse expiration > clock.now() ->
                    { (job' |> advanceJobPosition) with
                        status = WaitingForCooldown(DateTimeOffset.Parse expiration)
                        log = "Bestätigt: Abbau hat bereits stattgefunden." :: job'.log },
                    [ StartWait(job.jobId, DateTimeOffset.Parse expiration, CooldownWait) ]
                | _ ->
                    advance
                        clock
                        ({ job' with
                            status = Running
                            log = "Bestätigt: Abbau hat bereits stattgefunden." :: job'.log }
                         |> advanceJobPosition)
        else
            { job' with status = AwaitingApiResponse(attempt + 1, action, baseline) },
            [ QueueApiCall(job.jobId, job.shipSymbol, action, attempt + 1)
              LogMessage(job.jobId, "Aktion war noch nicht erfolgreich, versuche erneut...") ]
    | Reconciling(attempt, _, _), ApiAmbiguous _ when attempt = attemptNumber ->
        // The reconciliation fetch (a plain GET) is itself safe to retry indefinitely —
        // unlike the original action, it has no side effects.
        job, [ ReconcileShipState(job.jobId, job.shipSymbol, attempt) ]
    | Reconciling(attempt, _, _), ApiFailed msg when attempt = attemptNumber ->
        { job with status = Failed msg }, [ JobFailed(job.jobId, msg) ]
    | _ -> job, [] // stale attempt number or unrelated status/result pairing — defensive no-op

/// `step : Clock -> JobState -> SchedulerEvent -> (JobState * Effect list)` (§14).
let step (clock: Clock) (job: JobState) (event: SchedulerEvent) : JobState * Effect list =
    match event with
    | WakeTick ->
        match job.status with
        // A freshly-started job (status already `Running`, nothing walked yet) starts
        // its free-transition walk on the first tick, same as resuming from a wait.
        | Running -> advance clock job
        | WaitingForArrival until when clock.now() >= until -> advance clock { job with status = Running }
        | WaitingForCooldown until when clock.now() >= until -> advance clock { job with status = Running }
        | _ -> job, []
    | ApiResponseReceived(jobId, attemptNumber, result) when jobId = job.jobId ->
        handleApiResponse clock job attemptNumber result
    | ApiResponseReceived _ -> job, []
