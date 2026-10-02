using FieldMap.Core.Build;
using FieldMap.Core.Drawings;
using FieldMap.Core.Layers;
using FieldMap.Core.Live;
using FieldMap.Core.Routing;
using SkiaSharp;

namespace FieldMap.Map;

/// <summary>An installed region's tiles and extent.</summary>
public sealed record TileSource(Core.Tiles.MbTilesReader Tiles, GeoBounds Bounds);

/// <summary>Everything a map frame shows besides the base map tiles.</summary>
public sealed class MapScene
{
    public IReadOnlyList<TileSource> Regions { get; init; } = Array.Empty<TileSource>();
    public required TileCache Tiles { get; init; }
    public MapStyle Style { get; init; } = MapStyle.Standard;
    public Func<string, bool> LayerOn { get; init; } = _ => true;
    public PoiIndex? Pois { get; init; }
    public Poi? SelectedPoi { get; init; }
    public IReadOnlyList<Drawing> Drawings { get; init; } = Array.Empty<Drawing>();
    public Drawing? Draft { get; init; }
    public Guid? SelectedDrawing { get; init; }
    public Route? Route { get; init; }
    public IReadOnlyList<Route> Alternatives { get; init; } = Array.Empty<Route>();
    public double RouteProgressMeters { get; init; }
    public GeoFix? Fix { get; init; }
    public bool FixStale { get; init; }
    public IReadOnlyList<GeoPoint>? Breadcrumbs { get; init; }
    public IReadOnlyList<LiveResult> Live { get; init; } = Array.Empty<LiveResult>();
    public GeoPoint? Marker { get; init; }
    public string Attribution { get; init; } = "";
    public bool Synchronous { get; init; }
    public bool ShowControls { get; init; } = true;
    public float UiScale { get; init; } = 1f;
}

/// <summary>Where things were drawn, for tap hit-testing.</summary>
public sealed class RenderResult
{
    public List<(SKRect Rect, Poi Poi)> Pois { get; } = new();
    public SKRect NorthArrow { get; set; }
    public bool TilesPending { get; set; }
}

/// <summary>Draws a map frame with Skia: base map tiles, live layers, route, drawings, POIs, labels, GPS and overlays.</summary>
public static class MapRenderer
{
    private static readonly SKTypeface Regular = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
    private static readonly SKTypeface Bold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;

    private static readonly string[] RoadOrder = { "path", "track", "service", "minor", "tertiary", "secondary", "primary", "trunk", "motorway", "ferry" };

    private sealed record VisibleTile(PreparedTile Tile, SKMatrix Matrix, float Scale);

    public static RenderResult Render(SKCanvas canvas, Viewport vp, MapScene scene)
    {
        var result = new RenderResult();
        var st = scene.Style;
        canvas.Clear(st.Background);
        var tiles = CollectTiles(vp, scene, result);

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

        // Areas
        foreach (var t in tiles)
        {
            canvas.SetMatrix(t.Matrix);
            foreach (var (key, path) in t.Tile.Fills)
                if (key.StartsWith("landuse:", StringComparison.Ordinal))
                {
                    fill.Color = st.LanduseColor(key[8..]);
                    canvas.DrawPath(path, fill);
                }
        }
        foreach (var t in tiles)
        {
            canvas.SetMatrix(t.Matrix);
            if (t.Tile.Fills.TryGetValue("water", out var water))
            {
                fill.Color = st.Water;
                canvas.DrawPath(water, fill);
            }
            if (scene.LayerOn(LayerCatalog.Hazard) && t.Tile.Fills.TryGetValue("hazard", out var hz))
            {
                fill.Color = st.Hazard.WithAlpha(60);
                canvas.DrawPath(hz, fill);
                stroke.Color = st.Hazard;
                stroke.StrokeWidth = 2 / t.Scale;
                stroke.PathEffect = SKPathEffect.CreateDash(new[] { 8 / t.Scale, 5 / t.Scale }, 0);
                canvas.DrawPath(hz, stroke);
                stroke.PathEffect = null;
            }
        }

        // Lines: waterways, boundaries, roads (casings then fills), rail
        foreach (var t in tiles)
        {
            canvas.SetMatrix(t.Matrix);
            foreach (var (key, path) in t.Tile.Lines)
            {
                if (key.StartsWith("waterway:", StringComparison.Ordinal))
                {
                    var kind = key[9..];
                    stroke.Color = st.WaterLine;
                    stroke.StrokeWidth = (kind switch { "river" => Math.Clamp((float)(vp.Zoom - 6) * 0.8f, 1f, 6f), "coastline" => 1.5f, "canal" => 2f, _ => vp.Zoom >= 14 ? 1.5f : 1f }) / t.Scale;
                    canvas.DrawPath(path, stroke);
                }
                else if (key == "boundary")
                {
                    stroke.Color = st.Boundary;
                    stroke.StrokeWidth = 2 / t.Scale;
                    stroke.PathEffect = SKPathEffect.CreateDash(new[] { 10 / t.Scale, 4 / t.Scale, 2 / t.Scale, 4 / t.Scale }, 0);
                    canvas.DrawPath(path, stroke);
                    stroke.PathEffect = null;
                }
            }
        }
        DrawRoads(canvas, vp, scene, tiles, stroke);
        foreach (var t in tiles)
        {
            canvas.SetMatrix(t.Matrix);
            foreach (var (key, path) in t.Tile.Lines)
                if (key.StartsWith("rail:", StringComparison.Ordinal))
                {
                    var w = (key == "rail:rail" ? 2f : 1.2f) / t.Scale;
                    stroke.Color = st.Rail;
                    stroke.StrokeWidth = w;
                    canvas.DrawPath(path, stroke);
                    if (vp.Zoom >= 13)
                    {
                        stroke.Color = st.Background;
                        stroke.StrokeWidth = w * 0.5f;
                        stroke.PathEffect = SKPathEffect.CreateDash(new[] { 6 / t.Scale, 6 / t.Scale }, 0);
                        canvas.DrawPath(path, stroke);
                        stroke.PathEffect = null;
                    }
                }
        }
        canvas.ResetMatrix();

        var occupied = new List<SKRect>();
        DrawLive(canvas, vp, scene);
        DrawBreadcrumbsAndRoutes(canvas, vp, scene);
        DrawDrawings(canvas, vp, scene, scene.Drawings, false);
        if (scene.Draft != null)
            DrawDrawings(canvas, vp, scene, new[] { scene.Draft }, true);
        DrawPlaceLabels(canvas, vp, scene, tiles, occupied, 1, 2); // cities and towns first
        DrawPois(canvas, vp, scene, occupied, result);
        DrawPlaceLabels(canvas, vp, scene, tiles, occupied, 3, 9);
        DrawRoadLabels(canvas, vp, scene, tiles, occupied);
        DrawMarkerAndGps(canvas, vp, scene);
        if (scene.ShowControls)
            DrawOverlays(canvas, vp, scene, result);
        return result;
    }

