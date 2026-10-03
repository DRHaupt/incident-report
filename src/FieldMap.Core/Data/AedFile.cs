using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FieldMap.Core.Geo;
using FieldMap.Core.Search;

namespace FieldMap.Core.Data;

/// <summary>One AED site from an imported list.</summary>
public sealed class AedRow
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string City { get; set; } = "";
    public string Province { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Location { get; set; } = "";
    public string Access { get; set; } = "";
    public string Phone { get; set; } = "";
    public int? Count { get; set; }
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public string LastVerified { get; set; } = "";
    public double? Lat { get; set; }
    public double? Lon { get; set; }

    /// <summary>"" when the list gave coordinates; otherwise how the position was found.</summary>
    public string LocationMethod { get; set; } = "";

    public bool HasPosition => Lat is not null && Lon is not null;

    public string FullAddress => string.Join(", ", new[] { Address, City, Province, PostalCode }.Where(s => s.Length > 0));
}

public sealed record AedLocateReport(int Given, int FromText, int ByAddress, int NotPlaced);

/// <summary>
/// AED lists as organisations and registries hand them out (CSV, Excel, GeoJSON, KML/KMZ/GPX), read into one shape,
/// with sites that only have an address placed through the offline address search. The result is written as a
/// plain CSV in the AED imports folder, which every map build merges.
/// </summary>
public static partial class AedFile
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string FileFilter = "AED list (*.csv;*.xlsx;*.geojson;*.json;*.kml;*.kmz;*.gpx)|*.csv;*.xlsx;*.geojson;*.json;*.kml;*.kmz;*.gpx";

    /// <summary>Reads every row that names a site (with or without coordinates).</summary>
    public static List<AedRow> Read(string path, string defaultSource, string? sheet = null)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        IEnumerable<Dictionary<string, string>> rows = ext switch
        {
            ".xlsx" => XlsxReader.Rows(path, sheet),
            ".geojson" or ".json" => PoiImport.GeoJsonRows(path),
            ".kml" or ".kmz" or ".gpx" => Overlays.MapFileImporter.PointRows(Overlays.MapFileImporter.Load(path)),
            _ => ReadCsv(path)
        };
        var list = new List<AedRow>();
        foreach (var r in rows)
        {
            var row = new AedRow
            {
                Name = Get(r, "name", "venue / aed site", "venue", "site", "site name", "location name", "facility", "building") ?? "",
                Address = Get(r, "address", "street address", "street") ?? "",
                City = Get(r, "city", "municipality", "town", "community") ?? "",
                Province = Get(r, "province", "prov", "state") ?? "",
                PostalCode = Get(r, "postal code", "postal", "postcode", "zip") ?? "",
                Location = Get(r, "aed location in building", "location in building", "location", "placement", "defibrillator:location", "where") ?? "",
                Access = Get(r, "access", "hours", "opening_hours", "availability") ?? "",
                Phone = Get(r, "phone", "telephone") ?? "",
                Count = int.TryParse(Get(r, "# aeds", "aeds", "number of aeds", "aed count", "count", "units"), NumberStyles.Integer, Inv, out var n) && n > 0 ? n : null,
                Id = Get(r, "site id", "id", "registry id") ?? "",
                Source = Get(r, "source") ?? defaultSource,
                LastVerified = Get(r, "last_verified", "last verified", "verified", "check_date", "inspection date", "inspection_date") ?? "",
                Lat = Number(Get(r, "latitude", "lat", "y")),
                Lon = Number(Get(r, "longitude", "lon", "lng", "long", "x")),
                // Kept when Field Map's own normalised file is read back at build time.
                LocationMethod = Get(r, "location_method") ?? ""
            };
            if (row.Lat is { } la && row.Lon is { } lo && !Valid(la, lo))
                row.Lat = row.Lon = null;
            if (row.Name.Length == 0 && row.Address.Length == 0)
                continue;
            if (row.Name.Length == 0)
                row.Name = row.Address;
            list.Add(row);
        }
        return list;
    }

    private static IEnumerable<Dictionary<string, string>> ReadCsv(string path)
    {
        using var reader = CsvStream.Open(path);
        return CsvStream.Rows(reader).ToList();
    }

    private static bool Valid(double lat, double lon) => lat is >= -90 and <= 90 && lon is >= -180 and <= 180 && !(Math.Abs(lat) < 0.01 && Math.Abs(lon) < 0.01);

    private static double? Number(string? s) => double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : null;

    /// <summary>Exact header names first, then the same names ignoring case, spaces and punctuation.</summary>
    internal static string? Get(Dictionary<string, string> row, params string[] names)
    {
        foreach (var n in names)
            if (row.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
        foreach (var n in names)
        {
            var key = Norm(n);
            foreach (var (k, v) in row)
                if (Norm(k) == key && !string.IsNullOrWhiteSpace(v))
                    return v.Trim();
        }
        return null;
    }

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    [GeneratedRegex(@"(\d{1,3})\s*(?:DEGREES|DEG|°)\s*(\d{1,2})\s*(?:MINUTES|MIN|')\s*([\d.]+)\s*(?:SECONDS|SEC|"")?\s*(NORTH|SOUTH|N|S)\b.*?(\d{1,3})\s*(?:DEGREES|DEG|°)\s*(\d{1,2})\s*(?:MINUTES|MIN|')\s*([\d.]+)\s*(?:SECONDS|SEC|"")?\s*(EAST|WEST|E|W)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DmsTextRx();

    [GeneratedRegex(@"(-?\d{2}\.\d{3,})\s*[,;/ ]\s*(-?\d{2,3}\.\d{3,})")]
    private static partial Regex DecimalTextRx();

    /// <summary>Coordinates written into an address or note ("LATITUDE 49 DEGREES 4 MINUTES 46.7 SECONDS NORTH, …").</summary>
    public static GeoPoint? CoordinatesInText(string text)
    {
        var m = DmsTextRx().Match(text);
        if (m.Success)
        {
            double D(int g) => double.Parse(m.Groups[g].Value, Inv);
            var lat = D(1) + D(2) / 60 + D(3) / 3600;
            var lon = D(5) + D(6) / 60 + D(7) / 3600;
            if (m.Groups[4].Value.StartsWith("S", StringComparison.OrdinalIgnoreCase))
                lat = -lat;
            if (m.Groups[8].Value.StartsWith("W", StringComparison.OrdinalIgnoreCase))
                lon = -lon;
            return Valid(lat, lon) ? new GeoPoint(lat, lon) : null;
        }
        var d = DecimalTextRx().Match(text);
        if (d.Success && double.TryParse(d.Groups[1].Value, NumberStyles.Float, Inv, out var la) && double.TryParse(d.Groups[2].Value, NumberStyles.Float, Inv, out var lo))
        {
            // North America: longitudes are negative; accept a positive one written without the sign.
            if (lo > 0 && la > 0)
                lo = -lo;
            return Valid(la, lo) ? new GeoPoint(la, lo) : null;
        }
        return null;
    }

    [GeneratedRegex(@"^(?:#\s*\w+\s+|(?:UNIT|SUITE|STE|APT)\s*\w+\s*[,-]?\s*|\d+[A-Z]?\s*-\s*(?=\d))", RegexOptions.IgnoreCase)]
    private static partial Regex UnitPrefixRx();

    [GeneratedRegex(@"^(\d+[A-Z]?)\s+(\d+[A-Z]?\s+\D.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex UnitThenNumberRx();

    /// <summary>"#2 175B WRANGLER WY" / "207 2776 BOURQUIN CR" / "Unit 4, 120 Main St" → the street address without the unit.</summary>
    public static string StreetAddress(string address)
    {
        var a = address.Trim();
        a = UnitPrefixRx().Replace(a, "");
        var m = UnitThenNumberRx().Match(a);
        if (m.Success)
            a = m.Groups[2].Value;
        return a.Trim().TrimEnd('.', ',');
    }

    /// <summary>
    /// Gives a position to rows without one: coordinates written in the text first, then the offline address search
    /// (house-number matches only, in the same municipality when the row names one). Rows that can't be placed
    /// confidently are left without a position.
    /// </summary>
    public static AedLocateReport Locate(List<AedRow> rows, Func<string, IReadOnlyList<SearchResult>>? search)
    {
        int given = 0, fromText = 0, byAddress = 0, notPlaced = 0;
        foreach (var row in rows)
        {
            if (row.HasPosition)
            {
                given++;
                continue;
            }
            if (CoordinatesInText($"{row.Address} {row.Location}") is { } p)
            {
                (row.Lat, row.Lon) = (p.Lat, p.Lon);
                row.LocationMethod = "coordinates written in the list";
                fromText++;
                continue;
            }
            var street = StreetAddress(row.Address);
            if (search != null && street.Length > 0 && char.IsDigit(street[0]))
            {
                var city = Norm(row.City);
                var match = search(street)
                    .Where(r => r.Kind == ResultKind.Address)
                    .FirstOrDefault(r => city.Length == 0 || Norm(r.Name + " " + r.Detail).Contains(city));
                if (match != null)
                {
                    (row.Lat, row.Lon) = (match.Point.Lat, match.Point.Lon);
                    row.LocationMethod = match.Approximate ? $"street address, estimated along the street ({match.Name})" : $"street address ({match.Name})";
                    byAddress++;
                    continue;
                }
            }
            notPlaced++;
        }
        return new AedLocateReport(given, fromText, byAddress, notPlaced);
    }

    private static readonly string[] Columns =
        { "name", "latitude", "longitude", "address", "location", "access", "phone", "aed_count", "id", "source", "last_verified", "location_method", "retrieved" };

    /// <summary>Writes rows (those with a position, or the others) as the CSV the map builds read.</summary>
    public static void WriteCsv(string path, IEnumerable<AedRow> rows, DateTime retrievedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Columns));
        foreach (var r in rows)
        {
            var values = new[]
            {
                r.Name, r.Lat?.ToString("0.0000000", Inv) ?? "", r.Lon?.ToString("0.0000000", Inv) ?? "", r.FullAddress, r.Location, r.Access, r.Phone,
                r.Count?.ToString(Inv) ?? "", r.Id, r.Source, r.LastVerified, r.LocationMethod, retrievedUtc.ToString("yyyy-MM-dd", Inv)
            };
            sb.AppendLine(string.Join(",", values.Select(Quote)));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Quote(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
