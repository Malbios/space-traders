module UpdateTests

/// `Update.update` (really `SpaceKids.Client.Main.update`) is a plain function —
/// `IJSRuntime -> ...six remote-service records... -> Message -> Model -> Model *
/// Cmd<Message>` — nothing here needs a browser. Bolero remote services are ordinary
/// F# records of functions, trivially stubbed. `Cmd` values are inert (a list of
/// `Dispatch<Message> -> unit` closures) until something actually invokes them, so
/// these tests assert on the returned `Model` alone and never touch the `IJSRuntime`
/// stub — safe as long as the arms under test are pure state transitions, not ones
/// that kick off a real remote call.
///
/// Found in review (see the session that added the Markets/Shipyards tabs and
/// removed market/shipyard from the waypoint inspector): `update`'s ~150 `Message`
/// cases had zero direct test coverage — only pure helper functions were tested.
/// This file doesn't attempt exhaustive coverage of every case; it prioritizes the
/// newest, previously-untested logic (Markets/Shipyards) plus a representative slice
/// of older cases, establishing the pattern for future additions to follow.
open Xunit
open Elmish
open Microsoft.JSInterop
open SpaceKids.SpaceTraders
open SpaceKids.Client.Main

let private unexpectedCall name = fun _ -> failwith $"{name} should not be called by a pure state-transition test"

let private fakeWorkspaceService: WorkspaceService =
    { save = unexpectedCall "WorkspaceService.save"
      load = unexpectedCall "WorkspaceService.load" }

let private fakeAgentService: AgentService =
    { submitToken = unexpectedCall "AgentService.submitToken"
      loadDashboard = unexpectedCall "AgentService.loadDashboard"
      refreshDashboard = unexpectedCall "AgentService.refreshDashboard"
      getGalaxyCatalog = unexpectedCall "AgentService.getGalaxyCatalog"
      reloadGalaxy = unexpectedCall "AgentService.reloadGalaxy"
      reloadSystem = unexpectedCall "AgentService.reloadSystem"
      loadFactions = unexpectedCall "AgentService.loadFactions"
      loadSystemWaypoints = unexpectedCall "AgentService.loadSystemWaypoints"
      loadPublicAgents = unexpectedCall "AgentService.loadPublicAgents"
      getWaypointMarket = unexpectedCall "AgentService.getWaypointMarket"
      getWaypointShipyard = unexpectedCall "AgentService.getWaypointShipyard"
      acceptContract = unexpectedCall "AgentService.acceptContract"
      fulfillContract = unexpectedCall "AgentService.fulfillContract" }

let private fakeQueueService: QueueService =
    { getStatus = unexpectedCall "QueueService.getStatus" }

let private fakeJobService: JobService =
    { startJob = unexpectedCall "JobService.startJob"
      step = unexpectedCall "JobService.step"
      run = unexpectedCall "JobService.run"
      getStatus = unexpectedCall "JobService.getStatus"
      pause = unexpectedCall "JobService.pause"
      resume = unexpectedCall "JobService.resume"
      cancel = unexpectedCall "JobService.cancel"
      dismiss = unexpectedCall "JobService.dismiss"
      listJobs = unexpectedCall "JobService.listJobs"
      listHistory = unexpectedCall "JobService.listHistory"
      simulateProgram = unexpectedCall "JobService.simulateProgram" }

let private fakeCustomBlockService: CustomBlockService =
    { list = unexpectedCall "CustomBlockService.list"
      create = unexpectedCall "CustomBlockService.create"
      loadDefinition = unexpectedCall "CustomBlockService.loadDefinition"
      save = unexpectedCall "CustomBlockService.save"
      rename = unexpectedCall "CustomBlockService.rename"
      delete = unexpectedCall "CustomBlockService.delete" }

let private fakeProgramService: ProgramService =
    { list = unexpectedCall "ProgramService.list"
      create = unexpectedCall "ProgramService.create"
      loadDefinition = unexpectedCall "ProgramService.loadDefinition"
      rename = unexpectedCall "ProgramService.rename"
      delete = unexpectedCall "ProgramService.delete" }

