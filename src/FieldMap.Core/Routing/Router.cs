using FieldMap.Core.Geo;

namespace FieldMap.Core.Routing;

/// <summary>A position snapped to the nearest usable road segment.</summary>
public sealed record Snap(int Segment, double Along, GeoPoint Point, double DistanceMeters)
{
    /// <summary>Fraction of the segment length (0 at From, 1 at To).</summary>
    public double Fraction(RoadGraph g) => g.SegLength[Segment] <= 0 ? 0 : Math.Clamp(Along / g.SegLength[Segment], 0, 1);
}

/// <summary>A piece of a route: part or all of one segment, travelled forward (From→To) or backward.</summary>
public readonly record struct RouteLeg(int Segment, bool Forward, double FromAlong, double ToAlong)
{
    public double Length => Math.Abs(ToAlong - FromAlong);
}

public sealed class Route
{
    public required VehicleProfile Profile { get; init; }
    public required GeoPoint Start { get; init; }
    public required GeoPoint End { get; init; }
    public List<RouteLeg> Legs { get; init; } = new();
    public List<GeoPoint> Geometry { get; init; } = new();
    public double DistanceMeters { get; init; }
    public double DurationSeconds { get; init; }
    public List<RouteStep> Steps { get; set; } = new();

    /// <summary>Things the crew should know: seasonal or forest roads, gates, ferries, untagged truck limits…</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>Straight-line distance from the requested start/end to the road the route starts/ends on.</summary>
    public double StartOffsetMeters { get; init; }
    public double EndOffsetMeters { get; init; }

    /// <summary>Cumulative distance along <see cref="Geometry"/> for each point.</summary>
    public double[] CumulativeDistance()
    {
        var c = new double[Geometry.Count];
        for (var i = 1; i < Geometry.Count; i++)
            c[i] = c[i - 1] + Geometry[i - 1].DistanceTo(Geometry[i]);
        return c;
    }
}

/// <summary>
/// Offline router over a <see cref="RoadGraph"/>: A* for point-to-point routes, Dijkstra for "nearest N by drive time",
/// penalty-based alternatives and user-drawn closures. An instance keeps search arrays and isn't thread-safe; create one
/// per thread (they're cheap compared with the graph).
/// </summary>
public sealed class Router
{
    private readonly RoadGraph _g;

    // Search labels are kept per state. A state is a node (index < NodeCount), except at junctions with turn
    // restrictions, which have one state per arriving segment (so a slower arrival that may turn isn't discarded in
    // favour of a faster one that may not), plus one state per "through a connecting road" rule.
    private readonly int _stateCount;
    private readonly int[] _special;         // node → first arrival-state index (relative to NodeCount), or -1
    private readonly int[] _slotNode;        // arrival state → node
    private readonly int[] _viaNode;         // via-rule state → node at the end of the connecting road
    private readonly Dictionary<(int Node, int From), List<(int To, byte Vehicles)>> _turnBans = new();
    private readonly Dictionary<(int From, int Entry), int> _viaRule = new();
    private readonly List<List<(int To, byte Vehicles)>> _viaBans = new();
    private readonly List<byte> _viaRuleVehicles = new();

    private readonly float[] _time;
    private readonly float[] _dist;
    private readonly int[] _prevState;      // state reached from; -1 = from the start snap
    private readonly int[] _arrived;        // adjacency entry travelled to reach the state's node (segment*2 + direction)
    private readonly int[] _stamp;
    private readonly bool[] _settled;
    private int _generation;

    private const int FromStart = -1;

