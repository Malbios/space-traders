module Tests

open Xunit
open SpaceKids.Core.Dsl

let private noCustomBlocks: string -> CustomBlockDefinition option = fun _ -> None

let private textBlock (id: string) (value: string) =
    $$"""{ "type": "text", "id": "{{id}}", "fields": { "TEXT": "{{value}}" } }"""

let private numberBlock (id: string) (value: float) =
    $$"""{ "type": "math_number", "id": "{{id}}", "fields": { "NUM": {{value}} } }"""

[<Fact>]
let ``compiles a simple action-only program`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "navigate", "id": "b1", "inputs": {
                "DESTINATION": { "block": """ + textBlock "b1t" "X1-DF55-A1" + """ }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ ApiAction("b1", "navigate", Map [ "destination", Literal(StringLit "X1-DF55-A1") ]) ],
            program.instructions
        )

/// Regression test: a real bug found live — `purchaseShip`'s "WAYPOINT" input
/// compiled to the DSL arg key "waypoint", but `Step.fs`'s `emitApiAction`
/// looks up "waypointSymbol" — the two names never matched, so every
/// `purchaseShip` block failed with "Fehlendes Argument \"waypointSymbol\""
/// regardless of how it was wired. `SchedulerTests.fs`'s own `purchaseShip`
/// tests never caught this because they construct `ApiAction` instructions
/// directly, bypassing this exact compiler seam.
[<Fact>]
let ``compiles purchaseShip's WAYPOINT input to the "waypointSymbol" arg key`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "purchaseShip", "id": "b1", "inputs": {
                "SHIP_TYPE": { "block": """ + textBlock "b1a" "SHIP_MINING_DRONE" + """ },
                "WAYPOINT": { "block": """ + textBlock "b1b" "X1-DF55-A1" + """ }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ ApiAction(
                  "b1",
                  "purchaseShip",
                  Map [ "shipType", Literal(StringLit "SHIP_MINING_DRONE"); "waypointSymbol", Literal(StringLit "X1-DF55-A1") ]
              ) ],
            program.instructions
        )

[<Fact>]
let ``compiles supplyConstruction and patchShipNav arg keys`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "supplyConstruction", "id": "b1", "inputs": {
                "WAYPOINT_SYMBOL": { "block": """ + textBlock "b1a" "X1-NEARBY-C1" + """ },
                "TRADE_SYMBOL": { "block": """ + textBlock "b1b" "IRON" + """ },
                "UNITS": { "block": { "type": "math_number", "id": "b1c", "fields": { "NUM": 10 } } }
            } },
            { "type": "patchShipNav", "id": "b2", "inputs": {
                "FLIGHT_MODE": { "block": """ + textBlock "b2a" "BURN" + """ }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ ApiAction(
                  "b1",
                  "supplyConstruction",
                  Map
                      [ "waypointSymbol", Literal(StringLit "X1-NEARBY-C1")
                        "tradeSymbol", Literal(StringLit "IRON")
                        "units", Literal(NumberLit 10.0) ]
              )
              ApiAction("b2", "patchShipNav", Map [ "flightMode", Literal(StringLit "BURN") ]) ],
            program.instructions
        )

[<Fact>]
let ``an information block used inside an expression compiles to a hoisted temp (the §10 example)`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "variables_set", "id": "b1", "fields": { "VAR": "Markt" }, "inputs": {
                "VALUE": { "block": { "type": "getMarket", "id": "b2", "inputs": {
                    "WAYPOINT_SYMBOL": { "block": """ + textBlock "b2t" "X1-DF55-A1" + """ }
                } } }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ InfoRead("b2", "getMarket", Map [ "waypointSymbol", Literal(StringLit "X1-DF55-A1") ], "$t1")
              SetVariable("b1", "Markt", TempRef "$t1") ],
            program.instructions
        )

