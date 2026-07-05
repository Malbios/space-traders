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

    match Compiler.compileWorkspace noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ ApiAction("b1", "navigate", Map [ "destination", Literal(StringLit "X1-DF55-A1") ]) ],
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

    match Compiler.compileWorkspace noCustomBlocks json with
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

    match Compiler.compileWorkspace noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        Assert.Equal<Instruction list>(
            [ Repeat("rep", Literal(NumberLit 3.0), [ Wait("w1", Literal(NumberLit 1.0)) ]) ],
            program.instructions
        )

[<Fact>]
let ``rejects an unknown block type`` () =
    let json = """{ "blocks": { "languageVersion": 0, "blocks": [ { "type": "totally_unknown", "id": "b1" } ] } }"""

    match Compiler.compileWorkspace noCustomBlocks json with
    | Ok program -> Assert.Fail($"expected Error, got: %A{program}")
    | Error errors -> Assert.Contains(errors, fun e -> e.message.Contains("Unbekannter Blocktyp"))

[<Fact>]
let ``validate rejects a program with no start block`` () =
    let json = """{ "blocks": { "languageVersion": 0, "blocks": [] } }"""

    match Compiler.compileWorkspace noCustomBlocks json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        let errors = Validator.validate De program
        Assert.Contains(errors, fun e -> e.message.Contains("keinen Startblock"))

[<Fact>]
let ``validate's no-start-block message is English when the locale is English (Milestone 12)`` () =
    let json = """{ "blocks": { "languageVersion": 0, "blocks": [] } }"""

    match Compiler.compileWorkspace noCustomBlocks json with
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

    match Compiler.compileWorkspace noCustomBlocks json with
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

    match Compiler.resolveCustomBlockCall lookup "block-a" with
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

    match Compiler.resolveCustomBlockCall lookup "block-x" with
    | Ok customBlocks -> Assert.Fail($"expected Error (cycle), got: %A{customBlocks}")
    | Error errors -> Assert.Contains(errors, fun e -> e.message.Contains("ruft sich selbst auf"))

[<Fact>]
let ``validate rejects a custom-block call with mismatched arguments`` () =
    let blockDef: CustomBlockDefinition =
        { id = "needs-input"
          signature = { inputs = [ { name = "Anzahl"; inputType = "Zahl" } ]; output = None; outputFields = None }
          workspaceJson = simpleWaitBody }

    let lookup =
        function
        | "needs-input" -> Some blockDef
        | _ -> None

    // The call site omits the required "Anzahl" input entirely.
    let json = """{ "blocks": { "languageVersion": 0, "blocks": [ """ + customBlockCallJson "call1" "needs-input" "" + """ ] } }"""

    match Compiler.compileWorkspace lookup json with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok program ->
        let errors = Validator.validate De program
        Assert.Contains(errors, fun e -> e.message.Contains("Anzahl") && e.message.Contains("fehlt"))

[<Fact>]
let ``revalidateAgainstCurrentDefinitions catches a signature that changed after compile`` () =
    let originalSignature = { inputs = []; output = None; outputFields = None }
    let blockDef: CustomBlockDefinition =
        { id = "block-a"; signature = originalSignature; workspaceJson = simpleWaitBody }

    let json = """{ "blocks": { "languageVersion": 0, "blocks": [ """ + customBlockCallJson "call1" "block-a" "" + """ ] } }"""

    let compileLookup = function
        | "block-a" -> Some blockDef
        | _ -> None

    match Compiler.compileWorkspace compileLookup json with
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
                        signature = { inputs = [ { name = "Anzahl"; inputType = "Zahl" } ]; output = None; outputFields = None } }
            | _ -> None

        let errors = Validator.revalidateAgainstCurrentDefinitions De changedLookup program
        Assert.Contains(errors, fun e -> e.message.Contains("block-a") && e.message.Contains("geändert"))

// --- Part B (Milestone 9, §8): accessor blocks compile to Accessor, Eval resolves them ---

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

    match Compiler.compileWorkspace noCustomBlocks json with
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

    match Compiler.compileWorkspace noCustomBlocks json with
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

    match Compiler.compileWorkspace noCustomBlocks json with
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

    match Compiler.resolveCustomBlockCall lookup "marktinfo-block" with
    | Error errors -> Assert.Fail($"expected Ok, got errors: %A{errors}")
    | Ok customBlocks ->
        let compiled = customBlocks.["marktinfo-block"]
        Assert.Equal<Instruction list>([ Wait("w1", Literal(NumberLit 1.0)) ], compiled.instructions)
        Assert.Equal(Some(RecordLiteral [ "Wegpunkt", ParamRef "Wegpunkt" ]), compiled.returnExpr)
