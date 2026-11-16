using System.Globalization;
using System.Text.Json.Nodes;
using FieldMap.Core.Geo;
using FieldMap.Core.Live;

namespace FieldMap.Web;

/// <summary>Live hazard feeds ("Live" under "FieldMap" in appsettings.json). On by default.</summary>
public sealed class LiveOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Feed list. Default: the live-feeds.json shipped with the server (the same as the Windows app's).</summary>
    public string Catalog { get; set; } = "/etc/fieldmap-web/live-feeds.json";

    /// <summary>Keys for feeds that need one, by feed id (e.g. "ab511"). Kept on the server.</summary>
    public Dictionary<string, string> Keys { get; set; } = new();
}

/// <summary>
/// Fetches the live feeds (DriveBC and Alberta 511 road events, wildfire perimeters, evacuation orders and alerts,
/// weather alerts) on their own schedules with the Windows app's parsers, keeps the last good copy on disk for when a
/// feed is down, and turns them into one small GeoJSON for the phones: only items inside the installed regions, with
/// shapes simplified to about 25 m.
/// </summary>
public sealed class LiveHub : BackgroundService
{
    public const double SimplifyMeters = 25;

    private readonly LiveOptions _options;
    private readonly MapHost _host;
    private readonly ILogger<LiveHub> _log;
    private readonly LiveFeedService _service;
    private readonly List<LiveFeedDefinition> _feeds;
    private readonly Dictionary<string, LiveResult> _results = new();
    private readonly object _lock = new();
    private JsonObject? _cache;
    private int _version;
    private string _regionKey = "";

