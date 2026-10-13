using FieldMap.Core.Build;
using FieldMap.Core.Drawings;
using FieldMap.Core.Layers;
using FieldMap.Core.Aprs;
using FieldMap.Core.Live;
using FieldMap.Core.Overlays;
using FieldMap.Core.Routing;
using FieldMap.Core.Radio;
using FieldMap.Core.Terrain;
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

    /// <summary>APRS stations to draw (already filtered to those recently heard).</summary>
    public IReadOnlyList<AprsStation> Aprs { get; init; } = Array.Empty<AprsStation>();
    public string? SelectedAprs { get; init; }

    /// <summary>Imported event layers (KML/KMZ/GPX/GeoJSON/CSV) that are switched on.</summary>
    public IReadOnlyList<OverlayLayer> Overlays { get; init; } = Array.Empty<OverlayLayer>();

    /// <summary>Imagery tiles for the Imagery and Hybrid base maps (null: map only).</summary>
    public RasterTileCache? Imagery { get; init; }
    public BaseMapMode BaseMode { get; init; } = BaseMapMode.Map;
    public bool AprsTracks { get; init; } = true;

    /// <summary>Repeaters to draw, and the selected one.</summary>
    public IReadOnlyList<Repeater> Repeaters { get; init; } = Array.Empty<Repeater>();
    public string? SelectedRepeater { get; init; }

    /// <summary>Predicted repeater coverage rasters to shade.</summary>
    public IReadOnlyList<CoverageResult> Coverage { get; init; } = Array.Empty<CoverageResult>();

    /// <summary>The imported cell-coverage tileset, when shown.</summary>
    public TileSource? CellCoverage { get; init; }

    /// <summary>Stations older than this are drawn faded.</summary>
    public TimeSpan AprsFadeAfter { get; init; } = TimeSpan.FromMinutes(30);
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
    public string Attribution { get; init; } = "";
    public bool Synchronous { get; init; }
    public bool ShowControls { get; init; } = true;
    public float UiScale { get; init; } = 1f;
}

/// <summary>Where things were drawn, for tap hit-testing.</summary>
public sealed class RenderResult
{
    public List<(SKRect Rect, Poi Poi)> Pois { get; } = new();
    public List<(SKRect Rect, string Key)> Aprs { get; } = new();
    public List<(SKRect Rect, OverlayLayer Layer, OverlayFeature Feature)> OverlayPoints { get; } = new();
    public List<(SKRect Rect, string Id)> Repeaters { get; } = new();
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
        var tiles = CollectTiles(vp, scene, scene.Regions, 0, result);

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

        var imagery = scene.Imagery != null && scene.BaseMode != BaseMapMode.Map;
        if (imagery)
        {
            canvas.ResetMatrix();
            DrawImagery(canvas, vp, scene, result);
        }

        canvas.ResetMatrix();
        DrawCellCoverage(canvas, vp, scene, result);
        DrawCoverage(canvas, vp, scene);

