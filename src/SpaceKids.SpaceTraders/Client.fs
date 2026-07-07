namespace SpaceKids.SpaceTraders

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json

exception SpaceTradersApiException of statusCode: int * body: string

/// Raised specifically on HTTP 429 so the request queue (§13) can classify and wait
/// out the real `Retry-After` the server sent, instead of re-parsing status codes.
exception SpaceTradersRateLimitException of retryAfterSeconds: float * body: string

type SpaceTradersClient(httpClient: HttpClient) =

    static let jsonOptions =
        JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    member private _.CheckStatus(response: HttpResponseMessage, body: string) : unit =
        if int response.StatusCode = 429 then
            let retryAfterSeconds =
                match response.Headers.RetryAfter with
                | null -> 1.0
                | retryAfter when retryAfter.Delta.HasValue -> retryAfter.Delta.Value.TotalSeconds
                | retryAfter when retryAfter.Date.HasValue -> max 1.0 ((retryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds)
                | _ -> 1.0
            raise (SpaceTradersRateLimitException(retryAfterSeconds, body))
        if not response.IsSuccessStatusCode then
            raise (SpaceTradersApiException(int response.StatusCode, body))

    member private this.HandleResponse<'a>(response: HttpResponseMessage, body: string) : 'a =
        this.CheckStatus(response, body)
        let envelope = JsonSerializer.Deserialize<DataEnvelope<'a>>(body, jsonOptions)
        envelope.data

    member private this.GetData<'a>(token: string, path: string) : Async<'a> =
        async {
            use request = new HttpRequestMessage(HttpMethod.Get, path)
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
            let! response = httpClient.SendAsync(request) |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            return this.HandleResponse<'a>(response, body)
        }

    /// POST counterpart to `GetData` (Milestone 6, §13/§19) — same 429/error handling,
    /// a JSON-serialized request body. `body` is `Map.empty` for the no-payload actions
    /// (orbit/dock/extract).
    member private this.PostData<'a>(token: string, path: string, body: Map<string, obj>) : Async<'a> =
        async {
            use request = new HttpRequestMessage(HttpMethod.Post, path)
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
            let json = JsonSerializer.Serialize(body, jsonOptions)
            request.Content <- new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            let! response = httpClient.SendAsync(request) |> Async.AwaitTask
            let! respBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            return this.HandleResponse<'a>(response, respBody)
        }

    member private this.PatchData<'a>(token: string, path: string, body: Map<string, obj>) : Async<'a> =
        async {
            use request = new HttpRequestMessage(HttpMethod.Patch, path)
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
            let json = JsonSerializer.Serialize(body, jsonOptions)
            request.Content <- new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            let! response = httpClient.SendAsync(request) |> Async.AwaitTask
            let! respBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            return this.HandleResponse<'a>(response, respBody)
        }

    /// The real API paginates list endpoints (default 10/page, max 20/page) — a
    /// single unpaginated fetch silently truncates any account with more than one
    /// page of ships/contracts/waypoints (confirmed: a real home system alone can
    /// exceed one page of waypoints). Walks every page until `meta.total` is
    /// satisfied.
    member private this.GetAllPages<'a>(token: string, path: string) : Async<'a list> =
        let pageSize = 20

        let rec loop (page: int) (acc: 'a list) : Async<'a list> =
            async {
                if page > 1 then
                    do! Async.Sleep(550)

                let sep = if path.Contains("?") then "&" else "?"
                use request = new HttpRequestMessage(HttpMethod.Get, $"{path}{sep}page={page}&limit={pageSize}")
                request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
                let! response = httpClient.SendAsync(request) |> Async.AwaitTask
                let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                this.CheckStatus(response, body)
                let envelope = JsonSerializer.Deserialize<PagedEnvelope<'a>>(body, jsonOptions)
                let combined = acc @ envelope.data

                if envelope.data.IsEmpty || combined.Length >= envelope.meta.total then
                    return combined
                else
                    return! loop (page + 1) combined
            }

        loop 1 []

    member this.GetAgent(token: string) : Async<Agent> =
        this.GetData(token, "my/agent")

    member this.ListShips(token: string) : Async<Ship list> =
        this.GetAllPages(token, "my/ships")

    /// Single-ship fetch (Milestone 6) — the reconciliation call §13 describes: "get
    /// current ship state" to compare against a pre-call baseline after an ambiguous
    /// failure.
    member this.GetShip(token: string, shipSymbol: string) : Async<Ship> =
        this.GetData(token, $"my/ships/{shipSymbol}")

    member this.ListContracts(token: string) : Async<Contract list> =
        this.GetAllPages(token, "my/contracts")

    member this.ListWaypoints(token: string, systemSymbol: string) : Async<Waypoint list> =
        this.GetAllPages(token, $"systems/{systemSymbol}/waypoints")

    member this.GetMarket(token: string, systemSymbol: string, waypointSymbol: string) : Async<Market> =
        this.GetData(token, $"systems/{systemSymbol}/waypoints/{waypointSymbol}/market")

    member this.Navigate(token: string, shipSymbol: string, waypointSymbol: string) : Async<NavigateResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/navigate", Map.ofList [ "waypointSymbol", box waypointSymbol ])

    member this.Orbit(token: string, shipSymbol: string) : Async<NavResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/orbit", Map.empty)

    member this.Dock(token: string, shipSymbol: string) : Async<NavResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/dock", Map.empty)

    member this.Extract(token: string, shipSymbol: string) : Async<ExtractResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/extract", Map.empty)

    member this.BuyGood(token: string, shipSymbol: string, tradeSymbol: string, units: int) : Async<TradeResult> =
        this.PostData(
            token,
            $"my/ships/{shipSymbol}/purchase",
            Map.ofList [ "symbol", box tradeSymbol; "units", box units ]
        )

    member this.SellGood(token: string, shipSymbol: string, tradeSymbol: string, units: int) : Async<TradeResult> =
        this.PostData(
            token,
            $"my/ships/{shipSymbol}/sell",
            Map.ofList [ "symbol", box tradeSymbol; "units", box units ]
        )

    member this.Survey(token: string, shipSymbol: string) : Async<SurveyResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/survey", Map.empty)

    member this.DeliverContract
        (token: string, contractId: string, shipSymbol: string, tradeSymbol: string, units: int)
        : Async<DeliverContractResult> =
        this.PostData(
            token,
            $"my/contracts/{contractId}/deliver",
            Map.ofList [ "shipSymbol", box shipSymbol; "tradeSymbol", box tradeSymbol; "units", box units ]
        )

    member this.AcceptContract(token: string, contractId: string) : Async<AcceptContractResult> =
        this.PostData(token, $"my/contracts/{contractId}/accept", Map.empty)

    member this.FulfillContract(token: string, contractId: string) : Async<FulfillContractResult> =
        this.PostData(token, $"my/contracts/{contractId}/fulfill", Map.empty)

    member this.NegotiateContract(token: string, shipSymbol: string) : Async<NegotiateContractResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/negotiate/contract", Map.empty)

    member this.PurchaseShip(token: string, shipType: string, waypointSymbol: string) : Async<PurchaseShipResult> =
        this.PostData(
            token,
            "my/ships",
            Map.ofList [ "shipType", box shipType; "waypointSymbol", box waypointSymbol ]
        )

    member this.Refuel(token: string, shipSymbol: string) : Async<RefuelResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/refuel", Map.empty)

    member this.Jettison(token: string, shipSymbol: string, tradeSymbol: string, units: int) : Async<JettisonResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/jettison", Map.ofList [ "symbol", box tradeSymbol; "units", box units ])

    member this.Jump(token: string, shipSymbol: string, waypointSymbol: string) : Async<JumpResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/jump", Map.ofList [ "waypointSymbol", box waypointSymbol ])

    member this.Warp(token: string, shipSymbol: string, waypointSymbol: string) : Async<WarpResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/warp", Map.ofList [ "waypointSymbol", box waypointSymbol ])

    member this.TransferCargo
        (token: string, shipSymbol: string, tradeSymbol: string, units: int, targetShipSymbol: string)
        : Async<TransferResult> =
        this.PostData(
            token,
            $"my/ships/{shipSymbol}/transfer",
            Map.ofList [ "tradeSymbol", box tradeSymbol; "units", box units; "shipSymbol", box targetShipSymbol ]
        )

    member this.Siphon(token: string, shipSymbol: string) : Async<SiphonResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/siphon", Map.empty)

    member this.ScrapShip(token: string, shipSymbol: string) : Async<ScrapResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/scrap", Map.empty)

    member this.Repair(token: string, shipSymbol: string) : Async<RepairResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/repair", Map.empty)

    member this.Refine(token: string, shipSymbol: string, produce: string) : Async<RefineResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/refine", Map.ofList [ "produce", box produce ])

    member this.ScanShips(token: string, shipSymbol: string) : Async<ScanResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/scan/ships", Map.empty)

    member this.ScanSystems(token: string, shipSymbol: string) : Async<ScanResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/scan/systems", Map.empty)

    member this.ScanWaypoints(token: string, shipSymbol: string) : Async<ScanResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/scan/waypoints", Map.empty)

    member this.InstallModule(token: string, shipSymbol: string, moduleSymbol: string) : Async<ShipModificationResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/modules/install", Map.ofList [ "symbol", box moduleSymbol ])

    member this.RemoveModule(token: string, shipSymbol: string, moduleSymbol: string) : Async<ShipModificationResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/modules/remove", Map.ofList [ "symbol", box moduleSymbol ])

    member this.InstallMount(token: string, shipSymbol: string, mountSymbol: string) : Async<ShipModificationResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/mounts/install", Map.ofList [ "symbol", box mountSymbol ])

    member this.RemoveMount(token: string, shipSymbol: string, mountSymbol: string) : Async<ShipModificationResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/mounts/remove", Map.ofList [ "symbol", box mountSymbol ])

    member this.CreateChart(token: string, shipSymbol: string) : Async<ChartResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/chart", Map.empty)

    member this.ExtractWithSurvey(token: string, shipSymbol: string, surveySignature: string) : Async<ExtractResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/extract/survey", Map.ofList [ "signature", box surveySignature ])

    member this.GetContract(token: string, contractId: string) : Async<GetContractResult> =
        this.GetData(token, $"my/contracts/{contractId}")

    member this.GetShipyard(token: string, systemSymbol: string, waypointSymbol: string) : Async<Shipyard> =
        this.GetData(token, $"systems/{systemSymbol}/waypoints/{waypointSymbol}/shipyard")

    member this.ListFactions(token: string) : Async<Faction list> =
        this.GetAllPages(token, "factions")

    member this.ListMyFactions(token: string) : Async<FactionReputation list> =
        this.GetAllPages(token, "my/factions")

    member this.ListSystems(token: string) : Async<StarSystem list> =
        this.GetAllPages(token, "systems")

    member this.GetSystem(token: string, systemSymbol: string) : Async<StarSystem> =
        this.GetData(token, $"systems/{systemSymbol}")

    member this.GetWaypoint(token: string, systemSymbol: string, waypointSymbol: string) : Async<Waypoint> =
        this.GetData(token, $"systems/{systemSymbol}/waypoints/{waypointSymbol}")

    member this.GetJumpGate(token: string, systemSymbol: string, waypointSymbol: string) : Async<JumpGate> =
        this.GetData(token, $"systems/{systemSymbol}/waypoints/{waypointSymbol}/jump-gate")

    member this.GetConstruction(token: string, systemSymbol: string, waypointSymbol: string) : Async<Construction> =
        this.GetData(token, $"systems/{systemSymbol}/waypoints/{waypointSymbol}/construction")

    member this.SupplyConstruction
        (token: string, systemSymbol: string, waypointSymbol: string, shipSymbol: string, tradeSymbol: string, units: int)
        : Async<SupplyConstructionResult> =
        this.PostData(
            token,
            $"systems/{systemSymbol}/waypoints/{waypointSymbol}/construction/supply",
            Map.ofList [ "shipSymbol", box shipSymbol; "tradeSymbol", box tradeSymbol; "units", box units ]
        )

    member this.ListAgents(token: string) : Async<Agent list> =
        this.GetAllPages(token, "agents")

    member this.GetPublicAgent(token: string, agentSymbol: string) : Async<Agent> =
        this.GetData(token, $"agents/{agentSymbol}")

    member this.GetFaction(token: string, factionSymbol: string) : Async<Faction> =
        this.GetData(token, $"factions/{factionSymbol}")

    member this.GetSupplyChain(token: string) : Async<SupplyChainEntry list> =
        async {
            let! data = this.GetData<SupplyChainData>(token, "market/supply-chain")

            return
                data.exportToImportMap
                |> Map.toList
                |> List.collect (fun (exportSymbol, imports) ->
                    imports |> List.map (fun importSymbol -> { exportSymbol = exportSymbol; importSymbol = importSymbol }))
        }

    member this.GetRepairCost(token: string, shipSymbol: string) : Async<GetRepairResult> =
        this.GetData(token, $"my/ships/{shipSymbol}/repair")

    member this.GetScrapValue(token: string, shipSymbol: string) : Async<GetScrapResult> =
        this.GetData(token, $"my/ships/{shipSymbol}/scrap")

    member this.GetShipCooldown(token: string, shipSymbol: string) : Async<GetCooldownResult> =
        this.GetData(token, $"my/ships/{shipSymbol}/cooldown")

    member this.GetShipNav(token: string, shipSymbol: string) : Async<GetNavResult> =
        this.GetData(token, $"my/ships/{shipSymbol}/nav")

    member this.PatchShipNav(token: string, shipSymbol: string, flightMode: string) : Async<PatchNavResult> =
        this.PatchData(token, $"my/ships/{shipSymbol}/nav", Map.ofList [ "flightMode", box flightMode ])

    member this.GetShipModules(token: string, shipSymbol: string) : Async<GetModulesResult> =
        this.GetData(token, $"my/ships/{shipSymbol}/modules")

    member this.GetShipMounts(token: string, shipSymbol: string) : Async<GetMountsResult> =
        this.GetData(token, $"my/ships/{shipSymbol}/mounts")
