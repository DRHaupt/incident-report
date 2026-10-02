using System.Globalization;
using System.Text.Json;
using FieldMap.Core.Geo;

namespace FieldMap.Core.Live;

public enum LiveKind
{
    RoadEvent,
    FirePerimeter,
    Evacuation,
    WeatherAlert
}

/// <summary>One live item (road closure, fire perimeter, evacuation order, weather alert).</summary>
public sealed class LiveItem
{
    public string Id { get; set; } = "";
    public LiveKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Severity { get; set; } = "";
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>Rings/lines/points as lat/lon lists. Polygons: each list is one ring.</summary>
    public List<List<GeoPoint>> Parts { get; set; } = new();
    public bool IsArea { get; set; }

    public GeoBounds Bounds => GeoBounds.Of(Parts.SelectMany(p => p));
}

/// <summary>Configuration of one live feed (Content/live-feeds.json).</summary>
public sealed class LiveFeedDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public LiveKind Kind { get; set; }
    /// <summary>"open511", "ab511" or "geojson".</summary>
    public string Format { get; set; } = "geojson";
    public string Url { get; set; } = "";
    public string Licence { get; set; } = "";
    public string Terms { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Set for feeds that need a key (Alberta 511); "{key}" in the URL is replaced.</summary>
    public bool NeedsKey { get; set; }
    public string Key { get; set; } = "";
    public int RefreshMinutes { get; set; } = 10;
    /// <summary>GeoJSON property names tried in order for the title, description, severity and update time.</summary>
    public List<string> TitleFields { get; set; } = new();
    public List<string> DescriptionFields { get; set; } = new();
    public List<string> SeverityFields { get; set; } = new();
    public List<string> UpdatedFields { get; set; } = new();
    public string Verified { get; set; } = "unverified";

    public string EffectiveUrl => Url.Replace("{key}", Uri.EscapeDataString(Key));
}

public sealed class LiveFeedCatalog
{
    public List<LiveFeedDefinition> Feeds { get; set; } = new();

    public static LiveFeedCatalog LoadOrEmpty(string path)
    {
        try
        {
            return File.Exists(path) ? Json.Deserialize<LiveFeedCatalog>(File.ReadAllText(path)) : new LiveFeedCatalog();
        }
        catch (Exception)
        {
            return new LiveFeedCatalog();
        }
    }
}

public sealed record LiveResult(LiveFeedDefinition Feed, List<LiveItem> Items, DateTime FetchedUtc, bool FromCache, string? Error);

/// <summary>
/// Fetches live feeds when connected, caches the last good response on disk and falls back to it quietly when offline.
/// Nothing else in the app depends on these feeds.
/// </summary>
public sealed class LiveFeedService
{
    private readonly HttpClient _http;
    private readonly string _cacheDir;

    public LiveFeedService(HttpClient http, string cacheDir)
    {
        _http = http;
        _cacheDir = cacheDir;
    }

    private sealed class CacheEntry
    {
        public DateTime FetchedUtc { get; set; }
        public string Body { get; set; } = "";
    }

    private string CachePath(LiveFeedDefinition f) => Path.Combine(_cacheDir, f.Id + ".json");

    public async Task<LiveResult> FetchAsync(LiveFeedDefinition feed, CancellationToken cancel = default)
    {
        if (feed.NeedsKey && string.IsNullOrWhiteSpace(feed.Key))
            return Cached(feed, "needs a developer key (Settings)");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var body = await _http.GetStringAsync(feed.EffectiveUrl, cts.Token).ConfigureAwait(false);
            var items = Parse(feed, body);
            Directory.CreateDirectory(_cacheDir);
            var now = DateTime.UtcNow;
            Json.WriteFileAtomic(CachePath(feed), Json.Serialize(new CacheEntry { FetchedUtc = now, Body = body }));
            return new LiveResult(feed, items, now, false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return Cached(feed, ex is TaskCanceledException ? "timed out" : ex.Message);
        }
    }

    /// <summary>"LIVE DATA – fetched 14:05" or "cached – fetched 09:12 (offline)".</summary>
    public static string Label(LiveResult r) =>
        r.FetchedUtc == DateTime.MinValue ? (r.Error != null ? $"not available ({r.Error})" : "not fetched yet")
        : r.FromCache ? $"cached – fetched {r.FetchedUtc.ToLocalTime():HH:mm dd MMM}{(r.Error != null ? " (offline)" : "")}"
        : $"LIVE DATA – fetched {r.FetchedUtc.ToLocalTime():HH:mm}";

    public LiveResult Cached(LiveFeedDefinition feed, string? error = null)
    {
        try
        {
            if (File.Exists(CachePath(feed)))
            {
                var c = Json.Deserialize<CacheEntry>(File.ReadAllText(CachePath(feed)));
                return new LiveResult(feed, Parse(feed, c.Body), c.FetchedUtc, true, error);
            }
        }
        catch (Exception)
        {
            // A bad cache file is the same as none.
        }
        return new LiveResult(feed, new List<LiveItem>(), DateTime.MinValue, true, error);
    }

    // ------------------------------------------------------------------ parsers

    public static List<LiveItem> Parse(LiveFeedDefinition feed, string body) => feed.Format switch
    {
        "open511" => ParseOpen511(body, feed.Kind),
        "ab511" => ParseAb511(body, feed.Kind),
        _ => ParseGeoJson(body, feed)
    };

