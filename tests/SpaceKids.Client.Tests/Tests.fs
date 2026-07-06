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
