module SpaceKids.Core.Dsl.Compiler

open System.Text.Json
open SpaceKids.Core.Dsl
open SpaceKids.Core.Dsl.BlocklyJson

/// Blockly-input-name -> DSL-arg-name pairs for the catalog action blocks (docs/04-block-catalog.md).
let private ACTION_BLOCKS: Map<string, (string * string) list> =
    Map.ofList [
        "navigate", [ "DESTINATION", "destination" ]
        "orbit", []
        "dock", []
        "extract", []
        "survey", []
        "buyGood", [ "TRADE_SYMBOL", "tradeSymbol"; "UNITS", "units" ]
        "sellGood", [ "TRADE_SYMBOL", "tradeSymbol"; "UNITS", "units" ]
        "deliverContract", [ "CONTRACT_ID", "contractId"; "TRADE_SYMBOL", "tradeSymbol"; "UNITS", "units" ]
        "acceptContract", [ "CONTRACT_ID", "contractId" ]
        "fulfillContract", [ "CONTRACT_ID", "contractId" ]
        "negotiateContract", []
        "purchaseShip", [ "SHIP_TYPE", "shipType"; "WAYPOINT", "waypointSymbol" ]
        "refuel", []
        "createChart", []
        "extractWithSurvey", [ "SURVEY_SIGNATURE", "surveySignature" ]
        "installModule", [ "MODULE_SYMBOL", "moduleSymbol" ]
        "installMount", [ "MOUNT_SYMBOL", "mountSymbol" ]
        "jettison", [ "TRADE_SYMBOL", "tradeSymbol"; "UNITS", "units" ]
        "jump", [ "DESTINATION", "waypointSymbol" ]
        "refine", [ "PRODUCE", "produce" ]
        "removeModule", [ "MODULE_SYMBOL", "moduleSymbol" ]
        "removeMount", [ "MOUNT_SYMBOL", "mountSymbol" ]
        "repair", []
        "scanShips", []
        "scanSystems", []
        "scanWaypoints", []
        "scrapShip", []
        "siphon", []
        "transferCargo", [ "TRADE_SYMBOL", "tradeSymbol"; "UNITS", "units"; "SHIP_SYMBOL", "targetShipSymbol" ]
        "warp", [ "DESTINATION", "waypointSymbol" ]
        "supplyConstruction", [ "WAYPOINT_SYMBOL", "waypointSymbol"; "TRADE_SYMBOL", "tradeSymbol"; "UNITS", "units" ]
        "patchShipNav", [ "FLIGHT_MODE", "flightMode" ]
    ]

/// Same, for the 9 information blocks — these compile as effectful value blocks (§10),
/// never as statements.
let private INFO_BLOCKS: Map<string, (string * string) list> =
    Map.ofList [
        "getShipInfo", []
        "getFleetInfo", []
        "getWaypoints", [ "SYSTEM_SYMBOL", "systemSymbol" ]
        "getMarket", [ "WAYPOINT_SYMBOL", "waypointSymbol" ]
        "getShipyard", [ "WAYPOINT_SYMBOL", "waypointSymbol" ]
        "getContracts", []
        "getCargo", []
        "getFuel", []
        "getCredits", []
        "getRepairCost", []
        "getScrapValue", []
        "getWaypoint", [ "WAYPOINT_SYMBOL", "waypointSymbol" ]
        "getMyAgent", []
        "getPublicAgent", [ "AGENT_SYMBOL", "agentSymbol" ]
        "getPublicAgents", []
        "getCooldown", []
        "getNav", []
        "getSupplyChain", []
        "getShipModules", []
        "getShipMounts", []
        "getConstruction", [ "WAYPOINT_SYMBOL", "waypointSymbol" ]
        "getJumpGate", [ "WAYPOINT_SYMBOL", "waypointSymbol" ]
        "getSystems", []
        "getSystem", [ "SYSTEM_SYMBOL", "systemSymbol" ]
        "getFaction", [ "FACTION_SYMBOL", "factionSymbol" ]
        "getFactions", []
        "getMyFactions", []
    ]