    public Router(RoadGraph graph)
    {
        _g = graph;
        var n = graph.NodeCount;
        _special = new int[n];
        Array.Fill(_special, -1);
        var specialNodes = new SortedSet<int>(graph.TurnNode);
        for (var i = 0; i < graph.ViaFrom.Length; i++)
        {
            var w = graph.ViaEntry[i] >> 1;
            specialNodes.Add(graph.SegFrom[w]);
            specialNodes.Add(graph.SegTo[w]);
        }
        var slots = new List<int>();
        foreach (var node in specialNodes)
        {
            _special[node] = slots.Count;
            for (var k = graph.AdjStart[node]; k < graph.AdjStart[node + 1]; k++)
                slots.Add(node);
        }
        _slotNode = slots.ToArray();
        for (var i = 0; i < graph.TurnNode.Length; i++)
        {
            var key = (graph.TurnNode[i], graph.TurnFrom[i]);
            if (!_turnBans.TryGetValue(key, out var list))
                _turnBans[key] = list = new List<(int, byte)>();
            list.Add((graph.TurnTo[i], graph.TurnVehicles[i]));
        }
        var viaNodes = new List<int>();
        for (var i = 0; i < graph.ViaFrom.Length; i++)
        {
            var key = (graph.ViaFrom[i], graph.ViaEntry[i]);
            if (!_viaRule.TryGetValue(key, out var rule))
            {
                rule = _viaBans.Count;
                _viaRule[key] = rule;
                _viaBans.Add(new List<(int, byte)>());
                _viaRuleVehicles.Add(0);
                var w = graph.ViaEntry[i] >> 1;
                viaNodes.Add((graph.ViaEntry[i] & 1) == 0 ? graph.SegTo[w] : graph.SegFrom[w]);
            }
            _viaBans[rule].Add((graph.ViaTo[i], graph.ViaVehicles[i]));
            _viaRuleVehicles[rule] |= graph.ViaVehicles[i];
        }
        _viaNode = viaNodes.ToArray();
        _stateCount = n + _slotNode.Length + _viaNode.Length;
        _time = new float[_stateCount];
        _dist = new float[_stateCount];
        _prevState = new int[_stateCount];
        _arrived = new int[_stateCount];
        _stamp = new int[_stateCount];
        _settled = new bool[_stateCount];
    }

    public RoadGraph Graph => _g;

    /// <summary>Segments the user has closed (drawn road closures). Routes never use them.</summary>
    public HashSet<int> Closed { get; } = new();

    // ------------------------------------------------------------------ snapping

    /// <summary>The nearest point on a road the profile may use, within <paramref name="maxMeters"/>.</summary>
    public Snap? SnapTo(GeoPoint p, VehicleProfile profile, double maxMeters = 3000)
    {
        foreach (var radius in new[] { 250.0, 1000, maxMeters })
        {
            if (radius > maxMeters)
                break;
            Snap? best = null;
            var box = new GeoBounds(p.Lat, p.Lon, p.Lat, p.Lon).Expand(radius);
            foreach (var s in _g.Index.Query(box))
            {
                if (Closed.Contains(s) || (profile.Speed(_g, s, true) <= 0 && profile.Speed(_g, s, false) <= 0))
                    continue;
                var shape = _g.Shape(s);
                var (d, seg, t, along) = Geometry.NearestOnLine(p, shape);
                if (d <= radius && (best == null || d < best.DistanceMeters))
                    best = new Snap(s, along, Geometry.Lerp(shape[seg], shape[seg + 1], t), d);
            }
            if (best != null)
                return best;
        }
        return null;
    }

    // ------------------------------------------------------------------ costs

    private double SegmentTime(int segment, bool forward, double meters, VehicleProfile profile, IReadOnlyDictionary<int, double>? penalties)
    {
        var speed = Closed.Contains(segment) ? 0 : profile.Speed(_g, segment, forward);
        if (speed <= 0)
            return double.PositiveInfinity;
        var t = meters / (speed / 3.6);
        if (penalties != null && penalties.TryGetValue(segment, out var k))
            t *= k;
        return t;
    }

    private double Extra(int segment, VehicleProfile profile) =>
        !profile.IsWalking && (_g.SegFlags[segment] & SegmentFlags.Ferry) != 0 ? 600 : 0; // ferry loading/wait

    private void Reset()
    {
        if (++_generation == int.MaxValue)
        {
            Array.Clear(_stamp);
            _generation = 1;
        }
    }

    private bool Seen(int st) => _stamp[st] == _generation;

    private void Visit(int st, double time, double dist, int prevState, int arrivedEntry)
    {
        _stamp[st] = _generation;
        _time[st] = (float)time;
        _dist[st] = (float)dist;
        _prevState[st] = prevState;
        _arrived[st] = arrivedEntry;
        _settled[st] = false;
    }

    private int NodeOf(int st) =>
        st < _g.NodeCount ? st : st < _g.NodeCount + _slotNode.Length ? _slotNode[st - _g.NodeCount] : _viaNode[st - _g.NodeCount - _slotNode.Length];

    private static byte VehicleBit(VehicleProfile p) => p.Kind switch
    {
        ProfileKind.Car => (byte)Build.RestrictionVehicles.Car,
        ProfileKind.Truck => (byte)Build.RestrictionVehicles.Truck,
        ProfileKind.Emergency => (byte)Build.RestrictionVehicles.Emergency,
        _ => 0 // walking: turn restrictions don't apply
    };