[<Fact>]
let ``compiles nested controls_if, controls_repeat_ext, and controls_forEach`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "controls_repeat_ext", "id": "rep", "inputs": {
                "TIMES": { "block": """ + numberBlock "n1" 3.0 + """ },
                "DO": { "block": { "type": "sk_wait", "id": "w1", "inputs": {
                    "SECONDS": { "block": """ + numberBlock "n2" 1.0 + """ }
                } } }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ Repeat("rep", Literal(NumberLit 3.0), [ Wait("w1", Literal(NumberLit 1.0)) ]) ],
            program.instructions
        )

[<Fact>]
let ``compiles logic_boolean to a BoolLit literal`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "variables_set", "id": "b1", "fields": { "VAR": "Flag" }, "inputs": {
                "VALUE": { "block": { "type": "logic_boolean", "id": "b2", "fields": { "BOOL": "TRUE" } } }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>([ SetVariable("b1", "Flag", Literal(BoolLit true)) ], program.instructions)

[<Fact>]
let ``compiles logic_operation and logic_negate`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "variables_set", "id": "b1", "fields": { "VAR": "Flag" }, "inputs": {
                "VALUE": { "block": { "type": "logic_operation", "id": "b2", "fields": { "OP": "OR" }, "inputs": {
                    "A": { "block": { "type": "logic_boolean", "id": "b2a", "fields": { "BOOL": "FALSE" } } },
                    "B": { "block": { "type": "logic_negate", "id": "b2b", "inputs": {
                        "BOOL": { "block": { "type": "logic_boolean", "id": "b2c", "fields": { "BOOL": "FALSE" } } }
                    } } }
                } } }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ SetVariable(
                  "b1",
                  "Flag",
                  LogicalOp("OR", Literal(BoolLit false), LogicalNot(Literal(BoolLit false)))
              ) ],
            program.instructions
        )

[<Fact>]
let ``compiles controls_flow_statements to Break or Continue depending on FLOW`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "controls_forEach", "id": "loop1", "fields": { "VAR": "x" }, "inputs": {
                "LIST": { "block": { "type": "lists_create_with", "id": "l1", "extraState": { "itemCount": 0 } } },
                "DO": { "block": { "type": "controls_flow_statements", "id": "brk1", "fields": { "FLOW": "BREAK" }, "next": {
                    "block": { "type": "controls_flow_statements", "id": "cnt1", "fields": { "FLOW": "CONTINUE" } }
                } } }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ ForEach("loop1", "x", ListLiteral [], [ Break "brk1"; Continue "cnt1" ]) ],
            program.instructions
        )

[<Fact>]
let ``compiles withShip with unavailable branch and internal scope exit`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "withShip", "id": "ws1", "extraState": { "hasUnavailable": true }, "inputs": {
                "SHIP": { "block": """ + textBlock "ship1" "FAKE-AGENT-2" + """ },
                "DO": { "block": { "type": "orbit", "id": "orbit1" } },
                "ELSE": { "block": { "type": "sk_show_message", "id": "msg1", "inputs": {
                    "TEXT": { "block": """ + textBlock "msgtext" "missing" + """ }
                } } }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ WithShip(
                  "ws1",
                  Literal(StringLit "FAKE-AGENT-2"),
                  [ ApiAction("orbit1", "orbit", Map.empty); ExitShipScope "ws1:exit" ],
                  Some [ ShowMessage("msg1", Literal(StringLit "missing")) ]
              ) ],
            program.instructions
        )

[<Fact>]
let ``compiles withShip without unavailable branch by default`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "withShip", "id": "ws1", "inputs": {
                "SHIP": { "block": """ + textBlock "ship1" "FAKE-AGENT-2" + """ },
                "DO": { "block": { "type": "orbit", "id": "orbit1" } }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ WithShip(
                  "ws1",
                  Literal(StringLit "FAKE-AGENT-2"),
                  [ ApiAction("orbit1", "orbit", Map.empty); ExitShipScope "ws1:exit" ],
                  None
              ) ],
            program.instructions
        )

