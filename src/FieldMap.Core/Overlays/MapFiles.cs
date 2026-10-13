using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FieldMap.Core.Data;
using FieldMap.Core.Geo;

namespace FieldMap.Core.Overlays;

public enum OverlayGeometry
{
    Point,
    Line,
    Polygon
}

/// <summary>A feature from an imported map file, with the style the file gave it.</summary>
public sealed class OverlayFeature
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public OverlayGeometry Geometry { get; set; }

    /// <summary>Point: one part with one point. Line: one part. Polygon: outer ring then holes.</summary>
    public List<List<GeoPoint>> Parts { get; set; } = new();

    /// <summary>ARGB colours from the file, or null for the default style.</summary>
    public uint? Stroke { get; set; }
    public uint? Fill { get; set; }
    public float? Width { get; set; }

    /// <summary>Folder path in the file ("Medical / First aid posts").</summary>
    public string Folder { get; set; } = "";

    public Dictionary<string, string> Properties { get; set; } = new();

    public GeoBounds Bounds => Parts.Aggregate(GeoBounds.Empty, (b, p) => b.Include(GeoBounds.Of(p)));
    /// <summary>Where to put a label or start directions: the point, the middle of a line, or an area's centre.</summary>
    public GeoPoint Anchor => Geometry switch
    {
        OverlayGeometry.Point => Parts[0][0],
        OverlayGeometry.Line => Parts[0][Parts[0].Count / 2],
        _ => FieldMap.Core.Geo.Geometry.Centroid(Parts[0])
    };
}

/// <summary>An image placed on the map (KML/KMZ GroundOverlay): e.g. an event site plan.</summary>
public sealed class GroundOverlay
{
    public string Name { get; set; } = "";
    public byte[] Image { get; set; } = Array.Empty<byte>();

    /// <summary>Corners: lower-left, lower-right, upper-right, upper-left (as KML gx:LatLonQuad).</summary>
    public GeoPoint[] Corners { get; set; } = Array.Empty<GeoPoint>();

    /// <summary>Opacity from the file's colour (0–1).</summary>
    public float Opacity { get; set; } = 1f;

    public GeoBounds Bounds => GeoBounds.Of(Corners);
}

/// <summary>Everything read from one map file.</summary>
public sealed class MapFileContent
{
    public string Name { get; set; } = "";
    public List<OverlayFeature> Features { get; } = new();
    public List<GroundOverlay> GroundOverlays { get; } = new();

    /// <summary>Things the file contained that couldn't be used (network links, online images, bad coordinates…).</summary>
    public List<string> Warnings { get; } = new();

    public GeoBounds Bounds => Features.Aggregate(GeoBounds.Empty, (b, f) => b.Include(f.Bounds))
        .Include(GroundOverlays.Aggregate(GeoBounds.Empty, (b, g) => b.Include(g.Bounds)));

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            void Count(int n, string one, string many)
            {
                if (n > 0)
                    parts.Add($"{n:N0} {(n == 1 ? one : many)}");
            }
            Count(Features.Count(f => f.Geometry == OverlayGeometry.Point), "point", "points");
            Count(Features.Count(f => f.Geometry == OverlayGeometry.Line), "line", "lines");
            Count(Features.Count(f => f.Geometry == OverlayGeometry.Polygon), "area", "areas");
            Count(GroundOverlays.Count, "map image", "map images");
            return parts.Count == 0 ? "nothing usable" : string.Join(", ", parts);
        }
    }
}

/// <summary>
/// Reads the map files events and agencies hand out: KML and KMZ (Google Earth: placemarks, folders, styles,
/// multi-geometries, tracks, ground-overlay images packed in the KMZ), GPX (waypoints, routes, tracks), GeoJSON
/// (with simplestyle colours) and CSV with latitude/longitude columns.
/// </summary>
public static partial class MapFileImporter
{
    public const string FileFilter = "Map files (*.kml;*.kmz;*.gpx;*.geojson;*.json;*.csv)|*.kml;*.kmz;*.gpx;*.geojson;*.json;*.csv|All files (*.*)|*.*";

