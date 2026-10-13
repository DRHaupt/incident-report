using FieldMap.Core.Aprs;
using FieldMap.Core.Data;
using FieldMap.Core.Imagery;
using FieldMap.Core.Overlays;
using FieldMap.Core.Geo;
using FieldMap.Core.Drawings;
using FieldMap.Core.Routing;
using FieldMap.Core.Sample;
using FieldMap.Core.Tiles;
using FieldMap.Map;
using SkiaSharp;

namespace FieldMap.Tests;

[Collection("sample")]
public class RenderingTests
{
    private readonly SampleFixture _f;

    public RenderingTests(SampleFixture f) => _f = f;

    /// <summary>Rendered images are kept here for a person to look at.</summary>
    public static string OutputDir => Path.Combine(Path.GetTempPath(), "fieldmap-render");

    private SKBitmap Render(MapStyle style, double zoom, double bearing, string name, bool withExtras)
    {
        using var tiles = new MbTilesReader(Path.Combine(_f.Dir, RegionBuilder.TilesFile));
        var pois = new PoiIndex(_f.Search.LoadPois());
        var router = new Router(_f.Graph);
        var route = withExtras ? router.Route(SampleRegion.Intersection(0, 0), SampleRegion.HospitalPoint, VehicleProfile.Emergency) : null;
        var hazmat = new Drawing { Kind = DrawingKind.Hazmat, Shape = DrawingShape.Point, Points = { SampleRegion.At(300, 800) }, IsolationMeters = 60, ProtectiveMeters = 300, WindFromDegrees = 250, Label = "Tanker leak" };
        var closure = new Drawing { Kind = DrawingKind.RoadClosure, Shape = DrawingShape.Line, Points = { SampleRegion.At(SampleRegion.StreetX(4) - 30, -100), SampleRegion.At(SampleRegion.StreetX(4) + 30, -100) }, Label = "Closed" };
        var cordon = new Drawing { Kind = DrawingKind.Cordon, Shape = DrawingShape.Polygon, Points = { SampleRegion.At(200, 700), SampleRegion.At(420, 700), SampleRegion.At(420, 900), SampleRegion.At(200, 900) } };
        var scene = new MapScene
        {
            Regions = new[] { new TileSource(tiles, new GeoBounds(_f.Manifest.MinLat, _f.Manifest.MinLon, _f.Manifest.MaxLat, _f.Manifest.MaxLon)) },
            Tiles = new TileCache(),
            Style = style,
            Pois = pois,
            LayerOn = _ => true,
            Drawings = withExtras ? new[] { hazmat, closure, cordon } : Array.Empty<Drawing>(),
            Route = route,
            Fix = withExtras ? new GeoFix(SampleRegion.Origin.Lat - 0.0005, SampleRegion.Origin.Lon + 0.001, 25, null, DateTime.UtcNow, "Test", CourseDegrees: 45, SpeedMetersPerSecond: 12) : null,
            Attribution = "SAMPLE DATA – fictitious",
            Synchronous = true,
            UiScale = 1.3f,
            Aprs = withExtras ? SampleAprs() : Array.Empty<AprsStation>(),
            SelectedAprs = withExtras ? "VE7MED-9" : null
        };
        var vp = Viewport.At(SampleRegion.At(0, 560), zoom, bearing, 1000, 760);
        var bmp = new SKBitmap(vp.Width, vp.Height);
        using (var canvas = new SKCanvas(bmp))
            MapRenderer.Render(canvas, vp, scene);
        Directory.CreateDirectory(OutputDir);
        using (var img = SKImage.FromBitmap(bmp))
        using (var data = img.Encode(SKEncodedImageFormat.Png, 90))
        using (var fs = File.Create(Path.Combine(OutputDir, name + ".png")))
            data.SaveTo(fs);
        return bmp;
    }