/// Generic "field from X" accessor block types (post-roadmap redesign, replacing the
/// old one-block-type-per-field scheme) — one block per §8 record shape, each with a
/// `FIELD` dropdown whose value *is* the canonical field name directly, so no
/// type-keyed lookup table is needed (see the compileExpr/valueOnlyStatement match
/// arms below). Mirrors `blocks-catalog.ts`'s `RECORD_FIELD_BLOCKS`/
/// `catalogRecordFieldBlockTypes` (kept in sync manually; both are exhaustively
/// listed in docs/04-block-catalog.md).
let private GENERIC_ACCESSOR_TYPES: Set<string> =
    set [
        "shipField"
        "cargoField"
        "goodField"
        "shipyardField"
        "shipyardTypeField"
        "marketField"
        "tradeGoodField"
        "contractField"
        "waypointField"
        "agentField"
        "systemField"
        "factionField"
        "factionReputationField"
        "jumpGateField"
        "constructionField"
        "constructionMaterialField"
        "navField"
        "cooldownField"
        "priceField"
        "moduleField"
        "mountField"
        "supplyChainField"
        "recordField"
        "frameField"
        "reactorField"
        "engineField"
        "shipyardModuleField"
        "shipyardMountField"
        "crewField"
        "requirementsField"
    ]

/// Compatibility for the 29 one-block-type-per-field accessors the redesign above
/// replaced (§ commit "Replace per-field accessor blocks with generic field-dropdown
/// blocks") — a program/custom block saved *before* that redesign has these old block
/// types baked into its stored workspace JSON forever, and this compiler parses that
/// raw JSON directly (`BlocklyJson.parseWorkspace`), independent of whatever the
/// client currently renders. Unlike `GENERIC_ACCESSOR_TYPES`, the field name isn't
/// read from a `FIELD` dropdown (these blocks never had one) — it's implied by the
/// block's own type, hence a direct type-to-field map. Mirrors `blocks-catalog.ts`'s
/// `LEGACY_ACCESSOR_BLOCKS` (kept in sync manually, same as `GENERIC_ACCESSOR_TYPES`
/// already was).
let private LEGACY_ACCESSOR_FIELD_NAMES: Map<string, string> =
    Map [
        "shipName", "Name"
        "shipWaypoint", "Waypoint"
        "shipStatus", "Status"
        "shipFuel", "Fuel"
        "shipCargoUnits", "CargoUnits"
        "shipCargoCapacity", "CargoCapacity"
        "cargoUnits", "Units"
        "cargoCapacity", "Capacity"
        "cargoGoods", "Goods"
        "goodName", "Name"
        "goodUnits", "Units"
        "shipyardWaypoint", "Waypoint"
        "shipyardTypes", "Types"
        "shipyardTypeName", "Type"
        "shipyardTypePrice", "Price"
        "marketWaypoint", "Waypoint"
        "marketGoods", "Goods"
        "tradeGoodName", "Name"
        "tradeGoodBuyPrice", "BuyPrice"
        "tradeGoodSellPrice", "SellPrice"
        "contractId", "Id"
        "contractType", "Type"
        "contractAccepted", "Accepted"
        "contractFulfilled", "Fulfilled"
        "waypointSymbolField", "Symbol"
        "waypointTypeField", "Type"
        "waypointSystemField", "System"
        "waypointHasShipyard", "HasShipyard"
        "waypointHasMarket", "HasMarket"
    ]

type private CompileState =
    { mutable tempCounter: int
      errors: ResizeArray<DslError>
      customBlocks: System.Collections.Generic.Dictionary<string, CompiledCustomBlock>
      lookup: string -> CustomBlockDefinition option
      /// Custom blocks currently being compiled — a re-entry here is a cycle.
      inProgress: System.Collections.Generic.HashSet<string>
      /// Milestone 13 (bilingual compile errors): threaded through every helper via
      /// `state` already, so only the two public entry points below gain a new
      /// parameter — no other helper's signature changes.
      locale: Locale }

let private newTemp (state: CompileState) : string =
    state.tempCounter <- state.tempCounter + 1
    $"$t{state.tempCounter}"

let private fieldString (block: RawBlock) (name: string) : string option =
    match block.fields.TryFind name with
    | Some el when el.ValueKind = JsonValueKind.String -> Some(el.GetString())
    | Some el -> Some(el.ToString())
    | None -> None