[<Fact>]
let ``compiles parallel with mutator branch count`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "parallel", "id": "par1", "extraState": { "branchCount": 3 }, "inputs": {
                "DO0": { "block": { "type": "orbit", "id": "orbit1" } },
                "DO1": { "block": { "type": "dock", "id": "dock1" } },
                "DO2": { "block": { "type": "sk_show_message", "id": "msg1", "inputs": {
                    "TEXT": { "block": """ + textBlock "msgtext" "done" + """ }
                } } }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ Parallel(
                  "par1",
                  [ [ ApiAction("orbit1", "orbit", Map.empty) ]
                    [ ApiAction("dock1", "dock", Map.empty) ]
                    [ ShowMessage("msg1", Literal(StringLit "done")) ] ]
              ) ],
            program.instructions
        )

[<Fact>]
let ``validate rejects a Break outside any loop, but allows one inside a controls_forEach`` () =
    let outsideJson =
        """{ "blocks": { "languageVersion": 0, "blocks": [ { "type": "controls_flow_statements", "id": "brk1", "fields": { "FLOW": "BREAK" } } ] } }"""

    match Compiler.compileWorkspace De noCustomBlocks outsideJson with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        let errors = Validator.validate De program
        Assert.Contains(errors, fun e -> e.blockId = Some "brk1")

    let insideJson =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "controls_forEach", "id": "loop1", "fields": { "VAR": "x" }, "inputs": {
                "LIST": { "block": { "type": "lists_create_with", "id": "l1", "extraState": { "itemCount": 0 } } },
                "DO": { "block": { "type": "controls_flow_statements", "id": "brk2", "fields": { "FLOW": "BREAK" } } }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks insideJson with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        let errors = Validator.validate De program
        Assert.DoesNotContain(errors, fun e -> e.blockId = Some "brk2")

// --- programRequiresShip (§14 follow-up: ship-agnostic programs) ----------------------

let private aProgram (instructions: Instruction list) : CompiledProgram =
    { version = 1; customBlocks = Map.empty; instructions = instructions }

[<Fact>]
let ``programRequiresShip is false for a program using only ship-agnostic blocks`` () =
    let program =
        aProgram
            [ InfoRead("b1", "getWaypoints", Map.empty, "$t1")
              InfoRead("b2", "getShipyard", Map.empty, "$t2")
              ApiAction(
                  "b3",
                  "purchaseShip",
                  Map [ "shipType", Literal(StringLit "SHIP_MINING_DRONE"); "waypointSymbol", Literal(StringLit "X1-TEST-A1") ]
              )
              ApiAction("b4", "acceptContract", Map [ "contractId", Literal(StringLit "contract-1") ])
              ApiAction("b5", "fulfillContract", Map [ "contractId", Literal(StringLit "contract-1") ]) ]

    Assert.False(Validator.programRequiresShip program)

[<Fact>]
let ``programRequiresShip is true for a program using a ship-scoped action`` () =
    let program = aProgram [ ApiAction("b1", "navigate", Map [ "destination", Literal(StringLit "X1-TEST-B2") ]) ]
    Assert.True(Validator.programRequiresShip program)

[<Fact>]
let ``programRequiresShip is true for a program using a ship-scoped info read`` () =
    let program = aProgram [ InfoRead("b1", "getFuel", Map.empty, "$t1") ]
    Assert.True(Validator.programRequiresShip program)

[<Fact>]
let ``programRequiresShip detects a ship-scoped block nested inside If/loop bodies`` () =
    let program =
        aProgram
            [ ForEach(
                  "loop1",
                  "x",
                  ListLiteral [],
                  [ If("if1", [ (Literal(BoolLit true), [ ApiAction("b1", "dock", Map.empty) ]) ], None) ]
              ) ]

    Assert.True(Validator.programRequiresShip program)

[<Fact>]
let ``programRequiresShip detects a ship-scoped block inside a called custom block`` () =
    let shipScopedBlock: CompiledCustomBlock =
        { signature = { inputs = []; output = None; outputFields = None }
          instructions = [ ApiAction("b1", "orbit", Map.empty) ]
          returnExpr = None }

    let program =
        { version = 1
          customBlocks = Map [ "custom-1", shipScopedBlock ]
          instructions = [ CallCustomBlock("b2", "custom-1", Map.empty, None) ] }

    Assert.True(Validator.programRequiresShip program)

[<Fact>]
let ``programRequiresShip is false when ship-scoped work is only inside withShip`` () =
    let program =
        aProgram
            [ WithShip(
                  "ws1",
                  Literal(StringLit "FAKE-AGENT-1"),
                  [ ApiAction("b1", "negotiateContract", Map.empty); ExitShipScope "ws1:exit" ],
                  None
              ) ]

    Assert.False(Validator.programRequiresShip program)

[<Fact>]
let ``programRequiresShip is true when withShip unavailable branch uses a ship-scoped block`` () =
    let program =
        aProgram
            [ WithShip(
                  "ws1",
                  Literal(StringLit "FAKE-AGENT-2"),
                  [ ApiAction("b1", "orbit", Map.empty); ExitShipScope "ws1:exit" ],
                  Some [ ApiAction("b2", "dock", Map.empty) ]
              ) ]

    Assert.True(Validator.programRequiresShip program)

[<Fact>]
let ``programRequiresShip is false when a ship-scoped custom block is only called inside withShip`` () =
    let shipScopedBlock: CompiledCustomBlock =
        { signature = { inputs = []; output = None; outputFields = None }
          instructions = [ ApiAction("b1", "orbit", Map.empty) ]
          returnExpr = None }

    let program =
        { version = 1
          customBlocks = Map [ "custom-1", shipScopedBlock ]
          instructions =
            [ WithShip(
                  "ws1",
                  Literal(StringLit "FAKE-AGENT-1"),
                  [ CallCustomBlock("b2", "custom-1", Map.empty, None); ExitShipScope "ws1:exit" ],
                  None
              ) ] }

    Assert.False(Validator.programRequiresShip program)

[<Fact>]
let ``findShipRequirementAtStart names the first ship-scoped block outside withShip`` () =
    let program = aProgram [ ApiAction("nav-1", "navigate", Map [ "destination", Literal(StringLit "X1") ]) ]
    let req = Validator.findShipRequirementAtStart program |> Option.get
    Assert.Equal("nav-1", req.blockId)
    Assert.Equal("navigate", req.kind)

[<Fact>]
let ``findShipRequirementAtStart returns None for purchaseShip only`` () =
    let program =
        aProgram
            [ ApiAction(
                  "buy-1",
                  "purchaseShip",
                  Map [ "shipType", Literal(StringLit "SHIP_MINING_DRONE"); "waypointSymbol", Literal(StringLit "X1-TEST-A1") ]
              ) ]

    Assert.True(Validator.findShipRequirementAtStart program |> Option.isNone)

[<Fact>]
let ``programRequiresShip ignores unreachable custom blocks that contain ship-scoped work`` () =
    let shipScopedBlock: CompiledCustomBlock =
        { signature = { inputs = []; output = None; outputFields = None }
          instructions = [ ApiAction("b1", "orbit", Map.empty) ]
          returnExpr = None }

    let program =
        { version = 1
          customBlocks = Map [ "custom-1", shipScopedBlock ]
          instructions = [ ApiAction("b2", "acceptContract", Map [ "contractId", Literal(StringLit "c1") ]) ] }

    Assert.False(Validator.programRequiresShip program)

// --- Eval (previously-uncovered paths found in review) -------------------------------

[<Fact>]
let ``ListGet returns the item at a valid index`` () =
    let expr =
        ListGet(ListLiteral [ Literal(NumberLit 10.0); Literal(NumberLit 20.0); Literal(NumberLit 30.0) ], FromStart, Some(Literal(NumberLit 1.0)))

    Assert.Equal(VNumber 20.0, Eval.eval Map.empty expr)

[<Fact>]
let ``ListGet fails clearly on an out-of-range index`` () =
    let expr = ListGet(ListLiteral [ Literal(NumberLit 10.0) ], FromStart, Some(Literal(NumberLit 5.0)))
    let ex = Assert.Throws<System.Exception>(fun () -> Eval.eval Map.empty expr |> ignore)
    Assert.Equal("Listenindex außerhalb des gültigen Bereichs.", ex.Message)

[<Fact>]
let ``ListGet fails clearly on a negative index`` () =
    let expr = ListGet(ListLiteral [ Literal(NumberLit 10.0) ], FromStart, Some(Literal(NumberLit -1.0)))
    let ex = Assert.Throws<System.Exception>(fun () -> Eval.eval Map.empty expr |> ignore)
    Assert.Equal("Listenindex außerhalb des gültigen Bereichs.", ex.Message)

[<Fact>]
let ``ListGet supports FROM_END FIRST and LAST where modes`` () =
    let list = ListLiteral [ Literal(NumberLit 10.0); Literal(NumberLit 20.0); Literal(NumberLit 30.0) ]
    Assert.Equal(VNumber 30.0, Eval.eval Map.empty (ListGet(list, Last, None)))
    Assert.Equal(VNumber 10.0, Eval.eval Map.empty (ListGet(list, First, None)))
    Assert.Equal(VNumber 20.0, Eval.eval Map.empty (ListGet(list, FromEnd, Some(Literal(NumberLit 1.0)))))

[<Fact>]
let ``compiles lists_setIndex with FROM_END where mode`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "variables_set", "id": "init", "fields": { "VAR": { "id": "items", "name": "items", "type": "List" } },
              "inputs": { "VALUE": { "block": { "type": "lists_create_with", "id": "mk", "extraState": { "itemCount": 2 },
                "inputs": { "ADD0": { "block": { "type": "text", "id": "a", "fields": { "TEXT": "a" } } },
                            "ADD1": { "block": { "type": "text", "id": "b", "fields": { "TEXT": "b" } } } } } } } },
            { "type": "lists_setIndex", "id": "set1", "fields": { "MODE": "SET", "WHERE": "FROM_END" },
              "inputs": {
                "LIST": { "block": { "type": "variables_get", "id": "get", "fields": { "VAR": { "id": "items", "name": "items", "type": "List" } } } },
                "AT": { "block": { "type": "math_number", "id": "idx", "fields": { "NUM": 0 } } },
                "TO": { "block": { "type": "text", "id": "val", "fields": { "TEXT": "z" } } }
              } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Contains(
            program.instructions,
            function
            | ListSet("set1", "items", FromEnd, Some _, _) -> true
            | _ -> false
        )

/// Regression test: `asFloat`'s `VString` branch used to rely on the bare `float`
/// operator, which throws a raw, unlocalized `System.FormatException` instead of this
/// function's own consistent German `failwith` style used by every other branch.
[<Fact>]
let ``asFloat on a non-numeric string fails with a clear German message, not a raw FormatException`` () =
    let ex = Assert.Throws<System.Exception>(fun () -> Eval.asFloat (VString "abc") |> ignore)
    Assert.Equal("Erwarte eine Zahl, aber der Text \"abc\" lässt sich nicht in eine Zahl umwandeln.", ex.Message)

[<Fact>]
let ``rejects an unknown block type`` () =
    let json = """{ "blocks": { "languageVersion": 0, "blocks": [ { "type": "totally_unknown", "id": "b1" } ] } }"""

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Ok program -> Assert.Fail($"expected Error, got: %A{program}")
    | Error errors -> Assert.Contains(errors, fun e -> e.message.Contains("Unbekannter Blocktyp"))

[<Fact>]
let ``unknown-block-type compile error is English when the locale is English (Milestone 13)`` () =
    let json = """{ "blocks": { "languageVersion": 0, "blocks": [ { "type": "totally_unknown", "id": "b1" } ] } }"""

    match Compiler.compileWorkspace En noCustomBlocks json with
    | Ok program -> Assert.Fail($"expected Error, got: %A{program}")
    | Error errors -> Assert.Contains(errors, fun e -> e.message.Contains("Unknown block type"))

[<Fact>]
let ``missing-input compile error is English when the locale is English (Milestone 13)`` () =
    let json = """{ "blocks": { "languageVersion": 0, "blocks": [ { "type": "navigate", "id": "b1" } ] } }"""

    match Compiler.compileWorkspace En noCustomBlocks json with
    | Ok program -> Assert.Fail($"expected Error, got: %A{program}")
    | Error errors -> Assert.Contains(errors, fun e -> e.message.Contains("is missing"))

[<Fact>]
let ``validate rejects a program with no start block`` () =
    let json = """{ "blocks": { "languageVersion": 0, "blocks": [] } }"""

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        let errors = Validator.validate De program
        Assert.Contains(errors, fun e -> e.message.Contains("keinen Startblock"))

[<Fact>]
let ``validate's no-start-block message is English when the locale is English (Milestone 12)`` () =
    let json = """{ "blocks": { "languageVersion": 0, "blocks": [] } }"""

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        let errors = Validator.validate En program
        Assert.Contains(errors, fun e -> e.message.Contains("no start block"))

[<Fact>]
let ``validate rejects a variable reference out of scope`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "sk_show_message", "id": "b1", "inputs": {
                "TEXT": { "block": { "type": "variables_get", "id": "v1", "fields": { "VAR": "Nichtdeklariert" } } }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        let errors = Validator.validate De program
        Assert.Contains(errors, fun e -> e.message.Contains("Nichtdeklariert"))

let private customBlockCallJson (blockId: string) (customBlockId: string) (argInputs: string) =
    $$"""{ "type": "callCustomBlock", "id": "{{blockId}}", "extraState": { "customBlockId": "{{customBlockId}}" }, "inputs": { {{argInputs}} } }"""

let private simpleWaitBody =
    """{ "blocks": { "languageVersion": 0, "blocks": [ { "type": "sk_wait", "id": "w1", "inputs": { "SECONDS": { "block": """
    + numberBlock "n1" 1.0
    + """ } } } ] } }"""

[<Fact>]
let ``resolveCustomBlockCall compiles the full transitive closure`` () =
    let blockB: CustomBlockDefinition =
        { id = "block-b"
          signature = { inputs = []; output = None; outputFields = None }
          workspaceJson = simpleWaitBody }

    let blockAWorkspace =
        """{ "blocks": { "languageVersion": 0, "blocks": [ """ + customBlockCallJson "call1" "block-b" "" + """ ] } }"""

    let blockA: CustomBlockDefinition =
        { id = "block-a"
          signature = { inputs = []; output = None; outputFields = None }
          workspaceJson = blockAWorkspace }

    let lookup =
        function
        | "block-a" -> Some blockA
        | "block-b" -> Some blockB
        | _ -> None

    match Compiler.resolveCustomBlockCall De lookup "block-a" with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok customBlocks ->
        Assert.True(customBlocks.ContainsKey "block-a")
        Assert.True(customBlocks.ContainsKey "block-b")
        Assert.Equal<Instruction list>([ Wait("w1", Literal(NumberLit 1.0)) ], customBlocks.["block-b"].instructions)

[<Fact>]
let ``resolveCustomBlockCall rejects a cycle`` () =
    let workspaceCallingOther (otherId: string) =
        """{ "blocks": { "languageVersion": 0, "blocks": [ """ + customBlockCallJson "call1" otherId "" + """ ] } }"""

    let blockX: CustomBlockDefinition =
        { id = "block-x"
          signature = { inputs = []; output = None; outputFields = None }
          workspaceJson = workspaceCallingOther "block-y" }

    let blockY: CustomBlockDefinition =
        { id = "block-y"
          signature = { inputs = []; output = None; outputFields = None }
          workspaceJson = workspaceCallingOther "block-x" }

    let lookup =
        function
        | "block-x" -> Some blockX
        | "block-y" -> Some blockY
        | _ -> None

    match Compiler.resolveCustomBlockCall De lookup "block-x" with
    | Ok customBlocks -> Assert.Fail($"expected Error (cycle), got: %A{customBlocks}")
    | Error errors -> Assert.Contains(errors, fun e -> e.message.Contains("ruft sich selbst auf"))

[<Fact>]
let ``validate rejects a custom-block call with mismatched arguments`` () =
    let blockDef: CustomBlockDefinition =
        { id = "needs-input"
          signature = { inputs = [ { name = "Anzahl"; inputType = "Anzahl" } ]; output = None; outputFields = None }
          workspaceJson = simpleWaitBody }

    let lookup =
        function
        | "needs-input" -> Some blockDef
        | _ -> None

    // The call site omits the required "Anzahl" input entirely.
    let json = """{ "blocks": { "languageVersion": 0, "blocks": [ """ + customBlockCallJson "call1" "needs-input" "" + """ ] } }"""

    match Compiler.compileWorkspace De lookup json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        let errors = Validator.validate De program
        Assert.Contains(errors, fun e -> e.message.Contains("Anzahl") && e.message.Contains("fehlt"))

/// Regression test: `literalTypeMismatch` used to check `inputType = "Zahl"`, a label
/// the real compiler/client never actually produce (`"Anzahl"`/`"Number"` are the real
/// ones) — so this check silently never fired. This is the first test anywhere to
/// exercise its true-path directly.
[<Fact>]
let ``validate rejects a string literal passed where a custom block expects a number`` () =
    let blockDef: CustomBlockDefinition =
        { id = "needs-number"
          signature = { inputs = [ { name = "Anzahl"; inputType = "Anzahl" } ]; output = None; outputFields = None }
          workspaceJson = simpleWaitBody }

    let lookup =
        function
        | "needs-number" -> Some blockDef
        | _ -> None

    let argInputs = """"Anzahl": { "block": """ + textBlock "t1" "not a number" + " }"

    let json =
        """{ "blocks": { "languageVersion": 0, "blocks": [ """
        + customBlockCallJson "call1" "needs-number" argInputs
        + """ ] } }"""

    match Compiler.compileWorkspace De lookup json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        let errors = Validator.validate De program
        Assert.Contains(errors, fun e -> e.message.Contains("Anzahl") && e.message.Contains("falschen Typ"))

[<Fact>]
let ``validate rejects an accessor number passed where a custom block expects a string`` () =
    let blockDef: CustomBlockDefinition =
        { id = "needs-ship"
          signature = { inputs = [ { name = "Schiff"; inputType = "Schiff" } ]; output = None; outputFields = None }
          workspaceJson = simpleWaitBody }

    let lookup =
        function
        | "needs-ship" -> Some blockDef
        | _ -> None

    let argInputs =
        """"Schiff": { "block": { "type": "shipFuel", "id": "fuel1", "inputs": {
            "TARGET": { "block": { "type": "sk_build_record", "id": "rec1", "extraState": { "fields": [] } } }
        } } }"""

    let json =
        """{ "blocks": { "languageVersion": 0, "blocks": [ """
        + customBlockCallJson "call1" "needs-ship" argInputs
        + """ ] } }"""

    match Compiler.compileWorkspace De lookup json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        let errors = Validator.validate De program
        Assert.Contains(errors, fun e -> e.message.Contains("Schiff") && e.message.Contains("falschen Typ"))

[<Fact>]
let ``revalidateAgainstCurrentDefinitions catches a signature that changed after compile`` () =
    let originalSignature = { inputs = []; output = None; outputFields = None }
    let blockDef: CustomBlockDefinition =
        { id = "block-a"; signature = originalSignature; workspaceJson = simpleWaitBody }

    let json = """{ "blocks": { "languageVersion": 0, "blocks": [ """ + customBlockCallJson "call1" "block-a" "" + """ ] } }"""

    let compileLookup = function
        | "block-a" -> Some blockDef
        | _ -> None

    match Compiler.compileWorkspace De compileLookup json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        // No drift yet — same lookup used to compile and to revalidate.
        Assert.Empty(Validator.revalidateAgainstCurrentDefinitions De compileLookup program)

        // The signature changed since compile (a new required input was added).
        let changedLookup =
            function
            | "block-a" ->
                Some
                    { blockDef with
                        signature = { inputs = [ { name = "Anzahl"; inputType = "Anzahl" } ]; output = None; outputFields = None } }
            | _ -> None

        let errors = Validator.revalidateAgainstCurrentDefinitions De changedLookup program
        Assert.Contains(errors, fun e -> e.message.Contains("block-a") && e.message.Contains("geändert"))

// --- Part B (Milestone 9, §8): accessor blocks compile to Accessor, Eval resolves them ---

[<Fact>]
let ``a custom block RETURN chain with waypointSystemField compiles on save`` () =
    let defShellWorkspace =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "sk_custom_block_def", "id": "def1", "fields": { "BLOCK_NAME": "System vom Schiff" },
              "inputs": {
                "RETURN": { "block": { "type": "waypointSystemField", "id": "ws1", "inputs": {
                    "TARGET": { "block": { "type": "getWaypoint", "id": "gw1", "inputs": {
                        "WAYPOINT_SYMBOL": { "block": { "type": "shipWaypoint", "id": "sw1", "inputs": {
                            "TARGET": { "block": { "type": "getShipInfo", "id": "si1" } }
                        } } }
                    } } }
                } } }
              }
            }
        ] } }
        """

    let definition: CustomBlockDefinition =
        { id = "system-block"
          signature = { inputs = []; output = Some "$value"; outputFields = None }
          workspaceJson = defShellWorkspace }

    let lookup =
        function
        | "system-block" -> Some definition
        | _ -> None

    match Compiler.resolveCustomBlockCall De lookup "system-block" with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok customBlocks ->
        let compiled = customBlocks.["system-block"]
        Assert.Equal(Some(Accessor("System", TempRef "$t2")), compiled.returnExpr)

[<Fact>]
let ``waypointSystemField compiles to Accessor over System`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "variables_set", "id": "b1", "fields": { "VAR": "system" }, "inputs": {
                "VALUE": { "block": { "type": "waypointSystemField", "id": "b2", "inputs": {
                    "TARGET": { "block": { "type": "getWaypoint", "id": "b3", "inputs": {
                        "WAYPOINT_SYMBOL": { "block": """ + textBlock "b4" "X1-TEST-A1" + """ }
                    } } }
                } } }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ InfoRead(
                  "b3",
                  "getWaypoint",
                  Map [ "waypointSymbol", Literal(StringLit "X1-TEST-A1") ],
                  "$t1"
              )
              SetVariable("b1", "system", Accessor("System", TempRef "$t1")) ],
            program.instructions
        )