    public static readonly string[] Extensions = { ".kml", ".kmz", ".gpx", ".geojson", ".json", ".csv", ".txt" };

    private const int DefaultMaxPoints = 500_000;

    // Event files stay small; national coverage maps are allowed more (see LoadLarge).
    [ThreadStatic]
    private static int _maxPointsOverride;

    private static int MaxPoints => _maxPointsOverride > 0 ? _maxPointsOverride : DefaultMaxPoints;

    /// <summary>Loads a large data file (e.g. a national coverage map) with a higher point limit.</summary>
    public static MapFileContent LoadLarge(string path, int maxPoints)
    {
        _maxPointsOverride = maxPoints;
        try
        {
            return Load(path);
        }
        finally
        {
            _maxPointsOverride = 0;
        }
    }
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool CanRead(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static MapFileContent Load(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        MapFileContent content = ext switch
        {
            ".kmz" => ReadKmz(path),
            ".kml" => ReadKml(File.ReadAllText(path), null),
            ".gpx" => ReadGpx(File.ReadAllText(path)),
            ".geojson" or ".json" => ReadGeoJson(File.ReadAllText(path)),
            ".csv" or ".txt" => ReadCsv(path),
            _ => throw new InvalidDataException($"{Path.GetFileName(path)}: unsupported file type. Use KML, KMZ, GPX, GeoJSON or CSV.")
        };
        if (content.Name.Length == 0)
            content.Name = Path.GetFileNameWithoutExtension(path);
        if (content.Features.Count == 0 && content.GroundOverlays.Count == 0)
            content.Warnings.Insert(0, "No points, lines, areas or map images were found in the file.");
        return content;
    }

    // ------------------------------------------------------------------ KMZ / KML

    private static MapFileContent ReadKmz(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var kml = zip.Entries.FirstOrDefault(e => e.FullName.Equals("doc.kml", StringComparison.OrdinalIgnoreCase))
                  ?? zip.Entries.Where(e => e.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName.Count(c => c == '/')).FirstOrDefault()
                  ?? throw new InvalidDataException("The KMZ file has no KML inside.");
        string text;
        using (var r = new StreamReader(kml.Open()))
            text = r.ReadToEnd();
        var baseDir = kml.FullName.Contains('/') ? kml.FullName[..(kml.FullName.LastIndexOf('/') + 1)] : "";
        byte[]? Resolve(string href)
        {
            href = Uri.UnescapeDataString(href.Replace('\\', '/'));
            var entry = zip.GetEntry(baseDir + href) ?? zip.GetEntry(href) ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/" + href, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return null;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        return ReadKml(text, Resolve);
    }

    /// <summary>Reads KML; <paramref name="resolve"/> loads files packed alongside it (KMZ), or null for plain KML.</summary>
    public static MapFileContent ReadKml(string text, Func<string, byte[]?>? resolve)
    {
        var doc = XDocument.Parse(text);
        var content = new MapFileContent();
        var root = doc.Root ?? throw new InvalidDataException("Empty KML.");
        var styles = new Dictionary<string, (uint? Stroke, uint? Fill, float? Width)>();
        var styleMaps = new Dictionary<string, string>();
        foreach (var style in root.Descendants().Where(e => e.Name.LocalName == "Style" && e.Attribute("id") != null))
            styles[style.Attribute("id")!.Value] = ParseStyle(style);
        foreach (var map in root.Descendants().Where(e => e.Name.LocalName == "StyleMap" && e.Attribute("id") != null))
        {
            var normal = map.Elements().FirstOrDefault(p => p.Name.LocalName == "Pair" && Child(p, "key") == "normal");
            if (normal != null && Child(normal, "styleUrl") is { } url)
                styleMaps[map.Attribute("id")!.Value] = url.TrimStart('#');
        }
        (uint? Stroke, uint? Fill, float? Width) StyleFor(XElement placemark)
        {
            var inline = placemark.Elements().FirstOrDefault(e => e.Name.LocalName == "Style");
            if (inline != null)
                return ParseStyle(inline);
            if (Child(placemark, "styleUrl") is { } url)
            {
                var id = url[(url.IndexOf('#') + 1)..];
                if (styleMaps.TryGetValue(id, out var mapped))
                    id = mapped;
                if (styles.TryGetValue(id, out var st))
                    return st;
            }
            return (null, null, null);
        }

        var docName = root.Elements().FirstOrDefault(e => e.Name.LocalName is "Document" or "Folder") is { } top ? Child(top, "name") : null;
        content.Name = docName ?? "";
        var points = 0;

        void Walk(XElement element, string folder)
        {
            foreach (var e in element.Elements())
            {
                switch (e.Name.LocalName)
                {
                    case "Document" or "Folder":
                        var name = Child(e, "name") ?? "";
                        Walk(e, e.Name.LocalName == "Document" || name.Length == 0 ? folder : folder.Length > 0 ? $"{folder} / {name}" : name);
                        break;
                    case "Placemark":
                        var (stroke, fill, width) = StyleFor(e);
                        var props = ExtendedData(e);
                        foreach (var geom in Geometries(e))
                        {
                            var f = new OverlayFeature
                            {
                                Name = Child(e, "name") ?? "",
                                Description = StripHtml(Child(e, "description") ?? ""),
                                Folder = folder,
                                Stroke = stroke,
                                Fill = fill,
                                Width = width,
                                Properties = props
                            };
                            if (ToFeature(geom, f, content.Warnings))
                            {
                                points += f.Parts.Sum(p => p.Count);
                                if (points > MaxPoints)
                                    throw new InvalidDataException($"The file has more than {MaxPoints:N0} points; split it into smaller files.");
                                content.Features.Add(f);
                            }
                        }
                        break;
                    case "GroundOverlay":
                        ReadGroundOverlay(e, resolve, content);
                        break;
                    case "NetworkLink":
                        content.Warnings.Add($"Skipped a network link ({Child(e, "name") ?? "unnamed"}): it loads data from the internet and isn't available offline.");
                        break;
                    case "PhotoOverlay" or "ScreenOverlay":
                        content.Warnings.Add($"Skipped a {e.Name.LocalName} ({Child(e, "name") ?? "unnamed"}): not a map layer.");
                        break;
                }
            }
        }
        Walk(root, "");
        return content;
    }

    private static string? Child(XElement e, string localName) =>
        e.Elements().FirstOrDefault(c => c.Name.LocalName == localName)?.Value.Trim() is { Length: > 0 } v ? v : null;

    private static (uint? Stroke, uint? Fill, float? Width) ParseStyle(XElement style)
    {
        uint? stroke = null, fill = null;
        float? width = null;
        var line = style.Elements().FirstOrDefault(e => e.Name.LocalName == "LineStyle");
        if (line != null)
        {
            stroke = KmlColor(Child(line, "color"));
            if (Child(line, "width") is { } w && float.TryParse(w, NumberStyles.Float, Inv, out var wv))
                width = wv;
        }
        var poly = style.Elements().FirstOrDefault(e => e.Name.LocalName == "PolyStyle");
        if (poly != null)
        {
            fill = KmlColor(Child(poly, "color"));
            if (Child(poly, "fill") == "0")
                fill = 0;
        }
        var icon = style.Elements().FirstOrDefault(e => e.Name.LocalName == "IconStyle");
        if (icon != null && stroke == null)
            stroke = KmlColor(Child(icon, "color"));
        return (stroke, fill, width);
    }

    /// <summary>KML colours are aabbggrr hex; returns ARGB.</summary>
    public static uint? KmlColor(string? s)
    {
        if (s == null)
            return null;
        s = s.Trim().TrimStart('#');
        if (s.Length != 8 || !uint.TryParse(s, NumberStyles.HexNumber, Inv, out var v))
            return null;
        var a = v >> 24;
        var b = (v >> 16) & 0xFF;
        var g = (v >> 8) & 0xFF;
        var r = v & 0xFF;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private static IEnumerable<XElement> Geometries(XElement placemark)
    {
        foreach (var e in placemark.Elements())
        {
            if (e.Name.LocalName == "MultiGeometry")
            {
                foreach (var g in e.Descendants().Where(d => d.Name.LocalName is "Point" or "LineString" or "LinearRing" or "Polygon" or "Track"
                                                            && d.Ancestors().First(a => a.Name.LocalName is "Polygon" or "MultiGeometry").Name.LocalName == "MultiGeometry"))
                    yield return g;
            }
            else if (e.Name.LocalName is "Point" or "LineString" or "LinearRing" or "Polygon" or "Track")
            {
                yield return e;
            }
            else if (e.Name.LocalName == "MultiTrack")
            {
                foreach (var t in e.Elements().Where(t => t.Name.LocalName == "Track"))
                    yield return t;
            }
        }
    }

    private static bool ToFeature(XElement geom, OverlayFeature f, List<string> warnings)
    {
        switch (geom.Name.LocalName)
        {
            case "Point":
            {
                var pts = Coordinates(geom.Elements().FirstOrDefault(c => c.Name.LocalName == "coordinates")?.Value, warnings, f.Name);
                if (pts.Count == 0)
                    return false;
                f.Geometry = OverlayGeometry.Point;
                f.Parts.Add(new List<GeoPoint> { pts[0] });
                return true;
            }
            case "LineString":
            case "LinearRing":
            {
                var pts = Coordinates(geom.Elements().FirstOrDefault(c => c.Name.LocalName == "coordinates")?.Value, warnings, f.Name);
                if (pts.Count < 2)
                    return false;
                f.Geometry = geom.Name.LocalName == "LinearRing" && pts.Count >= 4 ? OverlayGeometry.Polygon : OverlayGeometry.Line;
                f.Parts.Add(pts);
                return true;
            }
            case "Polygon":
            {
                var outer = geom.Descendants().FirstOrDefault(d => d.Name.LocalName == "outerBoundaryIs")?.Descendants().FirstOrDefault(d => d.Name.LocalName == "coordinates");
                var ring = Coordinates(outer?.Value, warnings, f.Name);
                if (ring.Count < 3)
                    return false;
                f.Geometry = OverlayGeometry.Polygon;
                f.Parts.Add(ring);
                foreach (var inner in geom.Descendants().Where(d => d.Name.LocalName == "innerBoundaryIs"))
                {
                    var hole = Coordinates(inner.Descendants().FirstOrDefault(d => d.Name.LocalName == "coordinates")?.Value, warnings, f.Name);
                    if (hole.Count >= 3)
                        f.Parts.Add(hole);
                }
                return true;
            }
            case "Track":
            {
                // gx:Track: <gx:coord>lon lat alt</gx:coord> and matching <when> times.
                var pts = new List<GeoPoint>();
                foreach (var c in geom.Elements().Where(c => c.Name.LocalName == "coord"))
                {
                    var p = c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 2 && double.TryParse(p[0], NumberStyles.Float, Inv, out var lon) && double.TryParse(p[1], NumberStyles.Float, Inv, out var lat) && Valid(lat, lon))
                        pts.Add(new GeoPoint(lat, lon));
                }
                if (pts.Count == 0)
                    return false;
                f.Geometry = pts.Count == 1 ? OverlayGeometry.Point : OverlayGeometry.Line;
                f.Parts.Add(pts);
                var times = geom.Elements().Where(c => c.Name.LocalName == "when").Select(c => c.Value.Trim()).ToList();
                if (times.Count > 0)
                {
                    f.Properties["start"] = times[0];
                    f.Properties["end"] = times[^1];
                }
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>KML "lon,lat[,alt]" tuples separated by whitespace.</summary>
    private static List<GeoPoint> Coordinates(string? text, List<string> warnings, string name)
    {
        var list = new List<GeoPoint>();
        if (string.IsNullOrWhiteSpace(text))
            return list;
        var bad = 0;
        foreach (var tuple in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = tuple.Split(',');
            if (p.Length >= 2 && double.TryParse(p[0], NumberStyles.Float, Inv, out var lon) && double.TryParse(p[1], NumberStyles.Float, Inv, out var lat) && Valid(lat, lon))
                list.Add(new GeoPoint(lat, lon));
            else
                bad++;
        }
        if (bad > 0)
            warnings.Add($"{(name.Length > 0 ? name : "A feature")}: {bad} coordinate{(bad == 1 ? "" : "s")} could not be read and {(bad == 1 ? "was" : "were")} left out.");
        return list;
    }

    private static bool Valid(double lat, double lon) => lat is >= -90 and <= 90 && lon is >= -180 and <= 180 && !(lat == 0 && lon == 0);

    private static Dictionary<string, string> ExtendedData(XElement placemark)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ext = placemark.Elements().FirstOrDefault(e => e.Name.LocalName == "ExtendedData");
        if (ext == null)
            return d;
        foreach (var data in ext.Descendants().Where(e => e.Name.LocalName == "Data" && e.Attribute("name") != null))
            if (Child(data, "value") is { } v)
                d[data.Attribute("name")!.Value] = v;
        foreach (var sd in ext.Descendants().Where(e => e.Name.LocalName == "SimpleData" && e.Attribute("name") != null))
            if (sd.Value.Trim() is { Length: > 0 } v)
                d[sd.Attribute("name")!.Value] = v;
        return d;
    }

    private static void ReadGroundOverlay(XElement e, Func<string, byte[]?>? resolve, MapFileContent content)
    {
        var name = Child(e, "name") ?? "Map image";
        var href = e.Elements().FirstOrDefault(c => c.Name.LocalName == "Icon") is { } icon ? Child(icon, "href") : null;
        if (href == null)
        {
            content.Warnings.Add($"{name}: map image without a picture, skipped.");
            return;
        }
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            content.Warnings.Add($"{name}: the picture is on the internet ({href}); only pictures packed in a KMZ work offline.");
            return;
        }
        var image = resolve?.Invoke(href);
        if (image == null)
        {
            content.Warnings.Add($"{name}: picture {href} not found{(resolve == null ? " (open the KMZ instead of the KML)" : " in the KMZ")}.");
            return;
        }
        GeoPoint[]? corners = null;
        var quad = e.Elements().FirstOrDefault(c => c.Name.LocalName == "LatLonQuad");
        if (quad != null)
        {
            var pts = Coordinates(Child(quad, "coordinates"), content.Warnings, name);
            if (pts.Count == 4)
                corners = pts.ToArray();
        }
        var box = e.Elements().FirstOrDefault(c => c.Name.LocalName == "LatLonBox");
        if (corners == null && box != null)
        {
            double Get(string n) => double.Parse(Child(box, n) ?? throw new InvalidDataException($"{name}: LatLonBox without {n}."), NumberStyles.Float, Inv);
            double north = Get("north"), south = Get("south"), east = Get("east"), west = Get("west");
            var rotation = Child(box, "rotation") is { } r && double.TryParse(r, NumberStyles.Float, Inv, out var rv) ? rv : 0;
            corners = BoxCorners(north, south, east, west, rotation);
        }
        if (corners == null)
        {
            content.Warnings.Add($"{name}: map image without a position, skipped.");
            return;
        }
        var color = KmlColor(Child(e, "color"));
        content.GroundOverlays.Add(new GroundOverlay { Name = name, Image = image, Corners = corners, Opacity = color is { } c ? (c >> 24) / 255f : 1f });
    }

    /// <summary>Corners (LL, LR, UR, UL) of a LatLonBox rotated counter-clockwise about its centre (KML rotation).</summary>
    public static GeoPoint[] BoxCorners(double north, double south, double east, double west, double rotationDegrees)
    {
        var corners = new[] { new GeoPoint(south, west), new GeoPoint(south, east), new GeoPoint(north, east), new GeoPoint(north, west) };
        if (Math.Abs(rotationDegrees) < 1e-9)
            return corners;
        var center = new GeoPoint((north + south) / 2, (east + west) / 2);
        var a = rotationDegrees * Math.PI / 180;
        return corners.Select(c =>
        {
            // Rotate in local metres so the box stays rectangular on the ground.
            var v = FieldMap.Core.Geo.Geometry.ToLocal(center, c);
            var r = new Vec(v.X * Math.Cos(a) - v.Y * Math.Sin(a), v.X * Math.Sin(a) + v.Y * Math.Cos(a));
            return FieldMap.Core.Geo.Geometry.FromLocal(center, r);
        }).ToArray();
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRx();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex SpaceRx();

    public static string StripHtml(string s)
    {
        if (!s.Contains('<') && !s.Contains('&'))
            return s.Trim();
        s = Regex.Replace(s, @"<\s*(br|/p|/div|/tr|/li)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        s = TagRx().Replace(s, "");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = SpaceRx().Replace(s, " ");
        return string.Join("\n", s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    // ------------------------------------------------------------------ GPX

    public static MapFileContent ReadGpx(string text)
    {
        var doc = XDocument.Parse(text);
        var content = new MapFileContent();
        var root = doc.Root ?? throw new InvalidDataException("Empty GPX.");
        content.Name = root.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata") is { } meta ? Child(meta, "name") ?? "" : "";
        GeoPoint? Pt(XElement e)
        {
            if (double.TryParse(e.Attribute("lat")?.Value, NumberStyles.Float, Inv, out var lat) &&
                double.TryParse(e.Attribute("lon")?.Value, NumberStyles.Float, Inv, out var lon) && Valid(lat, lon))
                return new GeoPoint(lat, lon);
            return null;
        }
        foreach (var w in root.Elements().Where(e => e.Name.LocalName == "wpt"))
            if (Pt(w) is { } p)
            {
                var f = new OverlayFeature { Name = Child(w, "name") ?? "", Description = StripHtml(Child(w, "desc") ?? Child(w, "cmt") ?? ""), Geometry = OverlayGeometry.Point };
                f.Parts.Add(new List<GeoPoint> { p });
                if (Child(w, "sym") is { } sym)
                    f.Properties["symbol"] = sym;
                if (Child(w, "type") is { } type)
                    f.Properties["type"] = type;
                content.Features.Add(f);
            }
        foreach (var r in root.Elements().Where(e => e.Name.LocalName == "rte"))
        {
            var pts = r.Elements().Where(e => e.Name.LocalName == "rtept").Select(Pt).OfType<GeoPoint>().ToList();
            if (pts.Count >= 2)
                content.Features.Add(new OverlayFeature { Name = Child(r, "name") ?? "Route", Description = StripHtml(Child(r, "desc") ?? ""), Geometry = OverlayGeometry.Line, Parts = { pts }, Folder = "Routes" });
        }
        foreach (var t in root.Elements().Where(e => e.Name.LocalName == "trk"))
        {
            var name = Child(t, "name") ?? "Track";
            foreach (var seg in t.Elements().Where(e => e.Name.LocalName == "trkseg"))
            {
                var pts = seg.Elements().Where(e => e.Name.LocalName == "trkpt").Select(Pt).OfType<GeoPoint>().ToList();
                if (pts.Count >= 2)
                {
                    var f = new OverlayFeature { Name = name, Description = StripHtml(Child(t, "desc") ?? ""), Geometry = OverlayGeometry.Line, Parts = { pts }, Folder = "Tracks" };
                    var times = seg.Elements().Where(e => e.Name.LocalName == "trkpt").Select(e => Child(e, "time")).OfType<string>().ToList();
                    if (times.Count > 0)
                    {
                        f.Properties["start"] = times[0];
                        f.Properties["end"] = times[^1];
                    }
                    f.Properties["length"] = GeoMath.FormatDistance(FieldMap.Core.Geo.Geometry.Length(pts));
                    content.Features.Add(f);
                }
            }
        }
        return content;
    }

    // ------------------------------------------------------------------ GeoJSON

    public static MapFileContent ReadGeoJson(string text)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var content = new MapFileContent();
        var root = doc.RootElement;
        if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            content.Name = n.GetString() ?? "";
        void Feature(JsonElement feature)
        {
            if (!feature.TryGetProperty("geometry", out var geom) || geom.ValueKind != JsonValueKind.Object)
                return;
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (feature.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
                foreach (var prop in p.EnumerateObject())
                    if (prop.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        props[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()! : prop.Value.GetRawText();
            AddGeometry(geom, props, content);
        }
        switch (root.TryGetProperty("type", out var t) ? t.GetString() : null)
        {
            case "FeatureCollection":
                if (root.TryGetProperty("features", out var features))
                    foreach (var f in features.EnumerateArray())
                        Feature(f);
                break;
            case "Feature":
                Feature(root);
                break;
            case { } geometryType when geometryType.Length > 0:
                AddGeometry(root, new Dictionary<string, string>(), content);
                break;
            default:
                throw new InvalidDataException("Not a GeoJSON file.");
        }
        return content;
    }

    private static void AddGeometry(JsonElement geom, Dictionary<string, string> props, MapFileContent content)
    {
        var type = geom.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "GeometryCollection")
        {
            if (geom.TryGetProperty("geometries", out var gs))
                foreach (var g in gs.EnumerateArray())
                    AddGeometry(g, props, content);
            return;
        }
        if (!geom.TryGetProperty("coordinates", out var c))
            return;
        static GeoPoint? P(JsonElement a) =>
            a.ValueKind == JsonValueKind.Array && a.GetArrayLength() >= 2 && a[0].TryGetDouble(out var lon) && a[1].TryGetDouble(out var lat) && Valid(lat, lon) ? new GeoPoint(lat, lon) : null;
        static List<GeoPoint> Line(JsonElement a) => a.EnumerateArray().Select(P).OfType<GeoPoint>().ToList();
        OverlayFeature New(OverlayGeometry g) => new()
        {
            Name = Get(props, "name", "title", "label") ?? "",
            Description = StripHtml(Get(props, "description", "desc", "notes") ?? ""),
            Geometry = g,
            Stroke = HexColor(Get(props, "stroke", "marker-color", "color")),
            Fill = HexColor(Get(props, "fill")) is { } fill ? WithAlpha(fill, Get(props, "fill-opacity")) : null,
            Width = float.TryParse(Get(props, "stroke-width"), NumberStyles.Float, Inv, out var w) ? w : null,
            Properties = props
        };
        switch (type)
        {
            case "Point":
                if (P(c) is { } pt)
                    content.Features.Add(Add(New(OverlayGeometry.Point), new List<GeoPoint> { pt }));
                break;
            case "MultiPoint":
                foreach (var pt2 in Line(c))
                    content.Features.Add(Add(New(OverlayGeometry.Point), new List<GeoPoint> { pt2 }));
                break;
            case "LineString":
                if (Line(c) is { Count: >= 2 } l)
                    content.Features.Add(Add(New(OverlayGeometry.Line), l));
                break;
            case "MultiLineString":
                foreach (var part in c.EnumerateArray())
                    if (Line(part) is { Count: >= 2 } l2)
                        content.Features.Add(Add(New(OverlayGeometry.Line), l2));
                break;
            case "Polygon":
                AddPolygon(c);
                break;
            case "MultiPolygon":
                foreach (var poly in c.EnumerateArray())
                    AddPolygon(poly);
                break;
        }
        void AddPolygon(JsonElement rings)
        {
            var f = New(OverlayGeometry.Polygon);
            foreach (var ring in rings.EnumerateArray())
                if (Line(ring) is { Count: >= 3 } r)
                    f.Parts.Add(r);
            if (f.Parts.Count > 0)
                content.Features.Add(f);
        }
    }

    private static OverlayFeature Add(OverlayFeature f, List<GeoPoint> part)
    {
        f.Parts.Add(part);
        return f;
    }

    private static string? Get(Dictionary<string, string> d, params string[] keys)
    {
        foreach (var k in keys)
            if (d.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
        return null;
    }

    /// <summary>"#rrggbb" or "#rgb" → ARGB.</summary>
    public static uint? HexColor(string? s)
    {
        if (s == null)
            return null;
        s = s.Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s.Select(ch => $"{ch}{ch}"));
        return s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, Inv, out var v) ? 0xFF000000 | v : null;
    }

    private static uint WithAlpha(uint argb, string? opacity) =>
        double.TryParse(opacity, NumberStyles.Float, Inv, out var o) ? ((uint)Math.Clamp(o * 255, 0, 255) << 24) | (argb & 0xFFFFFF) : (0x60u << 24) | (argb & 0xFFFFFF);

    // ------------------------------------------------------------------ CSV

    public static MapFileContent ReadCsv(string path)
    {
        var content = new MapFileContent();
        using var reader = CsvStream.Open(path);
        var row = 0;
        var skipped = 0;
        foreach (var r in CsvStream.Rows(reader))
        {
            row++;
            var lat = CsvStream.Number(CsvStream.Get(r, "latitude", "lat", "y", "point_y"));
            var lon = CsvStream.Number(CsvStream.Get(r, "longitude", "lon", "lng", "long", "x", "point_x"));
            if (lat is not { } la || lon is not { } lo || !Valid(la, lo))
            {
                skipped++;
                continue;
            }
            var props = r.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            var f = new OverlayFeature
            {
                Name = CsvStream.Get(r, "name", "title", "label", "site", "location") ?? $"Row {row}",
                Description = CsvStream.Get(r, "description", "desc", "notes", "comment") ?? "",
                Geometry = OverlayGeometry.Point,
                Folder = CsvStream.Get(r, "category", "type", "folder", "group") ?? "",
                Properties = props
            };
            f.Parts.Add(new List<GeoPoint> { new(la, lo) });
            content.Features.Add(f);
        }
        if (row > 0 && content.Features.Count == 0)
            content.Warnings.Add("No latitude/longitude columns found. The CSV needs columns named latitude and longitude (or lat/lon, y/x), in decimal degrees.");
        else if (skipped > 0)
            content.Warnings.Add($"{skipped} row{(skipped == 1 ? "" : "s")} without a usable latitude/longitude were left out.");
        return content;
    }

    /// <summary>Point features as rows with name/latitude/longitude and the file's fields (for the AED import).</summary>
    public static IEnumerable<Dictionary<string, string>> PointRows(MapFileContent content) =>
        content.Features.Where(f => f.Geometry == OverlayGeometry.Point).Select(f =>
        {
            var row = new Dictionary<string, string>(f.Properties, StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = f.Name,
                ["latitude"] = f.Parts[0][0].Lat.ToString("0.0000000", Inv),
                ["longitude"] = f.Parts[0][0].Lon.ToString("0.0000000", Inv)
            };
            if (f.Description.Length > 0 && !row.ContainsKey("location"))
                row["location"] = f.Description;
            return row;
        });
}