    /// <summary>The state for arriving at <paramref name="node"/> along adjacency entry <paramref name="entry"/>, coming from a state that arrived on <paramref name="fromSegment"/>.</summary>
    private int StateFor(int node, int entry, int fromSegment, byte vehicle)
    {
        if (vehicle != 0 && fromSegment >= 0 && _viaRule.TryGetValue((fromSegment, entry), out var rule) && (_viaRuleVehicles[rule] & vehicle) != 0)
            return _g.NodeCount + _slotNode.Length + rule;
        var first = _special[node];
        if (first < 0)
            return node;
        var seg = entry >> 1;
        for (var k = _g.AdjStart[node]; k < _g.AdjStart[node + 1]; k++)
            if (_g.AdjEntry[k] >> 1 == seg)
                return _g.NodeCount + first + (k - _g.AdjStart[node]);
        return node;
    }

    /// <summary>Whether leaving the state's node on <paramref name="segment"/> is a banned turn.</summary>
    private bool Banned(int st, int segment, byte vehicle)
    {
        if (vehicle == 0 || _arrived[st] < 0)
            return false;
        var node = NodeOf(st);
        var from = _arrived[st] >> 1;
        if (_turnBans.TryGetValue((node, from), out var bans))
            foreach (var (to, v) in bans)
                if (to == segment && (v & vehicle) != 0)
                    return true;
        var viaBase = _g.NodeCount + _slotNode.Length;
        if (st >= viaBase)
            foreach (var (to, v) in _viaBans[st - viaBase])
                if (to == segment && (v & vehicle) != 0)
                    return true;
        return false;
    }

    private double Heuristic(int st, GeoPoint target, VehicleProfile profile) =>
        _g.Node(NodeOf(st)).DistanceTo(target) / (profile.MaxSpeedKmh / 3.6);

    // ------------------------------------------------------------------ point to point

    public Route? Route(GeoPoint from, GeoPoint to, VehicleProfile profile, IReadOnlyDictionary<int, double>? penalties = null)
    {
        var a = SnapTo(from, profile);
        var b = SnapTo(to, profile);
        if (a == null || b == null)
            return null;
        return Route(a, b, from, to, profile, penalties);
    }

    public Route? Route(Snap a, Snap b, GeoPoint from, GeoPoint to, VehicleProfile profile, IReadOnlyDictionary<int, double>? penalties = null)
    {
        Reset();
        var vehicle = VehicleBit(profile);
        var heap = new PriorityQueue<int, double>();
        var targetPoint = b.Point;
        void Seed(int node, int entry, double time, double dist)
        {
            var st = StateFor(node, entry, -1, vehicle);
            if (double.IsInfinity(time) || (Seen(st) && _time[st] <= time))
                return;
            Visit(st, time, dist, FromStart, entry);
            heap.Enqueue(st, time + Heuristic(st, targetPoint, profile));
        }
        var lenA = _g.SegLength[a.Segment];
        // Travelling backward along the start segment arrives at its From node, forward at its To node.
        Seed(_g.SegFrom[a.Segment], a.Segment * 2 + 1, SegmentTime(a.Segment, false, a.Along, profile, penalties), a.Along);
        Seed(_g.SegTo[a.Segment], a.Segment * 2, SegmentTime(a.Segment, true, lenA - a.Along, profile, penalties), lenA - a.Along);

        // Best way onto the target so far: (time, state it is reached from, direction along the target segment).
        var lenB = _g.SegLength[b.Segment];
        var best = double.PositiveInfinity;
        var bestState = -2;
        var bestForward = true;
        double bestDist = 0;
        if (a.Segment == b.Segment)
        {
            var forward = b.Along >= a.Along;
            var direct = SegmentTime(a.Segment, forward, Math.Abs(b.Along - a.Along), profile, penalties);
            if (!double.IsInfinity(direct))
            {
                best = direct;
                bestState = -1;
                bestForward = forward;
                bestDist = Math.Abs(b.Along - a.Along);
            }
        }

        while (heap.TryDequeue(out var st, out var f))
        {
            if (_settled[st])
                continue;
            if (f >= best)
                break;
            _settled[st] = true;
            var n = NodeOf(st);
            var tn = _time[st];
            // A locked gate (or bollard) stops the vehicle here: it can neither continue nor finish beyond it.
            if (_prevState[st] != FromStart && !profile.CanPass(_g, n))
                continue;
            // Reached an end of the target segment: try finishing along it (if turning onto it is allowed).
            if (n == _g.SegFrom[b.Segment] && !Banned(st, b.Segment, vehicle))
            {
                var t = tn + SegmentTime(b.Segment, true, b.Along, profile, penalties);
                if (t < best) { best = t; bestState = st; bestForward = true; bestDist = _dist[st] + b.Along; }
            }
            if (n == _g.SegTo[b.Segment] && !Banned(st, b.Segment, vehicle))
            {
                var t = tn + SegmentTime(b.Segment, false, lenB - b.Along, profile, penalties);
                if (t < best) { best = t; bestState = st; bestForward = false; bestDist = _dist[st] + lenB - b.Along; }
            }
            Relax(st, profile, vehicle, penalties, (m, time) => heap.Enqueue(m, time + Heuristic(m, targetPoint, profile)));
        }
        if (double.IsInfinity(best))
            return null;
        return BuildRoute(a, b, from, to, profile, bestState, bestForward, best, bestDist);
    }