let private fakeSettingsService: SettingsService =
    { getLocale = unexpectedCall "SettingsService.getLocale"
      setLocale = unexpectedCall "SettingsService.setLocale"
      getPollIntervalSeconds = unexpectedCall "SettingsService.getPollIntervalSeconds"
      setPollIntervalSeconds = unexpectedCall "SettingsService.setPollIntervalSeconds" }

/// Never invoked by any test here — only ever threaded through as an argument.
let private fakeJs: IJSRuntime = Unchecked.defaultof<IJSRuntime>

let private callUpdate (message: Message) (model: Model) : Model * Cmd<Message> =
    update
        fakeJs
        fakeWorkspaceService
        fakeAgentService
        fakeQueueService
        fakeJobService
        fakeCustomBlockService
        fakeProgramService
        fakeSettingsService
        message
        model

/// Invokes every effect in a `Cmd` with a capturing dispatch and returns whichever
/// messages it synchronously dispatched — only safe for `Cmd.ofMsg`/`Cmd.batch` of
/// those (true for every case exercised below; none of them are `Cmd.OfAsync.*`).
let private dispatchedBy (cmd: Cmd<Message>) : Message list =
    let dispatched = ResizeArray<Message>()
    for sub in cmd do
        sub dispatched.Add
    List.ofSeq dispatched

let private waypoint (symbol: string) (traits: string list) : Waypoint =
    { symbol = symbol
      ``type`` = "PLANET"
      systemSymbol = "X1-TEST"
      x = 0
      y = 0
      traits = traits |> List.map (fun t -> { symbol = t; name = t; description = "" }) }

let private market (symbol: string) : Market =
    { symbol = symbol; exports = []; imports = []; exchange = []; tradeGoods = None }

[<Fact>]
let ``SetMarketsFilter updates only marketsFilterText`` () =
    let model, cmd = callUpdate (SetMarketsFilter "X1-TE") initModel
    Assert.Equal("X1-TE", model.marketsFilterText)
    Assert.Equal("", model.shipyardsFilterText)
    Assert.Empty(cmd)

[<Fact>]
let ``SetShipyardsFilter updates only shipyardsFilterText`` () =
    let model, _ = callUpdate (SetShipyardsFilter "X1-TE") initModel
    Assert.Equal("X1-TE", model.shipyardsFilterText)
    Assert.Equal("", model.marketsFilterText)

[<Fact>]
let ``MarketsSystemWaypointsLoaded for a system the player has since navigated away from is dropped`` () =
    let model = { initModel with marketsSelectedSystem = Some "X1-OTHER" }
    let result, cmd = callUpdate (MarketsSystemWaypointsLoaded("X1-STALE", Ok [])) model
    Assert.Equal(model, result)
    Assert.Empty(cmd)

[<Fact>]
let ``MarketsSystemWaypointsLoaded filters to MARKETPLACE-trait waypoints and auto-loads each not already cached`` () =
    let model =
        { initModel with
            marketsSelectedSystem = Some "X1-TEST"
            marketsWaypointsLoading = true
            marketsData = Map.ofList [ "X1-TEST-B2", market "X1-TEST-B2" ] }

    let waypoints =
        [ waypoint "X1-TEST-A1" [ "MARKETPLACE" ] // not cached -> should auto-load
          waypoint "X1-TEST-B2" [ "MARKETPLACE" ] // already cached -> should not re-load
          waypoint "X1-TEST-C3" [ "SHIPYARD" ] ] // no MARKETPLACE trait -> excluded

    let result, cmd = callUpdate (MarketsSystemWaypointsLoaded("X1-TEST", Ok waypoints)) model

    Assert.False(result.marketsWaypointsLoading)
    Assert.Equal<string list>([ "X1-TEST-A1"; "X1-TEST-B2" ], result.marketsWaypoints |> List.map (fun w -> w.symbol))
    Assert.Equal<Message list>([ LoadMarketData "X1-TEST-A1" ], dispatchedBy cmd)

