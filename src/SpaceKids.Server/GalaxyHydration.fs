module SpaceKids.Server.GalaxyHydration

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading
open SpaceKids.SpaceTraders
open SpaceKids.Client.Main

let galaxyCatalogTtl = TimeSpan.FromHours(6.0)

let private pageSize = 20
let private pageDelayMs = 550
let private backgroundPriority = 2

let private jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

type private SystemsSyncMeta =
    {
        phase: string
        systemsLoaded: int
        /// `0` means unknown — persisted as a plain int so SQLite JSON round-trips
        /// without an F# `option` converter.
        systemsTotal: int
        nextPage: int
        error: string option
    }

let private systemsKey (agentSymbol: string) = $"galaxy:{agentSymbol}:systems"
let private systemsMetaKey (agentSymbol: string) = $"galaxy:{agentSymbol}:systems:meta"
let waypointsKey (agentSymbol: string) (systemSymbol: string) = $"galaxy:{agentSymbol}:waypoints:{systemSymbol}"

let private syncRuns = ConcurrentDictionary<string, bool>()
let private syncCancel = ConcurrentDictionary<string, CancellationTokenSource>()

let private defaultMeta =
    {
        phase = "idle"
        systemsLoaded = 0
        systemsTotal = 0
        nextPage = 1
        error = None
    }

let private readMeta (dbPath: string) (agentSymbol: string) : Async<SystemsSyncMeta> =
    async {
        let! cached = Persistence.ApiCacheRepository.tryGet dbPath (systemsMetaKey agentSymbol)

        match cached with
        | Some(json, _) ->
            try
                return JsonSerializer.Deserialize<SystemsSyncMeta>(json, jsonOptions)
            with _ ->
                return defaultMeta
        | None -> return defaultMeta
    }

let private writeMeta (dbPath: string) (agentSymbol: string) (meta: SystemsSyncMeta) : Async<unit> =
    Persistence.ApiCacheRepository.put dbPath (systemsMetaKey agentSymbol) (JsonSerializer.Serialize(meta, jsonOptions))

let readSystems (dbPath: string) (agentSymbol: string) : Async<StarSystem list> =
    async {
        let! cached = Persistence.ApiCacheRepository.tryGet dbPath (systemsKey agentSymbol)

        match cached with
        | Some(json, _) ->
            try
                return JsonSerializer.Deserialize<StarSystem list>(json, jsonOptions)
            with _ ->
                return []
        | None -> return []
    }

let isSystemsCacheStale (dbPath: string) (agentSymbol: string) : Async<bool> =
    async {
        let! cached = Persistence.ApiCacheRepository.tryGet dbPath (systemsKey agentSymbol)

        match cached with
        | None -> return true
        | Some(_, fetchedAt) -> return DateTimeOffset.UtcNow - fetchedAt > galaxyCatalogTtl
    }

let toSyncStatusDto (dbPath: string) (agentSymbol: string) : Async<GalaxySyncStatusDto> =
    async {
        let! meta = readMeta dbPath agentSymbol
        let! isStale = isSystemsCacheStale dbPath agentSymbol

        return
            {
                phase = meta.phase
                systemsLoaded = meta.systemsLoaded
                systemsTotal = if meta.systemsTotal > 0 then Some meta.systemsTotal else None
                isStale = isStale
                error = meta.error
            }
    }

let private fetchWaypointsCached
    (client: SpaceTradersClient)
    (dbPath: string)
    (agentSymbol: string)
    (token: string)
    (systemSymbol: string)
    : Async<Waypoint list> =
    let cacheKey = waypointsKey agentSymbol systemSymbol

    async {
        match! Persistence.ApiCacheRepository.tryGetFresh dbPath cacheKey galaxyCatalogTtl with
        | Some json -> return JsonSerializer.Deserialize<Waypoint list>(json, jsonOptions)
        | None ->
            match! Persistence.ApiCacheRepository.tryGet dbPath cacheKey with
            | Some(json, _) ->
                let stale = JsonSerializer.Deserialize<Waypoint list>(json, jsonOptions)

                Async.Start(
                    async {
                        try
                            let! fresh =
                                RequestQueue.enqueue dbPath backgroundPriority $"GET /systems/{systemSymbol}/waypoints" None (fun () ->
                                    client.ListWaypoints(token, systemSymbol))

                            do!
                                Persistence.ApiCacheRepository.put dbPath cacheKey (JsonSerializer.Serialize(fresh, jsonOptions))
                        with _ ->
                            ()
                    },
                    cancellationToken = CancellationToken.None)

                return stale
            | None ->
                let! waypoints =
                    RequestQueue.enqueue dbPath 1 $"GET /systems/{systemSymbol}/waypoints" None (fun () ->
                        client.ListWaypoints(token, systemSymbol))

                do! Persistence.ApiCacheRepository.put dbPath cacheKey (JsonSerializer.Serialize(waypoints, jsonOptions))
                return waypoints
    }

