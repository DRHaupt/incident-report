using System.Globalization;
using System.Text.RegularExpressions;
using FieldMap.Core.Build;
using FieldMap.Core.Geo;
using FieldMap.Core.Osm;

namespace FieldMap.Core.Routing;

/// <summary>Parses OSM measurement tags (maxspeed, maxheight, maxweight, maxlength) into metric values.</summary>
public static class TagParse
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"50", "50 km/h", "30 mph" → km/h; null for "none", "signals", "walk", missing or unreadable.</summary>
    public static double? SpeedKmh(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return null;
        var m = Regex.Match(v, @"^\s*(\d+(?:\.\d+)?)\s*(km/h|kmh|kph|mph)?\s*$", RegexOptions.IgnoreCase);
        if (!m.Success)
            return null;
        var n = double.Parse(m.Groups[1].Value, Inv);
        return m.Groups[2].Value.Equals("mph", StringComparison.OrdinalIgnoreCase) ? n * 1.609344 : n;
    }

    /// <summary>"4.2", "4.2 m", "13'6\"", "13 ft" → metres; null for "default", "none", "below_default".</summary>
    public static double? Meters(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return null;
        var feet = Regex.Match(v, @"^\s*(\d+(?:\.\d+)?)\s*(?:'|ft|feet)\s*(?:(\d+(?:\.\d+)?)\s*(?:""|in|inch(?:es)?)?)?\s*$", RegexOptions.IgnoreCase);
        if (feet.Success)
            return double.Parse(feet.Groups[1].Value, Inv) * 0.3048 + (feet.Groups[2].Success ? double.Parse(feet.Groups[2].Value, Inv) * 0.0254 : 0);
        var m = Regex.Match(v, @"^\s*(\d+(?:\.\d+)?)\s*(m|metres|meters)?\s*$", RegexOptions.IgnoreCase);
        return m.Success ? double.Parse(m.Groups[1].Value, Inv) : null;
    }

    /// <summary>"40", "40 t", "10000 kg", "20 st", "8000 lbs" → tonnes.</summary>
    public static double? Tonnes(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return null;
        var m = Regex.Match(v, @"^\s*(\d+(?:\.\d+)?)\s*(t|tonnes?|kg|lbs?|st|tons?)?\s*$", RegexOptions.IgnoreCase);
        if (!m.Success)
            return null;
        var n = double.Parse(m.Groups[1].Value, Inv);
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "kg" => n / 1000,
            "lb" or "lbs" => n * 0.000453592,
            "st" or "ton" or "tons" => n * 0.907185, // short tons, as used on North American signs
            _ => n
        };
    }
}

/// <summary>Builds the <see cref="RoadGraph"/> from the roads and barrier nodes of an OSM extract.</summary>
public static class GraphBuilder
{
    public static int DefaultSpeed(RoadClass c, string highway) => c switch
    {
        RoadClass.Motorway => highway.EndsWith("_link") ? 60 : 100,
        RoadClass.Trunk => highway.EndsWith("_link") ? 50 : 90,
        RoadClass.Primary => highway.EndsWith("_link") ? 50 : 80,
        RoadClass.Secondary => 70,
        RoadClass.Tertiary => 60,
        RoadClass.Minor => 50,
        RoadClass.Residential => 40,
        RoadClass.Service => 20,
        RoadClass.Track => 25,
        RoadClass.Ferry => 10,
        _ => 5
    };

    public static RoadClass ClassOf(OsmTags t) => t.Is("route", "ferry") ? RoadClass.Ferry : (t["highway"] ?? "") switch
    {
        "motorway" or "motorway_link" => RoadClass.Motorway,
        "trunk" or "trunk_link" => RoadClass.Trunk,
        "primary" or "primary_link" => RoadClass.Primary,
        "secondary" or "secondary_link" => RoadClass.Secondary,
        "tertiary" or "tertiary_link" => RoadClass.Tertiary,
        "unclassified" or "road" or "busway" => RoadClass.Minor,
        "residential" or "living_street" => RoadClass.Residential,
        "service" => RoadClass.Service,
        "track" => RoadClass.Track,
        _ => RoadClass.Path
    };