        // Lines: waterways, boundaries, roads (casings then fills), rail
        foreach (var t in tiles)
        {
            canvas.SetMatrix(t.Matrix);
            foreach (var (key, path) in t.Tile.Lines)
            {
                if (key.StartsWith("waterway:", StringComparison.Ordinal) && !imagery)
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
        if (!imagery)
        {
            DrawRoads(canvas, vp, scene, tiles, stroke);
        }
        else if (scene.BaseMode == BaseMapMode.Hybrid)
        {
            // Roads see-through over the imagery.
            canvas.ResetMatrix();
            using var layer = new SKPaint { Color = SKColors.White.WithAlpha(165) };
            canvas.SaveLayer(layer);
            DrawRoads(canvas, vp, scene, tiles, stroke);
            canvas.ResetMatrix();
            canvas.Restore();
        }
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
        DrawAprsTracks(canvas, vp, scene);
        DrawOverlayLayers(canvas, vp, scene, occupied, result);
        DrawDrawings(canvas, vp, scene, scene.Drawings, false);
        if (scene.Draft != null)
            DrawDrawings(canvas, vp, scene, new[] { scene.Draft }, true);
        var labels = !imagery || scene.BaseMode == BaseMapMode.Hybrid;
        if (labels)
            DrawPlaceLabels(canvas, vp, scene, tiles, occupied, 1, 2); // cities and towns first
        DrawPois(canvas, vp, scene, occupied, result);
        DrawRepeaters(canvas, vp, scene, occupied, result);
        DrawAprs(canvas, vp, scene, occupied, result);
        if (labels)
        {
            DrawPlaceLabels(canvas, vp, scene, tiles, occupied, 3, 9);
            DrawRoadLabels(canvas, vp, scene, tiles, occupied);
        }
        DrawMarkerAndGps(canvas, vp, scene);
        if (scene.ShowControls)
            DrawOverlays(canvas, vp, scene, result);
        return result;
    }

    // ------------------------------------------------------------------ tiles

    /// <summary>Region index base for the cell-coverage tileset in the shared tile cache.</summary>
    private const int CellCoverageIndex = 10_000;

    private static List<VisibleTile> CollectTiles(Viewport vp, MapScene scene, IReadOnlyList<TileSource> regions, int indexBase, RenderResult result)
    {
        var list = new List<VisibleTile>();
        var (minX, minY, maxX, maxY) = vp.VisibleWorld();
        for (var ri = 0; ri < regions.Count; ri++)
        {
            var r = indexBase + ri;
            var region = regions[ri];
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


    // ------------------------------------------------------------------ event layers

    public static readonly SKColor OverlayDefault = new(0xFF, 0x00, 0xC8);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GroundOverlay, SKImage> OverlayImages = new();

    private static SKImage? OverlayImage(GroundOverlay g)
    {
        if (OverlayImages.TryGetValue(g, out var img))
            return img;
        using var data = SKData.CreateCopy(g.Image);
        img = SKImage.FromEncodedData(data);
        if (img != null)
            OverlayImages.AddOrUpdate(g, img);
        return img;
    }

    /// <summary>
    /// The perspective matrix that maps the unit square's corners (0,0) (1,0) (1,1) (0,1) onto the four points given
    /// (Heckbert's square-to-quad), used to lay a map image onto its corner coordinates.
    /// </summary>
    public static SKMatrix SquareToQuad(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3)
    {
        float dx1 = p1.X - p2.X, dx2 = p3.X - p2.X, dx3 = p0.X - p1.X + p2.X - p3.X;
        float dy1 = p1.Y - p2.Y, dy2 = p3.Y - p2.Y, dy3 = p0.Y - p1.Y + p2.Y - p3.Y;
        float g = 0, h = 0;
        if (Math.Abs(dx3) > 1e-6f || Math.Abs(dy3) > 1e-6f)
        {
            var den = dx1 * dy2 - dx2 * dy1;
            g = (dx3 * dy2 - dx2 * dy3) / den;
            h = (dx1 * dy3 - dx3 * dy1) / den;
        }
        return new SKMatrix
        {
            ScaleX = p1.X - p0.X + g * p1.X, SkewX = p3.X - p0.X + h * p3.X, TransX = p0.X,
            SkewY = p1.Y - p0.Y + g * p1.Y, ScaleY = p3.Y - p0.Y + h * p3.Y, TransY = p0.Y,
            Persp0 = g, Persp1 = h, Persp2 = 1
        };
    }

    private static SKColor OverlayColor(uint? argb, MapScene scene) =>
        scene.Style.Symbol(argb is { } c && (c >> 24) > 0 ? new SKColor(c) : OverlayDefault);

    private static void DrawOverlayLayers(SKCanvas canvas, Viewport vp, MapScene scene, List<SKRect> occupied, RenderResult result)
    {
        if (scene.Overlays.Count == 0)
            return;
        var view = vp.VisibleBounds().Expand(500);
        var u = scene.UiScale;
        // Map images first (site plans), under the features.
        foreach (var layer in scene.Overlays)
            foreach (var g in layer.Content.GroundOverlays)
            {
                if (g.Corners.Length != 4 || !g.Bounds.Intersects(view) || OverlayImage(g) is not { } img)
                    continue;
                var c = g.Corners.Select(p => S(vp, p)).ToArray();
                // Image top-left = upper-left corner (index 3), then UR, LR, LL.
                var m = SquareToQuad(c[3], c[2], c[1], c[0]).PreConcat(SKMatrix.CreateScale(1f / img.Width, 1f / img.Height));
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(g.Opacity * layer.Info.ImageOpacity, 0.05f, 1f))) };
                canvas.Save();
                canvas.SetMatrix(m);
                canvas.DrawImage(img, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
                canvas.Restore();
                if (scene.Style == MapStyle.Night)
                {
                    using var path = new SKPath();
                    path.AddPoly(c, true);
                    using var dim = new SKPaint { Color = SKColors.Black.WithAlpha(120) };
                    canvas.DrawPath(path, dim);
                }
            }
        canvas.ResetMatrix();

        using var fill = new SKPaint { IsAntialias = true };
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
        using var labelFont = Font(11.5f * u, true);
        var points = new List<(SKPoint P, OverlayLayer Layer, OverlayFeature F, SKColor Color)>();
        foreach (var layer in scene.Overlays)
            foreach (var f in layer.Content.Features)
            {
                if (!f.Bounds.Intersects(view))
                    continue;
                var stroke = OverlayColor(f.Stroke, scene);
                switch (f.Geometry)
                {
                    case OverlayGeometry.Polygon:
                    {
                        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                        foreach (var ring in f.Parts)
                            path.AddPath(ScreenPath(vp, ring, true));
                        var fc = f.Fill is { } fa ? new SKColor(fa) : stroke.WithAlpha(45);
                        if (fc.Alpha > 0)
                        {
                            fill.Color = scene.Style.Symbol(fc).WithAlpha(fc.Alpha);
                            canvas.DrawPath(path, fill);
                        }
                        line.Color = stroke;
                        line.StrokeWidth = Math.Clamp(f.Width ?? 2.5f, 1f, 8f) * u;
                        canvas.DrawPath(path, line);
                        break;
                    }
                    case OverlayGeometry.Line:
                    {
                        using var path = ScreenPath(vp, f.Parts[0], false);
                        line.Color = SKColors.Black.WithAlpha(110);
                        line.StrokeWidth = (Math.Clamp(f.Width ?? 3f, 1f, 10f) + 2) * u;
                        canvas.DrawPath(path, line);
                        line.Color = stroke;
                        line.StrokeWidth = Math.Clamp(f.Width ?? 3f, 1f, 10f) * u;
                        canvas.DrawPath(path, line);
                        break;
                    }
                    default:
                        points.Add((S(vp, f.Parts[0][0]), layer, f, stroke));
                        break;
                }
            }
        var r = 7.5f * u;
        foreach (var (p, layer, f, color) in points.Take(3000))
        {
            // A diamond: distinct from facilities (circles) and APRS units (rounded squares).
            using var diamond = new SKPath();
            diamond.MoveTo(p.X, p.Y - r * 1.3f);
            diamond.LineTo(p.X + r * 1.3f, p.Y);
            diamond.LineTo(p.X, p.Y + r * 1.3f);
            diamond.LineTo(p.X - r * 1.3f, p.Y);
            diamond.Close();
            fill.Color = color;
            canvas.DrawPath(diamond, fill);
            line.Color = scene.Style == MapStyle.Night ? new SKColor(0x30, 0x30, 0x30) : SKColors.White;
            line.StrokeWidth = 2;
            canvas.DrawPath(diamond, line);
            var rect = new SKRect(p.X - r * 1.6f, p.Y - r * 1.6f, p.X + r * 1.6f, p.Y + r * 1.6f);
            result.OverlayPoints.Add((rect, layer, f));
            if (vp.Zoom >= 14 && f.Name.Length > 0)
            {
                var tw = labelFont.MeasureText(f.Name);
                var lr = new SKRect(p.X - tw / 2, p.Y + r * 1.4f, p.X + tw / 2, p.Y + r * 1.4f + labelFont.Size + 2);
                if (Free(occupied, lr))
                {
                    occupied.Add(lr);
                    HaloText(canvas, f.Name, p.X, lr.Bottom - 2, labelFont, scene.Style.Label, scene.Style.LabelHalo);
                }
            }
        }
    }

    // ------------------------------------------------------------------ imagery

    private static readonly SKSamplingOptions ImageSampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private static void DrawImagery(SKCanvas canvas, Viewport vp, MapScene scene, RenderResult result)
    {
        var cache = scene.Imagery!;
        var z = Math.Clamp((int)Math.Round(vp.Zoom), cache.MinZoom, cache.MaxZoom);
        var n = 1 << z;
        var (minX, minY, maxX, maxY) = vp.VisibleWorld();
        var x0 = Math.Max(0, (int)Math.Floor(minX * n));
        var x1 = Math.Min(n - 1, (int)Math.Floor(maxX * n));
        var y0 = Math.Max(0, (int)Math.Floor(minY * n));
        var y1 = Math.Min(n - 1, (int)Math.Floor(maxY * n));
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 400)
            return;
        using var paint = new SKPaint { IsAntialias = false };
        var scale = (float)(vp.Scale / n / 256.0);
        var bleed = 0.6f / scale;
        for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
            {
                SKImage? image = cache.Get(z, x, y, request: true);
                SKRect src;
                if (image != null)
                {
                    src = new SKRect(0, 0, image.Width, image.Height);
                }
                else
                {
                    // Not here (yet): stretch the nearest lower-zoom tile that is.
                    src = SKRect.Empty;
                    for (var pz = z - 1; pz >= Math.Max(cache.MinZoom, z - 6) && image == null; pz--)
                    {
                        var shift = z - pz;
                        image = cache.Get(pz, x >> shift, y >> shift, request: false);
                        if (image != null)
                        {
                            var part = (float)image.Width / (1 << shift);
                            var ox = (x - ((x >> shift) << shift)) * part;
                            var oy = (y - ((y >> shift) << shift)) * part;
                            src = new SKRect(ox, oy, ox + part, oy + part);
                        }
                    }
                    if (image == null)
                    {
                        result.TilesPending = true;
                        continue;
                    }
                }
                var (sx, sy) = vp.WorldToScreen((double)x / n, (double)y / n);
                var m = SKMatrix.CreateTranslation((float)sx, (float)sy)
                    .PreConcat(SKMatrix.CreateRotationDegrees((float)-vp.Bearing))
                    .PreConcat(SKMatrix.CreateScale(scale, scale));
                canvas.SetMatrix(m);
                canvas.DrawImage(image, src, new SKRect(-bleed, -bleed, 256 + bleed, 256 + bleed), ImageSampling, paint);
            }
        canvas.ResetMatrix();
        // Night: dim the photo so it doesn't ruin night vision; symbols drawn after stay readable.
        if (scene.Style == MapStyle.Night)
        {
            using var dim = new SKPaint { Color = SKColors.Black.WithAlpha(150) };
            canvas.DrawRect(new SKRect(0, 0, vp.Width, vp.Height), dim);
        }
    }