let private fieldNumber (block: RawBlock) (name: string) : float option =
    match block.fields.TryFind name with
    | Some el when el.ValueKind = JsonValueKind.Number -> Some(el.GetDouble())
    | Some el ->
        match System.Double.TryParse(el.GetString()) with
        | true, v -> Some v
        | false, _ -> None
    | None -> None

/// Blockly variable fields serialize as either a plain name string or an object
/// carrying one (version-dependent) — handled defensively either way.
let private variableName (block: RawBlock) (fieldName: string) : string =
    match block.fields.TryFind fieldName with
    | Some el when el.ValueKind = JsonValueKind.String -> el.GetString()
    | Some el ->
        match el.TryGetProperty("name") with
        | true, nameEl -> nameEl.GetString()
        | false, _ -> el.ToString()
    | None -> "?"

let private extraStateInt (block: RawBlock) (name: string) (defaultValue: int) : int =
    match block.extraState with
    | Some el ->
        match el.TryGetProperty(name) with
        | true, v -> v.GetInt32()
        | false, _ -> defaultValue
    | None -> defaultValue

let private extraStateBool (block: RawBlock) (name: string) (defaultValue: bool) : bool =
    match block.extraState with
    | Some el ->
        match el.TryGetProperty(name) with
        | true, v -> v.GetBoolean()
        | false, _ -> defaultValue
    | None -> defaultValue

let private extraStateString (block: RawBlock) (name: string) : string option =
    match block.extraState with
    | Some el ->
        match el.TryGetProperty(name) with
        | true, v -> Some(v.GetString())
        | false, _ -> None
    | None -> None

/// `sk_build_record`'s mutator-declared field names, in declaration order (§9
/// Outputs, Milestone 9/Part C) — read from `extraState.fields[].name`, matching the
/// client's `BuildRecordExtraState` shape.
let private listIndexWhere (block: RawBlock) : ListIndexWhere =
    match fieldString block "WHERE" with
    | Some "FROM_END" -> FromEnd
    | Some "FIRST" -> First
    | Some "LAST" -> Last
    | Some "RANDOM" -> Random
    | _ -> FromStart

let private listVariableNameFromInput (block: RawBlock) (inputName: string) : string option =
    match block.inputs.TryFind inputName with
    | Some valueBlock when valueBlock.blockType = "variables_get" -> Some(variableName valueBlock "VAR")
    | _ -> None

let private recordFieldNames (block: RawBlock) : string list =
    match block.extraState with
    | Some el ->
        match el.TryGetProperty("fields") with
        | true, arr when arr.ValueKind = JsonValueKind.Array ->
            [ for item in arr.EnumerateArray() ->
                match item.TryGetProperty("name") with
                | true, nameEl -> nameEl.GetString()
                | false, _ -> "" ]
        | _ -> []
    | None -> []