    private static readonly string[] NoValues = { "no", "private", "agricultural", "forestry", "delivery" };

    public static SegmentFlags FlagsOf(OsmTags t)
    {
        var f = SegmentFlags.None;
        var highway = t["highway"] ?? "";
        var cls = ClassOf(t);
        if (cls == RoadClass.Path)
            f |= SegmentFlags.WalkOnly;
        var oneway = t["oneway"];
        if (oneway is "yes" or "true" or "1" || (oneway == null && (highway is "motorway" || t.Is("junction", "roundabout") || t.Is("junction", "circular"))))
            f |= SegmentFlags.OnewayForward;
        else if (oneway == "-1")
            f |= SegmentFlags.OnewayBackward;
        var motor = t["motor_vehicle"] ?? t["motorcar"] ?? t["vehicle"] ?? t["access"];
        if (motor != null && NoValues.Contains(motor))
            f |= t.IsYes("emergency") ? SegmentFlags.EmergencyAccess : SegmentFlags.NoMotor;
        if (t["hgv"] is "no")
            f |= SegmentFlags.NoHgv;
        if (highway is "motorway" or "motorway_link" || t.Is("foot", "no"))
            f |= SegmentFlags.NoWalk;
        if (t.IsYes("toll")) f |= SegmentFlags.Toll;
        if (cls == RoadClass.Ferry) f |= SegmentFlags.Ferry;
        if (t["bridge"] is { } b && b != "no") f |= SegmentFlags.Bridge;
        if (t["tunnel"] is { } tu && tu != "no") f |= SegmentFlags.Tunnel;
        if (t.IsYes("seasonal") || t.IsYes("winter_road") || t.IsYes("ice_road")) f |= SegmentFlags.Seasonal;
        if (t["surface"] is "gravel" or "unpaved" or "dirt" or "ground" or "compacted" or "fine_gravel" or "mud" || cls == RoadClass.Track)
            f |= SegmentFlags.Unpaved;
        if (t.Is("junction", "roundabout") || t.Is("junction", "circular")) f |= SegmentFlags.Roundabout;
        if (highway.EndsWith("_link", StringComparison.Ordinal)) f |= SegmentFlags.Link;
        var name = t["name"] ?? "";
        if (name.Contains("FSR", StringComparison.Ordinal) || name.Contains("Forest Service", StringComparison.OrdinalIgnoreCase))
            f |= SegmentFlags.ForestService;
        return f;
    }

    public static NodeFlags BarrierFlags(OsmTags t)
    {
        var barrier = t["barrier"];
        if (barrier == null)
            return NodeFlags.None;
        var access = t["motor_vehicle"] ?? t["access"];
        var closed = access != null && NoValues.Contains(access) || t.IsYes("locked");
        var emergency = t.IsYes("emergency") ? NodeFlags.EmergencyPasses : NodeFlags.None;
        switch (barrier)
        {
            case "gate" or "lift_gate" or "swing_gate" or "chain" or "barrier_board":
                return closed ? NodeFlags.BlocksMotor | NodeFlags.Gate | emergency : NodeFlags.Gate;
            case "bollard" or "block" or "jersey_barrier" or "log" or "rising_bollard" or "debris":
                return access is "yes" or "permissive" ? NodeFlags.None : NodeFlags.BlocksMotor | emergency;
            case "cattle_grid":
                return NodeFlags.Gate;
            default:
                return NodeFlags.None;
        }
    }

