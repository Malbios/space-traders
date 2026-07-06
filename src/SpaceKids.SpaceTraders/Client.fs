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

    /// The real API paginates list endpoints (default 10/page, max 20/page) — a
    /// single unpaginated fetch silently truncates any account with more than one
    /// page of ships/contracts/waypoints (confirmed: a real home system alone can
    /// exceed one page of waypoints). Walks every page until `meta.total` is
    /// satisfied.
    member private this.GetAllPages<'a>(token: string, path: string) : Async<'a list> =
        let pageSize = 20

        let rec loop (page: int) (acc: 'a list) : Async<'a list> =
            async {
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

    member this.PurchaseShip(token: string, shipType: string, waypointSymbol: string) : Async<PurchaseShipResult> =
        this.PostData(
            token,
            "my/ships",
            Map.ofList [ "shipType", box shipType; "waypointSymbol", box waypointSymbol ]
        )

    member this.Refuel(token: string, shipSymbol: string) : Async<RefuelResult> =
        this.PostData(token, $"my/ships/{shipSymbol}/refuel", Map.empty)

    member this.GetContract(token: string, contractId: string) : Async<GetContractResult> =
        this.GetData(token, $"my/contracts/{contractId}")

    member this.GetShipyard(token: string, systemSymbol: string, waypointSymbol: string) : Async<Shipyard> =
        this.GetData(token, $"systems/{systemSymbol}/waypoints/{waypointSymbol}/shipyard")
