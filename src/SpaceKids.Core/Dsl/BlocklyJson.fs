module SpaceKids.Core.Dsl.BlocklyJson

open System.Text.Json

/// Minimal parse of Blockly's serialized workspace JSON, verified against the real
/// output of Blockly 13.1.0 (this project's pinned version) rather than guessed: a
/// block is `{type, id, x, y}` plus `fields`/`inputs`/`next`/`extraState` only when
/// populated. Value/statement inputs and `next` always nest their target under a
/// `"block"` key.
type RawBlock =
    { blockType: string
      id: string
      fields: Map<string, JsonElement>
      inputs: Map<string, RawBlock>
      next: RawBlock option
      extraState: JsonElement option }

let private parseFields (el: JsonElement) : Map<string, JsonElement> =
    el.EnumerateObject()
    |> Seq.map (fun p -> p.Name, p.Value.Clone())
    |> Map.ofSeq

/// JsonElements are only valid while their source JsonDocument is alive; every element
/// retained past this function's own parse (fields, extraState) is cloned so the
/// document can be disposed safely by the caller.
let rec private parseBlock (el: JsonElement) : RawBlock =
    let blockType = el.GetProperty("type").GetString()
    let id = el.GetProperty("id").GetString()

    let fields =
        match el.TryGetProperty("fields") with
        | true, f -> parseFields f
        | false, _ -> Map.empty

    let inputs =
        match el.TryGetProperty("inputs") with
        | true, inputsEl ->
            inputsEl.EnumerateObject()
            |> Seq.choose (fun p ->
                match p.Value.TryGetProperty("block") with
                | true, blockEl -> Some(p.Name, parseBlock blockEl)
                | false, _ -> None)
            |> Map.ofSeq
        | false, _ -> Map.empty

    let next =
        match el.TryGetProperty("next") with
        | true, nextEl ->
            match nextEl.TryGetProperty("block") with
            | true, blockEl -> Some(parseBlock blockEl)
            | false, _ -> None
        | false, _ -> None

    let extraState =
        match el.TryGetProperty("extraState") with
        | true, e -> Some(e.Clone())
        | false, _ -> None

    { blockType = blockType
      id = id
      fields = fields
      inputs = inputs
      next = next
      extraState = extraState }

/// Parses `{"blocks":{"languageVersion":...,"blocks":[...]}}` into the top-level block
/// list, in canvas order (matching Blockly's own `getTopBlocks(true)`). Blockly's own
/// `serialization.workspaces.save()` omits the top-level `"blocks"` section entirely
/// for a workspace with zero blocks (a real, reachable state — a player can start a
/// program before placing any blocks) rather than emitting an empty array, so that
/// case means "no blocks", not malformed input.
let parseWorkspace (json: string) : RawBlock list =
    use doc = JsonDocument.Parse(json)

    match doc.RootElement.TryGetProperty("blocks") with
    | true, blocksEl ->
        match blocksEl.TryGetProperty("blocks") with
        | true, blockArray -> blockArray.EnumerateArray() |> Seq.map parseBlock |> Seq.toList
        | false, _ -> []
    | false, _ -> []
