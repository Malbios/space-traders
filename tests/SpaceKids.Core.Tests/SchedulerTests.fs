module SchedulerTests

open System
open Xunit
open SpaceKids.Core.Dsl
open SpaceKids.Core.Scheduler

/// Milestone 6 (§14): the pure `step` core, tested with zero DB/network/real time —
/// a fake, manually-advanced clock and fabricated `ApiResponseReceived` events only.

let private fakeClock (current: DateTimeOffset ref) : Clock = { now = fun () -> current.Value }

let private mkProgram (instructions: Instruction list) : CompiledProgram =
    { version = 1
      customBlocks = Map.empty
      instructions = instructions }

let private mkCustomBlock (instructions: Instruction list) (returnExpr: Expr option) : CompiledCustomBlock =
    { signature = { inputs = []; output = (returnExpr |> Option.map (fun _ -> "Zahl")); outputFields = None }
      instructions = instructions
      returnExpr = returnExpr }

let private mkJobWithCustomBlocks
    (customBlocks: Map<string, CompiledCustomBlock>)
    (instructions: Instruction list)
    (lastKnownShip: ShipSnapshot option)
    : JobState =
    { jobId = "job1"
      programId = "prog1"
      program = { version = 1; customBlocks = customBlocks; instructions = instructions }
      shipSymbol = Some "SHIP-1"
      status = Running
      stack =
        [ { scope = "main"
            position = [ { bodyRef = MainBody; index = 0; loopState = None } ]
            locals = Map.empty
            returnTarget = None } ]
      lastKnownShip = lastKnownShip
      lastKnownFleetShipCount = Some 2
      log = []
      pausePending = false
      cancelPending = false }

let private mkJob (instructions: Instruction list) (lastKnownShip: ShipSnapshot option) : JobState =
    { jobId = "job1"
      programId = "prog1"
      program = mkProgram instructions
      shipSymbol = Some "SHIP-1"
      status = Running
      stack =
        [ { scope = "main"
            position = [ { bodyRef = MainBody; index = 0; loopState = None } ]
            locals = Map.empty
            returnTarget = None } ]
      lastKnownShip = lastKnownShip
      lastKnownFleetShipCount = Some 2
      log = []
      pausePending = false
      cancelPending = false }

let private defaultShip: ShipSnapshot =
    { navStatus = "DOCKED"
      navWaypoint = "X1-TEST-A1"
      navArrival = None
      cargoUnits = 0
      cargoInventory = Map.empty
      cooldownExpiration = None
      fuelCurrent = 400 }

let private epoch = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

// --- Free-transition walking ---------------------------------------------------------