let private runSystemsSyncWork (client: SpaceTradersClient) (dbPath: string) (agentSymbol: string) (token: string) (ct: CancellationToken) =
    async {
        let mutable meta = defaultMeta
        let mutable systems = []

        let! existing = readSystems dbPath agentSymbol
        let! existingMeta = readMeta dbPath agentSymbol

        if existingMeta.phase = "syncing" && existingMeta.nextPage > 1 then
            systems <- existing
            meta <- existingMeta
        else
            systems <- []
            meta <-
                { defaultMeta with
                    phase = "syncing"
                    nextPage = 1
                    error = None }

        let rec loop () =
            async {
                if ct.IsCancellationRequested then
                    return ()
                else
                    if meta.nextPage > 1 then
                        do! Async.Sleep(pageDelayMs)

                    if ct.IsCancellationRequested then
                        return ()
                    else
                        try
                            let! envelope =
                                RequestQueue.enqueue dbPath backgroundPriority $"GET /systems?page={meta.nextPage}" None (fun () ->
                                    client.GetListPage<StarSystem>(token, "systems", meta.nextPage, pageSize))

                            systems <- systems @ envelope.data

                            let complete = envelope.data.IsEmpty || systems.Length >= envelope.meta.total

                            meta <-
                                {
                                    phase = if complete then "complete" else "syncing"
                                    systemsLoaded = systems.Length
                                    systemsTotal = envelope.meta.total
                                    nextPage = meta.nextPage + 1
                                    error = None
                                }

                            do! Persistence.ApiCacheRepository.put dbPath (systemsKey agentSymbol) (JsonSerializer.Serialize(systems, jsonOptions))
                            do! writeMeta dbPath agentSymbol meta

                            if not complete then
                                return! loop ()
                        with ex ->
                            meta <-
                                { meta with
                                    phase = "error"
                                    error = Some ex.Message }

                            do! writeMeta dbPath agentSymbol meta
            }

        meta <- { meta with phase = "syncing"; error = None }
        do! writeMeta dbPath agentSymbol meta

        try
            do! loop ()
        finally
            syncRuns.TryRemove(agentSymbol) |> ignore
    }

let private runSystemsSyncBackground (client: SpaceTradersClient) (dbPath: string) (agentSymbol: string) (token: string) (ct: CancellationToken) =
    Async.Start(runSystemsSyncWork client dbPath agentSymbol token ct, cancellationToken = CancellationToken.None)

let ensureSystemsSync (client: SpaceTradersClient) (dbPath: string) (agentSymbol: string) (token: string) (force: bool) =
    if force then
        match syncCancel.TryGetValue(agentSymbol) with
        | true, old ->
            syncCancel.TryRemove(agentSymbol) |> ignore
            old.Cancel()
            old.Dispose()
        | false, _ -> ()

    match syncRuns.TryGetValue(agentSymbol) with
    | true, _ -> ()
    | false, _ ->
        let meta = readMeta dbPath agentSymbol |> Async.RunSynchronously
        let systems = readSystems dbPath agentSymbol |> Async.RunSynchronously
        let isStale = isSystemsCacheStale dbPath agentSymbol |> Async.RunSynchronously

        let needsSync =
            force
            || meta.phase = "error"
            || meta.phase = "syncing"
            || systems.IsEmpty
            || isStale
            || meta.phase <> "complete"

        if needsSync && syncRuns.TryAdd(agentSymbol, true) then
            let cts = new CancellationTokenSource()
            syncCancel.[agentSymbol] <- cts
            runSystemsSyncBackground client dbPath agentSymbol token cts.Token

