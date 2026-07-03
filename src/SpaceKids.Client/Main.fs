module SpaceKids.Client.Main

/// Milestone 0 spike page: proves the Blockly<->Bolero seam described in
/// plan.md §3a end to end (create/save/reload/highlight/read-only), nothing
/// more. Real toolbox, routing, and dashboards come in later milestones.
open Elmish
open Bolero
open Bolero.Html
open Bolero.Remoting
open Bolero.Remoting.Client
open Microsoft.JSInterop

/// Remote service for the Milestone 0 spike only (§12 Persistence lands the real
/// `workspaces` table etc. in Milestone 1) — proves workspace JSON round-trips through
/// SQLite, not just browser memory.
type WorkspaceService =
    {
        save: string * string -> Async<unit>
        load: string -> Async<string option>
    }

    interface IRemoteService with
        member this.BasePath = "/spike-workspaces"

type Model =
    {
        containerId: string
        workshopContainerId: string
        lastBlockId: string option
        readOnly: bool
        status: string
        publishedCustomBlockId: string option
    }

let initModel =
    {
        containerId = "blockly-spike"
        workshopContainerId = "blockly-workshop-spike"
        lastBlockId = None
        readOnly = false
        status = "Bereit."
        publishedCustomBlockId = None
    }

type Message =
    | Init
    | Inited
    | Save
    | Saved
    | Load
    | LoadedFromDb of string option
    | Loaded
    | HighlightFirstBlock
    | Highlighted
    | ToggleReadOnly
    | ReadOnlyToggled
    | PublishSignature
    | Published of string

let private callVoid (js: IJSRuntime) (identifier: string) (args: obj[]) : Async<unit> =
    js.InvokeVoidAsync(identifier, args).AsTask() |> Async.AwaitTask

let private call<'a> (js: IJSRuntime) (identifier: string) (args: obj[]) : Async<'a> =
    js.InvokeAsync<'a>(identifier, args).AsTask() |> Async.AwaitTask

let update (js: IJSRuntime) (remote: WorkspaceService) message model =
    match message with
    | Init ->
        let initBoth = async {
            do! callVoid js "spaceKids.initWorkspace" [| box model.containerId; box model.readOnly |]
            do! callVoid js "spaceKids.initWorkspace" [| box model.workshopContainerId; box false |]
        }
        model, Cmd.OfAsync.perform (fun () -> initBoth) () (fun () -> Inited)
    | Inited ->
        { model with status = "Werkstatt geladen." }, Cmd.none

    | Save ->
        let saveToDb = async {
            let! json = call<string> js "spaceKids.serializeWorkspace" [| box model.containerId |]
            do! remote.save (model.containerId, json)
        }
        model, Cmd.OfAsync.perform (fun () -> saveToDb) () (fun () -> Saved)
    | Saved ->
        { model with status = "In SQLite gespeichert." }, Cmd.none

    | Load ->
        model, Cmd.OfAsync.perform (fun () -> remote.load model.containerId) () LoadedFromDb
    | LoadedFromDb None ->
        { model with status = "Nichts zum Laden." }, Cmd.none
    | LoadedFromDb(Some json) ->
        model, Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.loadWorkspace" [| box model.containerId; box json |]) () (fun () -> Loaded)
    | Loaded ->
        { model with status = "Aus SQLite geladen." }, Cmd.none

    | HighlightFirstBlock ->
        let highlightFirst = async {
            let! idOpt = call<string option> js "spaceKids.firstBlockId" [| box model.containerId |]
            match idOpt with
            | Some id -> do! callVoid js "spaceKids.highlightBlock" [| box model.containerId; box id |]
            | None -> ()
        }
        model, Cmd.OfAsync.perform (fun () -> highlightFirst) () (fun () -> Highlighted)
    | Highlighted ->
        { model with status = "Block hervorgehoben (falls vorhanden)." }, Cmd.none

    | ToggleReadOnly ->
        let next = not model.readOnly
        model, Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.setReadOnly" [| box model.containerId; box next |]) () (fun () -> ReadOnlyToggled)
    | ReadOnlyToggled ->
        { model with readOnly = not model.readOnly; status = "Lesemodus umgeschaltet." }, Cmd.none

    | PublishSignature ->
        let existingId: obj = match model.publishedCustomBlockId with
                               | Some id -> box id
                               | None -> null
        model,
        Cmd.OfAsync.perform
            (fun () -> call<string> js "spaceKids.publishCustomBlockSignature" [| box model.workshopContainerId; box model.containerId; existingId |])
            ()
            Published
    | Published customBlockId ->
        { model with publishedCustomBlockId = Some customBlockId; status = "Signatur an Programm-Werkstatt übergeben." }, Cmd.none

let view model dispatch =
    div {
        attr.style "font-family: sans-serif; padding: 1rem"
        h1 { "SpaceKids – Blockly-Spike (Milestone 0)" }
        p { model.status }
        div {
            button { on.click (fun _ -> dispatch Save); "Speichern" }
            button { on.click (fun _ -> dispatch Load); "Laden" }
            button { on.click (fun _ -> dispatch HighlightFirstBlock); "Ersten Block hervorheben" }
            button {
                on.click (fun _ -> dispatch ToggleReadOnly)
                if model.readOnly then "Bearbeiten erlauben" else "Nur ansehen"
            }
        }
        h2 { "Programm" }
        div {
            attr.id model.containerId
            attr.style "height: 360px; width: 100%; border: 1px solid #ccc; margin-top: 0.5rem"
        }
        h2 { "Blockwerkstatt (Eigener Block definieren)" }
        p {
            "Ziehe \"Eigener Block\" auf die Fläche, öffne sein Zahnrad-Menü, füge eine Eingabe hinzu, dann:"
            button { on.click (fun _ -> dispatch PublishSignature); "Signatur an Programm übergeben" }
        }
        div {
            attr.id model.workshopContainerId
            attr.style "height: 360px; width: 100%; border: 1px solid #ccc; margin-top: 0.5rem"
        }
    }

type MyApp() =
    inherit ProgramComponent<Model, Message>()

    override this.Program =
        let js = this.JSRuntime
        let remote = this.Remote<WorkspaceService>()
        Program.mkProgram (fun _ -> initModel, Cmd.ofMsg Init) (update js remote) view
