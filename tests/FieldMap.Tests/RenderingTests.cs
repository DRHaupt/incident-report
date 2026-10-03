using FieldMap.Core.Aprs;
using FieldMap.Core.Data;
using FieldMap.Core.Imagery;
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
