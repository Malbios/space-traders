module Tests

open System
open Xunit
open SpaceKids.SpaceTraders
open SpaceKids.Client.Main

/// Found in review: `Main.fs` had zero automated test coverage of its non-trivial
/// pure logic (map bounds/scaling math, in-transit interpolation, the pilot-name
/// hash) despite none of it needing Blazor rendering — these are plain F# functions,
/// made `internal` (from `private`) specifically so this project can reach them via
/// `InternalsVisibleTo` (see `Startup.fs`), without standing up a full Blazor
/// component-testing framework.

let private waypoint (x: int) (y: int) : Waypoint =
    { symbol = "X1-TEST-A1"; ``type`` = "PLANET"; systemSymbol = "X1-TEST"; x = x; y = y; traits = [] }

[<Fact>]
let ``computeMapBounds returns a degenerate default range for an empty waypoint list`` () =
    Assert.Equal((0.0, 1.0, 0.0, 1.0), computeMapBounds [])

[<Fact>]
let ``computeMapBounds widens a single-point range so scaling never divides by zero`` () =
    let minX, maxX, minY, maxY = computeMapBounds [ waypoint 5 5 ]
    Assert.True(maxX > minX)
    Assert.True(maxY > minY)
    Assert.Equal(5.0, (minX + maxX) / 2.0, 3)
    Assert.Equal(5.0, (minY + maxY) / 2.0, 3)

[<Fact>]
let ``computeMapBounds spans the real min/max of a multi-waypoint list`` () =
    let minX, maxX, minY, maxY = computeMapBounds [ waypoint 0 0; waypoint 10 -5; waypoint -3 8 ]
    Assert.Equal(-3.0, minX)
    Assert.Equal(10.0, maxX)
    Assert.Equal(-5.0, minY)
    Assert.Equal(8.0, maxY)

[<Fact>]
let ``scaleMapPoint maps the bounds' center to the view's center`` () =
    let bounds = (0.0, 100.0, 0.0, 100.0)
    let sx, sy = scaleMapPoint bounds 50.0 50.0
    Assert.Equal(mapViewSize / 2.0, sx, 3)
    Assert.Equal(mapViewSize / 2.0, sy, 3)

[<Fact>]
let ``scaleMapPoint flips Y so a larger world-Y renders higher on screen`` () =
    let bounds = (0.0, 100.0, 0.0, 100.0)
    let _, syLow = scaleMapPoint bounds 0.0 0.0
    let _, syHigh = scaleMapPoint bounds 0.0 100.0
    // SVG is Y-down: a larger world-Y (further "north") must be a *smaller* screen-Y.
    Assert.True(syHigh < syLow)

[<Fact>]
let ``scaleMapPoint keeps every point within the padded view box`` () =
    let bounds = (0.0, 100.0, 0.0, 100.0)
    let sx, sy = scaleMapPoint bounds 0.0 0.0
    Assert.True(sx >= mapPadding - 0.001)
    Assert.True(sy <= mapViewSize - mapPadding + 0.001)

[<Fact>]
let ``mapMarkerSize shrinks viewBox units as zoom increases so dots stay screen-constant`` () =
    Assert.Equal(mapGalaxyDotRadius, mapMarkerSize 1.0 mapGalaxyDotRadius)
    Assert.Equal(mapGalaxyDotRadius / 4.0, mapMarkerSize 4.0 mapGalaxyDotRadius)

let private dockedShip (waypointSymbol: string) : Ship =
    { symbol = "SHIP-1"
      registration = { role = "COMMAND" }
      nav =
        { systemSymbol = "X1-TEST"
          waypointSymbol = waypointSymbol
          route =
            { destination = { symbol = waypointSymbol; ``type`` = "PLANET"; systemSymbol = "X1-TEST"; x = 0; y = 0 }
              origin = { symbol = waypointSymbol; ``type`` = "PLANET"; systemSymbol = "X1-TEST"; x = 0; y = 0 }
              departureTime = DateTimeOffset.UtcNow.ToString("o")
              arrival = DateTimeOffset.UtcNow.ToString("o") }
          status = "DOCKED"
          flightMode = "CRUISE" }
      fuel = { current = 400; capacity = 400 }
      cargo = { capacity = 40; units = 0; inventory = [] }
      cooldown = { shipSymbol = "SHIP-1"; totalSeconds = 0; remainingSeconds = 0; expiration = None } }

