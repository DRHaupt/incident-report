using System.Globalization;
using FieldMap.Core.Build;
using FieldMap.Core.Geo;
using FieldMap.Core.Osm;
using FieldMap.Core.Routing;

namespace FieldMap.Tests;

/// <summary>Turn restrictions on small made-up road networks (metres east/north of an origin in BC).</summary>
public class TurnRestrictionTests
{
    private static readonly GeoPoint Origin = new(50.0, -120.0);

    private static GeoPoint P(double east, double north) => Geometry.FromLocal(Origin, new Vec(east, north));

    private sealed class Net
    {
        private readonly Dictionary<long, GeoPoint> _nodes = new();
        public MapData Data { get; } = new();

        public long Node(long id, double e, double n)
        {
            _nodes[id] = P(e, n);
            return id;
        }

        public long Way(long id, string name, params long[] nodes)
        {
            Data.Roads.Add(new MapWay
            {
                Id = id,
                NodeIds = nodes,
                Points = nodes.Select(n => _nodes[n]).ToArray(),
                Tags = new OsmTags(new Dictionary<string, string> { ["highway"] = "residential", ["name"] = name, ["maxspeed"] = "50" })
            });
            return id;
        }

        public void Restrict(string kind, long from, long to, long? viaNode = null, long[]? viaWays = null, RestrictionVehicles vehicles = RestrictionVehicles.All) =>
            Data.Restrictions.Add(new OsmRestriction { Id = Data.Restrictions.Count + 1, Kind = kind, From = from, To = to, ViaNode = viaNode, ViaWays = viaWays ?? Array.Empty<long>(), Vehicles = vehicles });
    }

    /// <summary>
    /// A crossroads at C with arms S (way 1, northbound into C), N (2), W (3), E (4), and a slow way round from S to W
    /// through SW (way 5), so a banned left turn has a legal alternative.
    /// </summary>
    private static Net Crossroads()
    {
        var n = new Net();
        n.Node(1, 0, -500); // S
        n.Node(2, 0, 0);    // C
        n.Node(3, 0, 500);  // N
        n.Node(4, -500, 0); // W
        n.Node(5, 500, 0);  // E
        n.Node(6, -500, -500); // SW
        n.Way(1, "South Road", 1, 2);
        n.Way(2, "North Road", 2, 3);
        n.Way(3, "West Road", 2, 4);
        n.Way(4, "East Road", 2, 5);
        n.Way(5, "Loop Road", 1, 6, 4);
        return n;
    }

    /// <summary>Whether the route goes straight from a segment of one way onto a segment of the other.</summary>
    private static bool MakesMove(RoadGraph g, Route route, long fromWay, long toWay)
    {
        var legs = route.Legs.Where(l => l.Length > 0.5).ToList();
        for (var i = 1; i < legs.Count; i++)
            if (g.WayId(legs[i - 1].Segment) == fromWay && g.WayId(legs[i].Segment) == toWay)
                return true;
        return false;
    }

    private static Route Go(RoadGraph g, VehicleProfile profile, GeoPoint from, GeoPoint to) =>
        new Router(g).Route(from, to, profile) ?? throw new Xunit.Sdk.XunitException("no route");

    [Fact]
    public void BannedLeftTurnSendsTheRouteRoundTheBlock()
    {
        var plain = GraphBuilder.Build(Crossroads().Data);
        var straight = Go(plain, VehicleProfile.Car, P(0, -100), P(-100, 0));
        Assert.InRange(straight.DistanceMeters, 190, 210);

        var net = Crossroads();
        net.Restrict("no_left_turn", from: 1, to: 3, viaNode: 2);
        var g = GraphBuilder.Build(net.Data);
        Assert.Equal(1, g.TurnRestrictionCount);
        Assert.Equal(0, g.UnresolvedRestrictions);
        var detour = Go(g, VehicleProfile.Car, P(0, -100), P(-100, 0));
        // Legal alternatives: round by Loop Road, or on to a dead end, turn round there and come back.
        Assert.True(detour.DistanceMeters > 1000, $"expected a way round, got {detour.DistanceMeters:0} m");
        Assert.False(MakesMove(g, detour, fromWay: 1, toWay: 3));
        // Other movements at the junction are unaffected: straight on, right turn, and the reverse direction.
        Assert.InRange(Go(g, VehicleProfile.Car, P(0, -100), P(0, 100)).DistanceMeters, 190, 210);
        Assert.InRange(Go(g, VehicleProfile.Car, P(0, -100), P(100, 0)).DistanceMeters, 190, 210);
        Assert.InRange(Go(g, VehicleProfile.Car, P(-100, 0), P(0, -100)).DistanceMeters, 190, 210);
        // Walking ignores turn restrictions.
        Assert.InRange(Go(g, VehicleProfile.Walking, P(0, -100), P(-100, 0)).DistanceMeters, 190, 210);
    }