let rec private compileExpr (state: CompileState) (hoisted: ResizeArray<Instruction>) (block: RawBlock) : Expr =
    match block.blockType with
    | "math_number" -> Literal(NumberLit(fieldNumber block "NUM" |> Option.defaultValue 0.0))
    | "text" -> Literal(StringLit(fieldString block "TEXT" |> Option.defaultValue ""))
    | "logic_compare" ->
        let op = fieldString block "OP" |> Option.defaultValue "EQ"
        Comparison(op, compileInput state hoisted block "A", compileInput state hoisted block "B")
    | "logic_boolean" -> Literal(BoolLit(fieldString block "BOOL" = Some "TRUE"))
    | "logic_operation" ->
        let op = fieldString block "OP" |> Option.defaultValue "AND"
        LogicalOp(op, compileInput state hoisted block "A", compileInput state hoisted block "B")
    | "logic_negate" -> LogicalNot(compileInput state hoisted block "BOOL")
    | "math_arithmetic" ->
        let op = fieldString block "OP" |> Option.defaultValue "ADD"
        Arithmetic(op, compileInput state hoisted block "A", compileInput state hoisted block "B")
    | "variables_get" -> VariableRef(variableName block "VAR")
    | "sk_param_get" -> ParamRef(fieldString block "PARAM_NAME" |> Option.defaultValue "?")
    | "lists_create_with" ->
        let itemCount = extraStateInt block "itemCount" 0
        let items = [ for i in 0 .. itemCount - 1 -> compileInput state hoisted block $"ADD{i}" ]
        ListLiteral items
    | "lists_getIndex" ->
        let where = listIndexWhere block

        let index =
            match where with
            | FromStart | FromEnd -> Some(compileInput state hoisted block "AT")
            | First | Last | Random -> None

        ListGet(compileInput state hoisted block "VALUE", where, index)
    | "callCustomBlock" ->
        let instr = compileCustomBlockCall state hoisted block
        hoisted.Add instr
        match instr with
        | CallCustomBlock(_, _, _, Some temp) -> TempRef temp
        | CallCustomBlock(_, customBlockId, _, None) ->
            let message =
                match state.locale with
                | De -> $"Der eigene Block \"{customBlockId}\" hat kein Ergebnis und kann nicht als Wert verwendet werden."
                | En -> $"The custom block \"{customBlockId}\" has no result and cannot be used as a value."

            state.errors.Add { blockId = Some block.id; message = message }
            Literal(BoolLit false)
        | _ -> Literal(BoolLit false)
    | t when INFO_BLOCKS.ContainsKey t ->
        let args = compileArgs state hoisted block INFO_BLOCKS.[t]
        let temp = newTemp state
        hoisted.Add(InfoRead(block.id, t, args, temp))
        TempRef temp
    | t when GENERIC_ACCESSOR_TYPES.Contains t ->
        let fieldName = fieldString block "FIELD" |> Option.defaultValue ""
        Accessor(fieldName, compileInput state hoisted block "TARGET")
    | t when LEGACY_ACCESSOR_FIELD_NAMES.ContainsKey t ->
        Accessor(LEGACY_ACCESSOR_FIELD_NAMES.[t], compileInput state hoisted block "TARGET")
    | "sk_build_record" ->
        let fieldNames = recordFieldNames block
        let fields = fieldNames |> List.mapi (fun i name -> name, compileInput state hoisted block $"FIELD_{i}")
        RecordLiteral fields
    // Milestone 9/Part C — one dynamic accessor block per custom-block structured
    // output field (`accessor_<customBlockId>_<field>`), generated client-side rather
    // than declared in the static `GENERIC_ACCESSOR_TYPES` set above. The field name
    // is always the block type's own suffix, so no separate lookup table is needed.
    | t when t.StartsWith("accessor_") ->
        match t.LastIndexOf('_') with
        | idx when idx > "accessor_".Length - 1 ->
            let fieldName = t.Substring(idx + 1)
            Accessor(fieldName, compileInput state hoisted block "TARGET")
        | _ ->
            let message =
                match state.locale with
                | De -> $"Ungültiger Zugriffsblock: {t}."
                | En -> $"Invalid accessor block: {t}."

            state.errors.Add { blockId = Some block.id; message = message }
            Literal(BoolLit false)
    | other ->
        let message =
            match state.locale with
            | De -> $"Unbekannter Blocktyp: {other}."
            | En -> $"Unknown block type: {other}."

        state.errors.Add { blockId = Some block.id; message = message }
        Literal(BoolLit false)

and private compileInput (state: CompileState) (hoisted: ResizeArray<Instruction>) (block: RawBlock) (inputName: string) : Expr =
    match block.inputs.TryFind inputName with
    | Some inputBlock -> compileExpr state hoisted inputBlock
    | None ->
        let message =
            match state.locale with
            | De -> $"Eingabe \"{inputName}\" fehlt."
            | En -> $"Input \"{inputName}\" is missing."

        state.errors.Add { blockId = Some block.id; message = message }
        Literal(BoolLit false)

and private compileArgs (state: CompileState) (hoisted: ResizeArray<Instruction>) (block: RawBlock) (argNames: (string * string) list) : Map<string, Expr> =
    argNames
    |> List.map (fun (inputName, dslName) -> dslName, compileInput state hoisted block inputName)
    |> Map.ofList

/// Like `compileInput`, but a missing input is silently omitted rather than a compile
/// error — used only for custom-block call arguments, where arity/type mismatches are
/// the Validator's job (§11), not the compiler's.
and private compileInputOptional (state: CompileState) (hoisted: ResizeArray<Instruction>) (block: RawBlock) (inputName: string) : Expr option =
    block.inputs.TryFind inputName |> Option.map (compileExpr state hoisted)

