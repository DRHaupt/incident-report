using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FieldMap.Core.Build;
using FieldMap.Core.Geo;
using FieldMap.Core.Layers;

namespace FieldMap.Core.Data;

/// <summary>Streams rows from a CSV file (RFC 4180; quoted fields may contain commas and line breaks).</summary>
public static class CsvStream
{
    public static IEnumerable<Dictionary<string, string>> Rows(TextReader reader)
    {
        string[]? header = null;
        foreach (var record in Records(reader))
        {
            if (header == null)
            {
                header = record.Select(h => h.Trim().Trim('﻿').ToLowerInvariant()).ToArray();
                continue;
            }
            if (record.All(string.IsNullOrWhiteSpace))
                continue;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Length && i < record.Count; i++)
                row[header[i]] = record[i];
            yield return row;
        }
    }

    public static IEnumerable<List<string>> Records(TextReader reader)
    {
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        int c;
        while ((c = reader.Read()) != -1)
        {
            var ch = (char)c;
            if (quoted)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else quoted = false;
                }
                else field.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; break;
                case ',': record.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    record.Add(field.ToString()); field.Clear();
                    yield return record;
                    record = new List<string>();
                    break;
                default: field.Append(ch); break;
            }
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }

    /// <summary>Opens a CSV, or the matching entry inside a .zip.</summary>
    public static TextReader Open(string path, string? zipEntry = null)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var zip = ZipFile.OpenRead(path);
        var pattern = zipEntry ?? "*.csv";
        var regex = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var entry = zip.Entries.FirstOrDefault(e => regex.IsMatch(e.Name)) ?? throw new FileNotFoundException($"No file matching {pattern} in {Path.GetFileName(path)}.");
        return new ZipReader(zip, new StreamReader(entry.Open(), Encoding.UTF8, true));
    }

    private sealed class ZipReader : TextReader
    {
        private readonly ZipArchive _zip;
        private readonly StreamReader _inner;
        public ZipReader(ZipArchive zip, StreamReader inner) { _zip = zip; _inner = inner; }
        public override int Read() => _inner.Read();
        public override int Peek() => _inner.Peek();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _inner.Dispose(); _zip.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal static string? Get(Dictionary<string, string> row, params string[] names)
    {
        foreach (var n in names)
            if (row.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
        return null;
    }

    internal static double? Number(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>Address points from the StatCan Open Database of Addresses (or any CSV with similar columns).</summary>
public static class AddressImport
{
    public static int Import(string path, MapData data, string sourceName, string? zipEntry = null, GeoBounds? clip = null, CancellationToken cancel = default)
    {
        var count = 0;
        using var reader = CsvStream.Open(path, zipEntry);
        foreach (var row in CsvStream.Rows(reader))
        {
            if (++count % 50000 == 0)
                cancel.ThrowIfCancellationRequested();
            var lat = CsvStream.Number(CsvStream.Get(row, "latitude", "lat", "y"));
            var lon = CsvStream.Number(CsvStream.Get(row, "longitude", "lon", "long", "lng", "x"));
            var number = CsvStream.Get(row, "street_no", "number", "house_number", "civic_number", "civic_no");
            var street = CsvStream.Get(row, "street", "street_name", "full_street_name");
            if (street == null)
            {
                var parts = new[] { CsvStream.Get(row, "str_name"), CsvStream.Get(row, "str_type"), CsvStream.Get(row, "str_dir") }.Where(p => p != null);
                street = string.Join(' ', parts);
            }
            if (lat is not { } la || lon is not { } lo || number == null || string.IsNullOrWhiteSpace(street))
                continue;
            if (la is < -90 or > 90 || lo is < -180 or > 180 || (clip is { } c && !c.Contains(la, lo)))
                continue;
            data.Addresses.Add(new AddressPoint
            {
                Number = number,
                Street = TitleCase(street),
                City = TitleCase(CsvStream.Get(row, "city", "csdname", "municipality", "community") ?? ""),
                Unit = CsvStream.Get(row, "unit", "unit_no", "suite") ?? "",
                Lat = la,
                Lon = lo,
                Source = sourceName
            });
        }
        return data.Addresses.Count;
    }

    /// <summary>"MAIN ST NW" → "Main St NW" (keeps directions and ordinals tidy).</summary>
    public static string TitleCase(string s)
    {
        if (s.Any(char.IsLower))
            return s.Trim();
        var words = s.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w =>
            w is "ne" or "nw" or "se" or "sw" or "n" or "s" or "e" or "w" ? w.ToUpperInvariant() :
            char.IsDigit(w[0]) ? w : char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(' ', words);
    }
}

/// <summary>Facility tables (official CSV/GeoJSON lists and AED registries) merged into POI layers.</summary>
public static class PoiImport
{
    /// <summary>Reads POIs from a CSV or GeoJSON file using a column mapping.</summary>
    public static List<Poi> Read(string path, PoiMapping m, string sourceName, string? zipEntry = null)
    {
        var rows = path.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? GeoJsonRows(path)
            : ReadCsv(path, zipEntry);
        var list = new List<Poi>();
        foreach (var row in rows)
        {
            if (m.FilterColumn != null && !(row.TryGetValue(m.FilterColumn, out var fv) && m.FilterValues.Any(v => v.Equals(fv?.Trim(), StringComparison.OrdinalIgnoreCase))))
                continue;
            var lat = CsvStream.Number(CsvStream.Get(row, m.LatColumn));
            var lon = CsvStream.Number(CsvStream.Get(row, m.LonColumn));
            if (lat is not { } la || lon is not { } lo || la is < -90 or > 90 || lo is < -180 or > 180 || (la == 0 && lo == 0))
                continue;
            var kind = m.Kind;
            if (m.KindColumn != null && CsvStream.Get(row, m.KindColumn) is { } kv && m.KindMap.TryGetValue(kv, out var mapped))
                kind = mapped;
            var props = new Dictionary<string, string>();
            if (m.AddressColumn != null && CsvStream.Get(row, m.AddressColumn) is { } addr)
                props["address"] = addr;
            foreach (var (k, v) in row)
                if (!string.IsNullOrWhiteSpace(v) && k is "access" or "location" or "opening_hours" or "phone" or "notes" or "operator" or "description")
                    props[k] = v.Trim();
            var verified = m.VerifiedColumn != null ? CsvStream.Get(row, m.VerifiedColumn) : null;
            list.Add(new Poi
            {
                Layer = m.Layer,
                Kind = kind,
                Name = CsvStream.Get(row, m.NameColumn) ?? "",
                Lat = la,
                Lon = lo,
                Props = props,
                Source = sourceName,
                LastVerified = NormalizeDate(verified),
                SourceId = m.IdColumn != null ? CsvStream.Get(row, m.IdColumn) : null
            });
        }
        return list;
    }

    /// <summary>The mapping for AED registry files (CSV or GeoJSON). See docs/FIELDMAP-DATA-SOURCES.md.</summary>
    public static PoiMapping AedMapping => new()
    {
        Layer = LayerCatalog.Aed,
        Kind = "aed",
        NameColumn = "name",
        LatColumn = "latitude",
        LonColumn = "longitude",
        AddressColumn = "address",
        IdColumn = "id",
        VerifiedColumn = "last_verified",
        MergeRadiusMeters = 25
    };

    /// <summary>
    /// Reads an AED registry (CSV or GeoJSON). Columns: name, latitude/lat, longitude/lon/lng, address, access, location,
    /// last_verified, source (per row; defaults to <paramref name="sourceName"/>), id.
    /// </summary>
    public static List<Poi> ReadAeds(string path, string sourceName) =>
        AedFile.Read(path, sourceName).Where(r => r.HasPosition).Select(r =>
        {
            var props = new Dictionary<string, string>();
            void Set(string key, string value)
            {
                if (value.Length > 0)
                    props[key] = value;
            }
            Set("address", r.FullAddress);
            Set("access", r.Access);
            Set("defibrillator:location", r.Location);
            Set("phone", r.Phone);
            Set("aed_count", r.Count is > 1 ? r.Count.Value.ToString(CultureInfo.InvariantCulture) : "");
            Set("location_method", r.LocationMethod);
            return new Poi
            {
                Layer = LayerCatalog.Aed,
                Kind = "aed",
                Name = r.Name,
                Lat = r.Lat!.Value,
                Lon = r.Lon!.Value,
                Props = props,
                Source = r.Source.Length > 0 ? r.Source : sourceName,
                LastVerified = NormalizeDate(r.LastVerified.Length > 0 ? r.LastVerified : null),
                SourceId = r.Id.Length > 0 ? r.Id : null
            };
        }).ToList();

    private static IEnumerable<Dictionary<string, string>> ReadCsv(string path, string? zipEntry)
    {
        using var reader = CsvStream.Open(path, zipEntry);
        foreach (var row in CsvStream.Rows(reader))
            yield return row;
    }

    /// <summary>GeoJSON Point features as rows: properties plus "latitude"/"longitude".</summary>
    public static IEnumerable<Dictionary<string, string>> GeoJsonRows(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var features = doc.RootElement.TryGetProperty("features", out var f) ? f.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
        foreach (var feature in features)
        {
            if (!feature.TryGetProperty("geometry", out var g) || g.ValueKind != JsonValueKind.Object || g.GetProperty("type").GetString() != "Point")
                continue;
            var c = g.GetProperty("coordinates");
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["longitude"] = c[0].GetDouble().ToString(CultureInfo.InvariantCulture),
                ["latitude"] = c[1].GetDouble().ToString(CultureInfo.InvariantCulture)
            };
            if (feature.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                foreach (var p in props.EnumerateObject())
                    row[p.Name.ToLowerInvariant()] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
            yield return row;
        }
    }

    private static string? NormalizeDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : s.Trim();
    }

    /// <summary>
    /// Merges imported POIs into a list: an import within the mapping's radius of an existing POI on the same layer updates
    /// it (name, details and verified date from the import; both sources kept), otherwise it is added. Returns (merged, added).
    /// </summary>
    public static (int Merged, int Added) Merge(List<Poi> existing, IEnumerable<Poi> imported, double radiusMeters)
    {
        var merged = 0;
        var added = 0;
        var byLayer = existing.GroupBy(p => p.Layer).ToDictionary(g => g.Key, g => g.ToList());
        var nextId = existing.Count == 0 ? 1 : existing.Max(p => p.Id) + 1;
        foreach (var imp in imported)
        {
            byLayer.TryGetValue(imp.Layer, out var candidates);
            var match = candidates?.Where(p => p.Point.DistanceTo(imp.Point) <= radiusMeters).OrderBy(p => p.Point.DistanceTo(imp.Point)).FirstOrDefault();
            if (match != null)
            {
                if (imp.Name.Length > 0)
                    match.Name = imp.Name;
                foreach (var (k, v) in imp.Props)
                    match.Props[k] = v;
                match.LastVerified = imp.LastVerified ?? match.LastVerified;
                if (!match.Source.Split(" + ").Contains(imp.Source))
                    match.Source = match.Source.Length > 0 ? $"{imp.Source} + {match.Source}" : imp.Source;
                match.Lat = imp.Lat;
                match.Lon = imp.Lon;
                if (imp.Kind.Length > 0 && match.Kind is "hospital" or "")
                    match.Kind = imp.Kind;
                merged++;
            }
            else
            {
                imp.Id = nextId++;
                existing.Add(imp);
                if (candidates == null)
                    byLayer[imp.Layer] = candidates = new List<Poi>();
                candidates.Add(imp);
                added++;
            }
        }
        return (merged, added);
    }
}
