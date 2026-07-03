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
open SpaceKids.SpaceTraders

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

/// Milestone 2 (§19): the shared client/server remoting contract for real SpaceTraders
/// data. Every field here is read through the server's request queue stub (§13).
type DashboardState =
    {
        agent: Agent
        ships: Ship list
        contracts: Contract list
        waypoints: Waypoint list
        markets: Market list
    }

type AgentService =
    {
        submitToken: string -> Async<Result<DashboardState, string>>
        loadDashboard: unit -> Async<DashboardState option>
    }

    interface IRemoteService with
        member this.BasePath = "/agent"

type Model =
    {
        containerId: string
        workshopContainerId: string
        lastBlockId: string option
        readOnly: bool
        status: string
        publishedCustomBlockId: string option
        tokenInput: string
        dashboard: DashboardState option
        dashboardLoading: bool
        dashboardError: string option
    }

let initModel =
    {
        containerId = "blockly-spike"
        workshopContainerId = "blockly-workshop-spike"
        lastBlockId = None
        readOnly = false
        status = "Bereit."
        publishedCustomBlockId = None
        tokenInput = ""
        dashboard = None
        dashboardLoading = false
        dashboardError = None
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
    | SimulateRun
    | Simulated
    | TokenInputChanged of string
    | SubmitToken
    | TokenSubmitted of Result<DashboardState, string>
    | LoadDashboard
    | DashboardLoaded of DashboardState option

let private callVoid (js: IJSRuntime) (identifier: string) (args: obj[]) : Async<unit> =
    js.InvokeVoidAsync(identifier, args).AsTask() |> Async.AwaitTask

let private call<'a> (js: IJSRuntime) (identifier: string) (args: obj[]) : Async<'a> =
    js.InvokeAsync<'a>(identifier, args).AsTask() |> Async.AwaitTask

let update (js: IJSRuntime) (remote: WorkspaceService) (agentRemote: AgentService) message model =
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

    | SimulateRun ->
        { model with status = "Simuliere Ausführung..." },
        Cmd.OfAsync.perform (fun () -> callVoid js "spaceKids.simulateRun" [| box model.containerId |]) () (fun () -> Simulated)
    | Simulated ->
        { model with status = "Simulation beendet." }, Cmd.none

    | TokenInputChanged value ->
        { model with tokenInput = value }, Cmd.none
    | SubmitToken ->
        { model with dashboardLoading = true; dashboardError = None },
        Cmd.OfAsync.perform (fun () -> agentRemote.submitToken model.tokenInput) () TokenSubmitted
    | TokenSubmitted(Ok state) ->
        { model with dashboard = Some state; dashboardLoading = false; dashboardError = None }, Cmd.none
    | TokenSubmitted(Error message) ->
        { model with dashboardLoading = false; dashboardError = Some message }, Cmd.none
    | LoadDashboard ->
        { model with dashboardLoading = true }, Cmd.OfAsync.perform (fun () -> agentRemote.loadDashboard ()) () DashboardLoaded
    | DashboardLoaded stateOpt ->
        { model with dashboard = stateOpt; dashboardLoading = false }, Cmd.none

let private viewDashboard model dispatch =
    div {
        h2 { "Echte SpaceTraders-Daten (Milestone 2)" }
        if model.dashboardLoading then
            p { "Lädt..." }
        match model.dashboardError with
        | Some err -> p { $"Fehler: {err}" }
        | None -> ()
        match model.dashboard with
        | None ->
            div {
                input {
                    attr.``type`` "text"
                    attr.placeholder "SpaceTraders-Token einfügen"
                    attr.value model.tokenInput
                    on.change (fun e -> dispatch (TokenInputChanged(string e.Value)))
                }
                button { on.click (fun _ -> dispatch SubmitToken); "Anmelden" }
            }
        | Some state ->
            div {
                h3 { $"Pilot: {state.agent.symbol}" }
                p { $"Kontostand: {state.agent.credits} Credits" }
                p { $"Hauptquartier: {state.agent.headquarters}" }
                h3 { "Schiffe" }
                ul {
                    for ship in state.ships do
                        li { $"{ship.symbol} — {ship.registration.role} — {ship.nav.status} bei {ship.nav.waypointSymbol}" }
                }
                h3 { "Aufträge" }
                ul {
                    for contract in state.contracts do
                        li { $"{contract.id} ({contract.``type``}) — angenommen: {contract.accepted}, erfüllt: {contract.fulfilled}" }
                }
                h3 { "Wegpunkte" }
                ul {
                    for waypoint in state.waypoints do
                        li { $"{waypoint.symbol} ({waypoint.``type``})" }
                }
                h3 { "Markt" }
                for market in state.markets do
                    div {
                        p { $"Markt bei {market.symbol}" }
                        ul {
                            for good in market.exports do
                                li { $"Export: {good.name}" }
                        }
                    }
            }
    }

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
            button { on.click (fun _ -> dispatch SimulateRun); "Simuliere Ausführung" }
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
        viewDashboard model dispatch
    }

type MyApp() =
    inherit ProgramComponent<Model, Message>()

    override this.Program =
        let js = this.JSRuntime
        let remote = this.Remote<WorkspaceService>()
        let agentRemote = this.Remote<AgentService>()
        Program.mkProgram (fun _ -> initModel, Cmd.batch [ Cmd.ofMsg Init; Cmd.ofMsg LoadDashboard ]) (update js remote agentRemote) view