/// Compiles a `callCustomBlock` block into its `CallCustomBlock` instruction. Does
/// *not* add itself to `hoisted` — callers place it (statement position: appended
/// directly; value position: explicitly hoisted) since the right position differs.
and private compileCustomBlockCall (state: CompileState) (hoisted: ResizeArray<Instruction>) (block: RawBlock) : Instruction =
    let customBlockId = extraStateString block "customBlockId" |> Option.defaultValue ""
    resolveCustomBlock state customBlockId
    // `resolveCustomBlock` already resolved (and cached into `state.customBlocks`) the
    // same signature this needs — reading it back from there instead of calling the
    // external `state.lookup` a second time avoids a redundant fetch per call site
    // (found in review; harmless with the in-memory dictionaries used in tests, but a
    // real cost if `lookup` is DB/cache-backed in production).
    match state.customBlocks.TryGetValue customBlockId with
    | true, compiled ->
        let args =
            compiled.signature.inputs
            |> List.choose (fun input -> compileInputOptional state hoisted block input.name |> Option.map (fun e -> input.name, e))
            |> Map.ofList
        let resultTarget = if compiled.signature.output.IsSome then Some(newTemp state) else None
        CallCustomBlock(block.id, customBlockId, args, resultTarget)
    | false, _ ->
        // resolveCustomBlock already recorded the "not found"/cycle error.
        CallCustomBlock(block.id, customBlockId, Map.empty, None)

and private resolveCustomBlock (state: CompileState) (customBlockId: string) : unit =
    if state.customBlocks.ContainsKey customBlockId then
        ()
    elif state.inProgress.Contains customBlockId then
        let message =
            match state.locale with
            | De -> $"Der eigene Block \"{customBlockId}\" ruft sich selbst auf (direkt oder über andere Blöcke) — das ist nicht erlaubt."
            | En -> $"The custom block \"{customBlockId}\" calls itself (directly or through other blocks) — this is not allowed."

        state.errors.Add { blockId = None; message = message }
    else
        match state.lookup customBlockId with
        | None ->
            let message =
                match state.locale with
                | De -> $"Der eigene Block \"{customBlockId}\" wurde nicht gefunden."
                | En -> $"The custom block \"{customBlockId}\" was not found."

            state.errors.Add { blockId = None; message = message }
        | Some definition ->
            state.inProgress.Add customBlockId |> ignore
            let topBlocks = BlocklyJson.parseWorkspace definition.workspaceJson

            // A real workshop's stored JSON is the whole `sk_custom_block_def` shell
            // (§9b) — its "BODY" statement input is the actual body, its "RETURN"
            // value input (Milestone 9/Part C) compiles into `returnExpr`. Older
            // fixtures/tests that predate the real Blockwerkstatt UI store the body
            // directly with no shell block at all; both are supported so nothing that
            // already passed compiles differently now.
            let instructions, returnExpr =
                match topBlocks |> List.tryFind (fun b -> b.blockType = "sk_custom_block_def") with
                | Some defBlock ->
                    let returnHoisted = ResizeArray<Instruction>()

                    let body =
                        match defBlock.inputs.TryFind "BODY" with
                        | Some bodyBlock -> compileStatementChain state (Some bodyBlock)
                        | None -> []

                    // Hoisted instructions from the return expression (e.g. an info
                    // read plugged directly into "Ergebnis") run *after* the body,
                    // immediately before the call returns — not before it, which
                    // would run them ahead of the body's own effects.
                    let ret = defBlock.inputs.TryFind "RETURN" |> Option.map (compileExpr state returnHoisted)
                    body @ List.ofSeq returnHoisted, ret
                | None -> (topBlocks |> List.collect (fun b -> compileStatementChain state (Some b))), None

            state.customBlocks.[customBlockId] <-
                { signature = definition.signature
                  instructions = instructions
                  returnExpr = returnExpr }

            state.inProgress.Remove customBlockId |> ignore