    private void Relax(int st, VehicleProfile profile, byte vehicle, IReadOnlyDictionary<int, double>? penalties, Action<int, double> enqueue)
    {
        var n = NodeOf(st);
        var arrived = _arrived[st] >= 0 ? _arrived[st] >> 1 : -1;
        var degree = _g.AdjStart[n + 1] - _g.AdjStart[n];
        for (var k = _g.AdjStart[n]; k < _g.AdjStart[n + 1]; k++)
        {
            var entry = _g.AdjEntry[k];
            var s = entry >> 1;
            if (s == arrived && degree > 1)
                continue; // no U-turns except at dead ends
            if (Banned(st, s, vehicle))
                continue; // turn restriction
            var forward = (entry & 1) == 0;
            var t = SegmentTime(s, forward, _g.SegLength[s], profile, penalties);
            if (double.IsInfinity(t))
                continue;
            t += Extra(s, profile);
            var m = forward ? _g.SegTo[s] : _g.SegFrom[s];
            var target = StateFor(m, entry, arrived, vehicle);
            var time = _time[st] + t;
            if (Seen(target) && (_settled[target] || _time[target] <= time))
                continue;
            // Remember how the state was reached: from which state, along which segment and direction (bit 0 set = backward).
            Visit(target, time, _dist[st] + _g.SegLength[s], st, entry);
            enqueue(target, time);
        }
    }

    private Route BuildRoute(Snap a, Snap b, GeoPoint from, GeoPoint to, VehicleProfile profile, int lastState, bool lastForward, double time, double distance)
    {
        var legs = new List<RouteLeg>();
        if (lastState == -1)
        {
            legs.Add(new RouteLeg(a.Segment, lastForward, a.Along, b.Along));
        }
        else
        {
            var lenB = _g.SegLength[b.Segment];
            legs.Add(new RouteLeg(b.Segment, lastForward, lastForward ? 0 : lenB, b.Along));
            var st = lastState;
            while (_prevState[st] != FromStart)
            {
                var entry = _arrived[st];
                var s = entry >> 1;
                var forward = (entry & 1) == 0;
                legs.Add(new RouteLeg(s, forward, forward ? 0 : _g.SegLength[s], forward ? _g.SegLength[s] : 0));
                st = _prevState[st];
            }
            var n = NodeOf(st);
            // The first leg runs from the start snap to node n (the end of the start segment the search began from).
            bool startForward;
            if (_g.SegFrom[a.Segment] == _g.SegTo[a.Segment])
                startForward = SegmentTime(a.Segment, true, _g.SegLength[a.Segment] - a.Along, profile, null) <= SegmentTime(a.Segment, false, a.Along, profile, null);
            else
                startForward = n == _g.SegTo[a.Segment];
            legs.Add(new RouteLeg(a.Segment, startForward, a.Along, startForward ? _g.SegLength[a.Segment] : 0));
            legs.Reverse();
        }
        legs.RemoveAll(l => l.Length < 0.01 && legs.Count > 1);

        var geometry = new List<GeoPoint> { a.Point };
        foreach (var leg in legs)
            foreach (var p in SubShape(leg))
                if (geometry[^1].DistanceTo(p) > 0.05)
                    geometry.Add(p);
        if (geometry[^1].DistanceTo(b.Point) > 0.05)
            geometry.Add(b.Point);

        var route = new Route
        {
            Profile = profile,
            Start = from,
            End = to,
            Legs = legs,
            Geometry = geometry,
            DistanceMeters = distance,
            DurationSeconds = time,
            StartOffsetMeters = a.DistanceMeters,
            EndOffsetMeters = b.DistanceMeters
        };
        route.Steps = Instructions.Build(_g, route);
        route.Warnings = Instructions.Warnings(_g, route);
        return route;
    }