    /// <summary>Open511 JSON (DriveBC): { "events": [ { id, headline, description, severity, updated, geography: GeoJSON } ] }.</summary>
    public static List<LiveItem> ParseOpen511(string body, LiveKind kind)
    {
        using var doc = JsonDocument.Parse(body);
        var list = new List<LiveItem>();
        if (!doc.RootElement.TryGetProperty("events", out var events))
            return list;
        foreach (var e in events.EnumerateArray())
        {
            var item = new LiveItem
            {
                Id = Str(e, "id") ?? "",
                Kind = kind,
                Title = Str(e, "headline") ?? Str(e, "event_type") ?? "Road event",
                Description = Str(e, "description") ?? "",
                Severity = Str(e, "severity") ?? "",
                UpdatedUtc = Date(Str(e, "updated") ?? Str(e, "created"))
            };
            if (e.TryGetProperty("roads", out var roads) && roads.ValueKind == JsonValueKind.Array && roads.GetArrayLength() > 0 && Str(roads[0], "name") is { } road)
                item.Title = $"{item.Title} – {road}";
            if (e.TryGetProperty("geography", out var g))
                ReadGeometry(g, item);
            list.Add(item);
        }
        return list;
    }

    /// <summary>Alberta 511 (IBI) events: [ { ID, RoadwayName, EventType, Description, Latitude, Longitude, LastUpdated, IsFullClosure } ].</summary>
    public static List<LiveItem> ParseAb511(string body, LiveKind kind)
    {
        using var doc = JsonDocument.Parse(body);
        var list = new List<LiveItem>();
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.TryGetProperty("events", out var ev) ? ev : default;
        if (arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var e in arr.EnumerateArray())
        {
            var lat = Num(e, "Latitude");
            var lon = Num(e, "Longitude");
            var item = new LiveItem
            {
                Id = Str(e, "ID") ?? "",
                Kind = kind,
                Title = $"{Str(e, "EventType") ?? "Event"}{(Str(e, "RoadwayName") is { } r ? " – " + r : "")}",
                Description = Str(e, "Description") ?? "",
                Severity = Str(e, "Severity") ?? (e.TryGetProperty("IsFullClosure", out var fc) && fc.ValueKind == JsonValueKind.True ? "Full closure" : ""),
                UpdatedUtc = Num(e, "LastUpdated") is { } unix ? DateTimeOffset.FromUnixTimeSeconds((long)unix).UtcDateTime : null
            };
            if (lat is { } la && lon is { } lo)
                item.Parts.Add(new List<GeoPoint> { new(la, lo) });
            list.Add(item);
        }
        return list;
    }

    /// <summary>GeoJSON FeatureCollections (BC Wildfire WFS, BC evacuation ArcGIS, Alberta wildfire, ECCC alerts).</summary>
    public static List<LiveItem> ParseGeoJson(string body, LiveFeedDefinition feed)
    {
        using var doc = JsonDocument.Parse(body);
        var list = new List<LiveItem>();
        if (!doc.RootElement.TryGetProperty("features", out var features))
            return list;
        var n = 0;
        foreach (var f in features.EnumerateArray())
        {
            var props = f.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            string? First(List<string> keys) => props.ValueKind == JsonValueKind.Object ? keys.Select(k => Str(props, k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) : null;
            var item = new LiveItem
            {
                Id = (f.TryGetProperty("id", out var id) ? id.ToString() : null) ?? (n++).ToString(CultureInfo.InvariantCulture),
                Kind = feed.Kind,
                Title = First(feed.TitleFields) ?? feed.Name,
                Description = First(feed.DescriptionFields) ?? "",
                Severity = First(feed.SeverityFields) ?? "",
                UpdatedUtc = Date(First(feed.UpdatedFields))
            };
            if (f.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.Object)
                ReadGeometry(g, item);
            list.Add(item);
        }
        return list;
    }

    private static void ReadGeometry(JsonElement g, LiveItem item)
    {
        var type = Str(g, "type");
        if (!g.TryGetProperty("coordinates", out var c))
        {
            if (type == "GeometryCollection" && g.TryGetProperty("geometries", out var geoms))
                foreach (var sub in geoms.EnumerateArray())
                    ReadGeometry(sub, item);
            return;
        }
        GeoPoint P(JsonElement a) => new(a[1].GetDouble(), a[0].GetDouble());
        List<GeoPoint> Line(JsonElement a) => a.EnumerateArray().Select(P).ToList();
        switch (type)
        {
            case "Point": item.Parts.Add(new List<GeoPoint> { P(c) }); break;
            case "MultiPoint": foreach (var a in c.EnumerateArray()) item.Parts.Add(new List<GeoPoint> { P(a) }); break;
            case "LineString": item.Parts.Add(Line(c)); break;
            case "MultiLineString": foreach (var a in c.EnumerateArray()) item.Parts.Add(Line(a)); break;
            case "Polygon": item.IsArea = true; foreach (var ring in c.EnumerateArray()) item.Parts.Add(Line(ring)); break;
            case "MultiPolygon": item.IsArea = true; foreach (var poly in c.EnumerateArray()) foreach (var ring in poly.EnumerateArray()) item.Parts.Add(Line(ring)); break;
        }
    }

    private static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static DateTime? Date(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        if (long.TryParse(s, out var epoch))
            return epoch > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime : DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }
}
