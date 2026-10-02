using FieldMap.Core.Build;
using FieldMap.Core.Data;
using FieldMap.Core.Geo;
using FieldMap.Core.Live;
using FieldMap.Core.Osm;
using FieldMap.Core.Search;
using FieldMap.Core.Tiles;

namespace FieldMap.Tests;

public class CoordinateRoundTripTests
{
    /// <summary>Points across BC and Alberta, including the UTM zone 7–12 range, border areas and the far north.</summary>
    public static IEnumerable<object[]> Points() => new[]
    {
        new object[] { 49.0, -123.0 }, new object[] { 49.2827, -123.1207 }, new object[] { 48.4284, -123.3656 }, new object[] { 54.3150, -130.3208 },
        new object[] { 58.8050, -122.6972 }, new object[] { 59.9999, -139.0 }, new object[] { 50.6745, -120.3273 }, new object[] { 49.9, -114.06 },
        new object[] { 51.0447, -114.0719 }, new object[] { 53.5461, -113.4938 }, new object[] { 56.7264, -111.3803 }, new object[] { 59.9, -110.0 },
        new object[] { 49.0001, -110.0001 }, new object[] { 52.0, -120.0001 }, new object[] { 52.0, -119.9999 }, new object[] { 55.7596, -120.2353 }
    };

    [Theory]
    [MemberData(nameof(Points))]
    public void Mgrs_utm_dms_and_decimal_round_trip(double lat, double lon)
    {
        Assert.True(CoordinateFormat.TryParse(CoordinateFormat.Mgrs(lat, lon), out var mlat, out var mlon));
        Assert.True(GeoMath.Distance(lat, lon, mlat, mlon) < 1.5, "MGRS");
        Assert.True(QueryParser.TryUtm(CoordinateFormat.Utm(lat, lon), out var u));
        Assert.True(GeoMath.Distance(lat, lon, u.Lat, u.Lon) < 1.5, "UTM");
        Assert.True(CoordinateFormat.TryParse(CoordinateFormat.Dms(lat, lon), out var dlat, out var dlon));
        Assert.True(GeoMath.Distance(lat, lon, dlat, dlon) < 3.5, "DMS");
        Assert.True(CoordinateFormat.TryParse(CoordinateFormat.Ddm(lat, lon), out var mmlat, out var mmlon));
        Assert.True(GeoMath.Distance(lat, lon, mmlat, mmlon) < 2, "DDM");
        Assert.True(CoordinateFormat.TryParse(CoordinateFormat.Decimal(lat, lon), out var clat, out var clon));
        Assert.True(GeoMath.Distance(lat, lon, clat, clon) < 1, "decimal");
        var (x, y) = WebMercator.ToWorld(lat, lon);
        var back = WebMercator.FromWorld(x, y);
        Assert.True(GeoMath.Distance(lat, lon, back.Lat, back.Lon) < 0.01, "Web Mercator");
    }
}

public class TileFormatTests
{
    [Fact]
    public void Vector_tile_round_trips_through_gzip()
    {
        var tile = new VectorTile();
        var layer = new VectorLayer { Name = "roads" };
        layer.Features.Add(new VectorFeature { Type = GeomType.Line, Parts = { new[] { new Vec(10, 10), new Vec(100, 50), new Vec(4000, 4090) } }, Props = { ["name"] = "Main Street", ["oneway"] = 1L, ["bridge"] = true } });
        layer.Features.Add(new VectorFeature { Type = GeomType.Point, Parts = { new[] { new Vec(5, 6) } }, Props = { ["rank"] = 2L, ["height"] = 4.2 } });
        layer.Features.Add(new VectorFeature { Type = GeomType.Polygon, Parts = { new[] { new Vec(0, 0), new Vec(100, 0), new Vec(100, 100), new Vec(0, 100), new Vec(0, 0) } }, Props = { ["kind"] = "park" } });
        tile.Layers.Add(layer);
        var back = VectorTile.Decode(VectorTile.Gzip(tile.Encode()));
        var l = back.Layer("roads")!;
        Assert.Equal(3, l.Features.Count);
        Assert.Equal("Main Street", l.Features[0].Str("name"));
        Assert.Equal(1, l.Features[0].Int("oneway"));
        Assert.True(l.Features[0].Flag("bridge"));
        Assert.Equal(new[] { new Vec(10, 10), new Vec(100, 50), new Vec(4000, 4090) }, l.Features[0].Parts[0]);
        Assert.Equal(4.2, (double)l.Features[1].Props["height"]);
        Assert.Equal(GeomType.Polygon, l.Features[2].Type);
        Assert.Equal(5, l.Features[2].Parts[0].Length); // closed ring
    }