[<Fact>]
let ``MarketsSystemWaypointsLoaded Error sets marketsWaypointsError and stops loading`` () =
    let model = { initModel with marketsSelectedSystem = Some "X1-TEST"; marketsWaypointsLoading = true }
    let result, cmd = callUpdate (MarketsSystemWaypointsLoaded("X1-TEST", Error "boom")) model
    Assert.False(result.marketsWaypointsLoading)
    Assert.Equal(Some "boom", result.marketsWaypointsError)
    Assert.Empty(cmd)

[<Fact>]
let ``MarketDataLoaded(Some) inserts into marketsData and clears the loading flag`` () =
    let model = { initModel with marketsDataLoading = Set.ofList [ "X1-TEST-A1" ] }
    let result, _ = callUpdate (MarketDataLoaded("X1-TEST-A1", Some(market "X1-TEST-A1"))) model
    Assert.True(result.marketsData.ContainsKey "X1-TEST-A1")
    Assert.False(result.marketsDataLoading.Contains "X1-TEST-A1")

[<Fact>]
let ``MarketDataLoaded(None) clears the loading flag without inserting into marketsData`` () =
    let model = { initModel with marketsDataLoading = Set.ofList [ "X1-TEST-A1" ] }
    let result, _ = callUpdate (MarketDataLoaded("X1-TEST-A1", None)) model
    Assert.False(result.marketsData.ContainsKey "X1-TEST-A1")
    Assert.False(result.marketsDataLoading.Contains "X1-TEST-A1")

[<Fact>]
let ``ToggleContractsHistory flips the flag`` () =
    let model, _ = callUpdate ToggleContractsHistory initModel
    Assert.True(model.contractsHistoryExpanded)
    let model2, _ = callUpdate ToggleContractsHistory model
    Assert.False(model2.contractsHistoryExpanded)

[<Fact>]
let ``AcceptContractClicked sets acceptingContractId and clears any previous error`` () =
    let model = { initModel with contractActionError = Some "previous error" }
    let result, _ = callUpdate (AcceptContractClicked "contract-1") model
    Assert.Equal(Some "contract-1", result.acceptingContractId)
    Assert.Equal(None, result.contractActionError)

[<Fact>]
let ``InspectShip and InspectWaypoint set inspecting; CloseInspector clears it`` () =
    let afterShip, _ = callUpdate (InspectShip "FAKE-AGENT-1") initModel
    Assert.Equal(Some(InspectedShip "FAKE-AGENT-1"), afterShip.inspecting)

    let afterWaypoint, _ = callUpdate (InspectWaypoint "X1-TEST-A1") afterShip
    Assert.Equal(Some(InspectedWaypoint "X1-TEST-A1"), afterWaypoint.inspecting)

    let afterClose, _ = callUpdate CloseInspector afterWaypoint
    Assert.Equal(None, afterClose.inspecting)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``SwitchTab to MarketsTab dispatches LoadGalaxyCatalog only when logged in`` (loggedIn: bool) =
    let model =
        { initModel with
            dashboard =
                if loggedIn then
                    Some
                        { agent = { symbol = "FAKE-AGENT"; headquarters = "X1-TEST-A1"; credits = 0; startingFaction = "COSMIC"; shipCount = 1 }
                          ships = []
                          contracts = []
                          systems = []
                          selectedSystemSymbol = "X1-TEST"
                          waypoints = []
                          markets = [] }
                else
                    None }

    let result, cmd = callUpdate (SwitchTab MarketsTab) model
    Assert.Equal(MarketsTab, result.activeTab)
    let dispatched = dispatchedBy cmd
    Assert.Equal(loggedIn, dispatched |> List.contains LoadGalaxyCatalog)

[<Fact>]
let ``SwitchTab to ContractsTab dispatches RefreshDashboard when logged in`` () =
    let model =
        { initModel with
            dashboard =
                Some
                    { agent = { symbol = "FAKE-AGENT"; headquarters = "X1-TEST-A1"; credits = 0; startingFaction = "COSMIC"; shipCount = 1 }
                      ships = []
                      contracts = []
                      systems = []
                      selectedSystemSymbol = "X1-TEST"
                      waypoints = []
                      markets = [] } }

    let _, cmd = callUpdate (SwitchTab ContractsTab) model
    Assert.Equal<Message list>([ RefreshDashboard ], dispatchedBy cmd)
