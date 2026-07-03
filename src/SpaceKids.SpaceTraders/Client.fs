namespace SpaceKids.SpaceTraders

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json

exception SpaceTradersApiException of statusCode: int * body: string

type SpaceTradersClient(httpClient: HttpClient) =

    static let jsonOptions =
        JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    member private _.GetData<'a>(token: string, path: string) : Async<'a> =
        async {
            use request = new HttpRequestMessage(HttpMethod.Get, path)
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
            let! response = httpClient.SendAsync(request) |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            if not response.IsSuccessStatusCode then
                raise (SpaceTradersApiException(int response.StatusCode, body))
            let envelope = JsonSerializer.Deserialize<DataEnvelope<'a>>(body, jsonOptions)
            return envelope.data
        }

    member this.GetAgent(token: string) : Async<Agent> =
        this.GetData(token, "my/agent")

    member this.ListShips(token: string) : Async<Ship list> =
        this.GetData(token, "my/ships")

    member this.ListContracts(token: string) : Async<Contract list> =
        this.GetData(token, "my/contracts")

    member this.ListWaypoints(token: string, systemSymbol: string) : Async<Waypoint list> =
        this.GetData(token, $"systems/{systemSymbol}/waypoints")

    member this.GetMarket(token: string, systemSymbol: string, waypointSymbol: string) : Async<Market> =
        this.GetData(token, $"systems/{systemSymbol}/waypoints/{waypointSymbol}/market")