[<Fact>]
let ``walks vars, if, and show-message in one step call with no API actions`` () =
    let clock = fakeClock (ref epoch)

    let instructions =
        [ SetVariable("b1", "x", Literal(NumberLit 1.0))
          If(
              "b2",
              [ (Comparison("GT", VariableRef "x", Literal(NumberLit 0.0)), [ ShowMessage("b3", Literal(StringLit "hi")) ]) ],
              None
          ) ]

    let job = mkJob instructions None
    let job', effects = Step.step clock job WakeTick

    Assert.Equal(Completed, job'.status)
    Assert.Equal<string list>([ "hi" ], job'.log)
    Assert.Contains(JobCompleted "job1", effects)

[<Fact>]
let ``if with no matching branch and no else just advances`` () =
    let clock = fakeClock (ref epoch)

    let instructions =
        [ If("b1", [ (Literal(BoolLit false), [ ShowMessage("b2", Literal(StringLit "unreachable")) ]) ], None)
          ShowMessage("b3", Literal(StringLit "reached")) ]

    let job = mkJob instructions None
    let job', _ = Step.step clock job WakeTick

    Assert.Equal(Completed, job'.status)
    Assert.Equal<string list>([ "reached" ], job'.log)

[<Fact>]
let ``LogicalOp AND/OR and LogicalNot evaluate correctly`` () =
    let clock = fakeClock (ref epoch)

    let instructions =
        [ If(
              "b1",
              [ (LogicalOp("AND", Literal(BoolLit true), LogicalNot(Literal(BoolLit false))),
                 [ ShowMessage("b2", Literal(StringLit "and-true")) ])
                (LogicalOp("OR", Literal(BoolLit false), Literal(BoolLit true)), [ ShowMessage("b3", Literal(StringLit "unreachable")) ]) ],
              None
          ) ]

    let job = mkJob instructions None
    let job', _ = Step.step clock job WakeTick

    Assert.Equal(Completed, job'.status)
    Assert.Equal<string list>([ "and-true" ], job'.log)

[<Fact>]
let ``LogicalOp AND short-circuits: a false left operand never evaluates the right`` () =
    let clock = fakeClock (ref epoch)

    // If short-circuiting were broken, evaluating the unresolved `VariableRef` on the
    // right would throw ("Unbekannter Name zur Laufzeit"), failing the job instead of
    // completing it.
    let instructions =
        [ If(
              "b1",
              [ (LogicalOp("AND", Literal(BoolLit false), VariableRef "undefined"),
                 [ ShowMessage("b2", Literal(StringLit "unreachable")) ]) ],
              None
          )
          ShowMessage("b3", Literal(StringLit "reached")) ]

    let job = mkJob instructions None
    let job', _ = Step.step clock job WakeTick

    Assert.Equal(Completed, job'.status)
    Assert.Equal<string list>([ "reached" ], job'.log)

// --- Loop counter persistence across steps ------------------------------------------

[<Fact>]
let ``a repeat loop's counter persists across steps and re-enters its body each time`` () =
    let clock = fakeClock (ref epoch)
    let instructions = [ Repeat("rep", Literal(NumberLit 3.0), [ ApiAction("act1", "orbit", Map.empty) ]) ]
    let job0 = mkJob instructions (Some defaultShip)

    let job1, effects1 = Step.step clock job0 WakeTick
    Assert.Equal(AwaitingApiResponse(0, DoOrbit, DockOrbitBaseline "IN_ORBIT"), job1.status)
    Assert.Contains(QueueApiCall("job1", Some "SHIP-1", DoOrbit, 0), effects1)

    let respondOrbit (job: JobState) =
        Step.step clock job (ApiResponseReceived("job1", 0, NavResultOk("IN_ORBIT", "X1-TEST-A1")))

    let job2, _ = respondOrbit job1
    Assert.Equal(AwaitingApiResponse(0, DoOrbit, DockOrbitBaseline "IN_ORBIT"), job2.status)

    let job3, _ = respondOrbit job2
    Assert.Equal(AwaitingApiResponse(0, DoOrbit, DockOrbitBaseline "IN_ORBIT"), job3.status)

    let job4, effects4 = respondOrbit job3
    Assert.Equal(Completed, job4.status)
    Assert.Contains(JobCompleted "job1", effects4)

// --- Break/Continue (controls_flow_statements) ---------------------------------------

[<Fact>]
let ``a Break inside a forEach exits the loop early, skipping later items entirely`` () =
    let clock = fakeClock (ref epoch)

    let instructions =
        [ ForEach(
              "loop1",
              "x",
              ListLiteral [ Literal(NumberLit 1.0); Literal(NumberLit 2.0); Literal(NumberLit 3.0) ],
              [ If("if1", [ (Comparison("EQ", VariableRef "x", Literal(NumberLit 2.0)), [ Break "brk1" ]) ], None)
                ShowMessage("sm1", VariableRef "x") ]
          ) ]

    let job = mkJob instructions None
    let job', _ = Step.step clock job WakeTick

    Assert.Equal(Completed, job'.status)
    Assert.Equal<string list>([ "1" ], job'.log)

[<Fact>]
let ``a Continue inside a repeat skips the rest of that iteration but still runs the remaining ones`` () =
    let clock = fakeClock (ref epoch)

    let instructions =
        [ SetVariable("s1", "i", Literal(NumberLit 0.0))
          Repeat(
              "rep2",
              Literal(NumberLit 3.0),
              [ ChangeVariable("ch2", "i", Literal(NumberLit 1.0))
                If("if2", [ (Comparison("EQ", VariableRef "i", Literal(NumberLit 2.0)), [ Continue "cnt1" ]) ], None)
                ShowMessage("sm2", VariableRef "i") ]
          ) ]

    let job = mkJob instructions None
    let job', _ = Step.step clock job WakeTick

    Assert.Equal(Completed, job'.status)
    // i=1 and i=3 show their message; i=2's Continue skips it. log is newest-first.
    Assert.Equal<string list>([ "3"; "1" ], job'.log)

[<Fact>]
let ``a Continue inside a whileUntil re-evaluates the condition instead of crashing or looping forever`` () =
    let clock = fakeClock (ref epoch)

    let instructions =
        [ SetVariable("s2", "i", Literal(NumberLit 0.0))
          WhileUntil(
              "wu1",
              While,
              Comparison("LT", VariableRef "i", Literal(NumberLit 3.0)),
              [ ChangeVariable("ch3", "i", Literal(NumberLit 1.0))
                If("if3", [ (Comparison("EQ", VariableRef "i", Literal(NumberLit 1.0)), [ Continue "cnt2" ]) ], None)
                ShowMessage("sm3", VariableRef "i") ]
          ) ]

    let job = mkJob instructions None
    let job', _ = Step.step clock job WakeTick

    Assert.Equal(Completed, job'.status)
    // i=1's Continue skips its message; i=2 and i=3 still show theirs.
    Assert.Equal<string list>([ "3"; "2" ], job'.log)

// --- The 6 actions' happy paths -------------------------------------------------------

[<Fact>]
let ``navigate happy path updates ship and waits for arrival`` () =
    let clock = fakeClock (ref epoch)
    let instructions = [ ApiAction("b1", "navigate", Map [ "destination", Literal(StringLit "X1-TEST-B2") ]) ]
    let job0 = mkJob instructions (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick

    Assert.Equal(AwaitingApiResponse(0, DoNavigate "X1-TEST-B2", NavigateBaseline "X1-TEST-B2"), job1.status)

    let arrival = epoch.AddSeconds(30.0)
    let result = NavigateOk("IN_TRANSIT", "X1-TEST-B2", arrival.ToString("o"))
    let job2, effects2 = Step.step clock job1 (ApiResponseReceived("job1", 0, result))

    Assert.Equal(WaitingForArrival arrival, job2.status)
    Assert.Equal("X1-TEST-B2", job2.lastKnownShip.Value.navWaypoint)
    Assert.Contains(StartWait("job1", arrival, ArrivalWait), effects2)

[<Fact>]
let ``orbit happy path advances immediately`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "orbit", Map.empty) ] (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick
    let job2, effects2 = Step.step clock job1 (ApiResponseReceived("job1", 0, NavResultOk("IN_ORBIT", "X1-TEST-A1")))

    Assert.Equal(Completed, job2.status)
    Assert.Equal("IN_ORBIT", job2.lastKnownShip.Value.navStatus)
    Assert.Contains(JobCompleted "job1", effects2)

[<Fact>]
let ``dock happy path advances immediately`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "dock", Map.empty) ] (Some { defaultShip with navStatus = "IN_ORBIT" })
    let job1, _ = Step.step clock job0 WakeTick
    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, NavResultOk("DOCKED", "X1-TEST-A1")))

    Assert.Equal(Completed, job2.status)
    Assert.Equal("DOCKED", job2.lastKnownShip.Value.navStatus)

[<Fact>]
let ``extract happy path waits for cooldown`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "extract", Map.empty) ] (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick

    Assert.Equal(AwaitingApiResponse(0, DoExtract, ExtractBaseline(None, 0)), job1.status)

    let expiration = epoch.AddSeconds(10.0)
    let result = ExtractOk(expiration.ToString("o"), 10, Map [ "IRON", 10 ], "IRON", 10)
    let job2, effects2 = Step.step clock job1 (ApiResponseReceived("job1", 0, result))

    Assert.Equal(WaitingForCooldown expiration, job2.status)
    Assert.Equal(10, job2.lastKnownShip.Value.cargoUnits)
    Assert.Contains($"Abgebaut: 10x IRON.", job2.log)
    Assert.Contains(StartWait("job1", expiration, CooldownWait), effects2)

[<Fact>]
let ``buyGood happy path advances immediately`` () =
    let clock = fakeClock (ref epoch)

    let job0 =
        mkJob
            [ ApiAction(
                  "b1",
                  "buyGood",
                  Map [ "tradeSymbol", Literal(StringLit "FUEL"); "units", Literal(NumberLit 5.0) ]
              ) ]
            (Some defaultShip)

    let job1, _ = Step.step clock job0 WakeTick
    Assert.Equal(AwaitingApiResponse(0, DoBuy("FUEL", 5), CargoBaseline 0), job1.status)

    let result = TradeOk(5, Map [ "FUEL", 5 ], "PURCHASE", "FUEL", 5, 50)
    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, result))

    Assert.Equal(Completed, job2.status)
    Assert.Equal(5, job2.lastKnownShip.Value.cargoUnits)

[<Fact>]
let ``sellGood happy path advances immediately`` () =
    let clock = fakeClock (ref epoch)
    let shipWithCargo = { defaultShip with cargoUnits = 5; cargoInventory = Map [ "FUEL", 5 ] }

    let job0 =
        mkJob
            [ ApiAction(
                  "b1",
                  "sellGood",
                  Map [ "tradeSymbol", Literal(StringLit "FUEL"); "units", Literal(NumberLit 5.0) ]
              ) ]
            (Some shipWithCargo)

    let job1, _ = Step.step clock job0 WakeTick
    Assert.Equal(AwaitingApiResponse(0, DoSell("FUEL", 5), CargoBaseline 5), job1.status)

    let result = TradeOk(0, Map.empty, "SELL", "FUEL", 5, 50)
    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, result))

    Assert.Equal(Completed, job2.status)
    Assert.Equal(0, job2.lastKnownShip.Value.cargoUnits)

// --- Wait-then-resume via WakeTick ----------------------------------------------------

[<Fact>]
let ``navigate does not resume before arrival, resumes once the clock passes it`` () =
    let current = ref epoch
    let clock = fakeClock current
    let job0 = mkJob [ ApiAction("b1", "navigate", Map [ "destination", Literal(StringLit "X1-TEST-B2") ]) ] (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick

    let arrival = epoch.AddSeconds(30.0)
    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, NavigateOk("IN_TRANSIT", "X1-TEST-B2", arrival.ToString("o"))))
    Assert.Equal(WaitingForArrival arrival, job2.status)

    // not due yet
    let job3, effects3 = Step.step clock job2 WakeTick
    Assert.Equal(WaitingForArrival arrival, job3.status)
    Assert.Empty(effects3)

    // due now
    current.Value <- arrival
    let job4, effects4 = Step.step clock job3 WakeTick
    Assert.Equal(Completed, job4.status)
    Assert.Contains(JobCompleted "job1", effects4)

// --- Reconciliation (§13) -------------------------------------------------------------

[<Fact>]
let ``navigate reconciliation - already happened advances the job without a duplicate call`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "navigate", Map [ "destination", Literal(StringLit "X1-TEST-B2") ]) ] (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick
    let job2, effects2 = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))

    Assert.Equal(Reconciling(0, DoNavigate "X1-TEST-B2", NavigateBaseline "X1-TEST-B2"), job2.status)
    Assert.Contains(ReconcileShipState("job1", Some "SHIP-1", 0), effects2)

    let confirmed = { defaultShip with navStatus = "IN_ORBIT"; navWaypoint = "X1-TEST-B2"; navArrival = None }
    let job3, effects3 = Step.step clock job2 (ApiResponseReceived("job1", 0, ReconciliationShip confirmed))

    Assert.Equal(Completed, job3.status)
    Assert.DoesNotContain(effects3, (function QueueApiCall _ -> true | _ -> false))

[<Fact>]
let ``navigate reconciliation - not yet happened retries with the same baseline`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "navigate", Map [ "destination", Literal(StringLit "X1-TEST-B2") ]) ] (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick
    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))

    let stillAtOrigin = defaultShip
    let job3, effects3 = Step.step clock job2 (ApiResponseReceived("job1", 0, ReconciliationShip stillAtOrigin))

    Assert.Equal(AwaitingApiResponse(1, DoNavigate "X1-TEST-B2", NavigateBaseline "X1-TEST-B2"), job3.status)
    Assert.Contains(QueueApiCall("job1", Some "SHIP-1", DoNavigate "X1-TEST-B2", 1), effects3)

[<Fact>]
let ``dock/orbit reconciliation both branches`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "dock", Map.empty) ] (Some { defaultShip with navStatus = "IN_ORBIT" })
    let job1, _ = Step.step clock job0 WakeTick
    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))

    let notYet = { defaultShip with navStatus = "IN_ORBIT" }
    let job3, effects3 = Step.step clock job2 (ApiResponseReceived("job1", 0, ReconciliationShip notYet))
    Assert.Equal(AwaitingApiResponse(1, DoDock, DockOrbitBaseline "DOCKED"), job3.status)
    Assert.Contains(QueueApiCall("job1", Some "SHIP-1", DoDock, 1), effects3)

    // the retry (attempt 1) is issued as a real action call again, not another
    // reconciliation fetch — it now succeeds normally.
    let job4, _ = Step.step clock job3 (ApiResponseReceived("job1", 1, NavResultOk("DOCKED", "X1-TEST-A1")))
    Assert.Equal(Completed, job4.status)

[<Fact>]
let ``buy/sell reconciliation both branches, credits never consulted`` () =
    let clock = fakeClock (ref epoch)
    let job0 =
        mkJob
            [ ApiAction("b1", "buyGood", Map [ "tradeSymbol", Literal(StringLit "FUEL"); "units", Literal(NumberLit 5.0) ]) ]
            (Some defaultShip)

    let job1, _ = Step.step clock job0 WakeTick
    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))

    let unchanged = defaultShip
    let job3, effects3 = Step.step clock job2 (ApiResponseReceived("job1", 0, ReconciliationShip unchanged))
    Assert.Equal(AwaitingApiResponse(1, DoBuy("FUEL", 5), CargoBaseline 0), job3.status)
    Assert.Contains(QueueApiCall("job1", Some "SHIP-1", DoBuy("FUEL", 5), 1), effects3)

    // the retry (attempt 1) is issued as a real action call again, not another
    // reconciliation fetch — it now succeeds normally.
    let job4, _ =
        Step.step clock job3 (ApiResponseReceived("job1", 1, TradeOk(5, Map [ "FUEL", 5 ], "PURCHASE", "FUEL", 5, 50)))

    Assert.Equal(Completed, job4.status)

[<Fact>]
let ``extract reconciliation requires both a newer cooldown and a cargo delta, not either alone`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "extract", Map.empty) ] (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick
    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))

    // cooldown advanced but cargo unchanged — must still retry (conjunctive check)
    let cooldownOnly = { defaultShip with cooldownExpiration = Some(epoch.AddSeconds(10.0).ToString("o")) }
    let job3, effects3 = Step.step clock job2 (ApiResponseReceived("job1", 0, ReconciliationShip cooldownOnly))
    Assert.Equal(AwaitingApiResponse(1, DoExtract, ExtractBaseline(None, 0)), job3.status)
    Assert.Contains(QueueApiCall("job1", Some "SHIP-1", DoExtract, 1), effects3)

    // cargo changed but cooldown not newer — must still retry
    let job2b, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))
    let cargoOnly = { defaultShip with cargoUnits = 10; cargoInventory = Map [ "IRON", 10 ] }
    let job3b, effects3b = Step.step clock job2b (ApiResponseReceived("job1", 0, ReconciliationShip cargoOnly))
    Assert.Equal(AwaitingApiResponse(1, DoExtract, ExtractBaseline(None, 0)), job3b.status)
    Assert.Contains(QueueApiCall("job1", Some "SHIP-1", DoExtract, 1), effects3b)

    // both — treated as already happened
    let job2c, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))
    let both =
        { defaultShip with
            cargoUnits = 10
            cargoInventory = Map [ "IRON", 10 ]
            cooldownExpiration = Some(epoch.AddSeconds(10.0).ToString("o")) }
    let job3c, _ = Step.step clock job2c (ApiResponseReceived("job1", 0, ReconciliationShip both))
    Assert.Equal(WaitingForCooldown(epoch.AddSeconds(10.0)), job3c.status)

// --- Defensive: stale attempt numbers -------------------------------------------------

[<Fact>]
let ``a response with a mismatched attempt number is ignored`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "orbit", Map.empty) ] (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick

    let job2, effects2 = Step.step clock job1 (ApiResponseReceived("job1", 99, NavResultOk("IN_ORBIT", "X1-TEST-A1")))

    Assert.Equal(job1.status, job2.status)
    Assert.Empty(effects2)

// --- Out-of-scope block types fail cleanly --------------------------------------------

[<Fact>]
let ``CallCustomBlock fails the job with a clear German message instead of crashing`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ CallCustomBlock("b1", "custom1", Map.empty, None) ] (Some defaultShip)
    let job1, effects1 = Step.step clock job0 WakeTick

    match job1.status with
    | Failed _ -> ()
    | other -> Assert.Fail($"expected Failed, got {other}")

    Assert.Contains(effects1, (function JobFailed _ -> true | _ -> false))

// --- Pause/resume/cancel (§14/§15, Milestone 7) ---------------------------------------

[<Fact>]
let ``PauseRequested while Running pauses immediately`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "orbit", Map.empty) ] (Some defaultShip)
    let job1, effects1 = Step.step clock job0 PauseRequested

    Assert.Equal(Paused Running, job1.status)
    Assert.Empty(effects1)