    [Fact]
    public void OnlyStraightOnBansTheTurnsAndTheUTurn()
    {
        var net = Crossroads();
        net.Restrict("only_straight_on", from: 1, to: 2, viaNode: 2);
        var g = GraphBuilder.Build(net.Data);
        Assert.Equal(3, g.TurnRestrictionCount); // left, right and back
        Assert.InRange(Go(g, VehicleProfile.Car, P(0, -100), P(0, 100)).DistanceMeters, 190, 210);
        var west = Go(g, VehicleProfile.Car, P(0, -100), P(-100, 0));
        Assert.True(west.DistanceMeters > 1000);
        Assert.False(MakesMove(g, west, 1, 3));
        // East is only reachable by going straight on, then back from the north arm's end (a dead end, where turning is fine).
        Assert.True(Go(g, VehicleProfile.Car, P(0, -100), P(100, 0)).DistanceMeters > 900);
    }

    [Fact]
    public void RestrictionsApplyToTheVehiclesTheyName()
    {
        var net = Crossroads();
        net.Restrict("no_left_turn", from: 1, to: 3, viaNode: 2, vehicles: RestrictionVehicles.Car | RestrictionVehicles.Truck);
        var g = GraphBuilder.Build(net.Data);
        Assert.True(Go(g, VehicleProfile.Car, P(0, -100), P(-100, 0)).DistanceMeters > 1000);
        Assert.True(Go(g, VehicleProfile.Truck(), P(0, -100), P(-100, 0)).DistanceMeters > 1000);
        // except=emergency
        Assert.InRange(Go(g, VehicleProfile.Emergency, P(0, -100), P(-100, 0)).DistanceMeters, 190, 210);

        var trucks = Crossroads();
        trucks.Restrict("no_left_turn", from: 1, to: 3, viaNode: 2, vehicles: RestrictionVehicles.Truck);
        var gt = GraphBuilder.Build(trucks.Data);
        Assert.InRange(Go(gt, VehicleProfile.Car, P(0, -100), P(-100, 0)).DistanceMeters, 190, 210);
        Assert.True(Go(gt, VehicleProfile.Truck(), P(0, -100), P(-100, 0)).DistanceMeters > 1000);
    }

    [Fact]
    public void NearestByDriveTimeRespectsRestrictions()
    {
        var net = Crossroads();
        net.Restrict("no_left_turn", from: 1, to: 3, viaNode: 2);
        var g = GraphBuilder.Build(net.Data);
        var reach = new Router(g).NearestByTime(P(0, -100), new[] { P(-100, 0), P(100, 0) }, VehicleProfile.Car, 2);
        Assert.Equal(1, reach[0].Index); // the right turn is nearer by time now
        Assert.True(reach.Single(r => r.Index == 0).DistanceMeters > 1000);
    }

    /// <summary>
    /// A road from the south (A) reaches X, where a short connector (V) goes west to Y; from Y, B goes north to Z.
    /// D continues north from X to T, and E joins T to Z, so there's a legal way round.
    /// </summary>
    private static Net Connector()
    {
        var n = new Net();
        n.Node(1, 0, -500);   // S0
        n.Node(2, 0, 0);      // X
        n.Node(3, -200, 0);   // Y
        n.Node(4, -200, 500); // Z
        n.Node(5, 0, 500);    // T
        n.Node(6, -200, -500); // Q
        n.Way(10, "A Street", 1, 2);
        n.Way(11, "Connector", 2, 3);
        n.Way(12, "B Street", 3, 4);
        n.Way(13, "D Street", 2, 5);
        n.Way(14, "E Street", 5, 4);
        n.Way(15, "C Street", 3, 6);
        return n;
    }

    [Fact]
    public void ViaWayRestrictionBansTheWholeMovementOnly()
    {
        var plain = GraphBuilder.Build(Connector().Data);
        Assert.InRange(Go(plain, VehicleProfile.Car, P(0, -100), P(-200, 300)).DistanceMeters, 590, 610);

        var net = Connector();
        net.Restrict("no_left_turn", from: 10, to: 12, viaWays: new long[] { 11 });
        var g = GraphBuilder.Build(net.Data);
        Assert.Equal(1, g.TurnRestrictionCount);
        var route = Go(g, VehicleProfile.Car, P(0, -100), P(-200, 300));
        Assert.InRange(route.DistanceMeters, 990, 1010); // round by D and E
        // From A through the connector to C Street (south) is still fine…
        Assert.InRange(Go(g, VehicleProfile.Car, P(0, -100), P(-200, -300)).DistanceMeters, 590, 610);
        // …and so is the connector to B Street when you didn't come from A.
        Assert.InRange(Go(g, VehicleProfile.Car, P(-100, 0), P(-200, 300)).DistanceMeters, 390, 410);
    }

