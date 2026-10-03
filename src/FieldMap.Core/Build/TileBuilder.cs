using System.Globalization;
using FieldMap.Core.Geo;
using FieldMap.Core.Tiles;

namespace FieldMap.Core.Build;

/// <summary>
/// The vector-tile schema written by <see cref="TileBuilder"/> and drawn by the map control. Layer and property names are
/// part of the data format: change them only together with the renderer (and bump <see cref="Version"/>).
/// </summary>
public static class TileSchema
{
    public const int Version = 1;
    public const int MaxZoom = 14;
    public const int Extent = 4096;

    /// <summary>Polygons: kind=water.</summary>
    public const string Water = "water";
    /// <summary>Lines: kind=river|stream|canal|coastline, name.</summary>
    public const string Waterways = "waterways";
    /// <summary>Polygons: kind=park|glacier|wetland|residential|commercial|industrial|military|airport, name.</summary>
    public const string Landuse = "landuse";
    /// <summary>Polygons: kind=avalanche|rockfall|hazard, name.</summary>
    public const string Hazards = "hazards";
    /// <summary>Lines: class=motorway|trunk|primary|secondary|tertiary|minor|service|track|path, name, ref, bridge, tunnel, oneway, seasonal, unpaved, link.</summary>
    public const string Roads = "roads";
    /// <summary>Lines: class=rail|service, name.</summary>
    public const string Rail = "rail";
    /// <summary>Lines: admin_level.</summary>
    public const string Boundaries = "boundaries";
    /// <summary>Points: name, kind, rank (1 city … 5 locality), population.</summary>
    public const string Places = "places";

    public static string RoadClass(string highway) => highway switch
    {
        "motorway" or "motorway_link" => "motorway",
        "trunk" or "trunk_link" => "trunk",
        "primary" or "primary_link" => "primary",
        "secondary" or "secondary_link" => "secondary",
        "tertiary" or "tertiary_link" => "tertiary",
        "unclassified" or "residential" or "living_street" or "road" or "busway" => "minor",
        "service" => "service",
        "track" => "track",
        _ => "path"
    };

    public static int RoadMinZoom(string cls) => cls switch
    {
        "motorway" or "trunk" => 5,
        "primary" => 7,
        "secondary" => 9,
        "tertiary" => 10,
        "minor" => 12,
        "track" => 12,
        "service" => 13,
        "ferry" => 8,
        _ => 14
    };
}

/// <summary>Turns a <see cref="MapData"/> into MVT tiles (zoom 0–14) in an MBTiles file.</summary>
public sealed class TileBuilder
{
    private sealed class Feature
    {
        public required string Layer;
        public GeomType Type;
        public List<Vec[]> Parts = new();
        public Dictionary<string, object> Props = new();
        public int MinZoom;
        public int MaxZoom = TileSchema.MaxZoom;
        /// <summary>Polygons: the first OuterCount parts are outer rings, the rest holes.</summary>
        public int OuterCount = 1;
        public double MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue;

        public void ComputeBounds()
        {
            foreach (var part in Parts)
                foreach (var p in part)
                {
                    if (p.X < MinX) MinX = p.X;
                    if (p.Y < MinY) MinY = p.Y;
                    if (p.X > MaxX) MaxX = p.X;
                    if (p.Y > MaxY) MaxY = p.Y;
                }
        }
    }

    private readonly List<Feature> _features = new();

    /// <summary>Layer draw order inside each tile (bottom first).</summary>
    private static readonly string[] LayerOrder =
        { TileSchema.Landuse, TileSchema.Water, TileSchema.Hazards, TileSchema.Waterways, TileSchema.Boundaries, TileSchema.Rail, TileSchema.Roads, TileSchema.Places };

    public int TileCount { get; private set; }