    public static RoadGraph Build(MapData data, CancellationToken cancel = default)
    {
        // Routable ways with complete geometry (ways cut by the extract boundary are rendered but not routed).
        var ways = data.Roads.Where(w => w.Points.Length == w.NodeIds.Length && w.Points.Length >= 2).ToList();

        // Graph nodes: way ends, nodes shared by two or more ways (or used twice by one), and barriers.
        var all = new List<long>();
        foreach (var w in ways)
        {
            all.AddRange(w.NodeIds);
            all.Add(w.NodeIds[0]);
            all.Add(w.NodeIds[^1]);
        }
        foreach (var id in data.Barriers.Keys)
        {
            all.Add(id);
            all.Add(id);
        }
        all.Sort();
        var graphIds = new List<long>();
        for (var i = 0; i < all.Count;)
        {
            var j = i;
            while (j < all.Count && all[j] == all[i])
                j++;
            if (j - i >= 2)
                graphIds.Add(all[i]);
            i = j;
        }
        all.Clear();
        all.TrimExcess();
        var nodeIds = graphIds.ToArray();
        // Only barriers that are actually on a routable way count.
        var onRoad = new HashSet<long>();
        foreach (var w in ways)
            foreach (var id in w.NodeIds)
                if (data.Barriers.ContainsKey(id))
                    onRoad.Add(id);

        var lat = new int[nodeIds.Length];
        var lon = new int[nodeIds.Length];
        var nflags = new NodeFlags[nodeIds.Length];
        var known = new bool[nodeIds.Length];

        var strings = new Dictionary<string, int> { [""] = 0 };
        int Str(string? s) => s == null ? 0 : strings.TryGetValue(s, out var i) ? i : strings[s] = strings.Count;

        var segFrom = new List<int>(); var segTo = new List<int>(); var segLen = new List<float>(); var segSpeed = new List<byte>();
        var segClass = new List<RoadClass>(); var segFlags = new List<SegmentFlags>(); var segH = new List<ushort>(); var segW = new List<ushort>();
        var segL = new List<ushort>(); var segName = new List<int>(); var segRef = new List<int>(); var segShape = new List<int>();
        var shapeLat = new List<int>(); var shapeLon = new List<int>(); var segWay = new List<long>();

        foreach (var w in ways)
        {
            cancel.ThrowIfCancellationRequested();
            var t = w.Tags;
            var cls = ClassOf(t);
            var flags = FlagsOf(t);
            var speed = (byte)Math.Clamp((int)Math.Round(TagParse.SpeedKmh(t["maxspeed"]) ?? DefaultSpeed(cls, t["highway"] ?? "")), 5, 130);
            var h = (ushort)Math.Clamp((int)Math.Round((TagParse.Meters(t["maxheight"]) ?? 0) * 100), 0, ushort.MaxValue);
            var wt = (ushort)Math.Clamp((int)Math.Round((TagParse.Tonnes(t["maxweight"]) ?? 0) * 10), 0, ushort.MaxValue);
            var len = (ushort)Math.Clamp((int)Math.Round((TagParse.Meters(t["maxlength"]) ?? 0) * 10), 0, ushort.MaxValue);
            var name = Str(t["name"]);
            var rf = Str(t["ref"]);

            var startIndex = 0;
            var startNode = Array.BinarySearch(nodeIds, w.NodeIds[0]);
            var shapeStart = shapeLat.Count;
            double length = 0;
            Mark(startNode, w.Points[0], w.NodeIds[0]);
            for (var i = 1; i < w.NodeIds.Length; i++)
            {
                length += w.Points[i - 1].DistanceTo(w.Points[i]);
                var n = Array.BinarySearch(nodeIds, w.NodeIds[i]);
                if (n < 0)
                {
                    shapeLat.Add((int)Math.Round(w.Points[i].Lat * 1e7));
                    shapeLon.Add((int)Math.Round(w.Points[i].Lon * 1e7));
                    continue;
                }
                Mark(n, w.Points[i], w.NodeIds[i]);
                if (n == startNode && i - startIndex <= 1)
                {
                    // Repeated node (zero-length piece): skip.
                    shapeLat.RemoveRange(shapeStart, shapeLat.Count - shapeStart);
                    shapeLon.RemoveRange(shapeStart, shapeLon.Count - shapeStart);
                }
                else
                {
                    segFrom.Add(startNode); segTo.Add(n); segLen.Add((float)Math.Max(0.1, length)); segSpeed.Add(speed);
                    segClass.Add(cls); segFlags.Add(flags); segH.Add(h); segW.Add(wt); segL.Add(len);
                    segName.Add(name); segRef.Add(rf); segShape.Add(shapeStart); segWay.Add(w.Id);
                }
                startNode = n;
                startIndex = i;
                shapeStart = shapeLat.Count;
                length = 0;
            }
        }

        void Mark(int n, GeoPoint p, long osmId)
        {
            if (known[n])
                return;
            known[n] = true;
            lat[n] = (int)Math.Round(p.Lat * 1e7);
            lon[n] = (int)Math.Round(p.Lon * 1e7);
            if (onRoad.Contains(osmId) && data.Barriers.TryGetValue(osmId, out var bt))
                nflags[n] = BarrierFlags(bt);
        }

        var g = new RoadGraph
        {
            NodeCount = nodeIds.Length,
            NodeLat = lat,
            NodeLon = lon,
            NodeFlag = nflags,
            SegmentCount = segFrom.Count,
            SegFrom = segFrom.ToArray(),
            SegTo = segTo.ToArray(),
            SegLength = segLen.ToArray(),
            SegSpeed = segSpeed.ToArray(),
            SegClass = segClass.ToArray(),
            SegFlags = segFlags.ToArray(),
            SegMaxHeight = segH.ToArray(),
            SegMaxWeight = segW.ToArray(),
            SegMaxLength = segL.ToArray(),
            SegName = segName.ToArray(),
            SegRef = segRef.ToArray(),
            SegShapeStart = segShape.Append(shapeLat.Count).ToArray(),
            ShapeLat = shapeLat.ToArray(),
            ShapeLon = shapeLon.ToArray(),
            SegWayId = segWay.ToArray(),
            Strings = strings.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToArray()
        };
        BuildAdjacency(g);
        return Compact(g);
    }