[<Fact>]
let ``PauseRequested while waiting for arrival pauses immediately, preserving the wait target`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [] (Some defaultShip)
    let until = epoch.AddSeconds(30.0)
    let waiting = { job0 with status = WaitingForArrival until }

    let job1, _ = Step.step clock waiting PauseRequested
    Assert.Equal(Paused(WaitingForArrival until), job1.status)

[<Fact>]
let ``ResumeRequested from a paused Running job resumes the free walk immediately`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ShowMessage("b1", Literal(StringLit "hi")) ] (Some defaultShip)
    let paused = { job0 with status = Paused Running }

    let job1, effects1 = Step.step clock paused ResumeRequested
    Assert.Equal(Completed, job1.status)
    Assert.Equal<string list>([ "hi" ], job1.log)
    Assert.Contains(JobCompleted "job1", effects1)

[<Fact>]
let ``ResumeRequested from a paused wait just restores the wait, ticking resumes it later`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [] (Some defaultShip)
    let until = epoch.AddSeconds(30.0)
    let paused = { job0 with status = Paused(WaitingForArrival until) }

    let job1, effects1 = Step.step clock paused ResumeRequested
    Assert.Equal(WaitingForArrival until, job1.status)
    Assert.Empty(effects1)

[<Fact>]
let ``PauseRequested during AwaitingApiResponse defers and takes effect only once the action resolves`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "orbit", Map.empty) ] (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick
    Assert.Equal(AwaitingApiResponse(0, DoOrbit, DockOrbitBaseline "IN_ORBIT"), job1.status)

    // pause requested mid-flight: does not pause yet, and the flag doesn't
    // duplicate-log on a second request.
    let job2, effects2 = Step.step clock job1 PauseRequested
    Assert.Equal(AwaitingApiResponse(0, DoOrbit, DockOrbitBaseline "IN_ORBIT"), job2.status)
    Assert.True(job2.pausePending)
    Assert.Empty(effects2)
    let job2b, _ = Step.step clock job2 PauseRequested
    Assert.True(job2b.pausePending)

    // the in-flight action still resolves normally...
    let job3, effects3 = Step.step clock job2b (ApiResponseReceived("job1", 0, NavResultOk("IN_ORBIT", "X1-TEST-A1")))

    // ...but settles into Paused instead of continuing to run further instructions.
    Assert.Equal(Paused Running, job3.status)
    Assert.False(job3.pausePending)
    Assert.DoesNotContain(effects3, (function JobCompleted _ -> true | _ -> false))

[<Fact>]
let ``PauseRequested during Reconciling defers until the reconciliation settles`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "orbit", Map.empty) ] (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick
    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))
    Assert.Equal(Reconciling(0, DoOrbit, DockOrbitBaseline "IN_ORBIT"), job2.status)

    let job3, _ = Step.step clock job2 PauseRequested
    Assert.True(job3.pausePending)
    Assert.Equal(Reconciling(0, DoOrbit, DockOrbitBaseline "IN_ORBIT"), job3.status)

    let confirmed = { defaultShip with navStatus = "IN_ORBIT" }
    let job4, _ = Step.step clock job3 (ApiResponseReceived("job1", 0, ReconciliationShip confirmed))

    Assert.Equal(Paused Running, job4.status)
    Assert.False(job4.pausePending)

[<Fact>]
let ``CancelRequested while Running cancels immediately and releases the ship lock via JobCancelled`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "orbit", Map.empty) ] (Some defaultShip)
    let job1, effects1 = Step.step clock job0 CancelRequested

    Assert.Equal(Cancelled, job1.status)
    Assert.Contains(JobCancelled "job1", effects1)

