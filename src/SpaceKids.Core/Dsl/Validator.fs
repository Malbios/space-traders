module SpaceKids.Core.Dsl.Validator

open SpaceKids.Core.Dsl

/// Static validation (§11). Many of §11's checks (required inputs present, block
/// connections valid, only allowed block types present) are already enforced by
/// Compiler.fs — an unknown block type or a missing input becomes a compile error
/// before a `CompiledProgram` ever exists, so there's nothing left to re-check here.
/// This module covers what can only be checked once the whole program (and its
/// referenced custom blocks) is assembled: scope, custom-block call arity, transitive
/// closure, cycles, and — separately — the §9 signature-mismatch check.

let private maxCustomBlockDepth = 20

let rec private exprRefs (expr: Expr) : string list =
    match expr with
    | VariableRef name -> [ name ]
    | ParamRef name -> [ name ]
    | TempRef _ -> []
    | Literal _ -> []
    | Accessor(_, target) -> exprRefs target
    | Arithmetic(_, l, r) -> exprRefs l @ exprRefs r
    | Comparison(_, l, r) -> exprRefs l @ exprRefs r
    | LogicalOp(_, l, r) -> exprRefs l @ exprRefs r
    | LogicalNot operand -> exprRefs operand
    | ListLiteral items -> items |> List.collect exprRefs
    | RecordLiteral fields -> fields |> List.collect (snd >> exprRefs)
    | ListGet(list, index) -> exprRefs list @ exprRefs index

/// All variable/parameter names referenced anywhere in an instruction (including
/// nested bodies), and all customBlockIds called (including nested bodies).
let rec private walk (instr: Instruction) : (string list * string list) =
    let exprsOf (args: Map<string, Expr>) = args |> Map.toList |> List.collect (snd >> exprRefs)
    let bodyRefs (body: Instruction list) =
        body |> List.map walk |> List.fold (fun (rs, cs) (r, c) -> rs @ r, cs @ c) ([], [])

    match instr with
    | ApiAction(_, _, args) -> exprsOf args, []
    | InfoRead(_, _, args, _) -> exprsOf args, []
    | ShowMessage(_, text) -> exprRefs text, []
    | Wait(_, seconds) -> exprRefs seconds, []
    | SetVariable(_, _, value) -> exprRefs value, []
    | ChangeVariable(_, _, delta) -> exprRefs delta, []
    | If(_, branches, elseBranch) ->
        let branchRefs, branchCalls =
            branches
            |> List.map (fun (cond, body) ->
                let br, bc = bodyRefs body
                exprRefs cond @ br, bc)
            |> List.fold (fun (rs, cs) (r, c) -> rs @ r, cs @ c) ([], [])
        let elseRefs, elseCalls = elseBranch |> Option.map bodyRefs |> Option.defaultValue ([], [])
        branchRefs @ elseRefs, branchCalls @ elseCalls
    | Repeat(_, count, body) ->
        let r, c = bodyRefs body
        exprRefs count @ r, c
    | WhileUntil(_, _, cond, body) ->
        let r, c = bodyRefs body
        exprRefs cond @ r, c
    | ForEach(_, _, list, body) ->
        let r, c = bodyRefs body
        exprRefs list @ r, c
    | CallCustomBlock(_, customBlockId, args, _) -> exprsOf args, [ customBlockId ]
    | Break _ -> [], []
    | Continue _ -> [], []

/// Names "declared" within an instruction list's own scope (SetVariable targets,
/// ForEach loop variables) — used for the scope check below. Custom-block parameters
/// are declared by the block's signature, not by any instruction.
let rec private declaredNames (instructions: Instruction list) : string list =
    instructions
    |> List.collect (fun instr ->
        match instr with
        | SetVariable(_, name, _) -> [ name ]
        | ForEach(_, var, _, body) -> var :: declaredNames body
        | If(_, branches, elseBranch) ->
            (branches |> List.collect (snd >> declaredNames))
            @ (elseBranch |> Option.map declaredNames |> Option.defaultValue [])
        | Repeat(_, _, body) -> declaredNames body
        | WhileUntil(_, _, _, body) -> declaredNames body
        | _ -> [])

