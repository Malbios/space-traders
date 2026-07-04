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

let private mkJob (instructions: Instruction list) (lastKnownShip: ShipSnapshot option) : JobState =
    { jobId = "job1"
      program = mkProgram instructions
      shipSymbol = "SHIP-1"
      status = Running
      stack =
        [ { scope = "main"
            position = [ { bodyRef = MainBody; index = 0; loopState = None } ]
            locals = Map.empty } ]
      lastKnownShip = lastKnownShip
      log = [] }

let private defaultShip: ShipSnapshot =
    { navStatus = "DOCKED"
      navWaypoint = "X1-TEST-A1"
      navArrival = None
      cargoUnits = 0
      cargoInventory = Map.empty
      cooldownExpiration = None }

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

// --- Loop counter persistence across steps ------------------------------------------

[<Fact>]
let ``a repeat loop's counter persists across steps and re-enters its body each time`` () =
    let clock = fakeClock (ref epoch)
    let instructions = [ Repeat("rep", Literal(NumberLit 3.0), [ ApiAction("act1", "orbit", Map.empty) ]) ]
    let job0 = mkJob instructions (Some defaultShip)

    let job1, effects1 = Step.step clock job0 WakeTick
    Assert.Equal(AwaitingApiResponse(0, DoOrbit, DockOrbitBaseline "IN_ORBIT"), job1.status)
    Assert.Contains(QueueApiCall("job1", "SHIP-1", DoOrbit, 0), effects1)

    let respondOrbit (job: JobState) =
        Step.step clock job (ApiResponseReceived("job1", 0, NavResultOk("IN_ORBIT", "X1-TEST-A1")))

    let job2, _ = respondOrbit job1
    Assert.Equal(AwaitingApiResponse(0, DoOrbit, DockOrbitBaseline "IN_ORBIT"), job2.status)

    let job3, _ = respondOrbit job2
    Assert.Equal(AwaitingApiResponse(0, DoOrbit, DockOrbitBaseline "IN_ORBIT"), job3.status)

    let job4, effects4 = respondOrbit job3
    Assert.Equal(Completed, job4.status)
    Assert.Contains(JobCompleted "job1", effects4)

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
    Assert.Contains(ReconcileShipState("job1", "SHIP-1", 0), effects2)

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
    Assert.Contains(QueueApiCall("job1", "SHIP-1", DoNavigate "X1-TEST-B2", 1), effects3)

[<Fact>]
let ``dock/orbit reconciliation both branches`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ ApiAction("b1", "dock", Map.empty) ] (Some { defaultShip with navStatus = "IN_ORBIT" })
    let job1, _ = Step.step clock job0 WakeTick
    let job2, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))

    let notYet = { defaultShip with navStatus = "IN_ORBIT" }
    let job3, effects3 = Step.step clock job2 (ApiResponseReceived("job1", 0, ReconciliationShip notYet))
    Assert.Equal(AwaitingApiResponse(1, DoDock, DockOrbitBaseline "DOCKED"), job3.status)
    Assert.Contains(QueueApiCall("job1", "SHIP-1", DoDock, 1), effects3)

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
    Assert.Contains(QueueApiCall("job1", "SHIP-1", DoBuy("FUEL", 5), 1), effects3)

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
    Assert.Contains(QueueApiCall("job1", "SHIP-1", DoExtract, 1), effects3)

    // cargo changed but cooldown not newer — must still retry
    let job2b, _ = Step.step clock job1 (ApiResponseReceived("job1", 0, ApiAmbiguous "timeout"))
    let cargoOnly = { defaultShip with cargoUnits = 10; cargoInventory = Map [ "IRON", 10 ] }
    let job3b, effects3b = Step.step clock job2b (ApiResponseReceived("job1", 0, ReconciliationShip cargoOnly))
    Assert.Equal(AwaitingApiResponse(1, DoExtract, ExtractBaseline(None, 0)), job3b.status)
    Assert.Contains(QueueApiCall("job1", "SHIP-1", DoExtract, 1), effects3b)

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
let ``InfoRead fails the job with a clear German message instead of crashing`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ InfoRead("b1", "getFuel", Map.empty, "$t1") ] (Some defaultShip)
    let job1, effects1 = Step.step clock job0 WakeTick

    match job1.status with
    | Failed _ -> ()
    | other -> Assert.Fail($"expected Failed, got {other}")

    Assert.Contains(effects1, (function JobFailed _ -> true | _ -> false))

[<Fact>]
let ``CallCustomBlock fails the job with a clear German message instead of crashing`` () =
    let clock = fakeClock (ref epoch)
    let job0 = mkJob [ CallCustomBlock("b1", "custom1", Map.empty, None) ] (Some defaultShip)
    let job1, effects1 = Step.step clock job0 WakeTick

    match job1.status with
    | Failed _ -> ()
    | other -> Assert.Fail($"expected Failed, got {other}")

    Assert.Contains(effects1, (function JobFailed _ -> true | _ -> false))