[<Fact>]
let ``interpolatedShipPosition resolves a docked ship to its waypoint's coordinates`` () =
    let waypoints = [ waypoint 7 3 ]
    let ship = dockedShip "X1-TEST-A1"
    Assert.Equal(Some(7.0, 3.0), interpolatedShipPosition waypoints ship)

[<Fact>]
let ``interpolatedShipPosition returns None for a docked ship at a waypoint outside the loaded system`` () =
    let ship = dockedShip "X1-TEST-Z9"
    Assert.Equal(None, interpolatedShipPosition [ waypoint 0 0 ] ship)

[<Fact>]
let ``interpolatedShipPosition clamps a zero-duration transit to the destination`` () =
    let now = DateTimeOffset.UtcNow.ToString("o")

    let ship =
        { dockedShip "X1-TEST-A1" with
            nav =
                { (dockedShip "X1-TEST-A1").nav with
                    status = "IN_TRANSIT"
                    route =
                        { origin = { symbol = "X1-TEST-A1"; ``type`` = "PLANET"; systemSymbol = "X1-TEST"; x = 0; y = 0 }
                          destination = { symbol = "X1-TEST-B2"; ``type`` = "PLANET"; systemSymbol = "X1-TEST"; x = 10; y = 10 }
                          departureTime = now
                          arrival = now } } }

    // Zero-duration transit (departure == arrival) must clamp to the destination
    // (fraction = 1.0), not divide by zero.
    Assert.Equal(Some(10.0, 10.0), interpolatedShipPosition [] ship)

[<Fact>]
let ``interpolatedShipPosition returns None instead of throwing for an unparsable transit timestamp`` () =
    let ship =
        { dockedShip "X1-TEST-A1" with
            nav =
                { (dockedShip "X1-TEST-A1").nav with
                    status = "IN_TRANSIT"
                    route =
                        { origin = { symbol = "X1-TEST-A1"; ``type`` = "PLANET"; systemSymbol = "X1-TEST"; x = 0; y = 0 }
                          destination = { symbol = "X1-TEST-B2"; ``type`` = "PLANET"; systemSymbol = "X1-TEST"; x = 10; y = 10 }
                          departureTime = "not-a-timestamp"
                          arrival = "also-not-a-timestamp" } } }

    Assert.Equal(None, interpolatedShipPosition [] ship)

[<Fact>]
let ``pilotName is stable for the same key`` () =
    Assert.Equal(pilotName "SHIP-1", pilotName "SHIP-1")

[<Fact>]
let ``pilotName always returns a name from the real pool`` () =
    for key in [ ""; "a"; "SHIP-1"; "job-12345"; String.replicate 50 "x" ] do
        Assert.Contains(pilotName key, pilotNamePool)

let private contract (id: string) (fulfilled: bool) : Contract =
    { id = id
      factionSymbol = "COSMIC"
      ``type`` = "PROCUREMENT"
      terms =
        { deadline = "2026-12-31T00:00:00.000Z"
          payment = { onAccepted = 1000; onFulfilled = 5000 }
          deliver = [ { tradeSymbol = "IRON"; destinationSymbol = "X1-TEST-A1"; unitsRequired = 10; unitsFulfilled = 0 } ] }
      accepted = true
      fulfilled = fulfilled
      expiration = None
      deadlineToAccept = None }

[<Fact>]
let ``partitionContracts returns two empty lists for an empty input`` () =
    Assert.Equal(([], []), partitionContracts [])

[<Fact>]
let ``partitionContracts puts every fulfilled contract into history`` () =
    let contracts = [ contract "c1" true; contract "c2" true ]
    Assert.Equal(([], contracts), partitionContracts contracts)

[<Fact>]
let ``partitionContracts puts every unfulfilled contract into active`` () =
    let contracts = [ contract "c1" false; contract "c2" false ]
    Assert.Equal((contracts, []), partitionContracts contracts)

