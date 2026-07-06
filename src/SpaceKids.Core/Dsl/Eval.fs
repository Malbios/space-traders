module SpaceKids.Core.Dsl.Eval

/// Evaluates pure inline expressions (§10: "inline arguments are pure"). Used by the
/// scheduler's `step` (Milestone 6, §14) to resolve literals/variable refs/arithmetic/
/// comparisons while walking "free" transitions — never anything effectful, matching
/// the DSL's own invariant that `Expr` never performs an API call.

let asFloat (v: Value) : float =
    match v with
    | VNumber n -> n
    | VBool b -> if b then 1.0 else 0.0
    | VString s ->
        match System.Double.TryParse s with
        | true, n -> n
        | false, _ -> failwith $"Erwarte eine Zahl, aber der Text \"{s}\" lässt sich nicht in eine Zahl umwandeln."
    | VList _ -> failwith "Erwarte eine Zahl, aber eine Liste wurde übergeben."
    | VRecord _ -> failwith "Erwarte eine Zahl, aber ein Datensatz wurde übergeben."

let asInt (v: Value) : int = int (asFloat v)

let asBool (v: Value) : bool =
    match v with
    | VBool b -> b
    | VNumber n -> n <> 0.0
    | VString s -> s <> ""
    | VList items -> not (List.isEmpty items)
    | VRecord _ -> failwith "Erwarte einen Wahrheitswert, aber ein Datensatz wurde übergeben."

let asString (v: Value) : string =
    match v with
    | VString s -> s
    | VNumber n -> string n
    | VBool b -> string b
    | VList _ -> failwith "Erwarte einen Text, aber eine Liste wurde übergeben."
    | VRecord _ -> failwith "Erwarte einen Text, aber ein Datensatz wurde übergeben."

let asList (v: Value) : Value list =
    match v with
    | VList items -> items
    | _ -> failwith "Erwarte eine Liste."

let asRecord (v: Value) : Map<string, Value> =
    match v with
    | VRecord fields -> fields
    | _ -> failwith "Erwarte einen Datensatz (z.B. Schiff, Fracht, Markt)."

let rec eval (locals: Map<string, Value>) (expr: Expr) : Value =
    match expr with
    | Literal(StringLit s) -> VString s
    | Literal(NumberLit n) -> VNumber n
    | Literal(BoolLit b) -> VBool b
    // VariableRef/ParamRef/TempRef are all just named lookups in the current frame's
    // locals at runtime — the compiler/validator distinguish them for scope-checking
    // purposes (§11), but by the time `step` evaluates one, resolution is identical.
    | VariableRef name
    | ParamRef name
    | TempRef name ->
        match locals.TryFind name with
        | Some v -> v
        | None -> failwith $"Unbekannter Name zur Laufzeit: {name}"
    | Accessor(field, target) ->
        let fields = eval locals target |> asRecord

        match fields.TryFind field with
        | Some v -> v
        | None -> failwith $"Unbekanntes Feld \"{field}\" in diesem Datensatz."
    | Arithmetic(op, left, right) ->
        let l = eval locals left |> asFloat
        let r = eval locals right |> asFloat

        let result =
            match op with
            | "ADD" -> l + r
            | "MINUS" -> l - r
            | "MULTIPLY" -> l * r
            | "DIVIDE" -> l / r
            | "POWER" -> l ** r
            | other -> failwith $"Unbekannter Rechenoperator: {other}"

        VNumber result
    | LogicalOp(op, left, right) ->
        let result =
            match op with
            | "AND" -> (eval locals left |> asBool) && (eval locals right |> asBool)
            | "OR" -> (eval locals left |> asBool) || (eval locals right |> asBool)
            | other -> failwith $"Unbekannter Logikoperator: {other}"

        VBool result
    | LogicalNot operand -> VBool(not (eval locals operand |> asBool))
    | Comparison(op, left, right) ->
        let l = eval locals left
        let r = eval locals right

        let result =
            match op, l, r with
            | "EQ", VString a, VString b -> a = b
            | "NEQ", VString a, VString b -> a <> b
            | "EQ", _, _ -> asFloat l = asFloat r
            | "NEQ", _, _ -> asFloat l <> asFloat r
            | "LT", _, _ -> asFloat l < asFloat r
            | "LTE", _, _ -> asFloat l <= asFloat r
            | "GT", _, _ -> asFloat l > asFloat r
            | "GTE", _, _ -> asFloat l >= asFloat r
            | other, _, _ -> failwith $"Unbekannter Vergleichsoperator: {other}"

        VBool result
    | ListLiteral items -> VList(items |> List.map (eval locals))
    | RecordLiteral fields -> VRecord(fields |> List.map (fun (name, expr) -> name, eval locals expr) |> Map.ofList)
    | ListGet(list, index) ->
        let items = eval locals list |> asList
        let i = eval locals index |> asInt

        if i >= 0 && i < List.length items then
            items.[i]
        else
            failwith "Listenindex außerhalb des gültigen Bereichs."