let private checkScope (locale: Locale) (scopeName: string) (declared: Set<string>) (instructions: Instruction list) : DslError list =
    let allDeclared = declared + Set.ofList (declaredNames instructions)
    instructions
    |> List.collect (fun instr ->
        let refs, _ = walk instr
        refs
        |> List.filter (fun name -> not (allDeclared.Contains name))
        |> List.distinct
        |> List.map (fun name ->
            let message =
                match locale with
                | De -> $"Die Variable \"{name}\" ist in {scopeName} nicht bekannt."
                | En -> $"The variable \"{name}\" is not known in {scopeName}."

            { blockId = None; message = message }))

/// A `Break`/`Continue` is only meaningful inside a `Repeat`/`WhileUntil`/`ForEach`
/// body — Blockly's own `controls_flow_in_loop_check` extension already guards this
/// client-side, but a stored/hand-crafted program's JSON isn't guaranteed to have gone
/// through that UI, so this is the server-side backstop.
let rec private checkFlowStatements (locale: Locale) (insideLoop: bool) (instructions: Instruction list) : DslError list =
    instructions
    |> List.collect (fun instr ->
        match instr with
        | Break blockId
        | Continue blockId when not insideLoop ->
            let message =
                match locale with
                | De -> "\"Verlassen\"/\"Weiter\" darf nur innerhalb einer Schleife verwendet werden."
                | En -> "\"Break\"/\"Continue\" may only be used inside a loop."

            [ { blockId = Some blockId; message = message } ]
        | Break _
        | Continue _ -> []
        | If(_, branches, elseBranch) ->
            (branches |> List.collect (snd >> checkFlowStatements locale insideLoop))
            @ (elseBranch |> Option.map (checkFlowStatements locale insideLoop) |> Option.defaultValue [])
        | Repeat(_, _, body) -> checkFlowStatements locale true body
        | WhileUntil(_, _, _, body) -> checkFlowStatements locale true body
        | ForEach(_, _, _, body) -> checkFlowStatements locale true body
        | _ -> [])

let private literalTypeMismatch (inputType: string) (arg: Expr) : bool =
    match inputType, arg with
    | "Zahl", Literal(StringLit _) -> true
    | "Zahl", Literal(BoolLit _) -> true
    | _ -> false

let private checkCustomBlockCalls (locale: Locale) (program: CompiledProgram) (instructions: Instruction list) : DslError list =
    instructions
    |> List.collect (fun instr ->
        let _, callIds = walk instr
        callIds
        |> List.collect (fun customBlockId ->
            match program.customBlocks.TryFind customBlockId with
            | None ->
                let message =
                    match locale with
                    | De -> $"Der eigene Block \"{customBlockId}\" fehlt in der vollständigen Abschlussmenge (transitive closure)."
                    | En -> $"The custom block \"{customBlockId}\" is missing from the transitive closure."

                [ { blockId = None; message = message } ]
            | Some compiled ->
                match instr with
                | CallCustomBlock(_, _, arguments, _) ->
                    let expectedNames = compiled.signature.inputs |> List.map (fun i -> i.name) |> Set.ofList
                    let actualNames = arguments |> Map.toList |> List.map fst |> Set.ofList
                    let missing = Set.difference expectedNames actualNames
                    let extra = Set.difference actualNames expectedNames

                    let missingMessage n =
                        match locale with
                        | De -> $"Eingabe \"{n}\" fehlt beim Aufruf von \"{customBlockId}\"."
                        | En -> $"Input \"{n}\" is missing from the call to \"{customBlockId}\"."

                    let extraMessage n =
                        match locale with
                        | De -> $"Unbekannte Eingabe \"{n}\" beim Aufruf von \"{customBlockId}\"."
                        | En -> $"Unknown input \"{n}\" in the call to \"{customBlockId}\"."

                    let arityErrors =
                        (missing |> Set.toList |> List.map missingMessage) @ (extra |> Set.toList |> List.map extraMessage)

                    let typeErrors =
                        compiled.signature.inputs
                        |> List.choose (fun input ->
                            arguments.TryFind input.name
                            |> Option.bind (fun arg ->
                                if literalTypeMismatch input.inputType arg then
                                    let message =
                                        match locale with
                                        | De -> $"Eingabe \"{input.name}\" bei \"{customBlockId}\" hat den falschen Typ."
                                        | En -> $"Input \"{input.name}\" at \"{customBlockId}\" has the wrong type."

                                    Some message
                                else
                                    None))

                    (arityErrors @ typeErrors) |> List.map (fun m -> { blockId = None; message = m })
                | _ -> []))