[<Fact>]
let ``CancelRequested during AwaitingApiResponse defers and never abandons the in-flight action`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "orbit", Map.empty) ] (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick

    let job2, effects2 = Step.step clock job1 CancelRequested
    Assert.Equal(AwaitingApiResponse(0, DoOrbit, DockOrbitBaseline "IN_ORBIT"), job2.status)
    Assert.True(job2.cancelPending)
    Assert.Empty(effects2)

    let job3, effects3 = Step.step clock job2 (ApiResponseReceived("job1", 0, NavResultOk("IN_ORBIT", "X1-TEST-A1")))
    Assert.Equal(Cancelled, job3.status)
    Assert.Contains(JobCancelled "job1", effects3)

// --- Clock-skew catch-up (§14) ---------------------------------------------------------

[<Fact>]
let ``a WakeTick resumes a wait that is arbitrarily far in the past, not just barely due`` () =
    let current = ref epoch
    let clock = fakeClock current
    let job0 = mkJob [] (Some defaultShip)
    let until = epoch.AddSeconds(30.0)
    let waiting = { job0 with status = WaitingForArrival until }

    // the server was down for, say, three days — nowhere near "due a few seconds ago".
    current.Value <- until.AddDays(3.0)
    let job1, effects1 = Step.step clock waiting WakeTick

    Assert.Equal(Completed, job1.status)
    Assert.Contains(JobCompleted "job1", effects1)

// --- Restart recovery (§14): an unresolved in-flight call is ambiguous, not silently rerun --

[<Fact>]
let ``a job found in AwaitingApiResponse after a restart recovers via the same ambiguous-failure path`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "navigate", Map [ "destination", Literal(StringLit "X1-TEST-B2") ]) ] (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick
    Assert.Equal(AwaitingApiResponse(0, DoNavigate "X1-TEST-B2", NavigateBaseline "X1-TEST-B2"), job1.status)

    // Simulates the shell's recovery event on restart — the outcome of the
    // in-flight call is unknown, so it's treated as ambiguous (§13), never
    // silently re-issued as if nothing had happened.
    let job2, effects2 =
        Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "Server wurde neu gestartet"))

    Assert.Equal(Reconciling(0, DoNavigate "X1-TEST-B2", NavigateBaseline "X1-TEST-B2"), job2.status)
    Assert.Contains(ReconcileShipState("job1", Some "SHIP-1", 0), effects2)

