using FieldMap.Core.Drawings;
using FieldMap.Core.Geo;
using FieldMap.Core.Interop;

namespace FieldMap.Tests;

public class DrawingTests
{
    private static readonly GeoPoint Kamloops = new(50.6745, -120.3273);

    private static List<Drawing> Sample() => new()
    {
        new() { Kind = DrawingKind.Incident, Shape = DrawingShape.Point, Points = { Kamloops }, Label = "MVI 2 vehicles", Notes = "Hwy 1 eastbound, \"quoted\" & <tag>" },
        new() { Kind = DrawingKind.RoadClosure, Shape = DrawingShape.Line, Points = { Kamloops, Geometry.Destination(Kamloops, 90, 500) }, Label = "Closure" },
        new() { Kind = DrawingKind.Cordon, Shape = DrawingShape.Polygon, Points = { Geometry.Destination(Kamloops, 0, 100), Geometry.Destination(Kamloops, 120, 100), Geometry.Destination(Kamloops, 240, 100) }, ClosesRoads = true },
        new() { Kind = DrawingKind.RangeRings, Shape = DrawingShape.Point, Points = { Kamloops }, Rings = { 500, 1000, 2000 } },
        new() { Kind = DrawingKind.Hazmat, Shape = DrawingShape.Point, Points = { Kamloops }, IsolationMeters = 100, ProtectiveMeters = 800, WindFromDegrees = 270, UnNumber = "1005" }
    };

    [Fact]
    public void Hazmat_zone_points_downwind()
    {
        var h = Sample()[4];
        Assert.Equal(90, h.DownwindBearing);
        var iso = h.IsolationRing();
        Assert.All(iso, p => Assert.InRange(p.DistanceTo(Kamloops), 99, 101));
        var sector = h.ProtectiveZone();
        // Every point of the sector is east of the spill (wind from the west) and within 800 m.
        Assert.All(sector, p => Assert.True(p.Lon >= Kamloops.Lon - 1e-9));
        Assert.All(sector, p => Assert.True(p.DistanceTo(Kamloops) <= 801));
        Assert.Contains(sector, p => Math.Abs(p.DistanceTo(Kamloops) - 800) < 1 && Math.Abs(Kamloops.BearingTo(p) - 90) < 1);

        h.ProtectiveShape = ProtectiveShape.ErgSquare;
        var square = h.ProtectiveZone();
        Assert.Equal(5, square.Count);
        Assert.InRange(square[0].DistanceTo(square[3]), 799, 801); // width = protective distance
        Assert.InRange(square[0].DistanceTo(square[1]), 799, 801); // length = protective distance
        var text = h.DispatchText();
        Assert.Contains("UN1005", text);
        Assert.Contains("Isolate 100 m", text);
        Assert.Contains("wind from 270", text);
        Assert.Contains("downwind 090", text);
    }

    [Fact]
    public void Measurements()
    {
        var square = new Drawing
        {
            Kind = DrawingKind.Measure, Shape = DrawingShape.Polygon,
            Points = { Kamloops, Geometry.Destination(Kamloops, 90, 100), Geometry.Destination(Geometry.Destination(Kamloops, 90, 100), 0, 100), Geometry.Destination(Kamloops, 0, 100) }
        };
        Assert.InRange(square.AreaSquareMeters, 9_900, 10_100);
        Assert.InRange(square.LengthMeters, 399, 401);
        Assert.Equal("1.00 ha (2.5 acres)", Drawing.FormatArea(10_000));
        var line = Sample()[1];
        Assert.InRange(line.LengthMeters, 499, 501);
    }