    private static void BuildAdjacency(RoadGraph g)
    {
        var counts = new int[g.NodeCount + 1];
        for (var s = 0; s < g.SegmentCount; s++)
        {
            counts[g.SegFrom[s]]++;
            counts[g.SegTo[s]]++;
        }
        var start = new int[g.NodeCount + 1];
        for (var n = 0; n < g.NodeCount; n++)
            start[n + 1] = start[n] + counts[n];
        var fill = (int[])start.Clone();
        var entries = new int[start[g.NodeCount]];
        for (var s = 0; s < g.SegmentCount; s++)
        {
            entries[fill[g.SegFrom[s]]++] = s * 2;
            entries[fill[g.SegTo[s]]++] = s * 2 + 1;
        }
        g.AdjStart = start;
        g.AdjEntry = entries;
    }

    /// <summary>Drops graph nodes that no segment uses (e.g. barriers on unroutable ways).</summary>
    private static RoadGraph Compact(RoadGraph g)
    {
        var map = new int[g.NodeCount];
        var n = 0;
        for (var i = 0; i < g.NodeCount; i++)
            map[i] = g.AdjStart[i + 1] > g.AdjStart[i] ? n++ : -1;
        if (n == g.NodeCount)
            return g;
        int[] Pick(int[] a) { var r = new int[n]; for (var i = 0; i < g.NodeCount; i++) if (map[i] >= 0) r[map[i]] = a[i]; return r; }
        var flags = new NodeFlags[n];
        for (var i = 0; i < g.NodeCount; i++)
            if (map[i] >= 0)
                flags[map[i]] = g.NodeFlag[i];
        g.NodeLat = Pick(g.NodeLat);
        g.NodeLon = Pick(g.NodeLon);
        g.NodeFlag = flags;
        for (var s = 0; s < g.SegmentCount; s++)
        {
            g.SegFrom[s] = map[g.SegFrom[s]];
            g.SegTo[s] = map[g.SegTo[s]];
        }
        g.NodeCount = n;
        BuildAdjacency(g);
        return g;
    }
}