    [Fact]
    public void MalformedRestrictionsAreCountedNotApplied()
    {
        var net = Crossroads();
        net.Restrict("no_left_turn", from: 1, to: 3, viaNode: 999);          // via node not on the network
        net.Restrict("no_left_turn", from: 1, to: 3, viaWays: new long[] { 2, 4 }); // via of two ways
        var g = GraphBuilder.Build(net.Data);
        Assert.Equal(0, g.TurnRestrictionCount);
        Assert.Equal(2, g.UnresolvedRestrictions);
    }

    [Fact]
    public void RestrictionsSurviveSaveAndLoad()
    {
        var net = Connector();
        net.Restrict("no_left_turn", from: 10, to: 12, viaWays: new long[] { 11 });
        net.Restrict("no_right_turn", from: 13, to: 11, viaNode: 2);
        var g = GraphBuilder.Build(net.Data);
        var path = Path.Combine(Path.GetTempPath(), "fieldmap-turns-" + Guid.NewGuid().ToString("N")[..8] + ".graph");
        g.Save(path);
        var loaded = RoadGraph.Load(path);
        File.Delete(path);
        Assert.Equal(g.TurnRestrictionCount, loaded.TurnRestrictionCount);
        Assert.InRange(Go(loaded, VehicleProfile.Car, P(0, -100), P(-200, 300)).DistanceMeters, 990, 1010);
    }

    [Fact]
    public void ReadsRestrictionRelationsFromOsm()
    {
        string F(double v) => v.ToString(CultureInfo.InvariantCulture);
        var pts = new[] { (1, P(0, -500)), (2, P(0, 0)), (3, P(0, 500)), (4, P(-500, 0)), (5, P(500, 0)), (6, P(-500, -500)) };
        var xml = "<osm version=\"0.6\">" + string.Concat(pts.Select(p => $"<node id=\"{p.Item1}\" lat=\"{F(p.Item2.Lat)}\" lon=\"{F(p.Item2.Lon)}\"/>")) + """
            <way id="1"><nd ref="1"/><nd ref="2"/><tag k="highway" v="residential"/></way>
            <way id="2"><nd ref="2"/><nd ref="3"/><tag k="highway" v="residential"/></way>
            <way id="3"><nd ref="2"/><nd ref="4"/><tag k="highway" v="residential"/></way>
            <way id="4"><nd ref="2"/><nd ref="5"/><tag k="highway" v="residential"/></way>
            <way id="5"><nd ref="1"/><nd ref="6"/><nd ref="4"/><tag k="highway" v="residential"/></way>
            <relation id="100"><member type="way" ref="1" role="from"/><member type="node" ref="2" role="via"/><member type="way" ref="3" role="to"/>
              <tag k="type" v="restriction"/><tag k="restriction" v="no_left_turn"/><tag k="except" v="emergency"/></relation>
            <relation id="101"><member type="way" ref="1" role="from"/><member type="node" ref="2" role="via"/><member type="way" ref="4" role="to"/>
              <tag k="type" v="restriction"/><tag k="restriction:hgv" v="no_right_turn"/></relation>
            <relation id="102"><member type="way" ref="2" role="from"/><member type="node" ref="2" role="via"/><member type="way" ref="3" role="to"/>
              <tag k="type" v="restriction"/><tag k="restriction:conditional" v="no_right_turn @ (Mo-Fr 07:00-09:00)"/></relation>
            </osm>
            """;
        var path = Path.Combine(Path.GetTempPath(), "fieldmap-turns-" + Guid.NewGuid().ToString("N")[..8] + ".osm");
        File.WriteAllText(path, xml);
        var data = OsmExtractor.Extract(path);
        File.Delete(path);
        Assert.Equal(2, data.Restrictions.Count); // the conditional one isn't used
        var left = data.Restrictions.Single(r => r.Id == 100);
        Assert.Equal(RestrictionVehicles.Car | RestrictionVehicles.Truck, left.Vehicles);
        Assert.Equal(RestrictionVehicles.Truck, data.Restrictions.Single(r => r.Id == 101).Vehicles);
        var g = GraphBuilder.Build(data);
        Assert.True(Go(g, VehicleProfile.Car, P(0, -100), P(-100, 0)).DistanceMeters > 1000);
        Assert.InRange(Go(g, VehicleProfile.Emergency, P(0, -100), P(-100, 0)).DistanceMeters, 190, 210);
        Assert.InRange(Go(g, VehicleProfile.Car, P(0, -100), P(100, 0)).DistanceMeters, 190, 210);
        Assert.True(Go(g, VehicleProfile.Truck(), P(0, -100), P(100, 0)).DistanceMeters > 900);
    }
}
