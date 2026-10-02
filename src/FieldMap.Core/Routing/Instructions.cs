using System.Text;
using FieldMap.Core.Geo;

namespace FieldMap.Core.Routing;

public enum Maneuver
{
    Depart,
    Continue,
    SlightLeft,
    SlightRight,
    Left,
    Right,
    SharpLeft,
    SharpRight,
    UTurn,
    Roundabout,
    Ramp,
    Ferry,
    Arrive
}

public sealed class RouteStep
{
    public Maneuver Maneuver { get; init; }
    public string Instruction { get; init; } = "";

    /// <summary>The road the step continues on ("Main Street", "Hwy 97").</summary>
    public string Road { get; init; } = "";

    /// <summary>Distance and time from this maneuver to the next one.</summary>
    public double DistanceMeters { get; init; }
    public double DurationSeconds { get; init; }

    public GeoPoint Location { get; init; }

    /// <summary>Distance along the route geometry where the maneuver is.</summary>
    public double AlongMeters { get; init; }

    /// <summary>Roundabout exit number (1-based), when <see cref="Maneuver"/> is Roundabout.</summary>
    public int Exit { get; init; }
}

/// <summary>Turn-by-turn instructions and warnings from a route's legs.</summary>
public static class Instructions
{
    private sealed class Stretch
    {
        public string Road = "";
        public bool Roundabout;
        public bool Link;
        public bool Ferry;
        public List<GeoPoint> Points = new();
        public double Length;
        public double Time;
        public int ExitCount;
        public double Along;
    }

    /// <summary>"Main Street", "Hwy 97", "Lougheed Highway (Hwy 7)".</summary>
    public static string RoadLabel(string name, string rf)
    {
        rf = rf.Replace(";", " / ");
        if (rf.Length == 0)
            return name;
        var hwy = rf.All(c => char.IsDigit(c) || c is 'A' or 'B' or 'C' or ' ' or '/') ? "Hwy " + rf : rf;
        if (name.Length == 0)
            return hwy;
        return name.Contains(rf.Split(' ')[0], StringComparison.OrdinalIgnoreCase) ? name : $"{name} ({hwy})";
    }

    public static string Compass(double bearing) => GeoMath.CompassPoint(bearing) switch
    {
        "N" or "NNE" or "NNW" => "north",
        "NE" or "ENE" => "northeast",
        "E" or "ESE" => "east",
        "SE" or "SSE" => "southeast",
        "S" or "SSW" => "south",
        "SW" or "WSW" => "southwest",
        "W" or "WNW" => "west",
        _ => "northwest"
    };

    private static double BearingAtStart(List<GeoPoint> pts)
    {
        var a = pts[0];
        for (var i = 1; i < pts.Count; i++)
            if (a.DistanceTo(pts[i]) >= 15 || i == pts.Count - 1)
                return a.BearingTo(pts[i]);
        return 0;
    }

    private static double BearingAtEnd(List<GeoPoint> pts)
    {
        var b = pts[^1];
        for (var i = pts.Count - 2; i >= 0; i--)
            if (pts[i].DistanceTo(b) >= 15 || i == 0)
                return pts[i].BearingTo(b);
        return 0;
    }

    private static double Turn(double from, double to) => ((to - from + 540) % 360) - 180;