    // ------------------------------------------------------------------ radio: cell coverage, predicted coverage, repeaters

    public static SKColor CellCoverageColor(string kind) => kind switch
    {
        "5g" => new SKColor(0x6A, 0x1B, 0x9A),
        "lte" => new SKColor(0x15, 0x65, 0xC0),
        "3g" => new SKColor(0x00, 0x83, 0x8F),
        _ => new SKColor(0x75, 0x75, 0x75)
    };

    private static void DrawCellCoverage(SKCanvas canvas, Viewport vp, MapScene scene, RenderResult result)
    {
        if (scene.CellCoverage is not { } source)
            return;
        var tiles = CollectTiles(vp, scene, new[] { source }, CellCoverageIndex, result);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        // Older technologies underneath, 5G on top.
        foreach (var kind in new[] { "other", "3g", "lte", "5g" })
            foreach (var t in tiles)
                if (t.Tile.Fills.TryGetValue("coverage:" + kind, out var path))
                {
                    canvas.SetMatrix(t.Matrix);
                    fill.Color = scene.Style.Symbol(CellCoverageColor(kind)).WithAlpha(kind == "5g" ? (byte)70 : (byte)55);
                    canvas.DrawPath(path, fill);
                }
        canvas.ResetMatrix();
    }

    public static SKColor CoverageColor(CoverageClass c) => c switch
    {
        CoverageClass.Likely => new SKColor(0x2E, 0x7D, 0x32, 120),
        CoverageClass.Marginal => new SKColor(0xF9, 0xA8, 0x25, 120),
        CoverageClass.None => new SKColor(0xC6, 0x28, 0x28, 50),
        _ => SKColors.Transparent
    };

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CoverageResult, Dictionary<MapStyle, SKImage>> CoverageImages = new();