let rec private detectCycles (locale: Locale) (program: CompiledProgram) : DslError list =
    let visited = System.Collections.Generic.HashSet<string>()
    let onStack = System.Collections.Generic.HashSet<string>()
    let errors = ResizeArray<DslError>()

    let rec visit (id: string) (depth: int) =
        if depth > maxCustomBlockDepth then
            let message =
                match locale with
                | De -> $"Eigene Blöcke sind zu tief verschachtelt (mehr als {maxCustomBlockDepth} Ebenen)."
                | En -> $"Custom blocks are nested too deeply (more than {maxCustomBlockDepth} levels)."

            errors.Add { blockId = None; message = message }
        elif onStack.Contains id then
            let message =
                match locale with
                | De -> $"Der eigene Block \"{id}\" ruft sich selbst auf (direkt oder über andere Blöcke) — das ist nicht erlaubt."
                | En -> $"The custom block \"{id}\" calls itself (directly or through other blocks) — this is not allowed."

            errors.Add { blockId = None; message = message }
        elif not (visited.Contains id) then
            visited.Add id |> ignore
            onStack.Add id |> ignore
            match program.customBlocks.TryFind id with
            | Some compiled ->
                let _, calledIds = compiled.instructions |> List.map walk |> List.fold (fun (rs, cs) (r, c) -> rs @ r, cs @ c) ([], [])
                for calledId in calledIds |> List.distinct do
                    visit calledId (depth + 1)
            | None -> ()
            onStack.Remove id |> ignore

    for id in program.customBlocks.Keys do
        visit id 0

    List.ofSeq errors

/// The full static-validation pass (§11) for an already-compiled program.
let validate (locale: Locale) (program: CompiledProgram) : DslError list =
    let startBlockError =
        if List.isEmpty program.instructions then
            let message =
                match locale with
                | De -> "Das Programm hat keinen Startblock."
                | En -> "The program has no start block."

            [ { blockId = None; message = message } ]
        else
            []

    let mainScopeName =
        match locale with
        | De -> "im Hauptprogramm"
        | En -> "in the main program"

    let programScopeErrors = checkScope locale mainScopeName Set.empty program.instructions

    let customBlockScopeErrors =
        program.customBlocks
        |> Map.toList
        |> List.collect (fun (id, compiled) ->
            let paramNames = compiled.signature.inputs |> List.map (fun i -> i.name) |> Set.ofList

            let scopeName =
                match locale with
                | De -> $"im eigenen Block \"{id}\""
                | En -> $"in the custom block \"{id}\""

            checkScope locale scopeName paramNames compiled.instructions)

    let callErrors =
        checkCustomBlockCalls locale program program.instructions
        @ (program.customBlocks |> Map.toList |> List.collect (fun (_, c) -> checkCustomBlockCalls locale program c.instructions))

    let cycleErrors = detectCycles locale program

    let flowStatementErrors =
        checkFlowStatements locale false program.instructions
        @ (program.customBlocks |> Map.toList |> List.collect (fun (_, c) -> checkFlowStatements locale false c.instructions))

    startBlockError @ programScopeErrors @ customBlockScopeErrors @ callErrors @ cycleErrors @ flowStatementErrors

/// The §9 mismatch check: compares each custom block's frozen signature snapshot
/// against its *current* live definition. Only meaningful when re-checking an
/// already-compiled program later — nothing has had a chance to drift within a single
/// fresh `Compiler.compileWorkspace` call.
let revalidateAgainstCurrentDefinitions
    (locale: Locale)
    (lookup: string -> CustomBlockDefinition option)
    (program: CompiledProgram)
    : DslError list =
    program.customBlocks
    |> Map.toList
    |> List.collect (fun (id, compiled) ->
        match lookup id with
        | None ->
            let message =
                match locale with
                | De -> $"Der eigene Block \"{id}\" existiert nicht mehr."
                | En -> $"The custom block \"{id}\" no longer exists."

            [ { blockId = None; message = message } ]
        | Some definition when definition.signature <> compiled.signature ->
            let message =
                match locale with
                | De -> $"Der eigene Block \"{id}\" wurde geändert (andere Eingaben oder Ergebnis) und muss neu geprüft werden."
                | En -> $"The custom block \"{id}\" has changed (different inputs or result) and must be re-checked."

            [ { blockId = None; message = message } ]
        | Some _ -> [])
