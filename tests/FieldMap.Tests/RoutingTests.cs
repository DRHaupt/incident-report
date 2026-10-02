using FieldMap.Core.Build;
using FieldMap.Core.Geo;
using FieldMap.Core.Osm;
using FieldMap.Core.Routing;
using FieldMap.Core.Sample;

namespace FieldMap.Tests;

[Collection("sample")]
public class RoutingTests
{
    private readonly SampleFixture _f;

    public RoutingTests(SampleFixture f) => _f = f;

    private Router NewRouter() => new(_f.Graph);

    private bool UsesBridge(Route r, string bridgeName) =>
        r.Legs.Any(l => (_f.Graph.Flags(l.Segment) & SegmentFlags.Bridge) != 0 && _f.Graph.Name(l.Segment) == "Mill Road" && bridgeName == "Old Mill Bridge" ||
                        (_f.Graph.Flags(l.Segment) & SegmentFlags.Bridge) != 0 && _f.Graph.Ref(l.Segment) == "901" && bridgeName == "Sample River Bridge");

    [Fact]
    public void Car_takes_the_low_bridge_but_a_truck_goes_round()
    {
        var router = NewRouter();
        var from = SampleRegion.Intersection(7, 6);
        var car = router.Route(from, SampleRegion.MillRoadEast, VehicleProfile.Car)!;
        Assert.True(UsesBridge(car, "Old Mill Bridge"));

        var truck = router.Route(from, SampleRegion.MillRoadEast, VehicleProfile.Truck(heightMeters: 4.15))!;
        Assert.NotNull(truck);
        Assert.False(UsesBridge(truck, "Old Mill Bridge"));
        Assert.True(UsesBridge(truck, "Sample River Bridge")); // 4.6 m clearance is enough
        Assert.True(truck.DistanceMeters > car.DistanceMeters);
        Assert.Contains(truck.Warnings, w => w.Contains("only checked where", StringComparison.Ordinal));

        // A low van fits under the old bridge.
        var van = router.Route(from, SampleRegion.MillRoadEast, VehicleProfile.Truck(heightMeters: 3.0, weightTonnes: 5))!;
        Assert.True(UsesBridge(van, "Old Mill Bridge"));

        // Too heavy for the 40 t highway bridge as well: no route.
        Assert.Null(router.Route(from, SampleRegion.MillRoadEast, VehicleProfile.Truck(heightMeters: 4.15, weightTonnes: 60)));
    }

    [Fact]
    public void One_way_street_is_respected_by_vehicles_not_walkers()
    {
        var router = NewRouter();
        var from = SampleRegion.Intersection(2, 5);
        var to = SampleRegion.Intersection(2, 3);
        var car = router.Route(from, to, VehicleProfile.Car)!;
        Assert.True(car.DistanceMeters > 2 * SampleRegion.Block + 50); // has to go round the block
        var walk = router.Route(from, to, VehicleProfile.Walking)!;
        Assert.InRange(walk.DistanceMeters, 2 * SampleRegion.Block - 5, 2 * SampleRegion.Block + 5);

        var east = router.Route(to, from, VehicleProfile.Car)!; // with the one-way direction
        Assert.InRange(east.DistanceMeters, 2 * SampleRegion.Block - 5, 2 * SampleRegion.Block + 5);
    }