[<Fact>]
let ``partitionContracts splits a mix and preserves order within each partition`` () =
    let c1, c2, c3, c4 = contract "c1" false, contract "c2" true, contract "c3" false, contract "c4" true
    let active, history = partitionContracts [ c1; c2; c3; c4 ]
    Assert.Equal<Contract list>([ c1; c3 ], active)
    Assert.Equal<Contract list>([ c2; c4 ], history)

let private starSystem (symbol: string) (x: int) (y: int) : StarSystem =
    { symbol = symbol
      sectorSymbol = "SECTOR"
      constellation = None
      name = None
      ``type`` = "BLUE_STAR"
      x = x
      y = y }

[<Fact>]
let ``galaxyMapNodeBudget removes sampling cap at deep zoom`` () =
    Assert.Equal(galaxyMapMaxNodes, galaxyMapNodeBudget 1.0)
    Assert.True(galaxyMapNodeBudget 16.0 > 10000)

[<Fact>]
let ``filterGalaxyMapNodes shows every visible node when budget exceeds viewport count`` () =
    let systems =
        [ for i in 0..49 -> starSystem $"S{i}" (i % 10) (i / 10) ]

    let bounds = computeGalaxyBounds systems
    let nodes = buildGalaxyMapNodes systems bounds
    let rendered, visible = filterGalaxyMapNodes nodes 0.0 0.0 mapViewSize (galaxyMapNodeBudget 16.0) []

    Assert.Equal(visible, rendered.Length)

[<Fact>]
let ``filterGalaxyMapNodes caps rendered nodes but always keeps pinned systems`` () =
    let systems =
        [ for i in 0..99 -> starSystem $"S{i}" (i % 10) (i / 10) ]

    let bounds = computeGalaxyBounds systems
    let nodes = buildGalaxyMapNodes systems bounds
    let rendered, visible = filterGalaxyMapNodes nodes 0.0 0.0 mapViewSize 20 [ "S42" ]

    Assert.Equal(100, visible)
    Assert.True(rendered.Length <= 20)
    Assert.Contains(rendered, fun n -> n.system.symbol = "S42")

[<Fact>]
let ``pickGalaxyMapNodeAt chooses the nearest system within the hit radius`` () =
    let systems = [ starSystem "NEAR" 0 0; starSystem "FAR" 50 50 ]
    let bounds = computeGalaxyBounds systems
    let nodes = buildGalaxyMapNodes systems bounds
    let near = nodes |> List.find (fun n -> n.system.symbol = "NEAR")
    let picked = pickGalaxyMapNodeAt nodes near.sx near.sy 8.0
    Assert.Equal(Some "NEAR", picked)

let private shipInSystem (symbol: string) (systemSymbol: string) : Ship =
    { dockedShip "X1-TEST-A1" with
        symbol = symbol
        nav = { (dockedShip "X1-TEST-A1").nav with systemSymbol = systemSymbol } }

[<Fact>]
let ``systemsWithShips is empty for an empty fleet`` () =
    Assert.Empty(systemsWithShips [])

[<Fact>]
let ``systemsWithShips returns the one system a single ship is in`` () =
    let ships = [ shipInSystem "SHIP-1" "X1-ALPHA" ]
    Assert.Equal<Set<string>>(Set.ofList [ "X1-ALPHA" ], systemsWithShips ships)

[<Fact>]
let ``systemsWithShips dedupes multiple ships in the same system`` () =
    let ships = [ shipInSystem "SHIP-1" "X1-ALPHA"; shipInSystem "SHIP-2" "X1-ALPHA" ]
    Assert.Equal<Set<string>>(Set.ofList [ "X1-ALPHA" ], systemsWithShips ships)

[<Fact>]
let ``systemsWithShips returns every distinct system across a spread-out fleet`` () =
    let ships =
        [ shipInSystem "SHIP-1" "X1-ALPHA"
          shipInSystem "SHIP-2" "X1-BETA"
          shipInSystem "SHIP-3" "X1-ALPHA" ]

    Assert.Equal<Set<string>>(Set.ofList [ "X1-ALPHA"; "X1-BETA" ], systemsWithShips ships)