    /// <summary>A few fictitious APRS stations around the sample town (an ambulance with a track, an emergency, an object…).</summary>
    public static List<AprsStation> SampleAprs()
    {
        var t = new AprsStationTracker();
        var now = DateTime.UtcNow;
        void Pos(string call, GeoPoint p, char table, char code, double? course, double? kmh, string comment, int minutesAgo = 0) =>
            t.Process(AprsParser.Parse(AprsBeacon.Packet(call, "WIDE1-1", AprsBeacon.Position(p.Lat, p.Lon, table, code, course, kmh, null, comment))), AprsVia.Radio, now.AddMinutes(-minutesAgo));
        for (var i = 0; i < 5; i++)
            Pos("VE7MED-9", SampleRegion.At(-450 + i * 90, 300 + i * 40), '/', 'a', 70, 60, "Medic 12", 10 - i * 2);
        Pos("VE7FD-1", SampleRegion.At(250, 420), '/', 'f', 180, 40, "Engine 3");
        Pos("VE7WX", SampleRegion.At(-300, 900), '/', '_', null, null, "", 50);
        Pos("VE7HKR-7", SampleRegion.At(520, 650), '\\', '!', null, null, "Injured hiker");
        Pos("VA7DIG", SampleRegion.At(-150, 1000), 'S', '#', null, null, "");
        t.Process(AprsParser.Parse("VE7EOC>APZFMP:" + AprsBeacon.Object("STAGING", now, SampleRegion.At(80, 760).Lat, SampleRegion.At(80, 760).Lon, '/', 'c', "Staging area")), AprsVia.Internet, now);
        return t.Snapshot();
    }

