namespace FieldMap.Core.Geo;

/// <summary>A point in a planar coordinate system (world units, tile pixels or local metres).</summary>
public readonly record struct Vec(double X, double Y)
{
    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec operator *(Vec a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>Planar and geodesic geometry helpers used by the tile builder, router, search and drawing tools.</summary>
public static class Geometry
{
    private const double R = GeoMath.EarthRadius;

    private static double Rad(double d) => d * Math.PI / 180;
    private static double Deg(double r) => r * 180 / Math.PI;

    // ------------------------------------------------------------------ geodesic (sphere)

    /// <summary>The point reached by travelling <paramref name="meters"/> from a start point on a true bearing.</summary>
    public static GeoPoint Destination(GeoPoint start, double bearingDegrees, double meters)
    {
        var d = meters / R;
        var b = Rad(bearingDegrees);
        var lat1 = Rad(start.Lat);
        var lon1 = Rad(start.Lon);
        var lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(d) + Math.Cos(lat1) * Math.Sin(d) * Math.Cos(b));
        var lon2 = lon1 + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(lat1), Math.Cos(d) - Math.Sin(lat1) * Math.Sin(lat2));
        return new GeoPoint(Deg(lat2), (Deg(lon2) + 540) % 360 - 180);
    }

    /// <summary>Length of a polyline in metres.</summary>
    public static double Length(IReadOnlyList<GeoPoint> line)
    {
        var total = 0.0;
        for (var i = 1; i < line.Count; i++)
            total += line[i - 1].DistanceTo(line[i]);
        return total;
    }

    /// <summary>Area of a polygon ring in square metres (spherical excess formula; ring may be open or closed).</summary>
    public static double Area(IReadOnlyList<GeoPoint> ring)
    {
        if (ring.Count < 3)
            return 0;
        var total = 0.0;
        for (var i = 0; i < ring.Count; i++)
        {
            var p1 = ring[i];
            var p2 = ring[(i + 1) % ring.Count];
            total += Rad(p2.Lon - p1.Lon) * (2 + Math.Sin(Rad(p1.Lat)) + Math.Sin(Rad(p2.Lat)));
        }
        return Math.Abs(total * R * R / 2);
    }

    /// <summary>Circle as a closed ring of <paramref name="segments"/> points.</summary>
    public static List<GeoPoint> Circle(GeoPoint center, double radiusMeters, int segments = 72)
    {
        var ring = new List<GeoPoint>(segments + 1);
        for (var i = 0; i < segments; i++)
            ring.Add(Destination(center, 360.0 * i / segments, radiusMeters));
        ring.Add(ring[0]);
        return ring;
    }

    /// <summary>
    /// A sector (pie slice) from <paramref name="center"/>: centred on <paramref name="bearing"/>, <paramref name="width"/>
    /// degrees wide, out to <paramref name="radiusMeters"/>. Closed ring.
    /// </summary>
    public static List<GeoPoint> Sector(GeoPoint center, double bearing, double width, double radiusMeters, int segments = 24)
    {
        var ring = new List<GeoPoint> { center };
        for (var i = 0; i <= segments; i++)
            ring.Add(Destination(center, bearing - width / 2 + width * i / segments, radiusMeters));
        ring.Add(center);
        return ring;
    }

    // ------------------------------------------------------------------ local planar approximation

    /// <summary>Converts to metres east/north of an origin (equirectangular; fine within tens of kilometres).</summary>
    public static Vec ToLocal(GeoPoint origin, GeoPoint p) =>
        new(Rad(p.Lon - origin.Lon) * R * Math.Cos(Rad(origin.Lat)), Rad(p.Lat - origin.Lat) * R);

    public static GeoPoint FromLocal(GeoPoint origin, Vec v) =>
        new(origin.Lat + Deg(v.Y / R), origin.Lon + Deg(v.X / (R * Math.Cos(Rad(origin.Lat)))));

    /// <summary>Distance in metres from a point to a segment, and where along the segment (0–1) the closest point lies.</summary>
    public static (double Distance, double T) DistanceToSegment(GeoPoint p, GeoPoint a, GeoPoint b)
    {
        var va = ToLocal(p, a);
        var vb = ToLocal(p, b);
        var ab = vb - va;
        var len2 = ab.X * ab.X + ab.Y * ab.Y;
        var t = len2 == 0 ? 0 : Math.Clamp(-(va.X * ab.X + va.Y * ab.Y) / len2, 0, 1);
        var closest = va + ab * t;
        return (closest.Length, t);
    }

    public static GeoPoint Lerp(GeoPoint a, GeoPoint b, double t) => new(a.Lat + (b.Lat - a.Lat) * t, a.Lon + (b.Lon - a.Lon) * t);

    /// <summary>Nearest point on a polyline: distance, segment index, position on the segment, and distance along the line.</summary>
    public static (double Distance, int Segment, double T, double Along) NearestOnLine(GeoPoint p, IReadOnlyList<GeoPoint> line)
    {
        var best = (Distance: double.MaxValue, Segment: 0, T: 0.0, Along: 0.0);
        var along = 0.0;
        for (var i = 1; i < line.Count; i++)
        {
            var (d, t) = DistanceToSegment(p, line[i - 1], line[i]);
            var segLen = line[i - 1].DistanceTo(line[i]);
            if (d < best.Distance)
                best = (d, i - 1, t, along + segLen * t);
            along += segLen;
        }
        return best;
    }

    /// <summary>Ray-casting point-in-polygon test (ring in degrees, open or closed).</summary>
    public static bool Contains(IReadOnlyList<GeoPoint> ring, GeoPoint p)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Lat > p.Lat) != (b.Lat > p.Lat) && p.Lon < (b.Lon - a.Lon) * (p.Lat - a.Lat) / (b.Lat - a.Lat) + a.Lon)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>True when segments a1–a2 and b1–b2 cross or touch (planar test on lat/lon).</summary>
    public static bool SegmentsIntersect(GeoPoint a1, GeoPoint a2, GeoPoint b1, GeoPoint b2)
    {
        static double Cross(GeoPoint o, GeoPoint a, GeoPoint b) => (a.Lon - o.Lon) * (b.Lat - o.Lat) - (a.Lat - o.Lat) * (b.Lon - o.Lon);
        static bool OnSegment(GeoPoint p, GeoPoint q, GeoPoint r) =>
            Math.Min(p.Lon, r.Lon) <= q.Lon && q.Lon <= Math.Max(p.Lon, r.Lon) && Math.Min(p.Lat, r.Lat) <= q.Lat && q.Lat <= Math.Max(p.Lat, r.Lat);
        var d1 = Cross(b1, b2, a1);
        var d2 = Cross(b1, b2, a2);
        var d3 = Cross(a1, a2, b1);
        var d4 = Cross(a1, a2, b2);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;
        return (d1 == 0 && OnSegment(b1, a1, b2)) || (d2 == 0 && OnSegment(b1, a2, b2)) ||
               (d3 == 0 && OnSegment(a1, b1, a2)) || (d4 == 0 && OnSegment(a1, b2, a2));
    }

    /// <summary>True when a polyline crosses or lies within <paramref name="toleranceMeters"/> of another polyline.</summary>
    public static bool LinesTouch(IReadOnlyList<GeoPoint> a, IReadOnlyList<GeoPoint> b, double toleranceMeters)
    {
        for (var i = 1; i < a.Count; i++)
            for (var j = 1; j < b.Count; j++)
                if (SegmentsIntersect(a[i - 1], a[i], b[j - 1], b[j]))
                    return true;
        if (toleranceMeters <= 0)
            return false;
        foreach (var p in a)
            if (b.Count > 1 && NearestOnLine(p, b).Distance <= toleranceMeters)
                return true;
        foreach (var p in b)
            if (a.Count > 1 && NearestOnLine(p, a).Distance <= toleranceMeters)
                return true;
        return false;
    }

    public static GeoPoint Centroid(IReadOnlyList<GeoPoint> points)
    {
        if (points.Count == 0)
            return default;
        double lat = 0, lon = 0;
        var n = points.Count;
        if (n > 1 && points[0] == points[n - 1])
            n--;
        for (var i = 0; i < n; i++)
        {
            lat += points[i].Lat;
            lon += points[i].Lon;
        }
        return new GeoPoint(lat / n, lon / n);
    }

    // ------------------------------------------------------------------ planar (tile building)

    /// <summary>Douglas–Peucker simplification; keeps the end points.</summary>
    public static List<Vec> Simplify(IReadOnlyList<Vec> points, double tolerance)
    {
        if (points.Count <= 2 || tolerance <= 0)
            return points.ToList();
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, points.Count - 1));
        var tol2 = tolerance * tolerance;
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            var maxD = 0.0;
            var index = -1;
            for (var i = first + 1; i < last; i++)
            {
                var d = SegmentDistance2(points[i], points[first], points[last]);
                if (d > maxD)
                {
                    maxD = d;
                    index = i;
                }
            }
            if (index >= 0 && maxD > tol2)
            {
                keep[index] = true;
                stack.Push((first, index));
                stack.Push((index, last));
            }
        }
        var result = new List<Vec>();
        for (var i = 0; i < points.Count; i++)
            if (keep[i])
                result.Add(points[i]);
        return result;
    }

    private static double SegmentDistance2(Vec p, Vec a, Vec b)
    {
        var ab = b - a;
        var len2 = ab.X * ab.X + ab.Y * ab.Y;
        var t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
        var c = a + ab * t;
        var dx = p.X - c.X;
        var dy = p.Y - c.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>Clips a polyline to a rectangle; returns the pieces inside (Liang–Barsky per segment, pieces joined).</summary>
    public static List<List<Vec>> ClipLine(IReadOnlyList<Vec> line, double minX, double minY, double maxX, double maxY)
    {
        var pieces = new List<List<Vec>>();
        List<Vec>? current = null;
        for (var i = 1; i < line.Count; i++)
        {
            var a = line[i - 1];
            var b = line[i];
            if (!ClipSegment(ref a, ref b, minX, minY, maxX, maxY, out var clippedStart, out var clippedEnd))
            {
                current = null;
                continue;
            }
            if (current == null || clippedStart)
            {
                current = new List<Vec> { a };
                pieces.Add(current);
            }
            current.Add(b);
            if (clippedEnd)
                current = null;
        }
        return pieces;
    }

    private static bool ClipSegment(ref Vec a, ref Vec b, double minX, double minY, double maxX, double maxY, out bool clippedStart, out bool clippedEnd)
    {
        clippedStart = clippedEnd = false;
        double t0 = 0, t1 = 1;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        bool Test(double p, double q)
        {
            if (p == 0)
                return q >= 0;
            var r = q / p;
            if (p < 0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }
        if (!Test(-dx, a.X - minX) || !Test(dx, maxX - a.X) || !Test(-dy, a.Y - minY) || !Test(dy, maxY - a.Y))
            return false;
        var start = a;
        if (t1 < 1)
        {
            b = new Vec(start.X + t1 * dx, start.Y + t1 * dy);
            clippedEnd = true;
        }
        if (t0 > 0)
        {
            a = new Vec(start.X + t0 * dx, start.Y + t0 * dy);
            clippedStart = true;
        }
        return true;
    }

    /// <summary>Clips a polygon ring to a rectangle (Sutherland–Hodgman). Result may be empty.</summary>
    public static List<Vec> ClipPolygon(IReadOnlyList<Vec> ring, double minX, double minY, double maxX, double maxY)
    {
        var output = ring.ToList();
        if (output.Count > 1 && output[0] == output[^1])
            output.RemoveAt(output.Count - 1);
        output = ClipEdge(output, p => p.X >= minX, (a, b) => Intersect(a, b, minX, true));
        output = ClipEdge(output, p => p.X <= maxX, (a, b) => Intersect(a, b, maxX, true));
        output = ClipEdge(output, p => p.Y >= minY, (a, b) => Intersect(a, b, minY, false));
        output = ClipEdge(output, p => p.Y <= maxY, (a, b) => Intersect(a, b, maxY, false));
        return output;
    }

    private static List<Vec> ClipEdge(List<Vec> input, Func<Vec, bool> inside, Func<Vec, Vec, Vec> intersect)
    {
        var output = new List<Vec>(input.Count + 4);
        if (input.Count == 0)
            return output;
        var prev = input[^1];
        foreach (var cur in input)
        {
            if (inside(cur))
            {
                if (!inside(prev))
                    output.Add(intersect(prev, cur));
                output.Add(cur);
            }
            else if (inside(prev))
            {
                output.Add(intersect(prev, cur));
            }
            prev = cur;
        }
        return output;
    }

    private static Vec Intersect(Vec a, Vec b, double value, bool vertical)
    {
        if (vertical)
        {
            var t = (value - a.X) / (b.X - a.X);
            return new Vec(value, a.Y + t * (b.Y - a.Y));
        }
        var u = (value - a.Y) / (b.Y - a.Y);
        return new Vec(a.X + u * (b.X - a.X), value);
    }

    /// <summary>Signed area of a planar ring (positive = counter-clockwise in a y-up system).</summary>
    public static double SignedArea(IReadOnlyList<Vec> ring)
    {
        var a = 0.0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            a += (ring[j].X * ring[i].Y) - (ring[i].X * ring[j].Y);
        return a / 2;
    }
}