[<Fact>]
let ``an accessor block compiles to Accessor over its TARGET input`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "variables_set", "id": "b1", "fields": { "VAR": "treibstoff" }, "inputs": {
                "VALUE": { "block": { "type": "shipFuel", "id": "b2", "inputs": {
                    "TARGET": { "block": { "type": "getShipInfo", "id": "b3" } }
                } } }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ InfoRead("b3", "getShipInfo", Map.empty, "$t1")
              SetVariable("b1", "treibstoff", Accessor("Fuel", TempRef "$t1")) ],
            program.instructions
        )

[<Fact>]
let ``Eval Accessor resolves a known field from a VRecord`` () =
    let record = VRecord(Map.ofList [ "Treibstoff", VNumber 250.0; "Status", VString "DOCKED" ])
    let locals = Map.ofList [ "ship", record ]
    Assert.Equal(VNumber 250.0, Eval.eval locals (Accessor("Treibstoff", VariableRef "ship")))
    Assert.Equal(VString "DOCKED", Eval.eval locals (Accessor("Status", VariableRef "ship")))

[<Fact>]
let ``Eval Accessor on an unknown field raises a clear German error`` () =
    let locals = Map.ofList [ "ship", VRecord(Map.ofList [ "Treibstoff", VNumber 250.0 ]) ]
    let ex = Assert.Throws<System.Exception>(fun () -> Eval.eval locals (Accessor("Unbekannt", VariableRef "ship")) |> ignore)
    Assert.Contains("Unbekannt", ex.Message)

[<Fact>]
let ``Eval Accessor on a non-record value raises a clear German type-mismatch error`` () =
    let locals = Map.ofList [ "x", VNumber 5.0 ]
    let ex = Assert.Throws<System.Exception>(fun () -> Eval.eval locals (Accessor("Treibstoff", VariableRef "x")) |> ignore)
    Assert.Contains("Datensatz", ex.Message)

// --- Structured custom-block outputs (§9 Outputs, Milestone 9/Part C) -----------------

[<Fact>]
let ``sk_build_record compiles to a RecordLiteral with fields in declaration order`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "variables_set", "id": "b1", "fields": { "VAR": "info" }, "inputs": {
                "VALUE": { "block": {
                    "type": "sk_build_record", "id": "rec1",
                    "extraState": { "fields": [ { "name": "Wegpunkt" }, { "name": "Kaufpreis" } ] },
                    "inputs": {
                        "FIELD_0": { "block": """ + textBlock "f0t" "X1-TEST-A1" + """ },
                        "FIELD_1": { "block": """ + numberBlock "f1t" 42.0 + """ }
                    }
                } }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ SetVariable(
                  "b1",
                  "info",
                  RecordLiteral [ "Wegpunkt", Literal(StringLit "X1-TEST-A1"); "Kaufpreis", Literal(NumberLit 42.0) ]
              ) ],
            program.instructions
        )