// --- Part A: the 5 remaining actions (Milestone 9) ------------------------------------

[<Fact>]
let ``survey happy path waits for cooldown, no cargo change`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "survey", Map.empty) ] (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick

    Assert.Equal(AwaitingApiResponse(0, DoSurvey, SurveyBaseline None), job1.status)

    let expiration = epoch.AddSeconds(10.0)
    let job2, effects2 = Step.step clock job1 (ApiResponseReceived("job1", 0, SurveyOk(expiration.ToString("o"))))

    Assert.Equal(WaitingForCooldown expiration, job2.status)
    Assert.Equal(0, job2.lastKnownShip.Value.cargoUnits)
    Assert.Contains(StartWait("job1", expiration, CooldownWait), effects2)

[<Fact>]
let ``survey reconciliation both branches`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "survey", Map.empty) ] (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick
    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))

    let notYet = defaultShip
    let job3, effects3 = Step.step clock job2 (ApiResponseReceived("job1", 0, ReconciliationShip notYet))
    Assert.Equal(AwaitingApiResponse(1, DoSurvey, SurveyBaseline None), job3.status)
    Assert.Contains(QueueApiCall("job1", Some "SHIP-1", DoSurvey, 1), effects3)

    let job2b, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))
    let confirmed = { defaultShip with cooldownExpiration = Some(epoch.AddSeconds(10.0).ToString("o")) }
    let job3b, _ = Step.step clock job2b (ApiResponseReceived("job1", 0, ReconciliationShip confirmed))
    Assert.Equal(WaitingForCooldown(epoch.AddSeconds(10.0)), job3b.status)

[<Fact>]
let ``deliverContract happy path advances immediately`` () =
    let clock = fakeClock (ref epoch)
    let shipWithCargo = { defaultShip with cargoUnits = 5; cargoInventory = Map [ "IRON", 5 ] }

    let job0 =
        mkJob
            [ ApiAction(
                  "b1",
                  "deliverContract",
                  Map
                      [ "contractId", Literal(StringLit "contract-1")
                        "tradeSymbol", Literal(StringLit "IRON")
                        "units", Literal(NumberLit 5.0) ]
              ) ]
            (Some shipWithCargo)

    let job1, _ = Step.step clock job0 WakeTick
    Assert.Equal(AwaitingApiResponse(0, DoDeliverContract("contract-1", "IRON", 5), CargoBaseline 5), job1.status)

    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, DeliverOk(0, Map.empty, true)))
    Assert.Equal(Completed, job2.status)
    Assert.Equal(0, job2.lastKnownShip.Value.cargoUnits)

