using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FieldMap.Core.Geo;
using FieldMap.Core.Routing;

namespace FieldMap.Core.Drawings;

/// <summary>Export and import of drawings as GeoJSON, KML and GPX (and routes/trails as GPX).</summary>
public static class DrawingFormats
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------ GeoJSON

    public static string ToGeoJson(IEnumerable<Drawing> drawings)
    {
        var features = new JsonArray();
        foreach (var d in drawings)
        {
            var props = new JsonObject
            {
                ["kind"] = d.Kind.ToString(),
                ["label"] = d.Label,
                ["notes"] = d.Notes,
                ["created"] = d.CreatedUtc.ToString("o", Inv),
                ["modified"] = d.ModifiedUtc.ToString("o", Inv),
                ["id"] = d.Id.ToString(),
                ["active"] = d.Active,
                ["closesRoads"] = d.ClosesRoads,
                ["source"] = "Field Map"
            };
            if (d.Rings.Count > 0) props["rings"] = new JsonArray(d.Rings.Select(r => (JsonNode)r).ToArray());
            if (d.IsolationMeters is { } iso) props["isolationMeters"] = iso;
            if (d.ProtectiveMeters is { } pm) props["protectiveMeters"] = pm;
            if (d.WindFromDegrees is { } wf) props["windFromDegrees"] = wf;
            if (d.Kind == DrawingKind.Hazmat) { props["protectiveShape"] = d.ProtectiveShape.ToString(); props["sectorWidthDegrees"] = d.SectorWidthDegrees; }
            if (d.UnNumber != null) props["unNumber"] = d.UnNumber;
            if (d.Unit != null) props["unit"] = d.Unit;
            features.Add(Feature(Geom(d.Shape, d.Points), props));
            // Rings and zones as extra polygons so other tools can show them.
            foreach (var (name, ring) in d.DerivedRings())
                features.Add(Feature(Geom(DrawingShape.Polygon, ring), new JsonObject { ["kind"] = "derived", ["parent"] = d.Id.ToString(), ["label"] = $"{d.Title} – {name}" }));
        }
        return new JsonObject { ["type"] = "FeatureCollection", ["features"] = features }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Feature(JsonObject geometry, JsonObject props) => new() { ["type"] = "Feature", ["geometry"] = geometry, ["properties"] = props };

    private static JsonArray Pos(GeoPoint p) => new(Math.Round(p.Lon, 7), Math.Round(p.Lat, 7));

    private static JsonObject Geom(DrawingShape shape, IReadOnlyList<GeoPoint> pts) => shape switch
    {
        DrawingShape.Point => new JsonObject { ["type"] = "Point", ["coordinates"] = Pos(pts[0]) },
        DrawingShape.Line => new JsonObject { ["type"] = "LineString", ["coordinates"] = new JsonArray(pts.Select(p => (JsonNode)Pos(p)).ToArray()) },
        _ => new JsonObject
        {
            ["type"] = "Polygon",
            ["coordinates"] = new JsonArray(new JsonArray(pts.Concat(pts.Count > 0 && pts[0] != pts[^1] ? new[] { pts[0] } : Array.Empty<GeoPoint>()).Select(p => (JsonNode)Pos(p)).ToArray()))
        }
    };

    public static List<Drawing> FromGeoJson(string json)
    {
        var list = new List<Drawing>();
        var root = JsonNode.Parse(json) ?? throw new FormatException("Empty GeoJSON.");
        var features = root["type"]?.GetValue<string>() == "FeatureCollection" ? root["features"]!.AsArray().OfType<JsonNode>() : new[] { root };
        foreach (var f in features)
        {
            var props = f["properties"] as JsonObject ?? new JsonObject();
            if (props["kind"]?.GetValue<string>() == "derived")
                continue;
            var g = f["geometry"];
            if (g == null)
                continue;
            var type = g["type"]?.GetValue<string>();
            var coords = g["coordinates"];
            GeoPoint P(JsonNode n) => new(n[1]!.GetValue<double>(), n[0]!.GetValue<double>());
            var (shape, pts) = type switch
            {
                "Point" => (DrawingShape.Point, new List<GeoPoint> { P(coords!) }),
                "LineString" => (DrawingShape.Line, coords!.AsArray().Select(n => P(n!)).ToList()),
                "Polygon" => (DrawingShape.Polygon, coords![0]!.AsArray().Select(n => P(n!)).ToList()),
                "MultiPoint" => (DrawingShape.Point, new List<GeoPoint> { P(coords![0]!) }),
                _ => (DrawingShape.Point, new List<GeoPoint>())
            };
            if (pts.Count == 0)
                continue;
            if (shape == DrawingShape.Polygon && pts.Count > 1 && pts[0] == pts[^1])
                pts.RemoveAt(pts.Count - 1);
            var d = new Drawing
            {
                Kind = Enum.TryParse<DrawingKind>(Str(props, "kind"), true, out var k) ? k : shape switch { DrawingShape.Line => DrawingKind.Measure, DrawingShape.Polygon => DrawingKind.Perimeter, _ => DrawingKind.Note },
                Label = Str(props, "label") ?? Str(props, "name") ?? Str(props, "title") ?? "",
                Notes = Str(props, "notes") ?? Str(props, "description") ?? "",
                Shape = shape,
                Points = pts,
                Active = props["active"]?.GetValue<bool>() ?? true,
                ClosesRoads = props["closesRoads"]?.GetValue<bool>() ?? false,
                IsolationMeters = Num(props, "isolationMeters"),
                ProtectiveMeters = Num(props, "protectiveMeters"),
                WindFromDegrees = Num(props, "windFromDegrees"),
                UnNumber = Str(props, "unNumber"),
                Unit = Str(props, "unit")
            };
            if (Enum.TryParse<ProtectiveShape>(Str(props, "protectiveShape"), out var ps)) d.ProtectiveShape = ps;
            if (Num(props, "sectorWidthDegrees") is { } sw) d.SectorWidthDegrees = sw;
            if (props["rings"] is JsonArray rings) d.Rings = rings.Select(r => r!.GetValue<double>()).ToList();
            if (DateTime.TryParse(Str(props, "created"), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var c)) d.CreatedUtc = c;
            if (DateTime.TryParse(Str(props, "modified"), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var m)) d.ModifiedUtc = m;
            if (Guid.TryParse(Str(props, "id"), out var id)) d.Id = id;
            list.Add(d);
        }
        return list;
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[key]?.ToString();
    private static double? Num(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    // ------------------------------------------------------------------ KML

    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";

    private static string KmlCoords(IEnumerable<GeoPoint> pts) => string.Join(" ", pts.Select(p => string.Create(Inv, $"{p.Lon:0.0000000},{p.Lat:0.0000000},0")));

    public static string ToKml(IEnumerable<Drawing> drawings, string documentName = "Field Map drawings")
    {
        var doc = new XElement(Kml + "Document", new XElement(Kml + "name", documentName));
        foreach (var kind in Enum.GetValues<DrawingKind>())
            doc.Add(new XElement(Kml + "Style", new XAttribute("id", kind.ToString()),
                new XElement(Kml + "LineStyle", new XElement(Kml + "color", KmlColor(kind)), new XElement(Kml + "width", 3)),
                new XElement(Kml + "PolyStyle", new XElement(Kml + "color", "40" + KmlColor(kind)[2..]))));
        foreach (var d in drawings)
        {
            var data = new XElement(Kml + "ExtendedData",
                Data("kind", d.Kind.ToString()), Data("label", d.Label), Data("created", d.CreatedUtc.ToString("o", Inv)), Data("modified", d.ModifiedUtc.ToString("o", Inv)),
                Data("id", d.Id.ToString()), Data("active", d.Active ? "true" : "false"), Data("closesRoads", d.ClosesRoads ? "true" : "false"));
            if (d.Rings.Count > 0) data.Add(Data("rings", string.Join(";", d.Rings.Select(r => r.ToString(Inv)))));
            if (d.IsolationMeters is { } iso) data.Add(Data("isolationMeters", iso.ToString(Inv)));
            if (d.ProtectiveMeters is { } pm) data.Add(Data("protectiveMeters", pm.ToString(Inv)));
            if (d.WindFromDegrees is { } wf) data.Add(Data("windFromDegrees", wf.ToString(Inv)));
            if (d.UnNumber != null) data.Add(Data("unNumber", d.UnNumber));
            var folder = new XElement(Kml + "Folder", new XElement(Kml + "name", d.Title));
            folder.Add(Placemark(d.Title, d.Notes, d.Kind, Geometry(d.Shape, d.Points), data, d.CreatedUtc));
            foreach (var (name, ring) in d.DerivedRings())
                folder.Add(Placemark($"{d.Title} – {name}", "", d.Kind, Geometry(DrawingShape.Polygon, ring), new XElement(Kml + "ExtendedData", Data("kind", "derived")), d.CreatedUtc));
            doc.Add(folder);
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(Kml + "kml", doc)).ToString();

        static XElement Data(string name, string value) => new(Kml + "Data", new XAttribute("name", name), new XElement(Kml + "value", value));
    }

    private static XElement Placemark(string name, string notes, DrawingKind kind, XElement geometry, XElement data, DateTime created) =>
        new(Kml + "Placemark", new XElement(Kml + "name", name), new XElement(Kml + "description", notes),
            new XElement(Kml + "TimeStamp", new XElement(Kml + "when", created.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv))),
            new XElement(Kml + "styleUrl", "#" + kind), data, geometry);

    private static XElement Geometry(DrawingShape shape, IReadOnlyList<GeoPoint> pts) => shape switch
    {
        DrawingShape.Point => new XElement(Kml + "Point", new XElement(Kml + "coordinates", KmlCoords(pts.Take(1)))),
        DrawingShape.Line => new XElement(Kml + "LineString", new XElement(Kml + "tessellate", 1), new XElement(Kml + "coordinates", KmlCoords(pts))),
        _ => new XElement(Kml + "Polygon", new XElement(Kml + "outerBoundaryIs", new XElement(Kml + "LinearRing",
            new XElement(Kml + "coordinates", KmlCoords(pts.Concat(pts.Count > 0 && pts[0] != pts[^1] ? new[] { pts[0] } : Array.Empty<GeoPoint>())))))) 
    };

    private static string KmlColor(DrawingKind kind) => kind switch
    {
        // KML colours are aabbggrr.
        DrawingKind.RoadClosure or DrawingKind.Cordon => "ff0000ff",
        DrawingKind.Hazmat => "ff00a5ff",
        DrawingKind.Staging or DrawingKind.Rendezvous => "ff00ff00",
        DrawingKind.LandingZone => "ffff00ff",
        DrawingKind.Perimeter => "ff00ffff",
        _ => "ffffffff"
    };

    public static List<Drawing> FromKml(string kml)
    {
        var doc = XDocument.Parse(kml);
        var ns = doc.Root?.Name.Namespace ?? Kml;
        var list = new List<Drawing>();
        foreach (var pm in doc.Descendants(ns + "Placemark"))
        {
            var data = pm.Descendants(ns + "Data").ToDictionary(e => (string?)e.Attribute("name") ?? "", e => e.Element(ns + "value")?.Value ?? "");
            if (data.GetValueOrDefault("kind") == "derived")
                continue;
            var point = pm.Descendants(ns + "Point").FirstOrDefault();
            var line = pm.Descendants(ns + "LineString").FirstOrDefault();
            var poly = pm.Descendants(ns + "Polygon").FirstOrDefault();
            var (shape, coordsEl) = point != null ? (DrawingShape.Point, point) : line != null ? (DrawingShape.Line, line) : poly != null ? (DrawingShape.Polygon, poly.Descendants(ns + "outerBoundaryIs").FirstOrDefault() ?? poly) : (DrawingShape.Point, (XElement?)null);
            if (coordsEl == null)
                continue;
            var pts = ParseKmlCoords(coordsEl.Descendants(ns + "coordinates").FirstOrDefault()?.Value ?? "");
            if (pts.Count == 0)
                continue;
            if (shape == DrawingShape.Polygon && pts.Count > 1 && pts[0] == pts[^1])
                pts.RemoveAt(pts.Count - 1);
            var d = new Drawing
            {
                Kind = Enum.TryParse<DrawingKind>(data.GetValueOrDefault("kind"), true, out var k) ? k : shape switch { DrawingShape.Line => DrawingKind.Measure, DrawingShape.Polygon => DrawingKind.Perimeter, _ => DrawingKind.Note },
                Label = data.TryGetValue("label", out var label) ? label : pm.Element(ns + "name")?.Value ?? "",
                Notes = pm.Element(ns + "description")?.Value ?? "",
                Shape = shape,
                Points = pts,
                Active = data.GetValueOrDefault("active") != "false",
                ClosesRoads = data.GetValueOrDefault("closesRoads") == "true",
                IsolationMeters = D(data.GetValueOrDefault("isolationMeters")),
                ProtectiveMeters = D(data.GetValueOrDefault("protectiveMeters")),
                WindFromDegrees = D(data.GetValueOrDefault("windFromDegrees")),
                UnNumber = data.GetValueOrDefault("unNumber")
            };
            if (data.TryGetValue("rings", out var rings) && rings.Length > 0)
                d.Rings = rings.Split(';').Select(r => double.Parse(r, Inv)).ToList();
            if (DateTime.TryParse(data.GetValueOrDefault("created") ?? pm.Descendants(ns + "when").FirstOrDefault()?.Value, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var c)) d.CreatedUtc = c;
            if (DateTime.TryParse(data.GetValueOrDefault("modified"), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var m)) d.ModifiedUtc = m;
            if (Guid.TryParse(data.GetValueOrDefault("id"), out var id)) d.Id = id;
            list.Add(d);
        }
        return list;
    }

    private static double? D(string? s) => double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : null;

    private static List<GeoPoint> ParseKmlCoords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Split(','))
            .Where(p => p.Length >= 2)
            .Select(p => new GeoPoint(double.Parse(p[1], Inv), double.Parse(p[0], Inv)))
            .ToList();

    // ------------------------------------------------------------------ GPX

    private static readonly XNamespace Gpx = "http://www.topografix.com/GPX/1/1";

    public static string ToGpx(IEnumerable<Drawing> drawings, IEnumerable<(string Name, IReadOnlyList<GeoPoint> Points, IReadOnlyList<DateTime>? Times)>? tracks = null)
    {
        var gpx = new XElement(Gpx + "gpx", new XAttribute("version", "1.1"), new XAttribute("creator", "Field Map"),
            new XElement(Gpx + "metadata", new XElement(Gpx + "name", "Field Map export"), new XElement(Gpx + "time", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv))));
        var list = drawings.ToList();
        foreach (var d in list.Where(d => d.Shape == DrawingShape.Point))
            gpx.Add(Wpt(d.Points[0], d.Title, $"{Drawing.KindLabel(d.Kind)}{(d.Notes.Length > 0 ? ": " + d.Notes : "")}", d.CreatedUtc, d.Kind.ToString()));
        foreach (var d in list.Where(d => d.Shape != DrawingShape.Point))
        {
            var pts = d.Shape == DrawingShape.Polygon ? d.Points.Append(d.Points[0]).ToList() : d.Points;
            gpx.Add(new XElement(Gpx + "rte", new XElement(Gpx + "name", d.Title), new XElement(Gpx + "desc", d.Notes), new XElement(Gpx + "type", d.Kind.ToString()),
                pts.Select(p => new XElement(Gpx + "rtept", new XAttribute("lat", p.Lat.ToString("0.0000000", Inv)), new XAttribute("lon", p.Lon.ToString("0.0000000", Inv))))));
        }
        foreach (var (name, points, times) in tracks ?? Enumerable.Empty<(string, IReadOnlyList<GeoPoint>, IReadOnlyList<DateTime>?)>())
            gpx.Add(new XElement(Gpx + "trk", new XElement(Gpx + "name", name), new XElement(Gpx + "trkseg",
                points.Select((p, i) => new XElement(Gpx + "trkpt", new XAttribute("lat", p.Lat.ToString("0.0000000", Inv)), new XAttribute("lon", p.Lon.ToString("0.0000000", Inv)),
                    times != null && i < times.Count ? new XElement(Gpx + "time", times[i].ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", Inv)) : null)))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), gpx).ToString();
    }

    private static XElement Wpt(GeoPoint p, string name, string desc, DateTime time, string type) =>
        new(Gpx + "wpt", new XAttribute("lat", p.Lat.ToString("0.0000000", Inv)), new XAttribute("lon", p.Lon.ToString("0.0000000", Inv)),
            new XElement(Gpx + "time", time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", Inv)), new XElement(Gpx + "name", name), new XElement(Gpx + "desc", desc), new XElement(Gpx + "type", type));

    public static string RouteToGpx(Route route, string name) =>
        ToGpx(Array.Empty<Drawing>(), new[] { (name, (IReadOnlyList<GeoPoint>)route.Geometry, (IReadOnlyList<DateTime>?)null) });

    public static List<Drawing> FromGpx(string gpxText)
    {
        var doc = XDocument.Parse(gpxText);
        var ns = doc.Root?.Name.Namespace ?? Gpx;
        var list = new List<Drawing>();
        GeoPoint P(XElement e) => new(double.Parse((string)e.Attribute("lat")!, Inv), double.Parse((string)e.Attribute("lon")!, Inv));
        DateTime Time(XElement e) => DateTime.TryParse(e.Element(ns + "time")?.Value, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.UtcNow;
        DrawingKind Kind(XElement e, DrawingKind fallback) => Enum.TryParse<DrawingKind>(e.Element(ns + "type")?.Value, true, out var k) ? k : fallback;
        foreach (var w in doc.Descendants(ns + "wpt"))
            list.Add(new Drawing { Kind = Kind(w, DrawingKind.Note), Shape = DrawingShape.Point, Points = { P(w) }, Label = w.Element(ns + "name")?.Value ?? "", Notes = w.Element(ns + "desc")?.Value ?? "", CreatedUtc = Time(w) });
        foreach (var r in doc.Descendants(ns + "rte"))
        {
            var pts = r.Elements(ns + "rtept").Select(P).ToList();
            var kind = Kind(r, DrawingKind.Measure);
            var shape = Drawing.DefaultShape(kind) == DrawingShape.Polygon && pts.Count > 3 && pts[0] == pts[^1] ? DrawingShape.Polygon : DrawingShape.Line;
            if (shape == DrawingShape.Polygon)
                pts.RemoveAt(pts.Count - 1);
            if (pts.Count >= 2)
                list.Add(new Drawing { Kind = kind, Shape = shape, Points = pts, Label = r.Element(ns + "name")?.Value ?? "", Notes = r.Element(ns + "desc")?.Value ?? "" });
        }
        foreach (var t in doc.Descendants(ns + "trk"))
        {
            var pts = t.Descendants(ns + "trkpt").Select(P).ToList();
            if (pts.Count >= 2)
                list.Add(new Drawing { Kind = DrawingKind.Measure, Shape = DrawingShape.Line, Points = pts, Label = t.Element(ns + "name")?.Value ?? "Track" });
        }
        return list;
    }

    /// <summary>Imports by file extension (.geojson/.json, .kml, .gpx).</summary>
    public static List<Drawing> Import(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".kml" => FromKml(text),
            ".gpx" => FromGpx(text),
            _ => FromGeoJson(text)
        };
    }

    public static void Export(string path, IEnumerable<Drawing> drawings)
    {
        var text = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".kml" => ToKml(drawings),
            ".gpx" => ToGpx(drawings),
            _ => ToGeoJson(drawings)
        };
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}