[<Fact>]
let ``Eval evaluates a RecordLiteral into a VRecord`` () =
    let expr = RecordLiteral [ "Wegpunkt", Literal(StringLit "X1-TEST-A1"); "Kaufpreis", Literal(NumberLit 42.0) ]
    let result = Eval.eval Map.empty expr
    Assert.Equal(VRecord(Map.ofList [ "Wegpunkt", VString "X1-TEST-A1"; "Kaufpreis", VNumber 42.0 ]), result)

[<Fact>]
let ``a dynamic accessor_<id>_<field> block compiles to an Accessor for that field`` () =
    let json =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "variables_set", "id": "b1", "fields": { "VAR": "preis" }, "inputs": {
                "VALUE": { "block": {
                    "type": "accessor_marktinfo-block_Kaufpreis", "id": "acc1",
                    "inputs": { "TARGET": { "block": { "type": "variables_get", "id": "g1", "fields": { "VAR": "info" } } } }
                } }
            } }
        ] } }
        """

    match Compiler.compileWorkspace De noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ SetVariable("b1", "preis", Accessor("Kaufpreis", VariableRef "info")) ],
            program.instructions
        )

[<Fact>]
let ``a custom block whose workshop JSON is a real def-shell block compiles its BODY and RETURN socket`` () =
    let defShellWorkspace =
        """
        { "blocks": { "languageVersion": 0, "blocks": [
            { "type": "sk_custom_block_def", "id": "def1", "fields": { "BLOCK_NAME": "Prüfe Markt" },
              "extraState": { "inputs": [ { "name": "Wegpunkt", "typeLabel": "Wegpunkt" } ] },
              "inputs": {
                "BODY": { "block": { "type": "sk_wait", "id": "w1", "inputs": { "SECONDS": { "block": """ + numberBlock "n1" 1.0 + """ } } } },
                "RETURN": { "block": {
                    "type": "sk_build_record", "id": "rec1",
                    "extraState": { "fields": [ { "name": "Wegpunkt" } ] },
                    "inputs": { "FIELD_0": { "block": { "type": "sk_param_get", "id": "p1", "fields": { "PARAM_NAME": "Wegpunkt" } } } }
                } }
              }
            }
        ] } }
        """

    let definition: CustomBlockDefinition =
        { id = "marktinfo-block"
          signature =
            { inputs = [ { name = "Wegpunkt"; inputType = "Wegpunkt" } ]
              output = Some "$record"
              outputFields = Some [ "Wegpunkt" ] }
          workspaceJson = defShellWorkspace }

    let lookup =
        function
        | "marktinfo-block" -> Some definition
        | _ -> None

    match Compiler.resolveCustomBlockCall De lookup "marktinfo-block" with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok customBlocks ->
        let compiled = customBlocks.["marktinfo-block"]
        Assert.Equal<Instruction list>([ Wait("w1", Literal(NumberLit 1.0)) ], compiled.instructions)
        Assert.Equal(Some(RecordLiteral [ "Wegpunkt", ParamRef "Wegpunkt" ]), compiled.returnExpr)