    // ------------------------------------------------------------------ tiles

    private static List<VisibleTile> CollectTiles(Viewport vp, MapScene scene, RenderResult result)
    {
        var list = new List<VisibleTile>();
        var (minX, minY, maxX, maxY) = vp.VisibleWorld();
        for (var r = 0; r < scene.Regions.Count; r++)
        {
            var region = scene.Regions[r];
            var reader = region.Tiles;
            var z = (int)Math.Clamp(Math.Floor(vp.Zoom + 0.25), reader.MinZoom, reader.MaxZoom);
            var n = 1 << z;
            var x0 = Math.Max(0, (int)Math.Floor(minX * n));
            var x1 = Math.Min(n - 1, (int)Math.Floor(maxX * n));
            var y0 = Math.Max(0, (int)Math.Floor(minY * n));
            var y1 = Math.Min(n - 1, (int)Math.Floor(maxY * n));
            // Skip regions far outside the view.
            var b = region.Bounds;
            var (rx0, ry0) = WebMercator.ToWorld(b.MaxLat, b.MinLon);
            var (rx1, ry1) = WebMercator.ToWorld(b.MinLat, b.MaxLon);
            if (rx1 < minX || rx0 > maxX || ry1 < minY || ry0 > maxY)
                continue;
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 400)
                continue;
            var seen = new HashSet<(int, int, int)>();
            for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                {
                    var tile = scene.Synchronous ? scene.Tiles.LoadNow(r, reader, z, x, y) : Lookup(scene.Tiles, r, reader, z, x, y, result);
                    if (tile == null && !scene.Synchronous)
                    {
                        // Not loaded yet: draw the nearest loaded parent so the map doesn't flash empty.
                        for (var pz = z - 1; pz >= Math.Max(reader.MinZoom, z - 4) && tile == null; pz--)
                        {
                            var shift = z - pz;
                            if (scene.Tiles.TryGet(r, reader, pz, x >> shift, y >> shift, false, out var parent) && parent != null && seen.Add((pz, x >> shift, y >> shift)))
                                tile = parent;
                        }
                        if (tile == null)
                            continue;
                    }
                    if (tile == null)
                        continue;
                    var tn = 1 << tile.Z;
                    var (sx, sy) = vp.WorldToScreen((double)tile.X / tn, (double)tile.Y / tn);
                    var scale = (float)(vp.Scale / tn / 4096.0);
                    var m = SKMatrix.CreateTranslation((float)sx, (float)sy)
                        .PreConcat(SKMatrix.CreateRotationDegrees((float)-vp.Bearing))
                        .PreConcat(SKMatrix.CreateScale(scale, scale));
                    list.Add(new VisibleTile(tile, m, scale));
                }
        }
        // Parents first so children draw over them.
        return list.OrderBy(t => t.Tile.Z).ToList();
    }

    private static PreparedTile? Lookup(TileCache cache, int region, Core.Tiles.MbTilesReader reader, int z, int x, int y, RenderResult result)
    {
        if (cache.TryGet(region, reader, z, x, y, true, out var tile))
            return tile;
        result.TilesPending = true;
        return null;
    }

    private static void DrawRoads(SKCanvas canvas, Viewport vp, MapScene scene, List<VisibleTile> tiles, SKPaint stroke)
    {
        var st = scene.Style;
        var highlightAccess = scene.LayerOn(LayerCatalog.Access);
        var casingOn = vp.Zoom >= 11;
        // Casings, lowest class first.
        if (casingOn)
            foreach (var cls in RoadOrder)
                foreach (var t in tiles)
                {
                    canvas.SetMatrix(t.Matrix);
                    foreach (var (key, path) in t.Tile.Lines)
                    {
                        if (!IsRoad(key, cls, out var flags) || cls is "path" or "ferry")
                            continue;
                        var w = MapStyle.RoadWidth(cls, vp.Zoom) + (flags.Contains('b') ? 3.5f : 2f);
                        stroke.Color = st.Casing;
                        stroke.StrokeWidth = w / t.Scale;
                        stroke.PathEffect = flags.Contains('t') ? SKPathEffect.CreateDash(new[] { 6 / t.Scale, 4 / t.Scale }, 0) : null;
                        canvas.DrawPath(path, stroke);
                    }
                }
        stroke.PathEffect = null;
        foreach (var cls in RoadOrder)
            foreach (var t in tiles)
            {
                canvas.SetMatrix(t.Matrix);
                foreach (var (key, path) in t.Tile.Lines)
                {
                    if (!IsRoad(key, cls, out var flags))
                        continue;
                    var w = MapStyle.RoadWidth(cls, vp.Zoom);
                    var color = st.RoadColor(cls);
                    if (flags.Contains('t'))
                        color = color.WithAlpha(150);
                    stroke.Color = color;
                    stroke.StrokeWidth = w / t.Scale;
                    stroke.PathEffect = cls == "ferry" ? SKPathEffect.CreateDash(new[] { 10 / t.Scale, 6 / t.Scale }, 0)
                        : cls == "path" ? SKPathEffect.CreateDash(new[] { 3 / t.Scale, 3 / t.Scale }, 0)
                        : flags.Contains('u') && vp.Zoom >= 13 ? SKPathEffect.CreateDash(new[] { 9 / t.Scale, 3 / t.Scale }, 0)
                        : null;
                    canvas.DrawPath(path, stroke);
                    if (highlightAccess && flags.Contains('s'))
                    {
                        stroke.Color = st.Seasonal;
                        stroke.StrokeWidth = Math.Max(2.5f, w * 0.6f) / t.Scale;
                        stroke.PathEffect = SKPathEffect.CreateDash(new[] { 12 / t.Scale, 6 / t.Scale }, 0);
                        canvas.DrawPath(path, stroke);
                    }
                }
            }
        stroke.PathEffect = null;
    }

    private static bool IsRoad(string key, string cls, out string flags)
    {
        flags = "";
        if (!key.StartsWith("road:", StringComparison.Ordinal))
            return false;
        var second = key.IndexOf(':', 5);
        if (second < 0 || key.AsSpan(5, second - 5).SequenceEqual(cls) == false)
            return false;
        flags = key[(second + 1)..];
        return true;
    }

    // ------------------------------------------------------------------ helpers

    private static SKPoint S(Viewport vp, GeoPoint p)
    {
        var (x, y) = vp.GeoToScreen(p);
        return new SKPoint((float)x, (float)y);
    }

    private static SKPath ScreenPath(Viewport vp, IReadOnlyList<GeoPoint> pts, bool close)
    {
        var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        if (pts.Count == 0)
            return path;
        path.MoveTo(S(vp, pts[0]));
        for (var i = 1; i < pts.Count; i++)
            path.LineTo(S(vp, pts[i]));
        if (close)
            path.Close();
        return path;
    }

    private static SKFont Font(float size, bool bold = false) => new(bold ? Bold : Regular, size);

    /// <summary>Text with a contrasting halo (readable over any map colour).</summary>
    public static void HaloText(SKCanvas canvas, string text, float x, float y, SKFont font, SKColor color, SKColor halo, SKTextAlign align = SKTextAlign.Center)
    {
        using var haloPaint = new SKPaint { IsAntialias = true, Color = halo, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(3f, font.Size / 4), StrokeJoin = SKStrokeJoin.Round };
        using var paint = new SKPaint { IsAntialias = true, Color = color };
        canvas.DrawText(text, x, y, align, font, haloPaint);
        canvas.DrawText(text, x, y, align, font, paint);
    }

    private static bool Free(List<SKRect> occupied, SKRect r)
    {
        foreach (var o in occupied)
            if (o.IntersectsWith(r))
                return false;
        return true;
    }

    // ------------------------------------------------------------------ labels

    private static void DrawPlaceLabels(SKCanvas canvas, Viewport vp, MapScene scene, List<VisibleTile> tiles, List<SKRect> occupied, int minRank, int maxRank)
    {
        var st = scene.Style;
        var items = new List<(PointLabel Label, SKPoint P)>();
        foreach (var t in tiles)
            foreach (var p in t.Tile.Places)
                if (p.Rank >= minRank && p.Rank <= maxRank)
                    items.Add((p, t.Matrix.MapPoint(p.Position)));
        var drawn = new HashSet<string>();
        foreach (var (label, p) in items.OrderBy(i => i.Label.Rank).ThenByDescending(i => i.Label.Population))
        {
            if (label.Name.Length == 0 || p.X < -50 || p.Y < -20 || p.X > vp.Width + 50 || p.Y > vp.Height + 20 || !drawn.Add(label.Name))
                continue;
            var size = (label.Rank switch { 1 => 17f, 2 => 15f, 3 => 13.5f, _ => 12.5f }) * scene.UiScale;
            using var font = Font(size, label.Rank <= 2);
            var w = font.MeasureText(label.Name);
            var rect = new SKRect(p.X - w / 2 - 3, p.Y - size - 2, p.X + w / 2 + 3, p.Y + 4);
            if (!Free(occupied, rect))
                continue;
            occupied.Add(rect);
            HaloText(canvas, label.Name, p.X, p.Y, font, st.PlaceLabel, st.LabelHalo);
        }
    }

    private static void DrawRoadLabels(SKCanvas canvas, Viewport vp, MapScene scene, List<VisibleTile> tiles, List<SKRect> occupied)
    {
        var st = scene.Style;
        var placed = new List<(string Text, SKPoint P)>();
        var count = 0;
        using var font = Font(12f * scene.UiScale);
        using var shieldFont = Font(11.5f * scene.UiScale, true);
        foreach (var t in tiles.OrderByDescending(t => t.Tile.Z))
        {
            foreach (var label in t.Tile.RoadLabels.OrderBy(l => Array.IndexOf(RoadOrder, l.Class) * -1))
            {
                if (count > 220)
                    return;
                var minZoom = label.Class switch { "motorway" or "trunk" or "primary" => 11, "secondary" or "tertiary" => 13, "minor" or "track" => 15, _ => 16.5 };
                if (!label.IsRef && vp.Zoom < minZoom)
                    continue;
                if (label.IsRef && vp.Zoom < 8)
                    continue;
                var pts = label.Points.Select(p => t.Matrix.MapPoint(p)).ToArray();
                // Longest segment inside the screen.
                var best = -1;
                var bestLen = 0f;
                for (var i = 1; i < pts.Length; i++)
                {
                    var mid = new SKPoint((pts[i].X + pts[i - 1].X) / 2, (pts[i].Y + pts[i - 1].Y) / 2);
                    if (mid.X < 0 || mid.Y < 0 || mid.X > vp.Width || mid.Y > vp.Height)
                        continue;
                    var len = SKPoint.Distance(pts[i], pts[i - 1]);
                    if (len > bestLen)
                    {
                        bestLen = len;
                        best = i;
                    }
                }
                if (best < 0)
                    continue;
                var a = pts[best - 1];
                var b = pts[best];
                var c = new SKPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                var spacing = label.IsRef ? 260 : 320;
                if (placed.Any(p => p.Text == label.Text && SKPoint.Distance(p.P, c) < spacing * scene.UiScale))
                    continue;
                if (label.IsRef)
                {
                    var tw = shieldFont.MeasureText(label.Text);
                    var h = shieldFont.Size + 6;
                    var rect = new SKRect(c.X - tw / 2 - 6, c.Y - h / 2, c.X + tw / 2 + 6, c.Y + h / 2);
                    if (!Free(occupied, rect))
                        continue;
                    occupied.Add(rect);
                    using var back = new SKPaint { IsAntialias = true, Color = st.ShieldBack };
                    using var border = new SKPaint { IsAntialias = true, Color = st.Casing, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
                    using var text = new SKPaint { IsAntialias = true, Color = st.ShieldText };
                    canvas.DrawRoundRect(rect, 4, 4, back);
                    canvas.DrawRoundRect(rect, 4, 4, border);
                    canvas.DrawText(label.Text, c.X, c.Y + shieldFont.Size / 2 - 2, SKTextAlign.Center, shieldFont, text);
                }
                else
                {
                    var tw = font.MeasureText(label.Text);
                    if (bestLen < tw + 16)
                        continue;
                    var angle = (float)(Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI);
                    if (angle > 90) angle -= 180;
                    if (angle < -90) angle += 180;
                    var half = Math.Max(tw, font.Size) / 2 + 2;
                    var rect = new SKRect(c.X - half, c.Y - half, c.X + half, c.Y + half);
                    if (Math.Abs(angle) < 20)
                        rect = new SKRect(c.X - tw / 2 - 2, c.Y - font.Size, c.X + tw / 2 + 2, c.Y + 4);
                    if (!Free(occupied, rect))
                        continue;
                    occupied.Add(rect);
                    canvas.Save();
                    canvas.Translate(c.X, c.Y);
                    canvas.RotateDegrees(angle);
                    HaloText(canvas, label.Text, 0, font.Size / 2 - 2, font, st.Label, st.LabelHalo);
                    canvas.Restore();
                }
                placed.Add((label.Text, c));
                count++;
            }
        }
    }

    // ------------------------------------------------------------------ POIs

    private static void DrawPois(SKCanvas canvas, Viewport vp, MapScene scene, List<SKRect> occupied, RenderResult result)
    {
        if (scene.Pois == null)
            return;
        var bounds = vp.VisibleBounds().Expand(200);
        var layers = LayerCatalog.All.Where(l => scene.LayerOn(l.Id) && vp.Zoom >= l.MinZoom).ToList();
        if (layers.Count == 0)
            return;
        var order = layers.ToDictionary(l => l.Id, l => Array.IndexOf(SymbolPriority, l.Id) is var i and >= 0 ? i : 99);
        var radius = 11f * scene.UiScale * (vp.Zoom >= 16 ? 1.15f : 1f);
        using var font = Font(radius * 0.95f, true);
        using var smallFont = Font(radius * 0.72f, true);
        using var labelFont = Font(11.5f * scene.UiScale);
        using var fill = new SKPaint { IsAntialias = true };
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f };
        using var text = new SKPaint { IsAntialias = true };
        var drawn = 0;
        var symbolRects = new List<SKRect>();
        foreach (var poi in scene.Pois.Query(bounds).Where(p => order.ContainsKey(p.Layer)).OrderBy(p => order[p.Layer]))
        {
            if (drawn > 700)
                break;
            var layer = LayerCatalog.Find(poi.Layer)!;
            var p = S(vp, poi.Point);
            var rect = new SKRect(p.X - radius, p.Y - radius, p.X + radius, p.Y + radius);
            // Symbols may overlap a little so neighbours (a hospital and its AED) both show.
            var core = SKRect.Inflate(rect, -radius * 0.45f, -radius * 0.45f);
            if (!Free(symbolRects, core) && poi != scene.SelectedPoi)
                continue;
            symbolRects.Add(core);
            var color = scene.Style.Symbol(layer.Id == LayerCatalog.Hospital && poi.Kind == "ed" ? 0xFFE00000 : layer.Color);
            fill.Color = color;
            ring.Color = scene.Style == MapStyle.Daylight ? SKColors.Black : scene.Style.Casing == SKColors.Black && scene.Style != MapStyle.Night ? SKColors.White : new SKColor(0x30, 0x30, 0x30);
            if (layer.Id is LayerCatalog.Hospital or LayerCatalog.Aed)
                canvas.DrawRoundRect(rect, 4, 4, fill);
            else
                canvas.DrawCircle(p, radius, fill);
            if (layer.Id is LayerCatalog.Hospital or LayerCatalog.Aed)
                canvas.DrawRoundRect(rect, 4, 4, ring);
            else
                canvas.DrawCircle(p, radius, ring);
            var glyph = Glyph(poi.Layer, poi.Kind);
            var light = color.Red * 0.299 + color.Green * 0.587 + color.Blue * 0.114 > 150;
            text.Color = light ? SKColors.Black : (scene.Style == MapStyle.Night ? new SKColor(0xC4, 0xB4, 0x96) : SKColors.White);
            var f = glyph.Length > 1 ? smallFont : font;
            canvas.DrawText(glyph, p.X, p.Y + f.Size / 2.8f, SKTextAlign.Center, f, text);
            if (poi == scene.SelectedPoi)
            {
                ring.Color = scene.Style.Selection;
                ring.StrokeWidth = 4;
                canvas.DrawCircle(p, radius + 5, ring);
                ring.StrokeWidth = 2;
            }
            result.Pois.Add((SKRect.Inflate(rect, 6, 6), poi));
            drawn++;
            // Names at close zoom, if there's room.
            if (vp.Zoom >= 16 && poi.Name.Length > 0)
            {
                var tw = labelFont.MeasureText(poi.Name);
                var lr = new SKRect(p.X - tw / 2, p.Y + radius + 2, p.X + tw / 2, p.Y + radius + 4 + labelFont.Size);
                if (Free(occupied, lr))
                {
                    occupied.Add(lr);
                    HaloText(canvas, poi.Name, p.X, lr.Bottom - 2, labelFont, scene.Style.Label, scene.Style.LabelHalo);
                }
            }
        }
        occupied.AddRange(result.Pois.Select(x => SKRect.Inflate(x.Rect, -6, -6)));
    }

    /// <summary>Most important first: these win when symbols would overlap.</summary>
    private static readonly string[] SymbolPriority =
    {
        LayerCatalog.Hospital, LayerCatalog.Fire, LayerCatalog.Ambulance, LayerCatalog.Police, LayerCatalog.Aed, LayerCatalog.Air,
        LayerCatalog.Shelter, LayerCatalog.Hazard, LayerCatalog.Pharmacy, LayerCatalog.School, LayerCatalog.Access, LayerCatalog.Crossings,
        LayerCatalog.Fuel, LayerCatalog.Rest, LayerCatalog.Markers, LayerCatalog.Water
    };

    /// <summary>Plain-text symbol glyphs (no emoji, so they render with any font).</summary>
    public static string Glyph(string layer, string kind) => (layer, kind) switch
    {
        (LayerCatalog.Hospital, "ed") => "H+",
        (LayerCatalog.Hospital, _) => "H",
        (LayerCatalog.Air, "airport") => "Ap",
        (LayerCatalog.Air, _) => "Hp",
        (LayerCatalog.Fuel, _) => "Gas",
        (LayerCatalog.Shelter, "reception") => "RC",
        (LayerCatalog.Shelter, "assembly") => "AP",
        (LayerCatalog.Shelter, _) => "Sh",
        (LayerCatalog.Rest, "toilets") => "WC",
        (LayerCatalog.Rest, "pullout") => "P/O",
        (LayerCatalog.Rest, _) => "R",
        (LayerCatalog.Crossings, "bridge") => "Br",
        (LayerCatalog.Crossings, "tunnel") => "Tn",
        (LayerCatalog.Crossings, _) => "RR",
        (LayerCatalog.Water, "hydrant") => "W",
        (LayerCatalog.Water, _) => "WS",
        (LayerCatalog.Markers, "exit") => "Ex",
        _ => LayerCatalog.Find(layer)?.Glyph ?? "?"
    };

    // ------------------------------------------------------------------ live layers

    private static void DrawLive(SKCanvas canvas, Viewport vp, MapScene scene)
    {
        if (scene.Live.Count == 0)
            return;
        var st = scene.Style;
        var view = vp.VisibleBounds().Expand(1000);
        using var fill = new SKPaint { IsAntialias = true };
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, StrokeJoin = SKStrokeJoin.Round };
        using var font = Font(13f * scene.UiScale, true);
        foreach (var r in scene.Live)
            foreach (var item in r.Items)
            {
                if (item.Parts.Count == 0 || !item.Bounds.Intersects(view))
                    continue;
                var (color, alpha) = item.Kind switch
                {
                    LiveKind.FirePerimeter => (new SKColor(0xFF, 0x45, 0x00), (byte)70),
                    LiveKind.Evacuation => (item.Severity.Contains("Order", StringComparison.OrdinalIgnoreCase) ? new SKColor(0xE0, 0x00, 0x00) : new SKColor(0xFF, 0xC0, 0x00), (byte)60),
                    LiveKind.WeatherAlert => (new SKColor(0xB0, 0x60, 0xFF), (byte)30),
                    _ => (new SKColor(0xFF, 0x8C, 0x00), (byte)255)
                };
                color = st.Symbol(color);
                if (item.IsArea)
                {
                    using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                    foreach (var ring in item.Parts)
                        path.AddPath(ScreenPath(vp, ring, true));
                    fill.Color = color.WithAlpha(alpha);
                    canvas.DrawPath(path, fill);
                    line.Color = color;
                    canvas.DrawPath(path, line);
                }
                else
                {
                    foreach (var part in item.Parts)
                    {
                        if (part.Count >= 2)
                        {
                            using var path = ScreenPath(vp, part, false);
                            line.Color = color;
                            line.StrokeWidth = 5;
                            canvas.DrawPath(path, line);
                            line.StrokeWidth = 2.5f;
                        }
                        // Warning triangle at the first point.
                        var p = S(vp, part[0]);
                        var s = 11f * scene.UiScale;
                        using var tri = new SKPath();
                        tri.MoveTo(p.X, p.Y - s);
                        tri.LineTo(p.X + s, p.Y + s * 0.8f);
                        tri.LineTo(p.X - s, p.Y + s * 0.8f);
                        tri.Close();
                        fill.Color = color;
                        canvas.DrawPath(tri, fill);
                        line.Color = SKColors.Black;
                        line.StrokeWidth = 1.5f;
                        canvas.DrawPath(tri, line);
                        line.StrokeWidth = 2.5f;
                        using var tp = new SKPaint { IsAntialias = true, Color = SKColors.Black };
                        canvas.DrawText("!", p.X, p.Y + s * 0.6f, SKTextAlign.Center, font, tp);
                    }
                }
            }
    }

    // ------------------------------------------------------------------ route and trail

    private static void DrawBreadcrumbsAndRoutes(SKCanvas canvas, Viewport vp, MapScene scene)
    {
        var st = scene.Style;
        if (scene.Breadcrumbs is { Count: > 1 } crumbs)
        {
            using var path = ScreenPath(vp, crumbs, false);
            using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f * scene.UiScale, Color = st.Breadcrumb.WithAlpha(200), StrokeJoin = SKStrokeJoin.Round, PathEffect = SKPathEffect.CreateDash(new[] { 2f, 6f }, 0), StrokeCap = SKStrokeCap.Round };
            canvas.DrawPath(path, p);
        }
        foreach (var alt in scene.Alternatives)
        {
            using var path = ScreenPath(vp, alt.Geometry, false);
            using var casing = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 9 * scene.UiScale, Color = st.Casing, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6 * scene.UiScale, Color = st.RouteAlt, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
            canvas.DrawPath(path, casing);
            canvas.DrawPath(path, line);
        }
        if (scene.Route is { } route && route.Geometry.Count > 1)
        {
            using var path = ScreenPath(vp, route.Geometry, false);
            using var casing = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 12 * scene.UiScale, Color = st.Casing, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 8 * scene.UiScale, Color = st.Route, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
            canvas.DrawPath(path, casing);
            canvas.DrawPath(path, line);
            // Destination flag
            var end = S(vp, route.Geometry[^1]);
            using var flag = new SKPaint { IsAntialias = true, Color = st.Symbol(new SKColor(0xE0, 0x10, 0x10)) };
            canvas.DrawCircle(end, 9 * scene.UiScale, flag);
            using var white = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
            canvas.DrawCircle(end, 9 * scene.UiScale, white);
        }
    }

    // ------------------------------------------------------------------ drawings

    public static SKColor DrawingColor(DrawingKind kind) => kind switch
    {
        DrawingKind.Incident => new SKColor(0xE0, 0x10, 0x10),
        DrawingKind.RoadClosure or DrawingKind.Cordon => new SKColor(0xFF, 0x20, 0x20),
        DrawingKind.Hazmat => new SKColor(0xFF, 0x8C, 0x00),
        DrawingKind.Staging or DrawingKind.Rendezvous => new SKColor(0x20, 0xC0, 0x40),
        DrawingKind.LandingZone => new SKColor(0xE0, 0x30, 0xE0),
        DrawingKind.Perimeter => new SKColor(0xFF, 0xD0, 0x00),
        DrawingKind.RangeRings => new SKColor(0xF0, 0xF0, 0xF0),
        DrawingKind.Measure => new SKColor(0x00, 0xE0, 0xE0),
        DrawingKind.Unit => new SKColor(0x30, 0x70, 0xFF),
        _ => new SKColor(0xFF, 0xE0, 0x40)
    };

    public static string DrawingGlyph(DrawingKind kind) => kind switch
    {
        DrawingKind.Incident => "!",
        DrawingKind.Staging => "S",
        DrawingKind.Rendezvous => "RV",
        DrawingKind.LandingZone => "LZ",
        DrawingKind.Hazmat => "HZ",
        DrawingKind.Unit => "U",
        DrawingKind.RangeRings => "+",
        _ => "i"
    };

    private static void DrawDrawings(SKCanvas canvas, Viewport vp, MapScene scene, IEnumerable<Drawing> drawings, bool draft)
    {
        var st = scene.Style;
        var u = scene.UiScale;
        using var fill = new SKPaint { IsAntialias = true };
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
        using var labelFont = Font(13f * u, true);
        using var glyphFont = Font(12f * u, true);
        var halo = st == MapStyle.Daylight ? SKColors.White : SKColors.Black;
        var labelColor = st == MapStyle.Daylight ? SKColors.Black : st.OverlayText;
        foreach (var d in drawings)
        {
            if (d.Points.Count == 0)
                continue;
            var color = st.Symbol(DrawingColor(d.Kind));
            var selected = d.Id == scene.SelectedDrawing;
            if (!d.Active)
                color = color.WithAlpha(110);
            // Rings and hazmat zones
            foreach (var (name, ring) in d.DerivedRings())
            {
                using var path = ScreenPath(vp, ring, true);
                var isProtective = name.StartsWith("Protective", StringComparison.Ordinal);
                if (d.Kind == DrawingKind.Hazmat)
                {
                    fill.Color = color.WithAlpha((byte)(isProtective ? 45 : 70));
                    canvas.DrawPath(path, fill);
                }
                line.Color = color;
                line.StrokeWidth = (d.Kind == DrawingKind.Hazmat ? 3f : 2f) * u;
                line.PathEffect = d.Kind == DrawingKind.RangeRings ? SKPathEffect.CreateDash(new[] { 10f, 6f }, 0) : null;
                canvas.DrawPath(path, line);
                line.PathEffect = null;
                if (d.Kind == DrawingKind.RangeRings)
                {
                    // Label at the top of each ring.
                    var top = ring.OrderByDescending(p => p.Lat).First();
                    var sp = S(vp, top);
                    HaloText(canvas, name, sp.X, sp.Y - 4, labelFont, labelColor, halo);
                }
            }
            if (d.Kind == DrawingKind.Hazmat && d.DownwindBearing is { } bearing && d.ProtectiveMeters is { } pd)
            {
                var a = S(vp, d.Points[0]);
                var b = S(vp, Geometry.Destination(d.Points[0], bearing, pd * 0.9));
                line.Color = color;
                line.StrokeWidth = 4 * u;
                canvas.DrawLine(a, b, line);
                var ang = Math.Atan2(b.Y - a.Y, b.X - a.X);
                using var head = new SKPath();
                head.MoveTo(b);
                head.LineTo(b.X - 16 * u * (float)Math.Cos(ang - 0.45), b.Y - 16 * u * (float)Math.Sin(ang - 0.45));
                head.LineTo(b.X - 16 * u * (float)Math.Cos(ang + 0.45), b.Y - 16 * u * (float)Math.Sin(ang + 0.45));
                head.Close();
                fill.Color = color;
                canvas.DrawPath(head, fill);
            }
            switch (d.Shape)
            {
                case DrawingShape.Polygon when d.Points.Count >= 2:
                {
                    using var path = ScreenPath(vp, d.Points, d.Points.Count >= 3);
                    if (d.Points.Count >= 3)
                    {
                        fill.Color = color.WithAlpha(d.Kind == DrawingKind.Measure ? (byte)30 : (byte)50);
                        canvas.DrawPath(path, fill);
                    }
                    line.Color = color;
                    line.StrokeWidth = (selected ? 6f : 4f) * u;
                    line.PathEffect = d.Kind == DrawingKind.Cordon ? SKPathEffect.CreateDash(new[] { 14f * u, 7f * u }, 0) : null;
                    canvas.DrawPath(path, line);
                    line.PathEffect = null;
                    break;
                }
                case DrawingShape.Line when d.Points.Count >= 2:
                {
                    using var path = ScreenPath(vp, d.Points, false);
                    if (d.Kind == DrawingKind.RoadClosure)
                    {
                        line.Color = SKColors.Black;
                        line.StrokeWidth = 11 * u;
                        canvas.DrawPath(path, line);
                    }
                    line.Color = color;
                    line.StrokeWidth = (d.Kind == DrawingKind.RoadClosure ? 8f : 4f) * u * (selected ? 1.3f : 1f);
                    canvas.DrawPath(path, line);
                    if (d.Kind == DrawingKind.RoadClosure)
                        foreach (var p in new[] { d.Points[0], d.Points[^1] })
                            DrawX(canvas, S(vp, p), 9 * u, color);
                    break;
                }
            }
            // Measurement and closure labels
            var anchor = S(vp, d.Anchor);
            var label = d.Title;
            if (d.Kind == DrawingKind.Measure)
                label = d.Shape == DrawingShape.Polygon ? $"{Drawing.FormatArea(d.AreaSquareMeters)}" : GeoMath.FormatDistance(d.LengthMeters);
            if (d.Shape == DrawingShape.Point || d.Kind == DrawingKind.Hazmat || d.Kind == DrawingKind.RangeRings)
            {
                DrawPin(canvas, anchor, color, DrawingGlyph(d.Kind), glyphFont, u, selected, st);
                HaloText(canvas, label, anchor.X, anchor.Y + 22 * u, labelFont, labelColor, halo);
            }
            else
            {
                var lp = d.Shape == DrawingShape.Line ? S(vp, d.Points[^1]) : anchor;
                HaloText(canvas, label, lp.X, lp.Y - 10 * u, labelFont, labelColor, halo);
            }
            if (draft)
            {
                using var handle = new SKPaint { IsAntialias = true, Color = SKColors.White };
                using var handleRing = new SKPaint { IsAntialias = true, Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
                foreach (var p in d.Points)
                {
                    var sp = S(vp, p);
                    canvas.DrawCircle(sp, 7 * u, handle);
                    canvas.DrawCircle(sp, 7 * u, handleRing);
                }
            }
        }
    }

    private static void DrawX(SKCanvas canvas, SKPoint p, float s, SKColor color)
    {
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, Color = SKColors.White, StrokeCap = SKStrokeCap.Round };
        canvas.DrawLine(p.X - s, p.Y - s, p.X + s, p.Y + s, paint);
        canvas.DrawLine(p.X - s, p.Y + s, p.X + s, p.Y - s, paint);
    }

    private static void DrawPin(SKCanvas canvas, SKPoint p, SKColor color, string glyph, SKFont font, float u, bool selected, MapStyle st)
    {
        var r = 13 * u;
        using var path = new SKPath();
        path.MoveTo(p.X, p.Y);
        path.LineTo(p.X - r * 0.7f, p.Y - r * 1.3f);
        path.ArcTo(new SKRect(p.X - r, p.Y - r * 2.6f, p.X + r, p.Y - r * 0.6f), 140, 260, false);
        path.Close();
        using var fill = new SKPaint { IsAntialias = true, Color = color };
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = selected ? 4 : 2, Color = selected ? st.Selection : SKColors.Black };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, ring);
        var light = color.Red * 0.299 + color.Green * 0.587 + color.Blue * 0.114 > 150;
        using var text = new SKPaint { IsAntialias = true, Color = light ? SKColors.Black : SKColors.White };
        canvas.DrawText(glyph, p.X, p.Y - r * 1.6f + font.Size / 2.8f, SKTextAlign.Center, font, text);
    }

    // ------------------------------------------------------------------ GPS and marker

    private static void DrawMarkerAndGps(SKCanvas canvas, Viewport vp, MapScene scene)
    {
        var st = scene.Style;
        var u = scene.UiScale;
        if (scene.Marker is { } m)
        {
            var p = S(vp, m);
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 * u, Color = st.Selection };
            canvas.DrawCircle(p, 14 * u, paint);
            canvas.DrawLine(p.X - 22 * u, p.Y, p.X - 6 * u, p.Y, paint);
            canvas.DrawLine(p.X + 6 * u, p.Y, p.X + 22 * u, p.Y, paint);
            canvas.DrawLine(p.X, p.Y - 22 * u, p.X, p.Y - 6 * u, paint);
            canvas.DrawLine(p.X, p.Y + 6 * u, p.X, p.Y + 22 * u, paint);
        }
        if (scene.Fix is not { } fix)
            return;
        var gp = S(vp, new GeoPoint(fix.Latitude, fix.Longitude));
        var color = scene.FixStale ? st.GpsStale : st.Gps;
        if (fix.AccuracyMeters is { } acc && acc > 0)
        {
            var r = (float)(acc / vp.MetersPerPixel);
            if (r > 8 && r < 4000)
            {
                using var circle = new SKPaint { IsAntialias = true, Color = color.WithAlpha(45) };
                using var edge = new SKPaint { IsAntialias = true, Color = color.WithAlpha(150), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
                canvas.DrawCircle(gp, r, circle);
                canvas.DrawCircle(gp, r, edge);
            }
        }
        if (fix.CourseDegrees is { } course && (fix.SpeedMetersPerSecond ?? 0) > 1)
        {
            var a = (course - vp.Bearing) * Math.PI / 180;
            var tip = new SKPoint(gp.X + (float)Math.Sin(a) * 26 * u, gp.Y - (float)Math.Cos(a) * 26 * u);
            var left = new SKPoint(gp.X + (float)Math.Sin(a - 2.5) * 13 * u, gp.Y - (float)Math.Cos(a - 2.5) * 13 * u);
            var right = new SKPoint(gp.X + (float)Math.Sin(a + 2.5) * 13 * u, gp.Y - (float)Math.Cos(a + 2.5) * 13 * u);
            using var arrow = new SKPath();
            arrow.MoveTo(tip);
            arrow.LineTo(left);
            arrow.LineTo(right);
            arrow.Close();
            using var ap = new SKPaint { IsAntialias = true, Color = color };
            canvas.DrawPath(arrow, ap);
        }
        using var dot = new SKPaint { IsAntialias = true, Color = color };
        using var white = new SKPaint { IsAntialias = true, Color = st == MapStyle.Night ? new SKColor(0x90, 0x84, 0x70) : SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 3 * u };
        canvas.DrawCircle(gp, 9 * u, dot);
        canvas.DrawCircle(gp, 9 * u, white);
    }

    // ------------------------------------------------------------------ overlays

    private static void DrawOverlays(SKCanvas canvas, Viewport vp, MapScene scene, RenderResult result)
    {
        var st = scene.Style;
        var u = scene.UiScale;
        using var back = new SKPaint { IsAntialias = true, Color = st.Overlay };
        using var text = new SKPaint { IsAntialias = true, Color = st.OverlayText };
        using var lineP = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = st.OverlayText };
        using var font = Font(12f * u, true);
        using var small = Font(10.5f * u);

        // North arrow (top left). Tapping it turns the map back to north up.
        var r = 26 * u;
        var c = new SKPoint(14 * u + r, 14 * u + r);
        canvas.DrawCircle(c, r, back);
        canvas.Save();
        canvas.Translate(c);
        canvas.RotateDegrees((float)-vp.Bearing);
        using (var north = new SKPath())
        {
            north.MoveTo(0, -r * 0.8f);
            north.LineTo(r * 0.32f, r * 0.15f);
            north.LineTo(-r * 0.32f, r * 0.15f);
            north.Close();
            using var red = new SKPaint { IsAntialias = true, Color = st.Symbol(new SKColor(0xE0, 0x20, 0x20)) };
            canvas.DrawPath(north, red);
        }
        using (var south = new SKPath())
        {
            south.MoveTo(0, r * 0.8f);
            south.LineTo(r * 0.32f, r * 0.15f);
            south.LineTo(-r * 0.32f, r * 0.15f);
            south.Close();
            canvas.DrawPath(south, text);
        }
        canvas.DrawText("N", 0, -r * 0.82f - 3, SKTextAlign.Center, font, text);
        canvas.Restore();
        result.NorthArrow = new SKRect(c.X - r, c.Y - r, c.X + r, c.Y + r);

        // Live legend (under the north arrow) when live layers are shown.
        var legendY = c.Y + r + 10 * u;
        foreach (var live in scene.Live.Where(l => l.Items.Count > 0))
        {
            var label = $"{live.Feed.Name}: {LiveFeedService.Label(live)}";
            var w = small.MeasureText(label) + 12;
            canvas.DrawRoundRect(new SKRect(10 * u, legendY, 10 * u + w, legendY + small.Size + 8), 4, 4, back);
            canvas.DrawText(label, 10 * u + 6, legendY + small.Size + 2, SKTextAlign.Left, small, text);
            legendY += small.Size + 12;
        }

        // Scale bar (bottom left)
        var mpp = vp.MetersPerPixel;
        var target = 140 * u * mpp;
        var nice = NiceDistance(target);
        var px = (float)(nice / mpp);
        var x0 = 14 * u;
        var y0 = vp.Height - 34 * u;
        canvas.DrawRoundRect(new SKRect(x0 - 6, y0 - font.Size - 10, x0 + px + 12, y0 + 10), 4, 4, back);
        canvas.DrawLine(x0, y0, x0 + px, y0, lineP);
        canvas.DrawLine(x0, y0 - 6, x0, y0 + 4, lineP);
        canvas.DrawLine(x0 + px, y0 - 6, x0 + px, y0 + 4, lineP);
        canvas.DrawText(nice >= 1000 ? $"{nice / 1000:0.#} km" : $"{nice:0} m", x0 + px / 2, y0 - 8, SKTextAlign.Center, font, text);

        // Attribution (bottom right)
        if (scene.Attribution.Length > 0)
        {
            var w = small.MeasureText(scene.Attribution);
            var maxW = vp.Width * 0.7f;
            var shown = scene.Attribution;
            while (w > maxW && shown.Length > 10)
            {
                shown = shown[..(shown.Length - 4)] + "…";
                w = small.MeasureText(shown);
            }
            var rect = new SKRect(vp.Width - w - 14, vp.Height - small.Size - 10, vp.Width - 2, vp.Height - 2);
            canvas.DrawRoundRect(rect, 3, 3, back);
            canvas.DrawText(shown, vp.Width - 8, vp.Height - 7, SKTextAlign.Right, small, text);
        }
    }

    public static double NiceDistance(double target)
    {
        var pow = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, target))));
        foreach (var m in new[] { 5, 2, 1 })
            if (m * pow <= target)
                return m * pow;
        return pow;
    }
}