/// Blocking sync on the caller's async context — keeps test queue pumps and job
/// reads deterministic (background sync is UI-only via `ensureSystemsSync`).
let private runSystemsSyncAwait (client: SpaceTradersClient) (dbPath: string) (agentSymbol: string) (token: string) : Async<unit> =
    async {
        if syncRuns.TryAdd(agentSymbol, true) then
            try
                use cts = new CancellationTokenSource()
                do! runSystemsSyncWork client dbPath agentSymbol token cts.Token
            finally
                syncRuns.TryRemove(agentSymbol) |> ignore
        else
            let deadlineAt = DateTime.UtcNow + TimeSpan.FromMinutes(10.0)

            let rec wait () =
                async {
                    let! meta = readMeta dbPath agentSymbol

                    match meta.phase with
                    | "complete" -> return ()
                    | "error" -> return raise (Exception(meta.error |> Option.defaultValue "Galaxie-Sync fehlgeschlagen."))
                    | _ when DateTime.UtcNow >= deadlineAt -> return ()
                    | _ ->
                        do! Async.Sleep(50)
                        return! wait ()
                }

            do! wait ()
    }

/// Read-through cache for jobs and `loadRestOfState`: returns whatever is cached,
/// otherwise paginates inline through the request queue.
let fetchSystemsCached (client: SpaceTradersClient) (dbPath: string) (agentSymbol: string) (token: string) : Async<StarSystem list> =
    async {
        match! Persistence.ApiCacheRepository.tryGetFresh dbPath (systemsKey agentSymbol) galaxyCatalogTtl with
        | Some json -> return JsonSerializer.Deserialize<StarSystem list>(json, jsonOptions)
        | None ->
            let! systems = readSystems dbPath agentSymbol
            let! meta = readMeta dbPath agentSymbol

            if not systems.IsEmpty && meta.phase = "complete" then
                return systems
            else
                do! runSystemsSyncAwait client dbPath agentSymbol token
                return! readSystems dbPath agentSymbol
    }

let buildCatalogSnapshot (client: SpaceTradersClient) (dbPath: string) (agent: Agent) (token: string) : Async<GalaxyCatalogSnapshot> =
    async {
        let! systems = readSystems dbPath agent.symbol
        let! sync = toSyncStatusDto dbPath agent.symbol
        let hqSystem = Waypoint.systemSymbolOf agent.headquarters
        let! waypoints = fetchWaypointsCached client dbPath agent.symbol token hqSystem

        let! markets =
            async {
                try
                    let! market =
                        RequestQueue.enqueue dbPath 1 "GET /systems/{system}/waypoints/{waypoint}/market" None (fun () ->
                            client.GetMarket(token, hqSystem, agent.headquarters))

                    return [ market ]
                with _ ->
                    return []
            }

        let needsBackground =
            sync.phase <> "syncing"
            && (systems.IsEmpty || sync.isStale || sync.phase <> "complete")

        if needsBackground then
            ensureSystemsSync client dbPath agent.symbol token false

        return
            {
                systems = systems
                selectedSystemSymbol = hqSystem
                waypoints = waypoints
                markets = markets
                sync = sync
            }
    }

let reloadGalaxy (client: SpaceTradersClient) (dbPath: string) (agentSymbol: string) (token: string) : Async<unit> =
    async {
        do! Persistence.ApiCacheRepository.deleteKey dbPath (systemsKey agentSymbol)
        do! Persistence.ApiCacheRepository.deleteKey dbPath (systemsMetaKey agentSymbol)
        ensureSystemsSync client dbPath agentSymbol token true
    }

let reloadSystem
    (client: SpaceTradersClient)
    (dbPath: string)
    (agent: Agent)
    (token: string)
    (systemSymbol: string)
    : Async<GalaxyCatalogSnapshot> =
    async {
        do! Persistence.ApiCacheRepository.deleteKey dbPath (waypointsKey agent.symbol systemSymbol)

        let! waypoints =
            RequestQueue.enqueue dbPath 1 $"GET /systems/{systemSymbol}/waypoints" None (fun () ->
                client.ListWaypoints(token, systemSymbol))

        do!
            Persistence.ApiCacheRepository.put dbPath (waypointsKey agent.symbol systemSymbol) (JsonSerializer.Serialize(waypoints, jsonOptions))

        let! systems = readSystems dbPath agent.symbol
        let! sync = toSyncStatusDto dbPath agent.symbol

        let! markets =
            async {
                if systemSymbol = Waypoint.systemSymbolOf agent.headquarters then
                    try
                        let! market =
                            RequestQueue.enqueue dbPath 1 "GET /systems/{system}/waypoints/{waypoint}/market" None (fun () ->
                                client.GetMarket(token, systemSymbol, agent.headquarters))

                        return [ market ]
                    with _ ->
                        return []
                else
                    return []
            }

        return
            {
                systems = systems
                selectedSystemSymbol = systemSymbol
                waypoints = waypoints
                markets = markets
                sync = sync
            }
    }