[<Fact>]
let ``deliverContract reconciliation both branches`` () =
    let clock = fakeClock (ref epoch)
    let shipWithCargo = { defaultShip with cargoUnits = 5; cargoInventory = Map [ "IRON", 5 ] }

    let job0 =
        mkJob
            [ ApiAction(
                  "b1",
                  "deliverContract",
                  Map
                      [ "contractId", Literal(StringLit "contract-1")
                        "tradeSymbol", Literal(StringLit "IRON")
                        "units", Literal(NumberLit 5.0) ]
              ) ]
            (Some shipWithCargo)

    let job1, _ = Step.step clock job0 WakeTick
    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))

    let unchanged = shipWithCargo
    let job3, effects3 = Step.step clock job2 (ApiResponseReceived("job1", 0, ReconciliationShip unchanged))
    Assert.Equal(AwaitingApiResponse(1, DoDeliverContract("contract-1", "IRON", 5), CargoBaseline 5), job3.status)
    Assert.Contains(QueueApiCall("job1", Some "SHIP-1", DoDeliverContract("contract-1", "IRON", 5), 1), effects3)

    let job2b, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))
    let delivered = { defaultShip with cargoUnits = 0; cargoInventory = Map.empty }
    let job3b, _ = Step.step clock job2b (ApiResponseReceived("job1", 0, ReconciliationShip delivered))
    Assert.Equal(Completed, job3b.status)

[<Fact>]
let ``acceptContract happy path advances immediately, no ship change`` () =
    let clock = fakeClock (ref epoch)

    let job0 =
        mkJob [ ApiAction("b1", "acceptContract", Map [ "contractId", Literal(StringLit "contract-1") ]) ] (Some defaultShip)

    let job1, _ = Step.step clock job0 WakeTick
    Assert.Equal(AwaitingApiResponse(0, DoAcceptContract "contract-1", AcceptContractBaseline "contract-1"), job1.status)

    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, AcceptContractOk true))
    Assert.Equal(Completed, job2.status)

[<Fact>]
let ``acceptContract reconciliation via contract fetch, not ship state`` () =
    let clock = fakeClock (ref epoch)

    let job0 =
        mkJob [ ApiAction("b1", "acceptContract", Map [ "contractId", Literal(StringLit "contract-1") ]) ] (Some defaultShip)

    let job1, _ = Step.step clock job0 WakeTick
    let job2, effects2 = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))

    Assert.Equal(Reconciling(0, DoAcceptContract "contract-1", AcceptContractBaseline "contract-1"), job2.status)
    Assert.Contains(ReconcileContractState("job1", "contract-1", 0), effects2)

    // not yet accepted -> retries the original action
    let job3, effects3 = Step.step clock job2 (ApiResponseReceived("job1", 0, ReconciliationContract false))
    Assert.Equal(AwaitingApiResponse(1, DoAcceptContract "contract-1", AcceptContractBaseline "contract-1"), job3.status)
    Assert.Contains(QueueApiCall("job1", Some "SHIP-1", DoAcceptContract "contract-1", 1), effects3)

    // already accepted -> advances without a duplicate call
    let job2b, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))
    let job3b, _ = Step.step clock job2b (ApiResponseReceived("job1", 0, ReconciliationContract true))
    Assert.Equal(Completed, job3b.status)

/// §14 follow-up regression test: `acceptContract`/`purchaseShip` never read a ship
/// snapshot, so a ship-agnostic job (`lastKnownShip = None`, e.g. one started with no
/// ship at all) must still be able to run them — before the `emitApiAction` fix, *any*
/// `ApiAction` (including these two) was incorrectly gated behind `Some ship`.
[<Fact>]
let ``acceptContract and purchaseShip succeed even with no ship at all`` () =
    let clock = fakeClock (ref epoch)

    let acceptJob =
        mkJob [ ApiAction("b1", "acceptContract", Map [ "contractId", Literal(StringLit "contract-1") ]) ] None

    let acceptJob1, _ = Step.step clock acceptJob WakeTick
    Assert.Equal(AwaitingApiResponse(0, DoAcceptContract "contract-1", AcceptContractBaseline "contract-1"), acceptJob1.status)

    let purchaseJob =
        mkJob
            [ ApiAction(
                  "b1",
                  "purchaseShip",
                  Map [ "shipType", Literal(StringLit "SHIP_MINING_DRONE"); "waypointSymbol", Literal(StringLit "X1-TEST-A1") ]
              ) ]
            None

    let purchaseJob1, _ = Step.step clock purchaseJob WakeTick
    Assert.Equal(AwaitingApiResponse(0, DoPurchaseShip("SHIP_MINING_DRONE", "X1-TEST-A1"), FleetBaseline 2), purchaseJob1.status)

/// A genuinely ship-scoped action (unlike the two above) must still fail gracefully
/// with no ship — the fixed gate is narrower, not gone.
[<Fact>]
let ``a ship-scoped action still fails clearly with no ship at all`` () =
    let clock = fakeClock (ref epoch)
    let job = mkJob [ ApiAction("b1", "orbit", Map.empty) ] None
    let job1, effects1 = Step.step clock job WakeTick

    match job1.status with
    | Failed msg -> Assert.Equal("Kein Schiff ausgewählt.", msg)
    | other -> Assert.Fail($"expected Failed, got {other}")

    Assert.Contains(JobFailed("job1", "Kein Schiff ausgewählt."), effects1)

/// §14 follow-up: a ship-agnostic info read (e.g. `getWaypoints`) must succeed with
/// no ship symbol at all (it never reads `job.shipSymbol` server-side anyway).
[<Fact>]
let ``a ship-agnostic info read succeeds with no ship symbol at all`` () =
    let clock = fakeClock (ref epoch)
    let job = { mkJob [ InfoRead("b1", "getWaypoints", Map.empty, "$t1") ] None with shipSymbol = None }
    let job1, effects1 = Step.step clock job WakeTick
    Assert.Equal(AwaitingInfoResponse(0, "getWaypoints", Map.empty, "$t1"), job1.status)
    Assert.Contains(QueueInfoRead("job1", None, "getWaypoints", Map.empty, 0, "$t1"), effects1)