    [Fact]
    public void Locked_gate_blocks_cars_but_not_emergency_vehicles()
    {
        var router = NewRouter();
        var from = SampleRegion.Intersection(0, 4);
        Assert.Null(router.Route(from, SampleRegion.FsrEnd, VehicleProfile.Car));
        var fire = router.Route(from, SampleRegion.FsrEnd, VehicleProfile.Emergency)!;
        Assert.NotNull(fire);
        Assert.Contains(fire.Warnings, w => w.Contains("forest service road", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fire.Warnings, w => w.Contains("seasonal", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fire.Warnings, w => w.Contains("gate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fire.Warnings, w => w.Contains("gravel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Emergency_vehicles_are_faster_on_through_roads()
    {
        var router = NewRouter();
        var a = SampleRegion.At(-5000, -400);
        var b = SampleRegion.At(5000, -400);
        var car = router.Route(a, b, VehicleProfile.Car)!;
        var fire = router.Route(a, b, VehicleProfile.Emergency)!;
        Assert.True(fire.DurationSeconds < car.DurationSeconds * 0.9);
        Assert.InRange(car.DistanceMeters, 9900, 10100);
        Assert.InRange(car.DurationSeconds, 380, 420); // 10 km at 90 km/h = 400 s
    }

    [Fact]
    public void Drawn_closure_changes_the_route()
    {
        var router = NewRouter();
        var from = SampleRegion.Intersection(1, 4);
        var to = SampleRegion.At(SampleRegion.StreetX(4), -400); // highway at the Main Street exit
        var before = router.Route(from, to, VehicleProfile.Car)!;
        bool SouthOfTown(RouteLeg l) => _f.Graph.Shape(l.Segment).Any(p => p.Lat < SampleRegion.At(0, -50).Lat);
        Assert.Contains(before.Legs, l => _f.Graph.Name(l.Segment) == "Main Street" && SouthOfTown(l));

        // Close Main Street south of town (a line drawn across it).
        var closure = new List<GeoPoint> { SampleRegion.At(SampleRegion.StreetX(4) - 30, -100), SampleRegion.At(SampleRegion.StreetX(4) + 30, -100) };
        foreach (var s in router.SegmentsTouching(closure, isArea: false))
            router.Closed.Add(s);
        Assert.NotEmpty(router.Closed);
        var after = router.Route(from, to, VehicleProfile.Car)!;
        Assert.NotNull(after);
        Assert.True(after.DistanceMeters > before.DistanceMeters + 500);
        Assert.DoesNotContain(after.Legs, l => router.Closed.Contains(l.Segment));
    }

    [Fact]
    public void Nearest_by_drive_time_matches_individual_routes()
    {
        var router = NewRouter();
        var origin = SampleRegion.Intersection(0, 0);
        var hospitals = _f.Search.LoadPois().Where(p => p.Layer == "hospital").ToList();
        var points = hospitals.Select(h => h.Point).ToList();
        var reach = router.NearestByTime(origin, points, VehicleProfile.Car, 5);
        Assert.Equal(2, reach.Count);
        Assert.True(reach[0].DurationSeconds <= reach[1].DurationSeconds);
        foreach (var r in reach)
        {
            var route = router.Route(origin, points[r.Index], VehicleProfile.Car)!;
            Assert.InRange(r.DurationSeconds, route.DurationSeconds - 1, route.DurationSeconds + 1);
            Assert.InRange(r.DistanceMeters, route.DistanceMeters - 2, route.DistanceMeters + 2);
        }
    }

    [Fact]
    public void Instructions_describe_turns_and_arrival()
    {
        var router = NewRouter();
        var route = router.Route(SampleRegion.At(SampleRegion.StreetX(0), 40), SampleRegion.At(SampleRegion.StreetX(4) + 20, AvenueY(4)), VehicleProfile.Car)!;
        Assert.Equal(Maneuver.Depart, route.Steps[0].Maneuver);
        Assert.Equal(Maneuver.Arrive, route.Steps[^1].Maneuver);
        Assert.Contains(route.Steps, s => s.Maneuver is Maneuver.Left or Maneuver.Right);
        Assert.StartsWith("Head north on Alder Street", route.Steps[0].Instruction);
        Assert.InRange(route.Steps.Take(route.Steps.Count - 1).Sum(s => s.DistanceMeters), route.DistanceMeters - 1, route.DistanceMeters + 1);
        var text = Instructions.ToText(route, "Start", "Destination");
        Assert.Contains("ROUTE (Car)", text);
        Assert.Contains("Arrive at the destination", text);
        Assert.All(text.Split('\n'), line => Assert.True(line.Length < 200));
    }

    private static double AvenueY(int i) => SampleRegion.AvenueY(i);

    [Fact]
    public void Alternatives_differ_from_the_main_route()
    {
        var router = NewRouter();
        var routes = router.RouteWithAlternatives(SampleRegion.Intersection(0, 0), SampleRegion.Intersection(7, 7), VehicleProfile.Car, 2);
        Assert.True(routes.Count >= 2);
        Assert.True(routes[1].DurationSeconds <= routes[0].DurationSeconds * 1.5);
        var first = routes[0].Legs.Select(l => l.Segment).ToHashSet();
        Assert.True(routes[1].Legs.Count(l => !first.Contains(l.Segment)) > 0);
    }

    [Fact]
    public void Graph_round_trips_through_its_file()
    {
        var path = Path.Combine(_f.Root, "copy.graph");
        _f.Graph.Save(path);
        var g = RoadGraph.Load(path);
        Assert.Equal(_f.Graph.NodeCount, g.NodeCount);
        Assert.Equal(_f.Graph.SegmentCount, g.SegmentCount);
        for (var s = 0; s < g.SegmentCount; s++)
        {
            Assert.Equal(_f.Graph.Name(s), g.Name(s));
            Assert.Equal(_f.Graph.Flags(s), g.Flags(s));
            Assert.Equal(_f.Graph.Shape(s), g.Shape(s));
        }
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..100]);
        Assert.ThrowsAny<Exception>(() => RoadGraph.Load(path));
    }

    [Fact]
    public void Small_hand_built_graph_finds_the_fastest_path()
    {
        // A square with a slow diagonal: A(0,0) B(0,1000) C(1000,1000) D(1000,0); A–C diagonal is a track.
        var o = new GeoPoint(50, -120);
        GeoPoint P(double e, double n) => Geometry.FromLocal(o, new Vec(e, n));
        var data = new MapData();
        MapWay Way(long id, long[] ids, GeoPoint[] pts, params (string, string)[] tags) =>
            new() { Id = id, NodeIds = ids, Points = pts, Tags = new OsmTags(tags.Select(t => new KeyValuePair<string, string>(t.Item1, t.Item2)).ToArray()) };
        var a = P(0, 0); var b = P(0, 1000); var c = P(1000, 1000); var d = P(1000, 0);
        data.Roads.Add(Way(1, new long[] { 1, 2 }, new[] { a, b }, ("highway", "primary"), ("name", "West Road")));
        data.Roads.Add(Way(2, new long[] { 2, 3 }, new[] { b, c }, ("highway", "primary"), ("name", "North Road")));
        data.Roads.Add(Way(3, new long[] { 1, 4 }, new[] { a, d }, ("highway", "residential"), ("name", "South Road")));
        data.Roads.Add(Way(4, new long[] { 4, 3 }, new[] { d, c }, ("highway", "residential"), ("name", "East Road")));
        data.Roads.Add(Way(5, new long[] { 1, 3 }, new[] { a, c }, ("highway", "track"), ("name", "Diagonal Track")));
        var g = GraphBuilder.Build(data);
        Assert.Equal(4, g.NodeCount);
        Assert.Equal(5, g.SegmentCount);
        var router = new Router(g);
        var car = router.Route(a, c, VehicleProfile.Car)!;
        // Primary (80 km/h) via B: 2000 m / 22.2 m/s = 90 s; track (25 km/h) 1414 m = 204 s; residential (40) = 180 s.
        Assert.Equal(new[] { "West Road", "North Road" }, car.Legs.Select(l => g.Name(l.Segment)).Where(n => n.Length > 0).Distinct());
        Assert.InRange(car.DurationSeconds, 88, 92);
        var walk = router.Route(a, c, VehicleProfile.Walking)!;
        Assert.Contains(walk.Legs, l => g.Name(l.Segment) == "Diagonal Track"); // walking: shortest
    }
}