    [Theory]
    [InlineData(".geojson")]
    [InlineData(".kml")]
    public void Round_trip_keeps_kinds_labels_times_and_parameters(string ext)
    {
        var original = Sample();
        var path = Path.Combine(Path.GetTempPath(), "drawings-" + Guid.NewGuid().ToString("N")[..6] + ext);
        DrawingFormats.Export(path, original);
        var back = DrawingFormats.Import(path);
        File.Delete(path);
        Assert.Equal(original.Count, back.Count); // derived rings are not re-imported as drawings
        for (var i = 0; i < original.Count; i++)
        {
            var a = original[i];
            var b = back[i];
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.Shape, b.Shape);
            Assert.Equal(a.Label, b.Label);
            Assert.Equal(a.Notes, b.Notes);
            Assert.Equal(a.Points.Count, b.Points.Count);
            Assert.True(a.Points.Zip(b.Points).All(p => p.First.DistanceTo(p.Second) < 0.05));
            Assert.Equal(a.Rings, b.Rings);
            Assert.Equal(a.IsolationMeters, b.IsolationMeters);
            Assert.Equal(a.ProtectiveMeters, b.ProtectiveMeters);
            Assert.Equal(a.WindFromDegrees, b.WindFromDegrees);
            Assert.Equal(a.ClosesRoads, b.ClosesRoads);
            Assert.True(Math.Abs((a.CreatedUtc - b.CreatedUtc).TotalSeconds) < 1);
        }
    }

    [Fact]
    public void Gpx_exports_waypoints_routes_and_tracks()
    {
        var gpx = DrawingFormats.ToGpx(Sample(), new[] { ("Breadcrumbs", (IReadOnlyList<GeoPoint>)new[] { Kamloops, Geometry.Destination(Kamloops, 0, 50) }, (IReadOnlyList<DateTime>?)new[] { DateTime.UtcNow, DateTime.UtcNow }) });
        Assert.Contains("<wpt", gpx);
        Assert.Contains("<rte>", gpx);
        Assert.Contains("<trk>", gpx);
        var back = DrawingFormats.FromGpx(gpx);
        Assert.Contains(back, d => d.Kind == DrawingKind.Incident && d.Label == "MVI 2 vehicles");
        Assert.Contains(back, d => d.Kind == DrawingKind.RoadClosure && d.Shape == DrawingShape.Line);
        Assert.Contains(back, d => d.Kind == DrawingKind.Cordon && d.Shape == DrawingShape.Polygon && d.Points.Count == 3);
        Assert.Contains(back, d => d.Label == "Breadcrumbs");
    }

    [Fact]
    public void Foreign_geojson_and_kml_import_with_sensible_kinds()
    {
        var geojson = """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[-120,50],[-120.01,50],[-120.01,50.01],[-120,50]]]},"properties":{"name":"Fire edge"}}]}""";
        var d = DrawingFormats.FromGeoJson(geojson).Single();
        Assert.Equal(DrawingKind.Perimeter, d.Kind);
        Assert.Equal("Fire edge", d.Label);
        Assert.Equal(3, d.Points.Count);
        var kml = """<?xml version="1.0"?><kml xmlns="http://www.opengis.net/kml/2.2"><Placemark><name>RV</name><Point><coordinates>-120.5,50.5,0</coordinates></Point></Placemark></kml>""";
        var k = DrawingFormats.FromKml(kml).Single();
        Assert.Equal(DrawingKind.Note, k.Kind);
        Assert.Equal(50.5, k.Points[0].Lat);
    }

    [Fact]
    public void Exchange_folder_passes_points_between_apps()
    {
        var root = Path.Combine(Path.GetTempPath(), "exch-" + Guid.NewGuid().ToString("N")[..6]);
        var inbox = Exchange.Inbox(Exchange.ToFieldMap, root);
        Exchange.Send(inbox, new ExchangeMessage { Name = "Incident site", Lat = 50.67, Lon = -120.33, Note = "From Location tool", From = "Field Toolkit" });
        Exchange.Send(inbox, new ExchangeMessage { Name = "RV", Lat = 50.68, Lon = -120.34, From = "Field Toolkit" });
        File.WriteAllText(Path.Combine(inbox, "zzz.json"), "{ not json");
        File.WriteAllText(Path.Combine(inbox, "partial.tmp"), "{");
        var got = Exchange.Receive(inbox);
        Assert.Equal(new[] { "Incident site", "RV" }, got.Select(m => m.Name));
        Assert.Empty(Directory.GetFiles(inbox, "*.json"));
        Assert.True(File.Exists(Path.Combine(inbox, "zzz.json.bad")));
        Assert.True(File.Exists(Path.Combine(inbox, "partial.tmp"))); // half-written files are left alone
        Assert.Empty(Exchange.Receive(inbox));
        Directory.Delete(root, true);
    }
}