    [Fact]
    public void Line_clipping_and_simplification()
    {
        var pieces = Geometry.ClipLine(new[] { new Vec(-10, 5), new Vec(5, 5), new Vec(5, 20), new Vec(8, 5) }, 0, 0, 10, 10);
        Assert.Equal(2, pieces.Count);
        Assert.Equal(new Vec(0, 5), pieces[0][0]);
        var simple = Geometry.Simplify(new[] { new Vec(0, 0), new Vec(1, 0.01), new Vec(2, 0), new Vec(3, 5) }, 0.1);
        Assert.Equal(3, simple.Count);
        var poly = Geometry.ClipPolygon(new[] { new Vec(-5, -5), new Vec(5, -5), new Vec(5, 5), new Vec(-5, 5) }, 0, 0, 10, 10);
        Assert.Equal(4, poly.Count);
        Assert.All(poly, p => Assert.True(p.X >= 0 && p.Y >= 0));
    }
}

public class CatalogTests
{
    private static string Content(string name) => Path.Combine(AppContext.BaseDirectory, "Content", name);

    [Fact]
    public void Region_catalogue_lists_bc_and_ab_with_licences()
    {
        var cat = RegionCatalog.Load(Content("regions.json"));
        foreach (var id in new[] { "bc", "ab" })
        {
            var r = cat.Find(id)!;
            var osm = r.Sources.Single(s => s.Kind == SourceKinds.OsmPbf);
            Assert.True(osm.Required);
            Assert.StartsWith("https://download.geofabrik.de/", osm.Url);
            Assert.Equal("md5", osm.ChecksumType);
            Assert.All(r.Sources, s => Assert.False(string.IsNullOrWhiteSpace(s.Licence)));
            Assert.All(r.Sources, s => Assert.False(string.IsNullOrWhiteSpace(s.Attribution)));
            Assert.All(r.Sources.Where(s => s.Kind == SourceKinds.PoiTable), s => Assert.NotNull(s.Mapping));
            // Nothing unverified is switched on by default except optional address files (skipped quietly if they fail).
            Assert.All(r.Sources.Where(s => s.Enabled && s.Verified.StartsWith("unverified")), s => Assert.False(s.Required));
            Assert.True(r.ApproxDownloadBytes > 100_000_000);
        }
    }

    [Fact]
    public void Live_feed_catalogue_parses()
    {
        var cat = LiveFeedCatalog.LoadOrEmpty(Content("live-feeds.json"));
        Assert.Contains(cat.Feeds, f => f.Id == "drivebc" && f.Format == "open511");
        Assert.Contains(cat.Feeds, f => f.Id == "ab511" && f.NeedsKey);
        Assert.Contains(cat.Feeds, f => f.Kind == LiveKind.WeatherAlert);
        Assert.All(cat.Feeds, f => Assert.False(string.IsNullOrWhiteSpace(f.Licence)));
    }
}