    /// <summary>Points of a segment between two distances along it, in travel order.</summary>
    public List<GeoPoint> SubShape(RouteLeg leg)
    {
        var shape = _g.Shape(leg.Segment);
        var lo = Math.Min(leg.FromAlong, leg.ToAlong);
        var hi = Math.Max(leg.FromAlong, leg.ToAlong);
        var result = new List<GeoPoint>();
        var along = 0.0;
        for (var i = 0; i < shape.Count - 1; i++)
        {
            var len = shape[i].DistanceTo(shape[i + 1]);
            var s0 = along;
            var s1 = along + len;
            if (s1 >= lo && s0 <= hi)
            {
                var t0 = len <= 0 ? 0 : Math.Clamp((lo - s0) / len, 0, 1);
                var t1 = len <= 0 ? 1 : Math.Clamp((hi - s0) / len, 0, 1);
                var p0 = Geometry.Lerp(shape[i], shape[i + 1], t0);
                if (result.Count == 0 || result[^1].DistanceTo(p0) > 0.01)
                    result.Add(p0);
                result.Add(Geometry.Lerp(shape[i], shape[i + 1], t1));
            }
            along = s1;
        }
        if (!(leg.FromAlong <= leg.ToAlong))
            result.Reverse();
        return result;
    }

    // ------------------------------------------------------------------ alternatives

    /// <summary>The best route plus up to <paramref name="maxAlternatives"/> clearly different ones (≤ 50 % slower).</summary>
    public List<Route> RouteWithAlternatives(GeoPoint from, GeoPoint to, VehicleProfile profile, int maxAlternatives = 2)
    {
        var routes = new List<Route>();
        var a = SnapTo(from, profile);
        var b = SnapTo(to, profile);
        if (a == null || b == null)
            return routes;
        var best = Route(a, b, from, to, profile);
        if (best == null)
            return routes;
        routes.Add(best);
        var penalties = new Dictionary<int, double>();
        for (var attempt = 0; attempt < maxAlternatives * 2 && routes.Count <= maxAlternatives; attempt++)
        {
            foreach (var leg in routes[^1].Legs)
                penalties[leg.Segment] = penalties.TryGetValue(leg.Segment, out var k) ? k * 1.5 : 1.8;
            var alt = Route(a, b, from, to, profile, penalties);
            if (alt == null)
                break;
            // Report the alternative's real (unpenalised) time.
            var realTime = alt.Legs.Sum(l => SegmentTime(l.Segment, l.Forward, l.Length, profile, null) + 0.0);
            alt = new Route
            {
                Profile = alt.Profile, Start = alt.Start, End = alt.End, Legs = alt.Legs, Geometry = alt.Geometry, DistanceMeters = alt.DistanceMeters,
                DurationSeconds = realTime + alt.Legs.Select(l => l.Segment).Distinct().Sum(s => Extra(s, profile)),
                Steps = alt.Steps, Warnings = alt.Warnings, StartOffsetMeters = alt.StartOffsetMeters, EndOffsetMeters = alt.EndOffsetMeters
            };
            if (alt.DurationSeconds > best.DurationSeconds * 1.5)
                break;
            if (routes.All(r => Overlap(r, alt) < 0.7))
                routes.Add(alt);
        }
        return routes;
    }

    private static double Overlap(Route a, Route b)
    {
        var segs = a.Legs.Select(l => l.Segment).ToHashSet();
        var shared = b.Legs.Where(l => segs.Contains(l.Segment)).Sum(l => l.Length);
        return b.DistanceMeters <= 0 ? 1 : shared / b.DistanceMeters;
    }

    // ------------------------------------------------------------------ nearest N by travel time

    public sealed record Reach(int Index, double DurationSeconds, double DistanceMeters, double OffsetMeters);

