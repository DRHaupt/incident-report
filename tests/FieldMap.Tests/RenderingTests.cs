using FieldMap.Core.Data;
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
            UiScale = 1.3f
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