and private compileStatement (state: CompileState) (block: RawBlock) : Instruction list =
    let hoisted = ResizeArray<Instruction>()

    let valueOnlyStatement () =
        compileExpr state hoisted block |> ignore
        List.ofSeq hoisted

    match block.blockType with
    | t when INFO_BLOCKS.ContainsKey t -> valueOnlyStatement ()
    | t when GENERIC_ACCESSOR_TYPES.Contains t -> valueOnlyStatement ()
    | t when LEGACY_ACCESSOR_FIELD_NAMES.ContainsKey t -> valueOnlyStatement ()
    | t when t.StartsWith("accessor_") -> valueOnlyStatement ()
    | _ ->
        let instr =
            match block.blockType with
            | "sk_show_message" -> ShowMessage(block.id, compileInput state hoisted block "TEXT")
            | "sk_wait" -> Wait(block.id, compileInput state hoisted block "SECONDS")
            | "variables_set" -> SetVariable(block.id, variableName block "VAR", compileInput state hoisted block "VALUE")
            | "math_change" -> ChangeVariable(block.id, variableName block "VAR", compileInput state hoisted block "DELTA")
            | "lists_setIndex" when fieldString block "MODE" = Some "INSERT" ->
                let message =
                    match state.locale with
                    | De -> "Listen-Einfügen (INSERT) wird noch nicht unterstützt — nur SET."
                    | En -> "List insert (INSERT) is not supported yet — only SET."

                state.errors.Add { blockId = Some block.id; message = message }
                ShowMessage(block.id, Literal(StringLit message))
            | "lists_setIndex" ->
                match listVariableNameFromInput block "LIST" with
                | Some name ->
                    let where = listIndexWhere block

                    let index =
                        match where with
                        | FromStart | FromEnd -> Some(compileInput state hoisted block "AT")
                        | First | Last | Random -> None

                    ListSet(block.id, name, where, index, compileInput state hoisted block "TO")
                | None ->
                    let message =
                        match state.locale with
                        | De -> "Listen setzen braucht eine Listen-Variable als Eingabe."
                        | En -> "List set requires a list variable as input."

                    state.errors.Add { blockId = Some block.id; message = message }
                    ShowMessage(block.id, Literal(StringLit message))
            | "controls_repeat_ext" ->
                let count = compileInput state hoisted block "TIMES"
                Repeat(block.id, count, compileStatementInput state block "DO")
            | "controls_whileUntil" ->
                let mode = if fieldString block "MODE" = Some "UNTIL" then Until else While
                let cond = compileInput state hoisted block "BOOL"
                WhileUntil(block.id, mode, cond, compileStatementInput state block "DO")
            | "controls_forEach" ->
                let var = variableName block "VAR"
                let list = compileInput state hoisted block "LIST"
                ForEach(block.id, var, list, compileStatementInput state block "DO")
            | "withShip" ->
                let ship = compileInput state hoisted block "SHIP"
                let hasUnavailableBranch =
                    extraStateBool block "hasUnavailable" false || block.inputs.ContainsKey "ELSE"

                let elseBranch =
                    if hasUnavailableBranch then
                        Some(compileStatementInput state block "ELSE")
                    else
                        None

                let body = compileStatementInput state block "DO" @ [ ExitShipScope($"{block.id}:exit") ]

                WithShip(block.id, ship, body, elseBranch)
            | "parallel" ->
                let branchCount = max 2 (extraStateInt block "branchCount" 2)
                let branches = [ for i in 0 .. branchCount - 1 -> compileStatementInput state block $"DO{i}" ]
                Parallel(block.id, branches)
            | "controls_if" -> compileIf state hoisted block
            | "controls_flow_statements" ->
                if fieldString block "FLOW" = Some "CONTINUE" then Continue block.id else Break block.id
            | "callCustomBlock" -> compileCustomBlockCall state hoisted block
            | t when ACTION_BLOCKS.ContainsKey t -> ApiAction(block.id, t, compileArgs state hoisted block ACTION_BLOCKS.[t])
            | other ->
                let message =
                    match state.locale with
                    | De -> $"Unbekannter Blocktyp: {other}."
                    | En -> $"Unknown block type: {other}."

                state.errors.Add { blockId = Some block.id; message = message }
                ApiAction(block.id, other, Map.empty)

        List.ofSeq hoisted @ [ instr ]

and private compileStatementInput (state: CompileState) (block: RawBlock) (inputName: string) : Instruction list =
    match block.inputs.TryFind inputName with
    | Some bodyBlock -> compileStatementChain state (Some bodyBlock)
    | None -> []