/// A genuinely ship-scoped info read (getFuel/getShipInfo/getCargo) must still fail
/// clearly with no ship symbol, not silently thread a missing identity through to a
/// real API call.
[<Fact>]
let ``a ship-scoped info read fails clearly with no ship symbol at all`` () =
    let clock = fakeClock (ref epoch)
    let job = { mkJob [ InfoRead("b1", "getFuel", Map.empty, "$t1") ] None with shipSymbol = None }
    let job1, effects1 = Step.step clock job WakeTick

    match job1.status with
    | Failed msg -> Assert.Equal("Kein Schiff ausgewählt.", msg)
    | other -> Assert.Fail($"expected Failed, got {other}")

    Assert.Contains(JobFailed("job1", "Kein Schiff ausgewählt."), effects1)

[<Fact>]
let ``purchaseShip happy path advances immediately, updates fleet count`` () =
    let clock = fakeClock (ref epoch)

    let job0 =
        mkJob
            [ ApiAction(
                  "b1",
                  "purchaseShip",
                  Map [ "shipType", Literal(StringLit "SHIP_MINING_DRONE"); "waypointSymbol", Literal(StringLit "X1-TEST-A1") ]
              ) ]
            (Some defaultShip)

    let job1, _ = Step.step clock job0 WakeTick
    Assert.Equal(AwaitingApiResponse(0, DoPurchaseShip("SHIP_MINING_DRONE", "X1-TEST-A1"), FleetBaseline 2), job1.status)

    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, PurchaseShipOk("FAKE-AGENT-3", 3)))
    Assert.Equal(Completed, job2.status)
    Assert.Equal(Some 3, job2.lastKnownFleetShipCount)

[<Fact>]
let ``purchaseShip reconciliation via fleet fetch, not ship state`` () =
    let clock = fakeClock (ref epoch)

    let job0 =
        mkJob
            [ ApiAction(
                  "b1",
                  "purchaseShip",
                  Map [ "shipType", Literal(StringLit "SHIP_MINING_DRONE"); "waypointSymbol", Literal(StringLit "X1-TEST-A1") ]
              ) ]
            (Some defaultShip)

    let job1, _ = Step.step clock job0 WakeTick
    let job2, effects2 = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))

    Assert.Equal(Reconciling(0, DoPurchaseShip("SHIP_MINING_DRONE", "X1-TEST-A1"), FleetBaseline 2), job2.status)
    Assert.Contains(ReconcileFleetState("job1", 0), effects2)

    // fleet count unchanged -> retries the original action
    let job3, effects3 = Step.step clock job2 (ApiResponseReceived("job1", 0, ReconciliationFleet 2))
    Assert.Equal(AwaitingApiResponse(1, DoPurchaseShip("SHIP_MINING_DRONE", "X1-TEST-A1"), FleetBaseline 2), job3.status)
    Assert.Contains(QueueApiCall("job1", Some "SHIP-1", DoPurchaseShip("SHIP_MINING_DRONE", "X1-TEST-A1"), 1), effects3)

    // fleet count increased -> advances without a duplicate call
    let job2b, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))
    let job3b, _ = Step.step clock job2b (ApiResponseReceived("job1", 0, ReconciliationFleet 3))
    Assert.Equal(Completed, job3b.status)
    Assert.Equal(Some 3, job3b.lastKnownFleetShipCount)

[<Fact>]
let ``refuel happy path advances immediately, updates ship fuel`` () =
    let clock = fakeClock (ref epoch)
    let lowFuel = { defaultShip with fuelCurrent = 100 }
    let job0 = mkJob [ ApiAction("b1", "refuel", Map.empty) ] (Some lowFuel)
    let job1, _ = Step.step clock job0 WakeTick

    Assert.Equal(AwaitingApiResponse(0, DoRefuel, FuelBaseline 100), job1.status)

    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, RefuelOk 400))
    Assert.Equal(Completed, job2.status)
    Assert.Equal(400, job2.lastKnownShip.Value.fuelCurrent)

[<Fact>]
let ``refuel reconciliation both branches`` () =
    let clock = fakeClock (ref epoch)
    let lowFuel = { defaultShip with fuelCurrent = 100 }
    let job0 = mkJob [ ApiAction("b1", "refuel", Map.empty) ] (Some lowFuel)
    let job1, _ = Step.step clock job0 WakeTick
    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))

    let unchanged = lowFuel
    let job3, effects3 = Step.step clock job2 (ApiResponseReceived("job1", 0, ReconciliationShip unchanged))
    Assert.Equal(AwaitingApiResponse(1, DoRefuel, FuelBaseline 100), job3.status)
    Assert.Contains(QueueApiCall("job1", Some "SHIP-1", DoRefuel, 1), effects3)

    let job2b, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))
    let refueled = { lowFuel with fuelCurrent = 400 }
    let job3b, _ = Step.step clock job2b (ApiResponseReceived("job1", 0, ReconciliationShip refueled))
    Assert.Equal(Completed, job3b.status)

// --- Part B: info-read blocks and accessors (Milestone 9, §8) -------------------------

[<Fact>]
let ``getFuel info-read happy path sets the result local, no reconciliation ever`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ InfoRead("b1", "getFuel", Map.empty, "$t1") ] (Some defaultShip)
    let job1, effects1 = Step.step clock job0 WakeTick
    Assert.Equal(AwaitingInfoResponse(0, "getFuel", Map.empty, "$t1"), job1.status)
    Assert.Contains(QueueInfoRead("job1", Some "SHIP-1", "getFuel", Map.empty, 0, "$t1"), effects1)

    let job2, effects2 = Step.step clock job1 (ApiResponseReceived("job1", 0, InfoOk(VNumber 400.0)))
    Assert.Equal(Completed, job2.status)
    Assert.Contains(JobCompleted "job1", effects2)

    match job2.stack with
    | top :: _ -> Assert.Equal(VNumber 400.0, top.locals.["$t1"])
    | [] -> Assert.Fail("expected a stack frame")

[<Fact>]
let ``info-read ambiguous failure re-issues the same fetch, never reconciles`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ InfoRead("b1", "getFuel", Map.empty, "$t1") ] (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick
    let job2, effects2 = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))

    Assert.Equal(AwaitingInfoResponse(1, "getFuel", Map.empty, "$t1"), job2.status)
    Assert.Contains(QueueInfoRead("job1", Some "SHIP-1", "getFuel", Map.empty, 1, "$t1"), effects2)