    internal static List<RouteStep> Build(RoadGraph g, Route route)
    {
        var router = new Router(g);
        var stretches = new List<Stretch>();
        var along = 0.0;
        for (var li = 0; li < route.Legs.Count; li++)
        {
            var leg = route.Legs[li];
            var s = leg.Segment;
            var flags = g.SegFlags[s];
            var road = RoadLabel(g.Name(s), g.Ref(s));
            var roundabout = (flags & SegmentFlags.Roundabout) != 0;
            var pts = router.SubShape(leg);
            if (pts.Count < 2)
                continue;
            var speed = route.Profile.Speed(g, s, leg.Forward);
            var time = speed > 0 ? leg.Length / (speed / 3.6) : 0;
            var last = stretches.Count > 0 ? stretches[^1] : null;
            if (last != null && last.Road == road && last.Roundabout == roundabout && !(last.Link ^ ((flags & SegmentFlags.Link) != 0)))
            {
                last.Points.AddRange(pts.Skip(1));
                last.Length += leg.Length;
                last.Time += time;
            }
            else
            {
                last = new Stretch
                {
                    Road = road, Roundabout = roundabout, Link = (flags & SegmentFlags.Link) != 0, Ferry = (flags & SegmentFlags.Ferry) != 0,
                    Points = pts, Length = leg.Length, Time = time, Along = along
                };
                stretches.Add(last);
            }
            if (roundabout && li < route.Legs.Count - 1)
            {
                // Count the exits passed: each node inside the roundabout with a road leaving it.
                var node = leg.Forward ? g.SegTo[s] : g.SegFrom[s];
                for (var k = g.AdjStart[node]; k < g.AdjStart[node + 1]; k++)
                {
                    var other = g.AdjEntry[k] >> 1;
                    if ((g.SegFlags[other] & SegmentFlags.Roundabout) == 0 && route.Profile.Speed(g, other, (g.AdjEntry[k] & 1) == 0) > 0)
                    {
                        last.ExitCount++;
                        break;
                    }
                }
            }
            along += leg.Length;
        }

        var steps = new List<RouteStep>();
        if (stretches.Count == 0)
        {
            steps.Add(new RouteStep { Maneuver = Maneuver.Arrive, Instruction = "You are at the destination", Location = route.End });
            return steps;
        }
        var first = stretches[0];
        steps.Add(new RouteStep
        {
            Maneuver = Maneuver.Depart,
            Instruction = $"Head {Compass(BearingAtStart(first.Points))}" + (first.Road.Length > 0 ? $" on {first.Road}" : ""),
            Road = first.Road,
            DistanceMeters = first.Length,
            DurationSeconds = first.Time,
            Location = first.Points[0],
            AlongMeters = 0
        });
        for (var i = 1; i < stretches.Count; i++)
        {
            var prev = stretches[i - 1];
            var cur = stretches[i];
            var angle = Turn(BearingAtEnd(prev.Points), BearingAtStart(cur.Points));
            var onto = cur.Road.Length > 0 ? cur.Road : "the road";
            Maneuver m;
            string text;
            if (cur.Roundabout)
            {
                var next = i + 1 < stretches.Count ? stretches[i + 1] : null;
                var exit = Math.Max(1, cur.ExitCount);
                var nextRoad = next?.Road is { Length: > 0 } nr ? $" onto {nr}" : "";
                steps.Add(new RouteStep
                {
                    Maneuver = Maneuver.Roundabout,
                    Instruction = $"At the roundabout, take the {Ordinal(exit)} exit{nextRoad}",
                    Road = next?.Road ?? cur.Road,
                    DistanceMeters = cur.Length + (next?.Length ?? 0),
                    DurationSeconds = cur.Time + (next?.Time ?? 0),
                    Location = cur.Points[0],
                    AlongMeters = cur.Along,
                    Exit = exit
                });
                i++;
                continue;
            }
            if (cur.Ferry)
            {
                m = Maneuver.Ferry;
                text = $"Take the ferry{(cur.Road.Length > 0 ? " – " + cur.Road : "")}";
            }
            else if (cur.Link && !prev.Link)
            {
                m = Maneuver.Ramp;
                text = angle < -20 ? "Take the ramp on the left" : angle > 20 ? "Take the ramp on the right" : "Take the ramp";
                if (i + 1 < stretches.Count && stretches[i + 1].Road.Length > 0)
                    text += $" towards {stretches[i + 1].Road}";
            }
            else
            {
                (m, var verb) = Math.Abs(angle) switch
                {
                    < 20 => (Maneuver.Continue, "Continue"),
                    < 45 => (angle < 0 ? Maneuver.SlightLeft : Maneuver.SlightRight, angle < 0 ? "Keep left" : "Keep right"),
                    < 135 => (angle < 0 ? Maneuver.Left : Maneuver.Right, angle < 0 ? "Turn left" : "Turn right"),
                    < 170 => (angle < 0 ? Maneuver.SharpLeft : Maneuver.SharpRight, angle < 0 ? "Turn sharp left" : "Turn sharp right"),
                    _ => (Maneuver.UTurn, "Make a U-turn")
                };
                text = $"{verb} onto {onto}";
            }
            steps.Add(new RouteStep
            {
                Maneuver = m,
                Instruction = text,
                Road = cur.Road,
                DistanceMeters = cur.Length,
                DurationSeconds = cur.Time,
                Location = cur.Points[0],
                AlongMeters = cur.Along
            });
        }
        var lastStretch = stretches[^1];
        var side = "";
        if (route.EndOffsetMeters > 3 && lastStretch.Points.Count >= 2)
        {
            var dir = BearingAtEnd(lastStretch.Points);
            var toTarget = lastStretch.Points[^1].BearingTo(route.End);
            side = Turn(dir, toTarget) < 0 ? " on the left" : " on the right";
        }
        var off = route.EndOffsetMeters > 40 ? $" ({route.EndOffsetMeters:0} m from the road)" : "";
        steps.Add(new RouteStep
        {
            Maneuver = Maneuver.Arrive,
            Instruction = $"Arrive at the destination{side}{off}",
            Location = lastStretch.Points[^1],
            AlongMeters = route.DistanceMeters
        });
        return steps;
    }