    private static SKImage CoverageImage(CoverageResult c, MapStyle style)
    {
        var byStyle = CoverageImages.GetOrCreateValue(c);
        lock (byStyle)
        {
            if (byStyle.TryGetValue(style, out var img))
                return img;
            var colors = new uint[256];
            foreach (var cls in new[] { CoverageClass.None, CoverageClass.Marginal, CoverageClass.Likely })
            {
                var col = CoverageColor(cls);
                var s = style.Symbol(col).WithAlpha(col.Alpha);
                // Premultiplied BGRA for the raster.
                var a = s.Alpha / 255f;
                colors[(int)cls] = ((uint)s.Alpha << 24) | ((uint)(s.Red * a) << 16) | ((uint)(s.Green * a) << 8) | (uint)(s.Blue * a);
            }
            var pixels = new uint[c.Width * c.Height];
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = colors[c.Classes[i]];
            var info = new SKImageInfo(c.Width, c.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                using var pm = new SKPixmap(info, handle.AddrOfPinnedObject(), c.Width * 4);
                img = SKImage.FromPixelCopy(pm);
            }
            finally
            {
                handle.Free();
            }
            byStyle[style] = img;
            return img;
        }
    }

    private static void DrawCoverage(SKCanvas canvas, Viewport vp, MapScene scene)
    {
        if (scene.Coverage.Count == 0)
            return;
        var view = vp.VisibleBounds();
        using var paint = new SKPaint();
        foreach (var c in scene.Coverage)
        {
            if (!c.Bounds.Intersects(view))
                continue;
            var img = CoverageImage(c, scene.Style);
            var (sx, sy) = vp.WorldToScreen(c.MinX, c.MinY);
            var m = SKMatrix.CreateTranslation((float)sx, (float)sy)
                .PreConcat(SKMatrix.CreateRotationDegrees((float)-vp.Bearing))
                .PreConcat(SKMatrix.CreateScale((float)(vp.Scale * (c.MaxX - c.MinX) / c.Width), (float)(vp.Scale * (c.MaxY - c.MinY) / c.Height)));
            canvas.SetMatrix(m);
            canvas.DrawImage(img, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), paint);
        }
        canvas.ResetMatrix();
    }

    public static SKColor RepeaterColor(RepeaterSource s) => s switch
    {
        RepeaterSource.Own => new SKColor(0x28, 0x35, 0x93),
        RepeaterSource.Event => new SKColor(0xE6, 0x51, 0x00),
        RepeaterSource.Amateur => new SKColor(0x2E, 0x7D, 0x32),
        RepeaterSource.RepeaterBook => new SKColor(0x00, 0x79, 0x6B),
        _ => new SKColor(0x54, 0x6E, 0x7A)
    };

    private static void DrawRepeaters(SKCanvas canvas, Viewport vp, MapScene scene, List<SKRect> occupied, RenderResult result)
    {
        if (scene.Repeaters.Count == 0 || vp.Zoom < 6)
            return;
        var st = scene.Style;
        var u = scene.UiScale;
        var view = vp.VisibleBounds().Expand(500);
        var r = 11f * u;
        using var fill = new SKPaint { IsAntialias = true };
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f };
        using var glyph = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.8f * u, StrokeCap = SKStrokeCap.Round };
        using var labelFont = Font(11f * u, true);
        using var text = new SKPaint { IsAntialias = true };
        // Licensed stations can number thousands: only when zoomed in. Own and event repeaters always.
        var visible = scene.Repeaters.Where(x => view.Contains(x.Lat, x.Lon) &&
                                                 (x.Source is RepeaterSource.Own or RepeaterSource.Event || x.Id == scene.SelectedRepeater ||
                                                  vp.Zoom >= (x.Source == RepeaterSource.Licensed ? 11 : 8)))
            .OrderBy(x => x.Source is RepeaterSource.Own or RepeaterSource.Event).ThenBy(x => x.Id == scene.SelectedRepeater).ToList();
        if (visible.Count > 1500)
            visible = visible.Skip(visible.Count - 1500).ToList();
        var edge = st == MapStyle.Night ? new SKColor(0x30, 0x30, 0x30) : st == MapStyle.Daylight ? SKColors.Black : SKColors.White;
        foreach (var x in visible)
        {
            var p = S(vp, x.Point);
            var color = st.Symbol(RepeaterColor(x.Source));
            fill.Color = color;
            canvas.DrawCircle(p, r, fill);
            ring.Color = edge;
            canvas.DrawCircle(p, r, ring);
            // Mast with radio waves.
            glyph.Color = st == MapStyle.Night ? new SKColor(0xC4, 0xB4, 0x96) : SKColors.White;
            canvas.DrawLine(p.X, p.Y - r * 0.35f, p.X, p.Y + r * 0.6f, glyph);
            canvas.DrawLine(p.X - r * 0.3f, p.Y + r * 0.6f, p.X, p.Y - r * 0.05f, glyph);
            canvas.DrawLine(p.X + r * 0.3f, p.Y + r * 0.6f, p.X, p.Y - r * 0.05f, glyph);
            using (var waves = new SKPath())
            {
                waves.AddArc(new SKRect(p.X - r * 0.45f, p.Y - r * 0.8f, p.X + r * 0.45f, p.Y + r * 0.1f), 200, 140);
                waves.AddArc(new SKRect(p.X - r * 0.75f, p.Y - r * 1.05f, p.X + r * 0.75f, p.Y + r * 0.35f), 205, 130);
                canvas.DrawPath(waves, glyph);
            }
            var hit = new SKRect(p.X - r - 6, p.Y - r - 6, p.X + r + 6, p.Y + r + 6);
            if (x.Id == scene.SelectedRepeater)
            {
                ring.Color = st.Selection;
                ring.StrokeWidth = 4;
                canvas.DrawCircle(p, r + 5, ring);
                ring.StrokeWidth = 2;
            }
            result.Repeaters.Add((hit, x.Id));
            occupied.Add(new SKRect(p.X - r, p.Y - r, p.X + r, p.Y + r));
            if (vp.Zoom >= 10 || x.Id == scene.SelectedRepeater)
            {
                var label = x.Label;
                var tw = labelFont.MeasureText(label);
                var lr = new SKRect(p.X - tw / 2 - 4, p.Y + r + 3, p.X + tw / 2 + 4, p.Y + r + 7 + labelFont.Size);
                if (Free(occupied, lr) || x.Id == scene.SelectedRepeater)
                {
                    occupied.Add(lr);
                    fill.Color = st.Overlay.WithAlpha(230);
                    canvas.DrawRoundRect(lr, 4, 4, fill);
                    text.Color = st.OverlayText;
                    canvas.DrawText(label, p.X, lr.Bottom - 4 - labelFont.Size * 0.12f, SKTextAlign.Center, labelFont, text);
                }
            }
        }
    }

    // ------------------------------------------------------------------ APRS stations

    public static SKColor AprsColor(AprsSymbolGroup group) => group switch
    {
        AprsSymbolGroup.Emergency => new SKColor(0xD0, 0x10, 0x10),
        AprsSymbolGroup.Vehicle => new SKColor(0x15, 0x65, 0xC0),
        AprsSymbolGroup.Person => new SKColor(0x00, 0x89, 0x7B),
        AprsSymbolGroup.Aircraft => new SKColor(0x7B, 0x1F, 0xA2),
        AprsSymbolGroup.Boat => new SKColor(0x02, 0x77, 0xBD),
        AprsSymbolGroup.Weather => new SKColor(0x00, 0x97, 0xA7),
        AprsSymbolGroup.Infrastructure => new SKColor(0x61, 0x61, 0x61),
        AprsSymbolGroup.Facility => new SKColor(0x8D, 0x4E, 0x2A),
        _ => new SKColor(0x45, 0x5A, 0x64)
    };

    private static void DrawAprsTracks(SKCanvas canvas, Viewport vp, MapScene scene)
    {
        if (!scene.AprsTracks || scene.Aprs.Count == 0 || vp.Zoom < 8)
            return;
        var view = vp.VisibleBounds().Expand(2000);
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f * scene.UiScale, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
        using var dot = new SKPaint { IsAntialias = true };
        foreach (var s in scene.Aprs)
        {
            if (s.Track.Count < 2 || !GeoBounds.Of(s.Track.Select(t => new GeoPoint(t.Lat, t.Lon))).Intersects(view))
                continue;
            var color = scene.Style.Symbol(AprsColor(s.Emergency ? AprsSymbolGroup.Emergency : AprsSymbols.Group(s.SymbolTable, s.SymbolCode)));
            using var path = new SKPath();
            for (var i = 0; i < s.Track.Count; i++)
            {
                var p = S(vp, new GeoPoint(s.Track[i].Lat, s.Track[i].Lon));
                if (i == 0)
                    path.MoveTo(p);
                else
                    path.LineTo(p);
            }
            if (s.Point is { } now)
                path.LineTo(S(vp, now));
            line.Color = color.WithAlpha(s.Key == scene.SelectedAprs ? (byte)230 : (byte)150);
            canvas.DrawPath(path, line);
            if (vp.Zoom >= 13)
            {
                dot.Color = color;
                foreach (var t in s.Track)
                    canvas.DrawCircle(S(vp, new GeoPoint(t.Lat, t.Lon)), 2.5f * scene.UiScale, dot);
            }
        }
    }

    private static void DrawAprs(SKCanvas canvas, Viewport vp, MapScene scene, List<SKRect> occupied, RenderResult result)
    {
        if (scene.Aprs.Count == 0 || vp.Zoom < 5)
            return;
        var st = scene.Style;
        var u = scene.UiScale;
        var view = vp.VisibleBounds().Expand(500);
        var r = 12f * u;
        using var font = Font(r * 0.85f, true);
        using var small = Font(r * 0.66f, true);
        using var labelFont = Font(11.5f * u, true);
        using var fill = new SKPaint { IsAntialias = true };
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f };
        using var text = new SKPaint { IsAntialias = true };
        var drawn = 0;
        // Emergencies and the selected station last so they're on top; then most recently heard on top.
        var ordered = scene.Aprs.Where(s => s.Point is { } p && view.Contains(p.Lat, p.Lon))
            .OrderBy(s => s.Emergency).ThenBy(s => s.Key == scene.SelectedAprs).ThenBy(s => s.LastHeardUtc).ToList();
        if (ordered.Count > 1500)
            ordered = ordered.Skip(ordered.Count - 1500).ToList();
        foreach (var s in ordered)
        {
            var p = S(vp, s.Point!.Value);
            var group = s.Emergency ? AprsSymbolGroup.Emergency : AprsSymbols.Group(s.SymbolTable, s.SymbolCode);
            var faded = scene.NowUtc - s.LastHeardUtc > scene.AprsFadeAfter;
            var color = st.Symbol(AprsColor(group));
            var alpha = faded ? (byte)120 : (byte)255;

            if (s.Emergency)
            {
                ring.Color = st.Symbol(new SKColor(0xFF, 0x20, 0x20));
                ring.StrokeWidth = 4;
                canvas.DrawCircle(p, r + 7 * u, ring);
                canvas.DrawCircle(p, r + 13 * u, ring);
                ring.StrokeWidth = 2;
            }

            // Course arrow: a pointer sticking out of the symbol in the direction of travel.
            var arrowDown = false;
            if (s.CourseDegrees is { } course && s.Moving)
            {
                var a = (course - vp.Bearing) * Math.PI / 180;
                var len = r + 17 * u;
                var tip = new SKPoint(p.X + (float)Math.Sin(a) * len, p.Y - (float)Math.Cos(a) * len);
                var left = new SKPoint(p.X + (float)Math.Sin(a - 0.6) * r * 0.8f, p.Y - (float)Math.Cos(a - 0.6) * r * 0.8f);
                var right = new SKPoint(p.X + (float)Math.Sin(a + 0.6) * r * 0.8f, p.Y - (float)Math.Cos(a + 0.6) * r * 0.8f);
                using var arrow = new SKPath();
                arrow.MoveTo(tip);
                arrow.LineTo(left);
                arrow.LineTo(right);
                arrow.Close();
                fill.Color = color.WithAlpha(alpha);
                canvas.DrawPath(arrow, fill);
                ring.Color = (st == MapStyle.Night ? new SKColor(0x30, 0x30, 0x30) : st == MapStyle.Daylight ? SKColors.Black : SKColors.White).WithAlpha(alpha);
                canvas.DrawPath(arrow, ring);
                arrowDown = Math.Cos(a) < -0.3;
            }

            // Diamond-cornered square: distinct from the round facility symbols.
            var rect = new SKRect(p.X - r, p.Y - r, p.X + r, p.Y + r);
            fill.Color = color.WithAlpha(alpha);
            canvas.DrawRoundRect(rect, r * 0.35f, r * 0.35f, fill);
            ring.Color = (st == MapStyle.Night ? new SKColor(0x30, 0x30, 0x30) : st == MapStyle.Daylight ? SKColors.Black : SKColors.White).WithAlpha(alpha);
            canvas.DrawRoundRect(rect, r * 0.35f, r * 0.35f, ring);
            var glyph = AprsSymbols.Glyph(s.SymbolTable, s.SymbolCode);
            var f = glyph.Length > 2 ? small : font;
            text.Color = (st == MapStyle.Night ? new SKColor(0xC4, 0xB4, 0x96) : SKColors.White).WithAlpha(alpha);
            canvas.DrawText(glyph, p.X, p.Y + f.Size / 2.8f, SKTextAlign.Center, f, text);
            if (AprsSymbols.Overlay(s.SymbolTable) is { } overlay)
            {
                var o = new SKPoint(rect.Right - 2, rect.Bottom - 2);
                fill.Color = SKColors.White;
                canvas.DrawCircle(o, r * 0.42f, fill);
                text.Color = SKColors.Black;
                canvas.DrawText(overlay.ToString(), o.X, o.Y + small.Size / 2.8f, SKTextAlign.Center, small, text);
            }
            if (s.Key == scene.SelectedAprs)
            {
                ring.Color = st.Selection;
                ring.StrokeWidth = 4;
                canvas.DrawRoundRect(SKRect.Inflate(rect, 5, 5), r * 0.5f, r * 0.5f, ring);
                ring.StrokeWidth = 2;
            }
            result.Aprs.Add((SKRect.Inflate(rect, 6, 6), s.Key));
            occupied.Add(rect);
            drawn++;

            // Call sign label (always for emergencies and the selected station; otherwise if there's room).
            if (vp.Zoom >= 9 || s.Emergency || s.Key == scene.SelectedAprs)
            {
                var label = s.Emergency ? $"{s.Key} EMERGENCY" : s.Key;
                var tw = labelFont.MeasureText(label);
                var top = p.Y + r + 3 + (arrowDown ? 17 * u : 0);
                var lr = new SKRect(p.X - tw / 2 - 4, top, p.X + tw / 2 + 4, top + 4 + labelFont.Size);
                if (Free(occupied, lr) || s.Emergency || s.Key == scene.SelectedAprs)
                {
                    occupied.Add(lr);
                    fill.Color = (s.Emergency ? st.Symbol(new SKColor(0xD0, 0x10, 0x10)) : st.Overlay).WithAlpha(faded ? (byte)150 : (byte)230);
                    canvas.DrawRoundRect(lr, 4, 4, fill);
                    text.Color = s.Emergency ? SKColors.White : st.OverlayText;
                    canvas.DrawText(label, p.X, lr.Bottom - 4 - labelFont.Size * 0.12f, SKTextAlign.Center, labelFont, text);
                }
            }
        }
    }

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