    [Fact]
    public void Draws_aprs_stations_and_reports_their_hit_areas()
    {
        using var tiles = new MbTilesReader(Path.Combine(_f.Dir, RegionBuilder.TilesFile));
        var scene = new MapScene
        {
            Regions = new[] { new TileSource(tiles, new GeoBounds(_f.Manifest.MinLat, _f.Manifest.MinLon, _f.Manifest.MaxLat, _f.Manifest.MaxLon)) },
            Tiles = new TileCache(),
            Synchronous = true,
            Aprs = SampleAprs()
        };
        var vp = Viewport.At(SampleRegion.At(0, 560), 15, 0, 1000, 760);
        using var bmp = new SKBitmap(vp.Width, vp.Height);
        using var canvas = new SKCanvas(bmp);
        var result = MapRenderer.Render(canvas, vp, scene);
        var keys = result.Aprs.Select(a => a.Key).ToList();
        Assert.Contains("VE7MED-9", keys);
        Assert.Contains("VE7HKR-7", keys);
        Assert.Contains("STAGING", keys);
        Assert.Equal(6, keys.Count);
        // The emergency is drawn last (on top).
        Assert.Equal("VE7HKR-7", keys[^1]);
        var hiker = result.Aprs.First(a => a.Key == "VE7HKR-7").Rect;
        var (hx, hy) = vp.GeoToScreen(SampleRegion.At(520, 650));
        Assert.True(hiker.Contains((float)hx, (float)hy));
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("night")]
    [InlineData("daylight")]
    public void Draws_repeaters_predicted_coverage_and_cell_coverage(string theme)
    {
        var style = theme switch { "night" => MapStyle.Night, "daylight" => MapStyle.Daylight, _ => MapStyle.Standard };
        using var tiles = new MbTilesReader(Path.Combine(_f.Dir, RegionBuilder.TilesFile));
        var centre = SampleRegion.At(0, 560);
        var repeater = new FieldMap.Core.Radio.Repeater { Source = FieldMap.Core.Radio.RepeaterSource.Own, CallSign = "VE7TST", Lat = centre.Lat, Lon = centre.Lon, OutputMHz = 155.5 };
        // A 100×100 coverage raster 3 km across: west half likely, east half none.
        var nw = Geometry.Destination(Geometry.Destination(centre, 0, 1500), 270, 1500);
        var se = Geometry.Destination(Geometry.Destination(centre, 180, 1500), 90, 1500);
        var (minX, minY) = WebMercator.ToWorld(nw);
        var (maxX, maxY) = WebMercator.ToWorld(se);
        var classes = new byte[100 * 100];
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
                classes[y * 100 + x] = (byte)(x < 50 ? FieldMap.Core.Terrain.CoverageClass.Likely : FieldMap.Core.Terrain.CoverageClass.None);
        var coverage = new FieldMap.Core.Terrain.CoverageResult
        {
            Request = FieldMap.Core.Terrain.CoverageRequest.Handheld(centre.Lat, centre.Lon), Width = 100, Height = 100, Classes = classes,
            MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY, Bounds = new GeoBounds(se.Lat, nw.Lon, nw.Lat, se.Lon)
        };
        // Cell coverage: LTE over the whole sample area.
        var kml = Path.Combine(Path.GetTempPath(), $"cell-{Guid.NewGuid():N}.kml");
        var a = SampleRegion.At(-3000, -3000);
        var b = SampleRegion.At(3000, 3000);
        string F(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(kml, $"<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document><Placemark><name>LTE</name><Polygon><outerBoundaryIs><LinearRing><coordinates>" +
                               $"{F(a.Lon)},{F(a.Lat)} {F(b.Lon)},{F(a.Lat)} {F(b.Lon)},{F(b.Lat)} {F(a.Lon)},{F(b.Lat)} {F(a.Lon)},{F(a.Lat)}</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark></Document></kml>");
        var cellPath = Path.Combine(Path.GetTempPath(), $"cell-{Guid.NewGuid():N}.mbtiles");
        FieldMap.Core.Radio.CellCoverage.Build(new[] { kml }, cellPath, null);
        try
        {
            using var cell = new MbTilesReader(cellPath);
            SKBitmap Draw(bool extras)
            {
                var scene = new MapScene
                {
                    Regions = new[] { new TileSource(tiles, new GeoBounds(_f.Manifest.MinLat, _f.Manifest.MinLon, _f.Manifest.MaxLat, _f.Manifest.MaxLon)) },
                    Tiles = new TileCache(), Style = style, Synchronous = true, LayerOn = _ => true,
                    Repeaters = extras ? new[] { repeater } : Array.Empty<FieldMap.Core.Radio.Repeater>(),
                    SelectedRepeater = extras ? repeater.Id : null,
                    Coverage = extras ? new[] { coverage } : Array.Empty<FieldMap.Core.Terrain.CoverageResult>(),
                    CellCoverage = extras ? new TileSource(cell, new GeoBounds(-85, -180, 85, 180)) : null
                };
                var vp = Viewport.At(centre, 14, 0, 800, 600);
                var bmp = new SKBitmap(vp.Width, vp.Height);
                using var canvas = new SKCanvas(bmp);
                var result = MapRenderer.Render(canvas, vp, scene);
                if (extras)
                {
                    Assert.Contains(result.Repeaters, r => r.Id == repeater.Id);
                    var (rx, ry) = vp.GeoToScreen(centre);
                    Assert.True(result.Repeaters.First().Rect.Contains((float)rx, (float)ry));
                }
                return bmp;
            }
            using var plain = Draw(false);
            using var with = Draw(true);
            Directory.CreateDirectory(OutputDir);
            using (var img = SKImage.FromBitmap(with))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 90))
            using (var fs = File.Create(Path.Combine(OutputDir, $"radio-{theme}.png")))
                data.SaveTo(fs);
            // West of the site (likely) gets greener; east (unlikely) gets redder.
            var west = Geometry.Destination(centre, 270, 900);
            var east = Geometry.Destination(centre, 90, 900);
            var vp2 = Viewport.At(centre, 14, 0, 800, 600);
            var (wx, wy) = vp2.GeoToScreen(west);
            var (ex, ey) = vp2.GeoToScreen(east);
            SKColor P(SKBitmap bmp, double x, double y) => bmp.GetPixel((int)x, (int)y);
            var w0 = P(plain, wx, wy); var w1 = P(with, wx, wy);
            var e0 = P(plain, ex, ey); var e1 = P(with, ex, ey);
            Assert.True(w1.Green - w1.Red > w0.Green - w0.Red, $"west {w0} -> {w1}");
            Assert.True(e1.Red - e1.Green > e0.Red - e0.Green - 1, $"east {e0} -> {e1}");
            Assert.NotEqual(w0, w1);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(cellPath);
            File.Delete(kml);
        }
    }

    /// <summary>A synthetic "imagery" package for the sample area: textured fields with the tile number on each tile.</summary>
    private string SyntheticImagery()
    {
        var path = Path.Combine(_f.Dir, "..", "synthetic-imagery.mbtiles");
        if (File.Exists(path))
            return path;
        var area = new GeoBounds(_f.Manifest.MinLat, _f.Manifest.MinLon, _f.Manifest.MaxLat, _f.Manifest.MaxLon);
        using var store = new RasterTileStore(path);
        var rnd = new Random(7);
        foreach (var z in new[] { 12, 13, 14, 15 })
            foreach (var (tz, x, y) in ImageryMath.Tiles(area, z))
            {
                using var bmp = new SKBitmap(256, 256);
                using (var c = new SKCanvas(bmp))
                {
                    c.Clear(new SKColor(0x4A, 0x6B, 0x3A));
                    using var p = new SKPaint { IsAntialias = true };
                    for (var i = 0; i < 40; i++)
                    {
                        p.Color = new SKColor((byte)rnd.Next(40, 140), (byte)rnd.Next(70, 150), (byte)rnd.Next(30, 90));
                        c.DrawRect(rnd.Next(0, 230), rnd.Next(0, 230), rnd.Next(10, 60), rnd.Next(10, 60), p);
                    }
                    p.Color = SKColors.White;
                    using var f = new SKFont(SKTypeface.Default, 16);
                    c.DrawText($"{tz}/{x}/{y}", 8, 24, f, p);
                }
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Jpeg, 80);
                store.Put(tz, x, y, data.ToArray());
            }
        store.SetMetadata(new Dictionary<string, string> { ["name"] = "Synthetic test imagery", ["attribution"] = "SYNTHETIC IMAGERY (test)" });
        return path;
    }

    [Fact]
    public void SquareToQuad_maps_the_corners()
    {
        var p = new[] { new SKPoint(10, 20), new SKPoint(200, 40), new SKPoint(180, 300), new SKPoint(0, 260) };
        var m = MapRenderer.SquareToQuad(p[0], p[1], p[2], p[3]);
        var unit = new[] { new SKPoint(0, 0), new SKPoint(1, 0), new SKPoint(1, 1), new SKPoint(0, 1) };
        for (var i = 0; i < 4; i++)
        {
            var q = m.MapPoint(unit[i]);
            Assert.Equal(p[i].X, q.X, 2);
            Assert.Equal(p[i].Y, q.Y, 2);
        }
    }

    /// <summary>An event layer like an organiser's KMZ: a rotated site-plan image, a fence, a course and posts.</summary>
    private static OverlayLayer SampleEventLayer()
    {
        using var bmp = new SKBitmap(400, 300);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(new SKColor(0xF4, 0xF0, 0xE0));
            using var p = new SKPaint { IsAntialias = true };
            p.Color = new SKColor(0x30, 0x60, 0xC0);
            c.DrawRect(30, 30, 140, 80, p);
            p.Color = new SKColor(0xE0, 0x40, 0x40);
            c.DrawRect(230, 170, 120, 90, p);
            p.Color = SKColors.Black;
            using var f = new SKFont(SKTypeface.Default, 26);
            c.DrawText("MAIN STAGE", 34, 80, f, p);
            c.DrawText("FIRST AID", 236, 220, f, p);
            p.Style = SKPaintStyle.Stroke;
            p.StrokeWidth = 6;
            c.DrawRect(3, 3, 394, 294, p);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        var content = new MapFileContent { Name = "Sample Festival" };
        var sw = SampleRegion.At(80, 620);
        var ne = SampleRegion.At(480, 920);
        content.GroundOverlays.Add(new GroundOverlay
        {
            Name = "Site plan",
            Image = data.ToArray(),
            Corners = MapFileImporter.BoxCorners(ne.Lat, sw.Lat, ne.Lon, sw.Lon, 15)
        });
        content.Features.Add(new OverlayFeature
        {
            Name = "Fence", Geometry = OverlayGeometry.Polygon, Stroke = 0xFFFF8800,
            Parts = { new List<GeoPoint> { SampleRegion.At(60, 600), SampleRegion.At(500, 600), SampleRegion.At(500, 940), SampleRegion.At(60, 940) } }
        });
        content.Features.Add(new OverlayFeature
        {
            Name = "5 km course", Geometry = OverlayGeometry.Line, Stroke = 0xFF00A0FF, Width = 4,
            Parts = { new List<GeoPoint> { SampleRegion.At(-400, 300), SampleRegion.At(-100, 450), SampleRegion.At(60, 700) } }
        });
        foreach (var (name, x, y) in new[] { ("Gate A", 60, 770), ("Medic tent", 300, 650), ("Lost kids", 420, 880) })
            content.Features.Add(new OverlayFeature { Name = name, Geometry = OverlayGeometry.Point, Parts = { new List<GeoPoint> { SampleRegion.At(x, y) } } });
        return new OverlayLayer(new OverlayLayerInfo { Name = content.Name, ImageOpacity = 0.85f }, content);
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("night")]
    public void Draws_event_layers_with_their_site_plan(string theme)
    {
        using var tiles = new MbTilesReader(Path.Combine(_f.Dir, RegionBuilder.TilesFile));
        var layer = SampleEventLayer();
        var scene = new MapScene
        {
            Regions = new[] { new TileSource(tiles, new GeoBounds(_f.Manifest.MinLat, _f.Manifest.MinLon, _f.Manifest.MaxLat, _f.Manifest.MaxLon)) },
            Tiles = new TileCache(),
            Style = theme == "night" ? MapStyle.Night : MapStyle.Standard,
            Pois = new PoiIndex(_f.Search.LoadPois()),
            Synchronous = true,
            UiScale = 1.3f,
            Overlays = new[] { layer }
        };
        var vp = Viewport.At(SampleRegion.At(150, 680), 16, 0, 1000, 760);
        using var bmp = new SKBitmap(vp.Width, vp.Height);
        RenderResult result;
        using (var canvas = new SKCanvas(bmp))
            result = MapRenderer.Render(canvas, vp, scene);
        Directory.CreateDirectory(OutputDir);
        using (var img = SKImage.FromBitmap(bmp))
        using (var data = img.Encode(SKEncodedImageFormat.Png, 90))
        using (var fs = File.Create(Path.Combine(OutputDir, $"event-layer-{theme}.png")))
            data.SaveTo(fs);
        Assert.Equal(3, result.OverlayPoints.Count);
        var (mx, my) = vp.GeoToScreen(SampleRegion.At(300, 650));
        Assert.Contains(result.OverlayPoints, h => h.Feature.Name == "Medic tent" && h.Rect.Contains((float)mx, (float)my));
        if (theme == "standard")
        {
            // The site plan's cream background shows at its centre.
            var (cx, cy) = vp.GeoToScreen(SampleRegion.At(280, 770));
            var c = bmp.GetPixel((int)cx, (int)cy);
            Assert.True(c.Red > 150 && c.Green > 140, $"site plan not drawn: {c}");
        }
    }

    [Theory]
    [InlineData(BaseMapMode.Imagery, "standard")]
    [InlineData(BaseMapMode.Hybrid, "standard")]
    [InlineData(BaseMapMode.Hybrid, "night")]
    public void Draws_imagery_and_hybrid_base_maps(BaseMapMode mode, string theme)
    {
        var path = SyntheticImagery();
        var desc = RasterTileStore.Describe(path);
        Assert.Equal("Synthetic test imagery", desc.Name);
        Assert.Equal(12, desc.MinZoom);
        Assert.Equal(15, desc.MaxZoom);
        using var store = new RasterTileStore(path, readOnly: true);
        var requested = new List<(int, int, int)>();
        using var cache = new RasterTileCache(desc.MinZoom, desc.MaxZoom, store.Get, (z, x, y) => requested.Add((z, x, y)));
        using var tiles = new MbTilesReader(Path.Combine(_f.Dir, RegionBuilder.TilesFile));
        var style = theme == "night" ? MapStyle.Night : MapStyle.Standard;
        var scene = new MapScene
        {
            Regions = new[] { new TileSource(tiles, new GeoBounds(_f.Manifest.MinLat, _f.Manifest.MinLon, _f.Manifest.MaxLat, _f.Manifest.MaxLon)) },
            Tiles = new TileCache(),
            Style = style,
            Pois = new PoiIndex(_f.Search.LoadPois()),
            Synchronous = true,
            UiScale = 1.3f,
            Imagery = cache,
            BaseMode = mode,
            Aprs = SampleAprs(),
            Attribution = "SAMPLE DATA – fictitious · SYNTHETIC IMAGERY (test)"
        };
        // Zoom 16.4 is past the package's last level (15): tiles are stretched, nothing is missing.
        var vp = Viewport.At(SampleRegion.At(0, 560), 16.4, 0, 1000, 760);
        using var bmp = new SKBitmap(vp.Width, vp.Height);
        using (var canvas = new SKCanvas(bmp))
            MapRenderer.Render(canvas, vp, scene);
        Directory.CreateDirectory(OutputDir);
        using (var img = SKImage.FromBitmap(bmp))
        using (var data = img.Encode(SKEncodedImageFormat.Png, 90))
        using (var fs = File.Create(Path.Combine(OutputDir, $"imagery-{mode}-{theme}.png".ToLowerInvariant())))
            data.SaveTo(fs);
        Assert.Empty(requested);
        // Most of the frame is the (green) imagery, not the map background.
        var green = 0;
        for (var y = 0; y < bmp.Height; y += 8)
            for (var x = 0; x < bmp.Width; x += 8)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Green > c.Blue && c.Green >= c.Red)
                    green++;
            }
        Assert.True(green > (bmp.Width / 8) * (bmp.Height / 8) / 3, $"only {green} imagery samples");

        // Outside the package: the missing tile is requested once.
        var far = Viewport.At(new GeoPoint(SampleRegion.Origin.Lat + 1, SampleRegion.Origin.Lon), 13, 0, 400, 300);
        using var bmp2 = new SKBitmap(far.Width, far.Height);
        using (var canvas = new SKCanvas(bmp2))
            MapRenderer.Render(canvas, far, scene);
        Assert.NotEmpty(requested);
        var count = requested.Count;
        using (var canvas = new SKCanvas(bmp2))
            MapRenderer.Render(canvas, far, scene);
        Assert.Equal(count, requested.Count);
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("daylight")]
    [InlineData("night")]
    public void Renders_the_sample_town_in_every_theme(string theme)
    {
        var style = theme switch { "daylight" => MapStyle.Daylight, "night" => MapStyle.Night, _ => MapStyle.Standard };
        using var bmp = Render(style, 15.2, 0, "town-" + theme, withExtras: true);
        // A good share of the frame is something other than the background (roads, areas, labels).
        var other = 0;
        for (var y = 0; y < bmp.Height; y += 4)
            for (var x = 0; x < bmp.Width; x += 4)
                if (bmp.GetPixel(x, y) != style.Background)
                    other++;
        Assert.True(other > (bmp.Width / 4) * (bmp.Height / 4) / 8, $"only {other} non-background samples");
        if (theme == "night")
        {
            // Night: nothing bright except small symbols.
            var bright = 0;
            for (var y = 0; y < bmp.Height; y += 2)
                for (var x = 0; x < bmp.Width; x += 2)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.Red * 0.299 + c.Green * 0.587 + c.Blue * 0.114 > 200)
                        bright++;
                }
            Assert.True(bright < (bmp.Width / 2) * (bmp.Height / 2) / 200, $"{bright} bright pixels at night");
        }
    }

    [Fact]
    public void Rotated_and_regional_views_render()
    {
        using var rotated = Render(MapStyle.Standard, 14.5, 35, "rotated", withExtras: true);
        using var region = Render(MapStyle.Standard, 11.5, 0, "region", withExtras: false);
        using var far = Render(MapStyle.Daylight, 6, 0, "far", withExtras: false);
        Assert.Equal(1000, far.Width);
    }

    [Fact]
    public void Viewport_round_trips_screen_and_geo_with_rotation()
    {
        var vp = Viewport.At(new GeoPoint(50.67, -120.33), 15.3, 37, 1200, 800, 1.25f);
        var p = new GeoPoint(50.672, -120.325);
        var (x, y) = vp.GeoToScreen(p);
        var back = vp.ScreenToGeo(x, y);
        Assert.True(back.DistanceTo(p) < 0.01);
        var center = vp.ScreenToGeo(600, 400);
        Assert.True(center.DistanceTo(new GeoPoint(50.67, -120.33)) < 0.01);
        // With the map turned so east is up, a point to the east is above the centre.
        var eastUp = Viewport.At(new GeoPoint(50.67, -120.33), 15, 90, 1000, 1000);
        var (ex, ey) = eastUp.GeoToScreen(new GeoPoint(50.67, -120.32));
        Assert.True(ey < 500 - 100 && Math.Abs(ex - 500) < 1);
        Assert.Equal(500, MapRenderer.NiceDistance(730));
        Assert.Equal(2000, MapRenderer.NiceDistance(2100));
    }
}