and private compileIf (state: CompileState) (hoisted: ResizeArray<Instruction>) (block: RawBlock) : Instruction =
    let elseIfCount = extraStateInt block "elseIfCount" 0
    let hasElse = extraStateBool block "hasElse" false
    let branches =
        [ for i in 0 .. elseIfCount do
            match block.inputs.TryFind $"IF{i}" with
            | Some condBlock ->
                let cond = compileExpr state hoisted condBlock
                yield cond, compileStatementInput state block $"DO{i}"
            | None -> () ]
    let elseBranch = if hasElse then Some(compileStatementInput state block "ELSE") else None
    If(block.id, branches, elseBranch)

and private compileStatementChain (state: CompileState) (start: RawBlock option) : Instruction list =
    match start with
    | None -> []
    | Some block -> compileStatement state block @ compileStatementChain state block.next

let private newState (locale: Locale) (lookup: string -> CustomBlockDefinition option) : CompileState =
    { tempCounter = 0
      errors = ResizeArray<DslError>()
      customBlocks = System.Collections.Generic.Dictionary<string, CompiledCustomBlock>()
      lookup = lookup
      inProgress = System.Collections.Generic.HashSet<string>()
      locale = locale }

/// Resolves and compiles a custom block, and everything it transitively calls, via
/// `lookup` — detecting cycles and recording one error per unresolvable/cyclic
/// reference. This is the testable entry point for §9/§10's custom-block machinery
/// (transitive closure, cycle detection) independent of a real caller block existing
/// on any canvas yet (that's Milestone 9).
let resolveCustomBlockCall
    (locale: Locale)
    (lookup: string -> CustomBlockDefinition option)
    (customBlockId: string)
    : Result<Map<string, CompiledCustomBlock>, DslError list> =
    let state = newState locale lookup
    resolveCustomBlock state customBlockId
    if state.errors.Count > 0 then
        Error(List.ofSeq state.errors)
    else
        Ok(state.customBlocks |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)

/// Reads a custom block's own signature straight off its workshop's raw Blockly JSON
/// (§9b/§9c), independent of whatever was previously stored — the server-side
/// counterpart to `blocks.ts`'s `readSignature`, used when persisting a *new* version
/// (Milestone 9/Part D): the signature to save must reflect the mutator edits just
/// made, not the definition already on disk. Returns a void signature (no inputs, no
/// output) if no `sk_custom_block_def` block is found — the caller (`CustomBlockRemoting`)
/// surfaces that as a validation error before saving, same as any other empty program.
let deriveCustomBlockSignature (workspaceJson: string) : CustomBlockSignature =
    let topBlocks = BlocklyJson.parseWorkspace workspaceJson

    match topBlocks |> List.tryFind (fun b -> b.blockType = "sk_custom_block_def") with
    | None -> { inputs = []; output = None; outputFields = None }
    | Some defBlock ->
        let inputs =
            match defBlock.extraState with
            | Some el ->
                match el.TryGetProperty("inputs") with
                | true, arr when arr.ValueKind = JsonValueKind.Array ->
                    [ for item in arr.EnumerateArray() ->
                        let name = match item.TryGetProperty("name") with
                                   | true, n -> n.GetString()
                                   | false, _ -> ""
                        let typeLabel = match item.TryGetProperty("typeLabel") with
                                        | true, t -> t.GetString()
                                        | false, _ -> "Anzahl"
                        { name = name; inputType = typeLabel } ]
                | _ -> []
            | None -> []

        match defBlock.inputs.TryFind "RETURN" with
        | None -> { inputs = inputs; output = None; outputFields = None }
        | Some returnBlock when returnBlock.blockType = "sk_build_record" ->
            { inputs = inputs
              output = Some "$record"
              outputFields = Some(recordFieldNames returnBlock) }
        | Some _ -> { inputs = inputs; output = Some "$value"; outputFields = None }

/// Compiles a Blockly workspace's serialized JSON into the DSL (§10).
let compileWorkspace
    (locale: Locale)
    (lookup: string -> CustomBlockDefinition option)
    (workspaceJson: string)
    : Result<CompiledProgram, DslError list> =
    let state = newState locale lookup
    let topBlocks = BlocklyJson.parseWorkspace workspaceJson
    let instructions = topBlocks |> List.collect (fun b -> compileStatementChain state (Some b))
    if state.errors.Count > 0 then
        Error(List.ofSeq state.errors)
    else
        Ok
            { version = 1
              customBlocks = state.customBlocks |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
              instructions = instructions }