    public static int BuildMbTiles(MapData data, string path, IDictionary<string, string> metadata, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var b = new TileBuilder();
        b.Load(data);
        using var writer = new MbTilesWriter(path);
        var meta = new Dictionary<string, string>(metadata)
        {
            ["format"] = "pbf",
            ["minzoom"] = "0",
            ["maxzoom"] = TileSchema.MaxZoom.ToString(CultureInfo.InvariantCulture),
            ["schema_version"] = TileSchema.Version.ToString(CultureInfo.InvariantCulture),
            ["type"] = "baselayer"
        };
        if (!data.Bounds.IsEmpty)
        {
            var bb = data.Bounds;
            meta["bounds"] = string.Create(CultureInfo.InvariantCulture, $"{bb.MinLon:0.######},{bb.MinLat:0.######},{bb.MaxLon:0.######},{bb.MaxLat:0.######}");
            meta["center"] = string.Create(CultureInfo.InvariantCulture, $"{bb.Center.Lon:0.######},{bb.Center.Lat:0.######},10");
        }
        meta["json"] = "{\"vector_layers\":[" + string.Join(",", LayerOrder.Select(l => $"{{\"id\":\"{l}\",\"fields\":{{}}}}")) + "]}";
        writer.SetMetadata(meta);
        b.Generate(writer, progress, cancel);
        return writer.TileCount;
    }

    // ------------------------------------------------------------------ features

    private static Vec[] World(IReadOnlyList<GeoPoint> pts)
    {
        var a = new Vec[pts.Count];
        for (var i = 0; i < a.Length; i++)
        {
            var (x, y) = WebMercator.ToWorld(pts[i].Lat, pts[i].Lon);
            a[i] = new Vec(x, y);
        }
        return a;
    }

    private static int AreaMinZoom(double m2, int floor) => Math.Max(floor, m2 switch
    {
        > 100e6 => 4,
        > 10e6 => 6,
        > 1e6 => 8,
        > 0.1e6 => 10,
        _ => 12
    });

    private void Add(Feature f)
    {
        f.ComputeBounds();
        if (f.Parts.Count > 0)
            _features.Add(f);
    }

    /// <summary>Converts the map data to tile features. Road geometry is released as it is converted (to save memory on
    /// large extracts), so build the routing graph and search index first.</summary>
    internal void Load(MapData d)
    {
        foreach (var w in d.Roads)
        {
            var t = w.Tags;
            var highway = t["highway"] ?? "";
            var cls = t.Is("route", "ferry") ? "ferry" : TileSchema.RoadClass(highway);
            var props = new Dictionary<string, object> { ["class"] = cls };
            if (t["name"] is { } name) props["name"] = name;
            if (t["ref"] is { } rf) props["ref"] = rf;
            if (t["bridge"] is { } b && b != "no") props["bridge"] = true;
            if (t["tunnel"] is { } tu && tu != "no") props["tunnel"] = true;
            if (t.IsYes("oneway") || t.Is("oneway", "-1")) props["oneway"] = t.Is("oneway", "-1") ? -1L : 1L;
            if (highway.EndsWith("_link", StringComparison.Ordinal)) props["link"] = true;
            if (t.IsYes("seasonal") || t.IsYes("winter_road") || t.IsYes("ice_road") || (t["name"] ?? "").Contains("FSR", StringComparison.OrdinalIgnoreCase) ||
                (t["name"] ?? "").Contains("Forest Service", StringComparison.OrdinalIgnoreCase))
                props["seasonal"] = true;
            if (t["surface"] is "gravel" or "unpaved" or "dirt" or "ground" or "compacted" or "fine_gravel")
                props["unpaved"] = true;
            Add(new Feature { Layer = TileSchema.Roads, Type = GeomType.Line, Parts = { World(w.Points) }, Props = props, MinZoom = TileSchema.RoadMinZoom(cls) });
            w.Points = Array.Empty<GeoPoint>();
        }
        foreach (var w in d.Rail)
        {
            var service = w.Tags.Has("service");
            var props = new Dictionary<string, object> { ["class"] = service ? "service" : "rail" };
            if (w.Tags["name"] is { } name) props["name"] = name;
            Add(new Feature { Layer = TileSchema.Rail, Type = GeomType.Line, Parts = { World(w.Points) }, Props = props, MinZoom = service ? 13 : 9 });
        }
        foreach (var w in d.Waterways)
        {
            var kind = w.Tags["waterway"]!;
            var props = new Dictionary<string, object> { ["kind"] = kind };
            if (w.Tags["name"] is { } name) props["name"] = name;
            Add(new Feature { Layer = TileSchema.Waterways, Type = GeomType.Line, Parts = { World(w.Points) }, Props = props, MinZoom = kind == "river" ? 8 : kind == "canal" ? 10 : 12 });
        }
        foreach (var w in d.Coastlines)
            Add(new Feature { Layer = TileSchema.Waterways, Type = GeomType.Line, Parts = { World(w.Points) }, Props = { ["kind"] = "coastline" }, MinZoom = 4 });
        foreach (var w in d.Boundaries)
        {
            var level = w.Tags["admin_level"] ?? "4";
            Add(new Feature { Layer = TileSchema.Boundaries, Type = GeomType.Line, Parts = { World(w.Points) }, Props = { ["admin_level"] = level }, MinZoom = 0 });
        }
        foreach (var a in d.Water)
            AddArea(TileSchema.Water, a, AreaMinZoom(a.AreaM2, 4));
        foreach (var a in d.Landuse)
        {
            var floor = a.Kind switch { "park" or "glacier" => 6, "residential" or "commercial" or "industrial" => a.AreaM2 > 5e6 ? 8 : 10, "wetland" => 11, _ => 10 };
            AddArea(TileSchema.Landuse, a, AreaMinZoom(a.AreaM2, floor));
        }
        foreach (var a in d.Hazards)
            AddArea(TileSchema.Hazards, a, 10);
        foreach (var p in d.Places)
        {
            var minZoom = p.Kind switch { "city" => 4, "town" => 7, "village" => 10, "hamlet" or "suburb" => 12, _ => 13 };
            var (x, y) = WebMercator.ToWorld(p.Lat, p.Lon);
            Add(new Feature
            {
                Layer = TileSchema.Places, Type = GeomType.Point, Parts = { new[] { new Vec(x, y) } }, MinZoom = minZoom,
                Props = { ["name"] = p.Name, ["kind"] = p.Kind, ["rank"] = (long)p.Rank, ["population"] = p.Population }
            });
        }
    }