    public LiveHub(WebOptions options, MapHost host, IHttpClientFactory http, ILogger<LiveHub> log, IWebHostEnvironment env)
    {
        _options = options.Live;
        _host = host;
        _log = log;
        var catalog = File.Exists(_options.Catalog) ? _options.Catalog : Path.Combine(env.ContentRootPath, "Content", "live-feeds.json");
        if (!File.Exists(catalog)) catalog = Path.Combine(AppContext.BaseDirectory, "Content", "live-feeds.json");
        _feeds = LiveFeedCatalog.LoadOrEmpty(catalog).Feeds.Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Url)).ToList();
        foreach (var f in _feeds)
            if (_options.Keys.TryGetValue(f.Id, out var key)) f.Key = key;
        var client = http.CreateClient("live");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FieldMapWeb/1.0 (+https://github.com/DRHaupt/incident-report)");
        _service = new LiveFeedService(client, Path.Combine(options.DataDir, "live-cache"));
    }

    public bool Enabled => _options.Enabled;
    public IReadOnlyList<LiveFeedDefinition> Feeds => _feeds;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!_options.Enabled || _feeds.Count == 0)
            return;
        // Start from the last good copies, so phones have something straight away even when offline.
        foreach (var f in _feeds) Store(_service.Cached(f, "not fetched yet"));
        var due = _feeds.ToDictionary(f => f.Id, _ => DateTime.MinValue);
        while (!stop.IsCancellationRequested)
        {
            foreach (var f in _feeds.Where(f => DateTime.UtcNow >= due[f.Id]))
            {
                LiveResult r;
                try { r = await _service.FetchAsync(f, stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception ex) { r = _service.Cached(f, ex.Message); } // e.g. the cache folder isn't writable: one bad feed mustn't stop the rest
                Store(r);
                due[f.Id] = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Max(2, f.RefreshMinutes));
                if (r.Error != null) _log.LogWarning("Live feed {Feed}: {Error} (using the copy from {Fetched:u})", f.Id, r.Error, r.FetchedUtc);
                else _log.LogDebug("Live feed {Feed}: {Count} items", f.Id, r.Items.Count);
            }
            // The regions decide what's kept; follow a newly installed one.
            var regions = RegionKey();
            if (regions != _regionKey) lock (_lock) _cache = null;
            await Task.Delay(TimeSpan.FromSeconds(30), stop);
        }
    }

    private string RegionKey() => string.Join(';', _host.Current.Regions.Select(r => r.Id + r.Manifest.Version));

    private void Store(LiveResult r)
    {
        lock (_lock)
        {
            _results[r.Feed.Id] = r;
            _cache = null;
        }
    }

    /// <summary>Everything the phones show, rebuilt only when a feed or the regions changed.</summary>
    public (int Version, JsonObject Data) Snapshot()
    {
        lock (_lock)
        {
            if (_cache != null) return (_version, _cache);
            var regions = _host.Current.Regions.Select(r => r.Bounds).ToList();
            var features = new JsonArray();
            var feeds = new JsonArray();
            foreach (var f in _feeds)
            {
                _results.TryGetValue(f.Id, out var r);
                var kept = 0;
                foreach (var item in r?.Items ?? new List<LiveItem>())
                {
                    if (item.Parts.Count == 0) continue;
                    var b = item.Bounds;
                    if (regions.Count > 0 && !regions.Any(g => Overlaps(g, b))) continue;
                    if (Geometry(item) is not { } geometry) continue;
                    features.Add(new JsonObject
                    {
                        ["type"] = "Feature",
                        ["geometry"] = geometry,
                        ["properties"] = new JsonObject
                        {
                            ["id"] = f.Id + ":" + item.Id,
                            ["feed"] = f.Id,
                            ["kind"] = Kind(item.Kind),
                            ["title"] = Trim(item.Title, 160),
                            ["description"] = Trim(item.Description, 1500),
                            ["severity"] = Trim(item.Severity, 60),
                            ["updated"] = item.UpdatedUtc?.ToString("o", CultureInfo.InvariantCulture),
                            ["area"] = item.IsArea
                        }
                    });
                    kept++;
                }
                feeds.Add(new JsonObject
                {
                    ["id"] = f.Id, ["name"] = f.Name, ["kind"] = Kind(f.Kind), ["count"] = kept, ["licence"] = f.Licence,
                    ["fetched"] = r == null || r.FetchedUtc == DateTime.MinValue ? null : r.FetchedUtc.ToString("o", CultureInfo.InvariantCulture),
                    ["live"] = r is { FromCache: false },
                    ["error"] = r?.Error,
                    ["needsKey"] = f.NeedsKey && string.IsNullOrWhiteSpace(f.Key)
                });
            }
            _regionKey = RegionKey();
            _version++;
            _cache = new JsonObject { ["enabled"] = true, ["version"] = _version, ["feeds"] = feeds, ["features"] = features };
            return (_version, _cache);
        }
    }

    private static string Kind(LiveKind k) => k switch
    {
        LiveKind.RoadEvent => "road",
        LiveKind.FirePerimeter => "fire",
        LiveKind.Evacuation => "evacuation",
        _ => "weather"
    };

    private static bool Overlaps(GeoBounds a, GeoBounds b) =>
        a.MinLat <= b.MaxLat && b.MinLat <= a.MaxLat && a.MinLon <= b.MaxLon && b.MinLon <= a.MaxLon;

    private static string Trim(string s, int max) => s.Length > max ? s[..max].TrimEnd() + "…" : s;

    // ------------------------------------------------------------------ geometry

    private static JsonArray Pos(GeoPoint p) => new(Math.Round(p.Lon, 5), Math.Round(p.Lat, 5));
    private static JsonArray Coords(IEnumerable<GeoPoint> pts) => new(pts.Select(p => (JsonNode)Pos(p)).ToArray());

    /// <summary>GeoJSON for an item, simplified. Each ring of an area is drawn as its own polygon.</summary>
    internal static JsonObject? Geometry(LiveItem item)
    {
        if (item.IsArea)
        {
            var rings = item.Parts.Where(r => r.Count >= 4).Select(r => Simplify(r, SimplifyMeters, closed: true)).Where(r => r.Count >= 4).ToList();
            if (rings.Count == 0) return null;
            return rings.Count == 1
                ? new JsonObject { ["type"] = "Polygon", ["coordinates"] = new JsonArray(Coords(rings[0])) }
                : new JsonObject { ["type"] = "MultiPolygon", ["coordinates"] = new JsonArray(rings.Select(r => (JsonNode)new JsonArray(Coords(r))).ToArray()) };
        }
        var points = item.Parts.Where(p => p.Count == 1).Select(p => p[0]).ToList();
        var lines = item.Parts.Where(p => p.Count >= 2).Select(p => Simplify(p, SimplifyMeters, closed: false)).ToList();
        if (lines.Count > 0)
            return lines.Count == 1
                ? new JsonObject { ["type"] = "LineString", ["coordinates"] = Coords(lines[0]) }
                : new JsonObject { ["type"] = "MultiLineString", ["coordinates"] = new JsonArray(lines.Select(l => (JsonNode)Coords(l)).ToArray()) };
        if (points.Count == 0) return null;
        return points.Count == 1
            ? new JsonObject { ["type"] = "Point", ["coordinates"] = Pos(points[0]) }
            : new JsonObject { ["type"] = "MultiPoint", ["coordinates"] = Coords(points) };
    }

    /// <summary>Douglas–Peucker in metres (flat projection around the line: fine at feed scales).</summary>
    internal static List<GeoPoint> Simplify(List<GeoPoint> pts, double tolerance, bool closed)
    {
        if (pts.Count <= (closed ? 4 : 2)) return pts;
        var k = Math.Cos(pts[0].Lat * Math.PI / 180) * 111_195;
        double X(GeoPoint p) => p.Lon * k;
        double Y(GeoPoint p) => p.Lat * 111_195;
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double best = 0; var idx = -1;
            double ax = X(pts[a]), ay = Y(pts[a]), bx = X(pts[b]), by = Y(pts[b]);
            double dx = bx - ax, dy = by - ay, len = dx * dx + dy * dy;
            for (var i = a + 1; i < b; i++)
            {
                double px = X(pts[i]) - ax, py = Y(pts[i]) - ay;
                var d = len == 0 ? Math.Sqrt(px * px + py * py) : Math.Abs(px * dy - py * dx) / Math.Sqrt(len);
                if (d > best) { best = d; idx = i; }
            }
            if (idx >= 0 && best > tolerance)
            {
                keep[idx] = true;
                stack.Push((a, idx));
                stack.Push((idx, b));
            }
        }
        var result = pts.Where((_, i) => keep[i]).ToList();
        // A closed ring needs at least a triangle; keep the original when simplifying would collapse it.
        return closed && result.Count < 4 ? pts : result;
    }
}

public static class LiveApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/live", (int? v, LiveHub hub) =>
        {
            if (!hub.Enabled || hub.Feeds.Count == 0)
                return Results.Json(new { enabled = false });
            var (version, data) = hub.Snapshot();
            if (v == version)
                return Results.Json(new { enabled = true, version, unchanged = true, feeds = data["feeds"] });
            return Results.Content(data.ToJsonString(), "application/json");
        });
    }
}