[<Fact>]
let ``info-read followed by an accessor chain resolves the field end-to-end`` () =
    let clock = fakeClock (ref epoch)

    let instructions =
        [ InfoRead("b1", "getShipInfo", Map.empty, "$t1")
          SetVariable("b2", "treibstoff", Accessor("Treibstoff", TempRef "$t1")) ]

    let job0 = mkJob instructions (Some defaultShip)
    let job1, _ = Step.step clock job0 WakeTick
    Assert.Equal(AwaitingInfoResponse(0, "getShipInfo", Map.empty, "$t1"), job1.status)

    let shipRecord = VRecord(Map.ofList [ "Treibstoff", VNumber 380.0 ])
    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, InfoOk shipRecord))
    Assert.Equal(Completed, job2.status)

    match job2.stack with
    | top :: _ -> Assert.Equal(VNumber 380.0, top.locals.["treibstoff"])
    | [] -> Assert.Fail("expected a stack frame")

// --- Custom-block call-stack execution (§9d, Milestone 9/Part A) ---------------------

[<Fact>]
let ``a custom-block call binds its return value into the caller's resultTarget`` () =
    let clock = fakeClock (ref epoch)

    let verdopple =
        mkCustomBlock
            [ SetVariable("d1", "x", Arithmetic("MULTIPLY", ParamRef "Zahl", Literal(NumberLit 2.0))) ]
            (Some(VariableRef "x"))

    let instructions =
        [ CallCustomBlock("call1", "verdopple", Map [ "Zahl", Literal(NumberLit 5.0) ], Some "$t1")
          SetVariable("b2", "ergebnis", TempRef "$t1") ]

    let job0 = mkJobWithCustomBlocks (Map [ "verdopple", verdopple ]) instructions None
    let job1, effects1 = Step.step clock job0 WakeTick

    Assert.Equal(Completed, job1.status)
    Assert.Contains(JobCompleted "job1", effects1)

    match job1.stack with
    | top :: [] -> Assert.Equal(VNumber 10.0, top.locals.["ergebnis"])
    | _ -> Assert.Fail("expected exactly one (the main) frame left")

[<Fact>]
let ``a void custom-block call (no resultTarget) executes its body without binding anything`` () =
    let clock = fakeClock (ref epoch)

    let grussen = mkCustomBlock [ ShowMessage("g1", Literal(StringLit "Hallo!")) ] None

    let instructions = [ CallCustomBlock("call1", "gruessen", Map.empty, None) ]
    let job0 = mkJobWithCustomBlocks (Map [ "gruessen", grussen ]) instructions None
    let job1, _ = Step.step clock job0 WakeTick

    Assert.Equal(Completed, job1.status)
    Assert.Contains<string>("Hallo!", job1.log)

[<Fact>]
let ``nested custom-block calls resolve correctly through two levels`` () =
    let clock = fakeClock (ref epoch)

    let double =
        mkCustomBlock
            [ SetVariable("dd1", "x", Arithmetic("MULTIPLY", ParamRef "n", Literal(NumberLit 2.0))) ]
            (Some(VariableRef "x"))

    let quadruple =
        mkCustomBlock
            [ CallCustomBlock("ic1", "double", Map [ "n", ParamRef "n" ], Some "$q1")
              CallCustomBlock("ic2", "double", Map [ "n", TempRef "$q1" ], Some "$q2") ]
            (Some(TempRef "$q2"))

    let instructions =
        [ CallCustomBlock("call1", "quadruple", Map [ "n", Literal(NumberLit 3.0) ], Some "$t1")
          SetVariable("b2", "result", TempRef "$t1") ]

    let job0 = mkJobWithCustomBlocks (Map [ "double", double; "quadruple", quadruple ]) instructions None
    let job1, _ = Step.step clock job0 WakeTick

    Assert.Equal(Completed, job1.status)

    match job1.stack with
    | top :: [] -> Assert.Equal(VNumber 12.0, top.locals.["result"])
    | _ -> Assert.Fail("expected exactly one (the main) frame left")

[<Fact>]
let ``a call whose body suspends on an ApiAction keeps the caller frame on the stack until it resolves`` () =
    let clock = fakeClock (ref epoch)

    let orbitThenOne = mkCustomBlock [ ApiAction("ob1", "orbit", Map.empty) ] (Some(Literal(NumberLit 1.0)))

    let instructions =
        [ CallCustomBlock("call1", "orbitThenOne", Map.empty, Some "$t1")
          SetVariable("b2", "result", TempRef "$t1") ]

    let job0 = mkJobWithCustomBlocks (Map [ "orbitThenOne", orbitThenOne ]) instructions (Some defaultShip)
    let job1, effects1 = Step.step clock job0 WakeTick

    Assert.Equal(AwaitingApiResponse(0, DoOrbit, DockOrbitBaseline "IN_ORBIT"), job1.status)
    Assert.Contains(QueueApiCall("job1", Some "SHIP-1", DoOrbit, 0), effects1)
    Assert.Equal(2, List.length job1.stack)
    Assert.Equal<(string * string option) list>([ "orbitThenOne", Some "ob1"; "main", Some "call1" ], Step.blockIdPerFrame job1)

    let job2, effects2 = Step.step clock job1 (ApiResponseReceived("job1", 0, NavResultOk("IN_ORBIT", "X1-TEST-A1")))

    Assert.Equal(Completed, job2.status)
    Assert.Contains(JobCompleted "job1", effects2)
    Assert.Equal(1, List.length job2.stack)
    Assert.Equal<(string * string option) list>([ "main", None ], Step.blockIdPerFrame job2)

    match job2.stack with
    | top :: [] -> Assert.Equal(VNumber 1.0, top.locals.["result"])
    | _ -> Assert.Fail("expected exactly one (the main) frame left")