    private void AddArea(string layer, MapArea a, int minZoom)
    {
        var f = new Feature { Layer = layer, Type = GeomType.Polygon, MinZoom = minZoom, Props = { ["kind"] = a.Kind } };
        if (a.Name is { } name)
            f.Props["name"] = name;
        foreach (var o in a.Outers)
            f.Parts.Add(World(o));
        f.OuterCount = a.Outers.Count;
        foreach (var i in a.Inners)
            f.Parts.Add(World(i));
        Add(f);
    }

    // ------------------------------------------------------------------ tiles

    /// <summary>Zoom whose tiles are the units of progress (fine enough that the bar visibly moves on a province).</summary>
    private const int ProgressZoom = 9;

    /// <summary>Zoom from which subtrees are built in parallel.</summary>
    private const int ParallelZoom = 6;

    private void Generate(MbTilesWriter writer, IProgress<double>? progress, CancellationToken cancel)
    {
        // Progress: z9 tiles finished, out of the z9 tiles touched by small features. Big features (a province boundary,
        // a large lake) span thousands of cells, so they'd make the estimate meaningless; they are left out of it.
        var n = 1 << ProgressZoom;
        var cells = new HashSet<(int, int)>();
        foreach (var f in _features)
        {
            int x0 = Cell(f.MinX), x1 = Cell(f.MaxX), y0 = Cell(f.MinY), y1 = Cell(f.MaxY);
            if (x1 - x0 > 2 || y1 - y0 > 2)
                continue;
            for (var x = x0; x <= x1 && cells.Count < 2_000_000; x++)
                for (var y = y0; y <= y1; y++)
                    cells.Add((x, y));
        }
        int Cell(double v) => Math.Clamp((int)(v * n), 0, n - 1);
        var total = Math.Max(1, cells.Count);
        var done = 0;
        var gate = new object();

        // Reported under the lock so the bar only ever moves forward, whichever thread finishes first.
        void Advance()
        {
            lock (gate)
                progress?.Report(Math.Min(1, (double)++done / total));
        }

        void Write(int z, int x, int y, List<Feature> inTile)
        {
            var tile = BuildTile(z, WebMercator.TileBounds(z, x, y), inTile);
            if (tile.Layers.Count == 0)
                return;
            var bytes = VectorTile.Gzip(tile.Encode());
            lock (gate)
            {
                writer.Add(z, x, y, bytes);
                TileCount++;
            }
        }

        // Each tile only sees its parent's features clipped to the parent tile, so the work per tile follows the
        // geometry inside it – not the full size of every big feature whose bounding box happens to cover it.
        void Tile(int z, int x, int y, List<Feature> candidates, List<(int X, int Y, List<Feature> Features)>? defer)
        {
            cancel.ThrowIfCancellationRequested();
            var inTile = Clip(z, x, y, candidates);
            if (inTile.Count == 0)
            {
                if (z == ProgressZoom && cells.Contains((x, y)))
                    Advance();
                return;
            }
            if (defer != null && z == ParallelZoom)
            {
                defer.Add((x, y, inTile));
                return;
            }
            Write(z, x, y, inTile);
            if (z < TileSchema.MaxZoom)
                for (var dy = 0; dy < 2; dy++)
                    for (var dx = 0; dx < 2; dx++)
                        Tile(z + 1, x * 2 + dx, y * 2 + dy, inTile, defer);
            if (z == ProgressZoom && cells.Contains((x, y)))
                Advance();
        }

        // z0–z5 in order, then the z6 subtrees on all but one core (the app stays responsive).
        var subtrees = new List<(int X, int Y, List<Feature> Features)>();
        Tile(0, 0, 0, _features, subtrees);
        Parallel.ForEach(subtrees,
            new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            s =>
            {
                Write(ParallelZoom, s.X, s.Y, s.Features);
                for (var dy = 0; dy < 2; dy++)
                    for (var dx = 0; dx < 2; dx++)
                        Tile(ParallelZoom + 1, s.X * 2 + dx, s.Y * 2 + dy, s.Features, null);
            });
        progress?.Report(1);
    }

