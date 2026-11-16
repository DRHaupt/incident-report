using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FieldMap.Core.Geo;
using FieldMap.Core.Live;

namespace FieldMap.Web.Tests;

/// <summary>Live hazards: feeds fetched by the server, kept to the installed regions, simplified for phones.</summary>
public sealed class LiveTests : IClassFixture<ServerFixture>, IDisposable
{
    private readonly ServerFixture _fx;
    private readonly HttpListener _feeds = new();
    private readonly string _catalog = Path.Combine(Path.GetTempPath(), "fm-live-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly int _port;

    // Inside the sample region (around 57.40 N, 125.90 W) and far outside it (Vancouver).
    private const double InLat = 57.405, InLon = -125.902;

    public LiveTests(ServerFixture fx)
    {
        _fx = fx;
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _feeds.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _feeds.Start();
        _ = Task.Run(Serve);
        File.WriteAllText(_catalog, JsonSerializer.Serialize(new
        {
            feeds = new object[]
            {
                new { id = "roads", name = "Test roads", kind = "roadEvent", format = "open511", url = $"http://127.0.0.1:{_port}/roads", refreshMinutes = 5 },
                new { id = "fires", name = "Test fires", kind = "firePerimeter", format = "geojson", url = $"http://127.0.0.1:{_port}/fires", refreshMinutes = 5,
                      titleFields = new[] { "FIRE_NUMBER" }, severityFields = new[] { "FIRE_STATUS" } },
                new { id = "keyed", name = "Needs a key", kind = "roadEvent", format = "ab511", url = $"http://127.0.0.1:{_port}/keyed?key={{key}}", needsKey = true },
                new { id = "off", name = "Not configured", kind = "evacuation", format = "geojson", url = "", enabled = true }
            }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    public void Dispose()
    {
        _feeds.Close();
        File.Delete(_catalog);
    }

    private static string Ring(double lat, double lon, int points)
    {
        // A circle of about 1 km radius with many points: simplification must shrink it a lot.
        var inv = CultureInfo.InvariantCulture;
        var pts = Enumerable.Range(0, points).Select(i =>
        {
            var a = 2 * Math.PI * i / points;
            return string.Format(inv, "[{0},{1}]", lon + 0.016 * Math.Cos(a), lat + 0.009 * Math.Sin(a));
        }).ToList();
        pts.Add(pts[0]);
        return "[" + string.Join(',', pts) + "]";
    }

    private async Task Serve()
    {
        while (_feeds.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _feeds.GetContextAsync(); } catch { return; }
            var inv = CultureInfo.InvariantCulture;
            var body = ctx.Request.Url!.AbsolutePath switch
            {
                "/roads" => $$"""
                    { "events": [
                      { "id": "A1", "headline": "INCIDENT", "description": "Vehicle fire, lane closed", "severity": "MAJOR", "updated": "2026-11-12T03:00:00Z",
                        "roads": [ { "name": "Hwy 37" } ], "geography": { "type": "Point", "coordinates": [{{InLon.ToString(inv)}}, {{InLat.ToString(inv)}}] } },
                      { "id": "B2", "headline": "CONSTRUCTION", "severity": "MINOR", "geography": { "type": "Point", "coordinates": [-123.1, 49.25] } }
                    ] }
                    """,
                "/fires" => $$"""
                    { "type": "FeatureCollection", "features": [
                      { "type": "Feature", "id": "F1", "properties": { "FIRE_NUMBER": "G70001", "FIRE_STATUS": "Out of Control" },
                        "geometry": { "type": "Polygon", "coordinates": [ {{Ring(InLat, InLon, 2000)}} ] } }
                    ] }
                    """,
                _ => null
            };
            ctx.Response.StatusCode = body == null ? 404 : 200;
            if (body != null)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            ctx.Response.Close();
        }
    }

    [Fact]
    public async Task Feeds_are_fetched_kept_to_the_regions_and_simplified()
    {
        using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            Assert.Contains("INCIDENT", await probe.GetStringAsync($"http://127.0.0.1:{_port}/roads"));
        using var factory = _fx.Factory().WithWebHostBuilder(b => b.UseSetting("FieldMap:Live:Catalog", _catalog));
        var client = factory.CreateClient();

        JsonElement live = default;
        for (var i = 0; i < 50; i++)
        {
            live = await client.GetFromJsonAsync<JsonElement>("/api/live");
            if (live.TryGetProperty("features", out var f) && f.GetArrayLength() >= 2) break;
            await Task.Delay(200);
        }

        Assert.True(live.GetProperty("enabled").GetBoolean());
        var features = live.GetProperty("features").EnumerateArray().ToList();
        Assert.True(features.Count == 2, live.GetProperty("feeds").ToString()); // the Vancouver road event is outside the sample region
        var road = features.Single(f => f.GetProperty("properties").GetProperty("kind").GetString() == "road");
        Assert.Equal("INCIDENT – Hwy 37", road.GetProperty("properties").GetProperty("title").GetString());
        Assert.Equal("MAJOR", road.GetProperty("properties").GetProperty("severity").GetString());

        var fire = features.Single(f => f.GetProperty("properties").GetProperty("kind").GetString() == "fire");
        Assert.Equal("Out of Control", fire.GetProperty("properties").GetProperty("severity").GetString());
        var ring = fire.GetProperty("geometry").GetProperty("coordinates")[0];
        Assert.InRange(ring.GetArrayLength(), 4, 120); // 2001 points in, a few dozen out at 25 m

        // Feeds that aren't configured are left out; one that needs a key says so.
        var feeds = live.GetProperty("feeds").EnumerateArray().ToList();
        Assert.Equal(new[] { "roads", "fires", "keyed" }, feeds.Select(f => f.GetProperty("id").GetString()));
        Assert.True(feeds.Single(f => f.GetProperty("id").GetString() == "keyed").GetProperty("needsKey").GetBoolean());
        Assert.True(feeds.Single(f => f.GetProperty("id").GetString() == "roads").GetProperty("live").GetBoolean());

        var version = live.GetProperty("version").GetInt32();
        var again = await client.GetFromJsonAsync<JsonElement>($"/api/live?v={version}");
        Assert.True(again.GetProperty("unchanged").GetBoolean());
        Assert.False(again.TryGetProperty("features", out _));

        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/info")).GetProperty("live").GetBoolean());
    }

    [Fact]
    public async Task Can_be_turned_off()
    {
        using var factory = _fx.Factory().WithWebHostBuilder(b => { b.UseSetting("FieldMap:Live:Enabled", "false"); b.UseSetting("FieldMap:Live:Catalog", _catalog); });
        var client = factory.CreateClient();
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/live")).GetProperty("enabled").GetBoolean());
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/info")).GetProperty("live").GetBoolean());
    }

    [Fact]
    public void Simplify_keeps_shape_and_never_collapses_a_ring()
    {
        // A straight line with wobble under the tolerance becomes its two ends.
        var line = Enumerable.Range(0, 100).Select(i => new GeoPoint(50 + i * 0.001, -120 + (i % 2) * 0.00005)).ToList();
        Assert.Equal(2, LiveHub.Simplify(line, 25, closed: false).Count);
        // A tiny ring (smaller than the tolerance) stays as it was rather than vanishing.
        var tiny = new List<GeoPoint> { new(50, -120), new(50.00005, -120), new(50.00005, -120.00005), new(50, -120.00005), new(50, -120) };
        Assert.Equal(5, LiveHub.Simplify(tiny, 25, closed: true).Count);
        // Points, lines and areas come out as the right GeoJSON types.
        Assert.Equal("Point", LiveHub.Geometry(new LiveItem { Parts = { new() { new(50, -120) } } })!["type"]!.GetValue<string>());
        Assert.Equal("MultiPolygon", LiveHub.Geometry(new LiveItem { IsArea = true, Parts = { tiny, tiny } })!["type"]!.GetValue<string>());
    }
}