    private static string Ordinal(int n) => n switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => n + "th"
    };

    internal static List<string> Warnings(RoadGraph g, Route route)
    {
        var w = new List<string>();
        string Names(Func<SegmentFlags, bool> pred) => string.Join(", ", route.Legs.Where(l => pred(g.SegFlags[l.Segment]))
            .Select(l => RoadLabel(g.Name(l.Segment), g.Ref(l.Segment))).Where(n => n.Length > 0).Distinct().Take(4));
        if (route.Legs.Any(l => (g.SegFlags[l.Segment] & SegmentFlags.ForestService) != 0))
            w.Add($"Uses forest service road(s): {Names(f => (f & SegmentFlags.ForestService) != 0)}. Check road status and radio channel signs.");
        if (route.Legs.Any(l => (g.SegFlags[l.Segment] & SegmentFlags.Seasonal) != 0))
            w.Add($"Uses seasonal road(s) that may be closed: {Names(f => (f & SegmentFlags.Seasonal) != 0)}.");
        var unpaved = route.Legs.Where(l => (g.SegFlags[l.Segment] & SegmentFlags.Unpaved) != 0).Sum(l => l.Length);
        if (unpaved >= 500)
            w.Add($"Includes {unpaved / 1000:0.0} km of gravel or unpaved road.");
        if (route.Legs.Any(l => (g.SegFlags[l.Segment] & SegmentFlags.Ferry) != 0))
            w.Add("Includes a ferry crossing (time includes 10 min loading; check sailing times).");
        if (route.Legs.Any(l => (g.SegFlags[l.Segment] & SegmentFlags.EmergencyAccess) != 0))
            w.Add("Uses an emergency-access road.");
        if (route.Legs.Any(l => (g.SegFlags[l.Segment] & SegmentFlags.Toll) != 0))
            w.Add("Includes a toll road.");
        var gates = 0;
        for (var i = 0; i < route.Legs.Count - 1; i++)
        {
            var leg = route.Legs[i];
            var node = leg.Forward ? g.SegTo[leg.Segment] : g.SegFrom[leg.Segment];
            if ((g.NodeFlag[node] & NodeFlags.Gate) != 0)
                gates++;
        }
        if (gates > 0)
            w.Add(gates == 1 ? "Passes through a gate." : $"Passes through {gates} gates.");
        if (route.Profile.Kind == ProfileKind.Truck)
            w.Add("Height, weight and length limits are only checked where they are recorded in the map data. Watch for posted limits.");
        if (route.StartOffsetMeters > 100)
            w.Add($"The start is {route.StartOffsetMeters:0} m from the nearest road.");
        return w;
    }

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : t.TotalMinutes >= 1 ? $"{Math.Round(t.TotalMinutes)} min" : $"{t.Seconds} s";
    }

    public static string FormatDistance(double meters) =>
        meters < 950 ? $"{Math.Round(meters / 10) * 10:0} m" : meters < 9950 ? $"{meters / 1000:0.0} km" : $"{meters / 1000:0} km";

    /// <summary>Plain-text route for pasting into dispatch or a message.</summary>
    public static string ToText(Route route, string? fromLabel = null, string? toLabel = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ROUTE ({route.Profile.Name}) – {FormatDistance(route.DistanceMeters)}, about {FormatDuration(route.DurationSeconds)}");
        sb.AppendLine($"From: {fromLabel ?? CoordinateFormat.Decimal(route.Start.Lat, route.Start.Lon)}");
        sb.AppendLine($"To:   {toLabel ?? CoordinateFormat.Decimal(route.End.Lat, route.End.Lon)} (MGRS {CoordinateFormat.Mgrs(route.End.Lat, route.End.Lon)})");
        var n = 1;
        foreach (var s in route.Steps)
        {
            var dist = s.Maneuver == Maneuver.Arrive ? "" : $" – {FormatDistance(s.DistanceMeters)}";
            sb.AppendLine($"{n++,2}. {s.Instruction}{dist}");
        }
        foreach (var w in route.Warnings)
            sb.AppendLine("! " + w);
        sb.Append($"Generated {DateTime.Now:HH:mm dd MMM yyyy} by Field Map (offline routing; verify with dispatch).");
        return sb.ToString();
    }
}
