using System.Globalization;
using System.Text.Json.Nodes;
using FieldMap.Core.Overlays;

namespace FieldMap.Web;

/// <summary>Turns Field Map overlay content (from KML, KMZ, GPX, GeoJSON or CSV) into GeoJSON for the phone.</summary>
public static class OverlayGeoJson
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static JsonObject ToFeatureCollection(MapFileContent content, string id, string name)
    {
        var features = new JsonArray();
        var index = 0;
        foreach (var f in content.Features)
        {
            var geometry = Geometry(f);
            if (geometry == null)
                continue;
            var props = new JsonObject
            {
                ["i"] = index++,
                ["name"] = f.Name,
                ["folder"] = f.Folder,
                ["description"] = f.Description.Length > 2000 ? f.Description[..2000] + "…" : f.Description,
                ["kind"] = f.Geometry switch { OverlayGeometry.Point => "point", OverlayGeometry.Line => "line", _ => "area" }
            };
            if (Hex(f.Stroke) is { } stroke)
                props["stroke"] = stroke;
            if (Hex(f.Fill) is { } fill)
            {
                props["fill"] = fill;
                props["fillOpacity"] = Math.Round(((f.Fill!.Value >> 24) & 0xFF) / 255.0, 2);
            }
            if (f.Width is { } w)
                props["width"] = Math.Round(w, 1);
            if (f.Properties.Count > 0)
            {
                var attrs = new JsonObject();
                foreach (var (k, v) in f.Properties.Take(40))
                    attrs[k] = v;
                props["attrs"] = attrs;
            }
            features.Add(new JsonObject { ["type"] = "Feature", ["geometry"] = geometry, ["properties"] = props });
        }
        var b = content.Bounds;
        return new JsonObject
        {
            ["type"] = "FeatureCollection",
            ["id"] = id,
            ["name"] = name,
            ["summary"] = content.Summary,
            ["warnings"] = new JsonArray(content.Warnings.Take(20).Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()),
            ["bbox"] = b.IsEmpty ? null : new JsonArray(R(b.MinLon), R(b.MinLat), R(b.MaxLon), R(b.MaxLat)),
            ["features"] = features
        };
    }

    private static JsonNode? Geometry(OverlayFeature f)
    {
        var parts = f.Parts.Where(p => p.Count > 0).ToList();
        if (parts.Count == 0)
            return null;
        static JsonArray Pos(GeoPoint p) => new(R(p.Lon), R(p.Lat));
        static JsonArray Line(IEnumerable<GeoPoint> pts) => new(pts.Select(p => (JsonNode?)Pos(p)).ToArray());
        switch (f.Geometry)
        {
            case OverlayGeometry.Point:
                return new JsonObject { ["type"] = "Point", ["coordinates"] = Pos(parts[0][0]) };
            case OverlayGeometry.Line:
                return parts.Count == 1
                    ? new JsonObject { ["type"] = "LineString", ["coordinates"] = Line(parts[0]) }
                    : new JsonObject { ["type"] = "MultiLineString", ["coordinates"] = new JsonArray(parts.Select(p => (JsonNode?)Line(p)).ToArray()) };
            default:
                // Each part is a ring; close it if the file didn't.
                var rings = parts.Where(p => p.Count >= 3).Select(p => (JsonNode?)Line(p[0] == p[^1] ? p : p.Append(p[0]))).ToArray();
                return rings.Length == 0 ? null : new JsonObject { ["type"] = "Polygon", ["coordinates"] = new JsonArray(rings) };
        }
    }

    private static double R(double v) => Math.Round(v, 6);

    private static string? Hex(uint? argb) => argb is { } c ? "#" + (c & 0xFFFFFF).ToString("x6", Inv) : null;
}

/// <summary>The shared event files in the events folder, re-read when a file changes.</summary>
public sealed class EventFiles
{
    private readonly string _dir;
    private readonly ILogger<EventFiles> _log;
    private readonly Dictionary<string, (DateTime Stamp, JsonObject Json)> _cache = new();

    public EventFiles(WebOptions options, ILogger<EventFiles> log)
    {
        _dir = options.EventsFolder;
        _log = log;
    }

    public string Folder => _dir;

    public sealed record Entry(string Id, string Name, string File, string Summary, long Bytes, DateTime ModifiedUtc);

    public List<Entry> List()
    {
        if (!Directory.Exists(_dir))
            return new();
        var list = new List<Entry>();
        foreach (var path in Directory.EnumerateFiles(_dir).Where(MapFileImporter.CanRead).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var json = Load(path);
            if (json == null)
                continue;
            var info = new FileInfo(path);
            list.Add(new Entry(IdOf(path), (string)json["name"]!, info.Name, (string)json["summary"]!, info.Length, info.LastWriteTimeUtc));
        }
        return list;
    }

    public JsonObject? Get(string id)
    {
        if (!Directory.Exists(_dir))
            return null;
        var path = Directory.EnumerateFiles(_dir).Where(MapFileImporter.CanRead).FirstOrDefault(p => IdOf(p) == id);
        return path == null ? null : Load(path);
    }

    private static string IdOf(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(path).ToLowerInvariant())))[..12].ToLowerInvariant();

    private JsonObject? Load(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (_cache)
            if (_cache.TryGetValue(path, out var c) && c.Stamp == stamp)
                return c.Json;
        try
        {
            var content = MapFileImporter.LoadLarge(path, 400_000);
            var name = content.Name.Length > 0 ? content.Name : Path.GetFileNameWithoutExtension(path);
            var json = OverlayGeoJson.ToFeatureCollection(content, IdOf(path), name);
            lock (_cache)
                _cache[path] = (stamp, json);
            return json;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Event file {File} could not be read", path);
            return null;
        }
    }
}