    /// <summary>The features that reach into tile z/x/y (plus its buffer), cut down to that area.</summary>
    private static List<Feature> Clip(int z, int x, int y, List<Feature> candidates)
    {
        var (minX, minY, maxX, maxY) = WebMercator.TileBounds(z, x, y);
        var buffer = (maxX - minX) * 64 / TileSchema.Extent;
        double bx0 = minX - buffer, by0 = minY - buffer, bx1 = maxX + buffer, by1 = maxY + buffer;
        var result = new List<Feature>();
        foreach (var f in candidates)
        {
            if (f.MaxX < bx0 || f.MinX > bx1 || f.MaxY < by0 || f.MinY > by1)
                continue;
            if (f.MinX >= bx0 && f.MaxX <= bx1 && f.MinY >= by0 && f.MaxY <= by1)
            {
                result.Add(f); // entirely inside: share it, no copy
                continue;
            }
            var c = new Feature { Layer = f.Layer, Type = f.Type, Props = f.Props, MinZoom = f.MinZoom, MaxZoom = f.MaxZoom };
            switch (f.Type)
            {
                case GeomType.Point:
                    foreach (var part in f.Parts)
                        if (part[0].X >= bx0 && part[0].X <= bx1 && part[0].Y >= by0 && part[0].Y <= by1)
                            c.Parts.Add(part);
                    break;
                case GeomType.Line:
                    foreach (var part in f.Parts)
                        foreach (var piece in Geometry.ClipLine(part, bx0, by0, bx1, by1))
                            if (piece.Count >= 2)
                                c.Parts.Add(piece.ToArray());
                    break;
                case GeomType.Polygon:
                {
                    var outers = 0;
                    for (var i = 0; i < f.OuterCount && i < f.Parts.Count; i++)
                        if (Geometry.ClipPolygon(f.Parts[i], bx0, by0, bx1, by1) is { Count: >= 3 } ring)
                        {
                            c.Parts.Add(ring.ToArray());
                            outers++;
                        }
                    if (outers == 0)
                        continue;
                    var holes = new List<Vec[]>();
                    var boxArea = (bx1 - bx0) * (by1 - by0);
                    bool Covers(Vec[] ring) => Math.Abs(Geometry.SignedArea(ring)) >= boxArea * 0.999;
                    for (var i = f.OuterCount; i < f.Parts.Count; i++)
                        if (Geometry.ClipPolygon(f.Parts[i], bx0, by0, bx1, by1) is { Count: >= 3 } hole)
                        {
                            var h = hole.ToArray();
                            if (!Covers(h))
                            {
                                holes.Add(h);
                                continue;
                            }
                            // The whole tile is inside this hole (an island in a lake): the outer ring around it
                            // contributes nothing here, so drop both rather than carry a cancelled-out square.
                            var before = c.Parts.Count;
                            c.Parts.RemoveAll(Covers);
                            outers -= before - c.Parts.Count;
                        }
                    if (outers == 0)
                        continue;
                    c.OuterCount = outers;
                    c.Parts.AddRange(holes);
                    break;
                }
            }
            if (c.Parts.Count == 0)
                continue;
            c.ComputeBounds();
            result.Add(c);
        }
        return result;
    }