    /// <summary>
    /// Travel time from <paramref name="origin"/> to each candidate, nearest first, using one Dijkstra search that stops
    /// once <paramref name="count"/> candidates are certain or <paramref name="maxSeconds"/> is reached. Candidates that
    /// can't be reached (or are more than 3 km from a usable road) are left out.
    /// </summary>
    public List<Reach> NearestByTime(GeoPoint origin, IReadOnlyList<GeoPoint> candidates, VehicleProfile profile, int count, double maxSeconds = 4 * 3600)
    {
        var result = new List<Reach>();
        var a = SnapTo(origin, profile);
        if (a == null)
            return result;
        // Attach every candidate to the ends of its snapped segment.
        var attach = new Dictionary<int, List<(int Index, double Time, double Dist)>>();
        var snaps = new Snap?[candidates.Count];
        var direct = new List<(int Index, double Time, double Dist)>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var s = SnapTo(candidates[i], profile);
            snaps[i] = s;
            if (s == null)
                continue;
            var len = _g.SegLength[s.Segment];
            void Add(int node, double time, double dist)
            {
                if (double.IsInfinity(time))
                    return;
                if (!attach.TryGetValue(node, out var list))
                    attach[node] = list = new();
                list.Add((i, time, dist));
            }
            Add(_g.SegFrom[s.Segment], SegmentTime(s.Segment, true, s.Along, profile, null), s.Along);
            Add(_g.SegTo[s.Segment], SegmentTime(s.Segment, false, len - s.Along, profile, null), len - s.Along);
            if (s.Segment == a.Segment)
            {
                var forward = s.Along >= a.Along;
                var t = SegmentTime(a.Segment, forward, Math.Abs(s.Along - a.Along), profile, null);
                if (!double.IsInfinity(t))
                    direct.Add((i, t, Math.Abs(s.Along - a.Along)));
            }
        }
        var best = new Dictionary<int, (double Time, double Dist)>();
        foreach (var (i, t, d) in direct)
            best[i] = (t, d);

        Reset();
        var vehicle = VehicleBit(profile);
        var heap = new PriorityQueue<int, double>();
        void Seed(int node, int entry, double time, double dist)
        {
            var st = StateFor(node, entry, -1, vehicle);
            if (double.IsInfinity(time) || (Seen(st) && _time[st] <= time))
                return;
            Visit(st, time, dist, FromStart, entry);
            heap.Enqueue(st, time);
        }
        var lenA = _g.SegLength[a.Segment];
        Seed(_g.SegFrom[a.Segment], a.Segment * 2 + 1, SegmentTime(a.Segment, false, a.Along, profile, null), a.Along);
        Seed(_g.SegTo[a.Segment], a.Segment * 2, SegmentTime(a.Segment, true, lenA - a.Along, profile, null), lenA - a.Along);
        var final = new HashSet<int>();
        while (heap.TryDequeue(out var st, out var t))
        {
            if (_settled[st])
                continue;
            if (t > maxSeconds)
                break;
            _settled[st] = true;
            // Candidates whose best time is now below the frontier are final.
            foreach (var (i, bt) in best)
                if (bt.Time <= t)
                    final.Add(i);
            if (final.Count >= count)
                break;
            var n = NodeOf(st);
            if (_prevState[st] != FromStart && !profile.CanPass(_g, n))
                continue;
            if (attach.TryGetValue(n, out var list))
                foreach (var (i, extra, dist) in list)
                {
                    if (snaps[i] is { } cs && Banned(st, cs.Segment, vehicle))
                        continue;
                    var total = t + extra;
                    if (!best.TryGetValue(i, out var cur) || total < cur.Time)
                        best[i] = (total, _dist[st] + dist);
                }
            Relax(st, profile, vehicle, null, (m, time) => heap.Enqueue(m, time));
        }
        foreach (var (i, (time, dist)) in best.OrderBy(kv => kv.Value.Time))
        {
            if (time > maxSeconds)
                continue;
            result.Add(new Reach(i, time, dist, snaps[i]!.DistanceMeters));
            if (result.Count >= count)
                break;
        }
        return result;
    }

    // ------------------------------------------------------------------ closures

    /// <summary>Segments a drawn closure line (within <paramref name="toleranceMeters"/>) or area touches.</summary>
    public List<int> SegmentsTouching(IReadOnlyList<GeoPoint> geometry, bool isArea, double toleranceMeters = 12)
    {
        var result = new List<int>();
        if (geometry.Count == 0)
            return result;
        var box = GeoBounds.Of(geometry).Expand(toleranceMeters + 5);
        foreach (var s in _g.Index.Query(box))
        {
            var shape = _g.Shape(s);
            var touches = isArea
                ? shape.Any(p => Geometry.Contains(geometry, p)) || Geometry.LinesTouch(shape, geometry, 0)
                : geometry.Count == 1
                    ? Geometry.NearestOnLine(geometry[0], shape).Distance <= toleranceMeters
                    : Geometry.LinesTouch(shape, geometry, toleranceMeters);
            if (touches)
                result.Add(s);
        }
        return result;
    }
}