public class LiveFeedTests
{
    [Fact]
    public void Parses_open511_ab511_and_geojson()
    {
        var open511 = """{"events":[{"id":"drivebc.ca/DBC-1","headline":"INCIDENT","description":"Highway 1 closed both directions","severity":"MAJOR","updated":"2026-10-02T10:00:00-07:00","roads":[{"name":"Highway 1"}],"geography":{"type":"Point","coordinates":[-121.5,50.2]}}]}""";
        var e = LiveFeedService.ParseOpen511(open511, LiveKind.RoadEvent).Single();
        Assert.Equal("INCIDENT – Highway 1", e.Title);
        Assert.Equal("MAJOR", e.Severity);
        Assert.Equal(new DateTime(2026, 10, 2, 17, 0, 0, DateTimeKind.Utc), e.UpdatedUtc);
        Assert.Equal(50.2, e.Parts[0][0].Lat);

        var ab = """[{"ID":"1","RoadwayName":"Hwy 2","EventType":"closures","Description":"Closed","Latitude":51.0,"Longitude":-114.0,"LastUpdated":1790000000,"IsFullClosure":true}]""";
        var a = LiveFeedService.ParseAb511(ab, LiveKind.RoadEvent).Single();
        Assert.Equal("closures – Hwy 2", a.Title);
        Assert.Equal("Full closure", a.Severity);

        var feed = new LiveFeedDefinition { Name = "Fires", Kind = LiveKind.FirePerimeter, TitleFields = { "FIRE_NUMBER" }, SeverityFields = { "FIRE_STATUS" }, UpdatedFields = { "TRACK_DATE" } };
        var gj = """{"type":"FeatureCollection","features":[{"type":"Feature","id":"f1","properties":{"FIRE_NUMBER":"K71234","FIRE_STATUS":"Out of Control","TRACK_DATE":1790000000000},"geometry":{"type":"MultiPolygon","coordinates":[[[[-120,50],[-120.1,50],[-120.1,50.1],[-120,50]]]]}}]}""";
        var f = LiveFeedService.ParseGeoJson(gj, feed).Single();
        Assert.Equal("K71234", f.Title);
        Assert.True(f.IsArea);
        Assert.Equal(4, f.Parts[0].Count);
        Assert.NotNull(f.UpdatedUtc);
    }

    [Fact]
    public async Task Offline_fetch_falls_back_to_the_cache_quietly()
    {
        using var server = new TestHttpServer();
        server.Serve("events", System.Text.Encoding.UTF8.GetBytes("""{"events":[{"id":"1","headline":"CLOSURE","geography":{"type":"Point","coordinates":[-120,50]}}]}"""));
        var cache = Path.Combine(Path.GetTempPath(), "live-" + Guid.NewGuid().ToString("N")[..6]);
        var service = new LiveFeedService(new HttpClient(), cache);
        var feed = new LiveFeedDefinition { Id = "t", Name = "Test", Format = "open511", Url = server.BaseUrl + "events" };
        var live = await service.FetchAsync(feed);
        Assert.False(live.FromCache);
        Assert.Single(live.Items);

        feed.Url = "http://127.0.0.1:1/unreachable";
        var offline = await service.FetchAsync(feed);
        Assert.True(offline.FromCache);
        Assert.NotNull(offline.Error);
        Assert.Single(offline.Items);
        Assert.Equal(live.FetchedUtc, offline.FetchedUtc);

        var keyed = await service.FetchAsync(new LiveFeedDefinition { Id = "k", Format = "ab511", Url = "http://127.0.0.1:1/?key={key}", NeedsKey = true });
        Assert.Empty(keyed.Items);
        Assert.Contains("key", keyed.Error);
        Directory.Delete(cache, true);
    }
}

