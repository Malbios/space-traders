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
    | ListLiteral items -> items |> List.collect exprRefs
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

let private checkScope (scopeName: string) (declared: Set<string>) (instructions: Instruction list) : DslError list =
    let allDeclared = declared + Set.ofList (declaredNames instructions)
    instructions
    |> List.collect (fun instr ->
        let refs, _ = walk instr
        refs
        |> List.filter (fun name -> not (allDeclared.Contains name))
        |> List.distinct
        |> List.map (fun name ->
            { blockId = None
              message = $"Die Variable \"{name}\" ist in {scopeName} nicht bekannt." }))

let private literalTypeMismatch (inputType: string) (arg: Expr) : bool =
    match inputType, arg with
    | "Zahl", Literal(StringLit _) -> true
    | "Zahl", Literal(BoolLit _) -> true
    | _ -> false

let private checkCustomBlockCalls (program: CompiledProgram) (instructions: Instruction list) : DslError list =
    instructions
    |> List.collect (fun instr ->
        let _, callIds = walk instr
        callIds
        |> List.collect (fun customBlockId ->
            match program.customBlocks.TryFind customBlockId with
            | None ->
                [ { blockId = None
                    message = $"Der eigene Block \"{customBlockId}\" fehlt in der vollständigen Abschlussmenge (transitive closure)." } ]
            | Some compiled ->
                match instr with
                | CallCustomBlock(_, _, arguments, _) ->
                    let expectedNames = compiled.signature.inputs |> List.map (fun i -> i.name) |> Set.ofList
                    let actualNames = arguments |> Map.toList |> List.map fst |> Set.ofList
                    let missing = Set.difference expectedNames actualNames
                    let extra = Set.difference actualNames expectedNames
                    let arityErrors =
                        (missing |> Set.toList |> List.map (fun n -> $"Eingabe \"{n}\" fehlt beim Aufruf von \"{customBlockId}\"."))
                        @ (extra |> Set.toList |> List.map (fun n -> $"Unbekannte Eingabe \"{n}\" beim Aufruf von \"{customBlockId}\"."))
                    let typeErrors =
                        compiled.signature.inputs
                        |> List.choose (fun input ->
                            arguments.TryFind input.name
                            |> Option.bind (fun arg ->
                                if literalTypeMismatch input.inputType arg then
                                    Some $"Eingabe \"{input.name}\" bei \"{customBlockId}\" hat den falschen Typ."
                                else
                                    None))
                    (arityErrors @ typeErrors) |> List.map (fun m -> { blockId = None; message = m })
                | _ -> []))

let rec private detectCycles (program: CompiledProgram) : DslError list =
    let visited = System.Collections.Generic.HashSet<string>()
    let onStack = System.Collections.Generic.HashSet<string>()
    let errors = ResizeArray<DslError>()

    let rec visit (id: string) (depth: int) =
        if depth > maxCustomBlockDepth then
            errors.Add
                { blockId = None
                  message = $"Eigene Blöcke sind zu tief verschachtelt (mehr als {maxCustomBlockDepth} Ebenen)." }
        elif onStack.Contains id then
            errors.Add
                { blockId = None
                  message = $"Der eigene Block \"{id}\" ruft sich selbst auf (direkt oder über andere Blöcke) — das ist nicht erlaubt." }
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
let validate (program: CompiledProgram) : DslError list =
    let startBlockError =
        if List.isEmpty program.instructions then
            [ { blockId = None; message = "Das Programm hat keinen Startblock." } ]
        else
            []

    let programScopeErrors = checkScope "im Hauptprogramm" Set.empty program.instructions

    let customBlockScopeErrors =
        program.customBlocks
        |> Map.toList
        |> List.collect (fun (id, compiled) ->
            let paramNames = compiled.signature.inputs |> List.map (fun i -> i.name) |> Set.ofList
            checkScope $"im eigenen Block \"{id}\"" paramNames compiled.instructions)

    let callErrors =
        checkCustomBlockCalls program program.instructions
        @ (program.customBlocks |> Map.toList |> List.collect (fun (_, c) -> checkCustomBlockCalls program c.instructions))

    let cycleErrors = detectCycles program

    startBlockError @ programScopeErrors @ customBlockScopeErrors @ callErrors @ cycleErrors

/// The §9 mismatch check: compares each custom block's frozen signature snapshot
/// against its *current* live definition. Only meaningful when re-checking an
/// already-compiled program later — nothing has had a chance to drift within a single
/// fresh `Compiler.compileWorkspace` call.
let revalidateAgainstCurrentDefinitions
    (lookup: string -> CustomBlockDefinition option)
    (program: CompiledProgram)
    : DslError list =
    program.customBlocks
    |> Map.toList
    |> List.collect (fun (id, compiled) ->
        match lookup id with
        | None -> [ { blockId = None; message = $"Der eigene Block \"{id}\" existiert nicht mehr." } ]
        | Some definition when definition.signature <> compiled.signature ->
            [ { blockId = None
                message = $"Der eigene Block \"{id}\" wurde geändert (andere Eingaben oder Ergebnis) und muss neu geprüft werden." } ]
        | Some _ -> [])