    private static VectorTile BuildTile(int z, (double MinX, double MinY, double MaxX, double MaxY) bounds, List<Feature> features)
    {
        var (minX, minY, maxX, _) = bounds;
        var size = maxX - minX;
        var tile = new VectorTile();
        var layers = new Dictionary<string, VectorLayer>();
        var scale = TileSchema.Extent / size;
        const double buf = 64;
        var tolerance = z >= 12 ? 1.0 : 2.0;
        foreach (var f in features)
        {
            if (z < f.MinZoom || z > f.MaxZoom)
                continue;
            var parts = new List<Vec[]>();
            for (var pi = 0; pi < f.Parts.Count; pi++)
            {
                var part = f.Parts[pi];
                var local = new Vec[part.Length];
                for (var i = 0; i < part.Length; i++)
                    local[i] = new Vec((part[i].X - minX) * scale, (part[i].Y - minY) * scale);
                switch (f.Type)
                {
                    case GeomType.Point:
                        if (local[0].X >= 0 && local[0].X < TileSchema.Extent && local[0].Y >= 0 && local[0].Y < TileSchema.Extent)
                            parts.Add(local);
                        break;
                    case GeomType.Line:
                        foreach (var piece in Geometry.ClipLine(local, -buf, -buf, TileSchema.Extent + buf, TileSchema.Extent + buf))
                        {
                            var simple = Geometry.Simplify(piece, tolerance);
                            if (simple.Count >= 2 && LineLength(simple) >= 4)
                                parts.Add(simple.ToArray());
                        }
                        break;
                    case GeomType.Polygon:
                    {
                        var clipped = Geometry.ClipPolygon(local, -buf, -buf, TileSchema.Extent + buf, TileSchema.Extent + buf);
                        if (clipped.Count < 3)
                            break;
                        clipped.Add(clipped[0]);
                        var simple = Geometry.Simplify(clipped, tolerance);
                        if (simple.Count >= 4 && Math.Abs(ShoelaceArea(simple)) >= 16)
                            parts.Add(Orient(simple, exterior: pi < f.OuterCount));
                        break;
                    }
                }
            }
            if (parts.Count == 0)
                continue;
            // A polygon whose outer ring was dropped can't keep its holes.
            if (f.Type == GeomType.Polygon && ShoelaceArea(parts[0]) <= 0)
                continue;
            if (!layers.TryGetValue(f.Layer, out var layer))
                layers[f.Layer] = layer = new VectorLayer { Name = f.Layer, Extent = TileSchema.Extent };
            layer.Features.Add(new VectorFeature { Type = f.Type, Parts = parts, Props = f.Props });
        }
        foreach (var name in LayerOrder)
            if (layers.TryGetValue(name, out var l))
                tile.Layers.Add(l);
        return tile;
    }

    private static double LineLength(List<Vec> line)
    {
        var len = 0.0;
        for (var i = 1; i < line.Count; i++)
            len += (line[i] - line[i - 1]).Length;
        return len;
    }

    /// <summary>Shoelace area in tile coordinates (y down): positive for clockwise-on-screen rings, as MVT requires for exteriors.</summary>
    private static double ShoelaceArea(IReadOnlyList<Vec> ring)
    {
        var a = 0.0;
        for (var i = 0; i < ring.Count - 1; i++)
            a += ring[i].X * ring[i + 1].Y - ring[i + 1].X * ring[i].Y;
        return a / 2;
    }

    private static Vec[] Orient(List<Vec> ring, bool exterior)
    {
        var positive = ShoelaceArea(ring) > 0;
        if (positive != exterior)
            ring.Reverse();
        return ring.ToArray();
    }
}