/// <summary>Real BC/AB names in a small index: abbreviations, typos and spelling variants people type in the field.</summary>
public class RealPlaceSearchTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "realplaces-" + Guid.NewGuid().ToString("N")[..6] + ".sqlite");
    private readonly SearchEngine _search;

    public RealPlaceSearchTests()
    {
        var data = new MapData();
        void Place(string name, string kind, double lat, double lon) => data.Places.Add(new Place { Name = name, Kind = kind, Lat = lat, Lon = lon });
        Place("Vancouver", "city", 49.2827, -123.1207);
        Place("Kamloops", "city", 50.6745, -120.3273);
        Place("Prince George", "city", 53.9171, -122.7497);
        Place("Fort St. John", "city", 56.2524, -120.8466);
        Place("Kelowna", "city", 49.8880, -119.4960);
        Place("Calgary", "city", 51.0447, -114.0719);
        Place("Edmonton", "city", 53.5461, -113.4938);
        Place("Fort McMurray", "town", 56.7264, -111.3803);
        Place("Red Deer", "city", 52.2681, -113.8112);
        Place("Lethbridge", "city", 49.6935, -112.8418);
        Place("Grande Prairie", "city", 55.1707, -118.7947);
        Place("Williams Lake", "city", 52.1417, -122.1417);
        Place("100 Mile House", "town", 51.6418, -121.2940);
        void Poi(string name, string layer, string kind, double lat, double lon) =>
            data.Pois.Add(new Poi { Id = data.Pois.Count + 1, Name = name, Layer = layer, Kind = kind, Lat = lat, Lon = lon });
        Poi("Royal Columbian Hospital", "hospital", "ed", 49.2270, -122.8916);
        Poi("Vancouver General Hospital", "hospital", "ed", 49.2618, -123.1236);
        Poi("St. Paul's Hospital", "hospital", "ed", 49.2805, -123.1286);
        Poi("Kelowna General Hospital", "hospital", "ed", 49.8735, -119.4934);
        Poi("Royal Inland Hospital", "hospital", "ed", 50.6719, -120.3335);
        Poi("University Hospital of Northern British Columbia", "hospital", "ed", 53.9170, -122.7680);
        Poi("Foothills Medical Centre", "hospital", "ed", 51.0652, -114.1334);
        Poi("Peter Lougheed Centre", "hospital", "ed", 51.0790, -113.9858);
        Poi("Royal Alexandra Hospital", "hospital", "ed", 53.5578, -113.4975);
        Poi("Red Deer Regional Hospital Centre", "hospital", "ed", 52.2604, -113.8140);
        GeoPoint[] L(params (double, double)[] p) => p.Select(x => new GeoPoint(x.Item1, x.Item2)).ToArray();
        MapWay Road(string name, params (double, double)[] p) => new() { Points = L(p), NodeIds = new long[p.Length], Tags = new OsmTags(new[] { new KeyValuePair<string, string>("highway", "primary"), new("name", name) }) };
        data.Roads.Add(Road("Lougheed Highway", (49.2490, -122.8900), (49.2400, -122.8000)));
        data.Roads.Add(Road("Deerfoot Trail NE", (51.0600, -114.0300), (51.1000, -114.0200)));
        data.Roads.Add(Road("Yellowhead Highway", (53.5800, -113.6000), (53.5800, -113.4000)));
        data.Roads.Add(Road("Kingsway", (49.2400, -123.0500), (49.2200, -123.0000)));
        SearchIndexBuilder.Build(data, null, _path, new Dictionary<string, string>());
        _search = new SearchEngine(_path);
    }

    [Theory]
    [InlineData("Kamloosp", "Kamloops")]
    [InlineData("kamloops", "Kamloops")]
    [InlineData("Prince Goerge", "Prince George")]
    [InlineData("Ft St John", "Fort St. John")]
    [InlineData("fort saint john", "Fort St. John")]
    [InlineData("Ft McMurray", "Fort McMurray")]
    [InlineData("Grand Prairie", "Grande Prairie")]
    [InlineData("100 mile", "100 Mile House")]
    [InlineData("Royal Columbian Hosp", "Royal Columbian Hospital")]
    [InlineData("royal colombian", "Royal Columbian Hospital")]
    [InlineData("VGH", null)]
    [InlineData("Kelowna Gen Hospital", "Kelowna General Hospital")]
    [InlineData("St Pauls Hospital", "St. Paul's Hospital")]
    [InlineData("Foothills Medical Center", "Foothills Medical Centre")]
    [InlineData("foothills hosp", "Foothills Medical Centre")]
    [InlineData("Royal Alex", "Royal Alexandra Hospital")]
    [InlineData("UHNBC", null)]
    [InlineData("University Hospital Northern BC", "University Hospital of Northern British Columbia")]
    [InlineData("Lougheed Hwy", "Lougheed Highway")]
    [InlineData("Deerfoot Trl", "Deerfoot Trail NE")]
    [InlineData("yellowhead hwy", "Yellowhead Highway")]
    [InlineData("Red Deer Regional", "Red Deer Regional Hospital Centre")]
    public void Finds_real_names(string query, string? expected)
    {
        var results = _search.Search(query);
        if (expected == null)
        {
            // Acronyms aren't expanded (yet): they must not produce a confident wrong answer.
            Assert.True(results.Count == 0 || results[0].Score < 0.9, $"{query} → {results.FirstOrDefault()}");
            return;
        }
        Assert.NotEmpty(results);
        Assert.Equal(expected, results[0].Name);
    }

    [Fact]
    public void Distance_breaks_ties_between_similar_names()
    {
        // "General Hospital" near Kelowna should prefer Kelowna General over Vancouver General.
        var near = _search.Search("General Hospital", new GeoPoint(49.88, -119.49));
        Assert.Equal("Kelowna General Hospital", near[0].Name);
        var far = _search.Search("General Hospital", new GeoPoint(49.26, -123.12));
        Assert.Equal("Vancouver General Hospital", far[0].Name);
    }

    public void Dispose()
    {
        _search.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }
}
