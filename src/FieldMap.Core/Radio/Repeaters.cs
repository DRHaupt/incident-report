using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FieldMap.Core.Geo;
using Shared.Core;

namespace FieldMap.Core.Radio;

/// <summary>Where a repeater on the map came from.</summary>
public enum RepeaterSource
{
    /// <summary>The organisation's own fixed repeaters.</summary>
    Own,
    /// <summary>Portable or temporary repeaters set up for an event.</summary>
    Event,
    /// <summary>Amateur repeaters from the user's own list (club or coordinator CSV).</summary>
    Amateur,
    /// <summary>RepeaterBook data the user downloaded with their own account.</summary>
    RepeaterBook,
    /// <summary>Licensed land-mobile stations from ISED's spectrum data.</summary>
    Licensed
}

/// <summary>A repeater (or base station) shown on the map, with what's needed to talk through it and predict coverage.</summary>
public sealed class Repeater
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public RepeaterSource Source { get; set; } = RepeaterSource.Own;
    /// <summary>File or list it came from (re-importing the same file replaces its entries).</summary>
    public string SourceName { get; set; } = "";
    public string Name { get; set; } = "";
    public string CallSign { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    /// <summary>Repeater transmit frequency (what you listen on), MHz.</summary>
    public double OutputMHz { get; set; }
    /// <summary>Repeater receive frequency (what you transmit on), MHz; null for simplex.</summary>
    public double? InputMHz { get; set; }
    /// <summary>Access tone/code as written: "CTCSS 100.0", "DCS 023", "NAC 293", "CC 1".</summary>
    public string Tone { get; set; } = "";
    /// <summary>FM, DMR, P25, NXDN, D-STAR, Fusion…</summary>
    public string Mode { get; set; } = "FM";
    public double? AntennaHeightM { get; set; }
    public double? ErpWatts { get; set; }
    public string Owner { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public GeoPoint Point => new(Lat, Lon);
    public double? OffsetMHz => InputMHz is { } i ? Math.Round(i - OutputMHz, 4) : null;
    public string Label => CallSign.Length > 0 ? CallSign : Name.Length > 0 ? Name : $"{OutputMHz:0.000#}";

    public string FrequencyText =>
        $"{OutputMHz:0.000#} MHz" + (OffsetMHz is { } o && Math.Abs(o) > 0.0001 ? $" ({(o > 0 ? "+" : "−")}{Math.Abs(o):0.0##})" : "") +
        (Tone.Length > 0 ? " " + Tone : "");

    public static string SourceLabel(RepeaterSource s) => s switch
    {
        RepeaterSource.Own => "Our repeater",
        RepeaterSource.Event => "Event repeater",
        RepeaterSource.Amateur => "Amateur (my list)",
        RepeaterSource.RepeaterBook => "RepeaterBook",
        RepeaterSource.Licensed => "Licensed (ISED)",
        _ => s.ToString()
    };
}

/// <summary>The repeaters on this computer (repeaters.json), shared by every map window.</summary>
public sealed class RepeaterStore
{
    private readonly JsonListStore<Repeater> _store;

    public RepeaterStore(string? path) => _store = new JsonListStore<Repeater>(path);

    public IReadOnlyList<Repeater> All => _store.Items;
    public string? LoadError => _store.LoadError;

    public event EventHandler? Changed
    {
        add => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    public Repeater? Find(string id) => _store.Items.FirstOrDefault(r => r.Id == id);

    public void Save(Repeater r)
    {
        r.UpdatedUtc = DateTime.UtcNow;
        var i = _store.Items.FindIndex(x => x.Id == r.Id);
        if (i >= 0)
            _store.Items[i] = r;
        else
            _store.Items.Add(r);
        _store.Save();
    }

    public void Remove(string id)
    {
        if (_store.Items.RemoveAll(r => r.Id == id) > 0)
            _store.Save();
    }

    /// <summary>Replaces everything previously imported from the same source and file with <paramref name="items"/>.</summary>
    public int ReplaceImport(RepeaterSource source, string sourceName, IReadOnlyCollection<Repeater> items)
    {
        _store.Items.RemoveAll(r => r.Source == source && string.Equals(r.SourceName, sourceName, StringComparison.OrdinalIgnoreCase));
        foreach (var r in items)
        {
            r.Source = source;
            r.SourceName = sourceName;
        }
        _store.Items.AddRange(items);
        _store.Save();
        return items.Count;
    }

    public int RemoveSource(RepeaterSource source)
    {
        var n = _store.Items.RemoveAll(r => r.Source == source);
        if (n > 0)
            _store.Save();
        return n;
    }
}

/// <summary>Reads repeater lists: any CSV with recognisable columns, RepeaterBook JSON exports, and ISED land-mobile data.</summary>
public static class RepeaterImport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// A CSV with a header row. Columns are matched loosely: latitude/longitude (decimal or DMS), output/frequency/rx,
    /// input/tx or offset, tone/ctcss/pl/dcs/nac, call sign, name/location, mode, antenna height, ERP/power, owner, notes.
    /// </summary>
    public static List<Repeater> ReadCsv(string path, List<string>? warnings = null)
    {
        var rows = Csv.Parse(File.ReadAllText(path));
        if (rows.Count < 2)
            throw new InvalidDataException("The file has no data rows.");
        var header = rows[0].Select(h => Regex.Replace(h.Trim().ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim()).ToList();
        int Col(params string[] names)
        {
            foreach (var n in names)
            {
                var i = header.FindIndex(h => h == n);
                if (i >= 0)
                    return i;
            }
            foreach (var n in names)
            {
                var i = header.FindIndex(h => h.Split(' ').Contains(n) || h.StartsWith(n + " ", StringComparison.Ordinal));
                if (i >= 0)
                    return i;
            }
            return -1;
        }
        int lat = Col("latitude", "lat"), lon = Col("longitude", "long", "lon", "lng");
        int output = Col("output", "output frequency", "frequency", "freq", "rx", "receive", "downlink", "rx frequency");
        int input = Col("input", "input frequency", "input freq", "tx", "transmit", "uplink", "tx frequency");
        int offset = Col("offset", "shift");
        int tone = Col("tone", "ctcss", "pl", "access", "dcs", "nac", "uplink tone", "tone up");
        int call = Col("call sign", "callsign", "call");
        int name = Col("name", "location", "site", "landmark", "nearest city", "city");
        int mode = Col("mode", "type", "emission");
        int height = Col("antenna height", "height", "agl", "hagl");
        int erp = Col("erp", "power", "watts");
        int owner = Col("owner", "licensee", "sponsor", "club", "trustee");
        int notes = Col("notes", "comment", "comments", "remarks");
        if (lat < 0 || lon < 0)
            throw new InvalidDataException("No latitude/longitude columns were found.");
        if (output < 0)
            throw new InvalidDataException("No frequency column (output, frequency or rx) was found.");

        string Get(List<string> row, int i) => i >= 0 && i < row.Count ? row[i].Trim() : "";
        var list = new List<Repeater>();
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.All(string.IsNullOrWhiteSpace))
                continue;
            if (ParseCoordinate(Get(row, lat), true) is not { } la || ParseCoordinate(Get(row, lon), false) is not { } lo || ParseMHz(Get(row, output)) is not { } outMHz)
            {
                warnings?.Add($"Row {r + 1}: skipped (missing position or frequency).");
                continue;
            }
            // Western hemisphere data sometimes omits the minus sign.
            if (lo > 0 && lo > 100 && la > 40)
                lo = -lo;
            double? inMHz = ParseMHz(Get(row, input));
            if (inMHz == null && ParseOffset(Get(row, offset), outMHz) is { } off)
                inMHz = Math.Round(outMHz + off, 5);
            list.Add(new Repeater
            {
                Lat = la, Lon = lo, OutputMHz = outMHz, InputMHz = inMHz, Tone = NormalizeTone(Get(row, tone)),
                CallSign = Get(row, call).ToUpperInvariant(), Name = Get(row, name), Mode = Get(row, mode) is { Length: > 0 } m ? m : "FM",
                AntennaHeightM = ParseNumber(Get(row, height)), ErpWatts = ParseNumber(Get(row, erp)), Owner = Get(row, owner), Notes = Get(row, notes)
            });
        }
        return list;
    }

    /// <summary>
    /// A RepeaterBook API/JSON export ({"results": [{"Callsign", "Frequency", "Input Freq", "PL", "TSQ", "Lat", "Long",
    /// "Nearest City", "Landmark", "Use", "Operational Status", "DMR", "D-Star", …}]}), downloaded with the user's own
    /// RepeaterBook account. Off-air repeaters are left out.
    /// </summary>
    public static List<Repeater> ReadRepeaterBookJson(string path, List<string>? warnings = null)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var results = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
            : doc.RootElement.TryGetProperty("results", out var r) ? r : throw new InvalidDataException("No \"results\" list in the RepeaterBook file.");
        string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
        bool Yes(JsonElement e, string name) => S(e, name) is "Yes" or "yes" or "1" or "true";
        var list = new List<Repeater>();
        var i = 0;
        foreach (var e in results.EnumerateArray())
        {
            i++;
            if (S(e, "Operational Status") is { Length: > 0 } status && !status.StartsWith("On", StringComparison.OrdinalIgnoreCase))
                continue;
            if (ParseCoordinate(S(e, "Lat"), true) is not { } la || ParseCoordinate(S(e, "Long"), false) is not { } lo || ParseMHz(S(e, "Frequency")) is not { } freq)
            {
                warnings?.Add($"Entry {i}: skipped (missing position or frequency).");
                continue;
            }
            var mode = Yes(e, "DMR") ? "DMR" : Yes(e, "APCO P-25") ? "P25" : Yes(e, "NXDN") ? "NXDN" : Yes(e, "D-Star") ? "D-STAR" : Yes(e, "System Fusion") ? "Fusion" : "FM";
            var tone = mode == "DMR" && S(e, "DMR Color Code") is { Length: > 0 } cc ? "CC " + cc : NormalizeTone(S(e, "PL"));
            var place = string.Join(", ", new[] { S(e, "Nearest City"), S(e, "Landmark") }.Where(x => x.Length > 0));
            list.Add(new Repeater
            {
                Lat = la, Lon = lo, OutputMHz = freq, InputMHz = ParseMHz(S(e, "Input Freq")), Tone = tone, Mode = mode,
                CallSign = S(e, "Callsign").ToUpperInvariant(), Name = place, Owner = S(e, "Sponsor"),
                Notes = string.Join(" · ", new[] { S(e, "Use"), S(e, "Notes") }.Where(x => x.Length > 0 && x != "OPEN"))
            });
        }
        return list;
    }

    /// <summary>
    /// ISED spectrum data (land mobile) as CSV. Rows are stations; a station with different transmit and receive
    /// frequencies is taken as a repeater. Columns are matched by name (transmit/receive frequency, latitude/longitude,
    /// licensee, station location, antenna height, ERP), so minor format changes still import. Only stations inside
    /// <paramref name="area"/> are kept.
    /// </summary>
    public static List<Repeater> ReadIsedCsv(string path, GeoBounds? area, List<string>? warnings = null)
    {
        var rows = Csv.Parse(File.ReadAllText(path));
        if (rows.Count < 2)
            throw new InvalidDataException("The file has no data rows.");
        var header = rows[0].Select(h => Regex.Replace(h.Trim().ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim()).ToList();
        int Find(Func<string, bool> match) => header.FindIndex(h => match(h));
        int tx = Find(h => (h.Contains("tx") || h.Contains("transmit")) && h.Contains("freq"));
        int rx = Find(h => (h.Contains("rx") || h.Contains("receive")) && h.Contains("freq"));
        int lat = Find(h => h.Contains("latitude") || h == "lat");
        int lon = Find(h => h.Contains("longitude") || h == "long" || h == "lon");
        int licensee = Find(h => h.Contains("licensee") || h.Contains("company") || h.Contains("client name"));
        int location = Find(h => h.Contains("station location") || h.Contains("location") || h.Contains("site name"));
        int call = Find(h => h.Contains("call sign") || h.Contains("callsign"));
        int height = Find(h => h.Contains("antenna") && h.Contains("height"));
        int erp = Find(h => h.Contains("erp"));
        int tone = Find(h => h.Contains("tone") || h.Contains("ctcss"));
        if (tx < 0 || lat < 0 || lon < 0)
            throw new InvalidDataException("This doesn't look like ISED land-mobile data (no transmit frequency or latitude/longitude columns).");

        string Get(List<string> row, int i) => i >= 0 && i < row.Count ? row[i].Trim() : "";
        var seen = new HashSet<(long, long, long)>();
        var list = new List<Repeater>();
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (ParseMHz(Get(row, tx)) is not { } txMHz || ParseMHz(Get(row, rx)) is not { } rxMHz || Math.Abs(txMHz - rxMHz) < 0.001)
                continue; // simplex or incomplete: not a repeater
            if (ParseCoordinate(Get(row, lat), true) is not { } la || ParseCoordinate(Get(row, lon), false) is not { } lo)
                continue;
            if (lo > 0 && la > 40)
                lo = -lo; // ISED longitudes are often written without the minus sign
            if (area is { } a && !a.Contains(la, lo))
                continue;
            // The same repeater appears once per licence line; keep one per site and channel.
            if (!seen.Add(((long)Math.Round(la * 1000), (long)Math.Round(lo * 1000), (long)Math.Round(txMHz * 10000))))
                continue;
            list.Add(new Repeater
            {
                Lat = la, Lon = lo, OutputMHz = txMHz, InputMHz = rxMHz, Owner = Get(row, licensee), Name = Get(row, location),
                CallSign = Get(row, call).ToUpperInvariant(), AntennaHeightM = ParseNumber(Get(row, height)), ErpWatts = ParseNumber(Get(row, erp)),
                Tone = NormalizeTone(Get(row, tone)), Mode = ""
            });
        }
        if (list.Count == 0)
            warnings?.Add("No repeaters (stations with separate transmit and receive frequencies) were found in the area.");
        return list;
    }

    // ------------------------------------------------------------------ parsing helpers

    /// <summary>Decimal degrees ("49.2827", "-123.12", "123.12W") or DMS ("49 16 58 N", "491658N", "49°16'58\"N").</summary>
    public static double? ParseCoordinate(string text, bool latitude)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;
        var negative = false;
        var hemi = char.ToUpperInvariant(text[^1]);
        if (hemi is 'N' or 'S' or 'E' or 'W')
        {
            negative = hemi is 'S' or 'W';
            text = text[..^1].Trim();
        }
        else if (char.ToUpperInvariant(text[0]) is 'N' or 'S' or 'E' or 'W')
        {
            negative = char.ToUpperInvariant(text[0]) is 'S' or 'W';
            text = text[1..].Trim();
        }
        double value;
        var parts = Regex.Split(text, @"[\s°'""′″:]+").Where(p => p.Length > 0).ToArray();
        if (parts.Length >= 2)
        {
            if (!double.TryParse(parts[0], NumberStyles.Float, Inv, out var d) || !double.TryParse(parts[1], NumberStyles.Float, Inv, out var m))
                return null;
            var s = parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, Inv, out var sv) ? sv : 0;
            value = Math.Abs(d) + m / 60 + s / 3600;
            negative |= d < 0;
        }
        else if (Regex.IsMatch(text, @"^\d{6,7}(\.\d+)?$"))
        {
            // Packed DDMMSS / DDDMMSS (ISED style).
            var digits = text.Split('.')[0];
            var degLen = digits.Length - 4;
            var d = int.Parse(digits[..degLen], Inv);
            var m = int.Parse(digits.Substring(degLen, 2), Inv);
            var s = double.Parse(text[(degLen + 2)..], Inv);
            value = d + m / 60.0 + s / 3600;
        }
        else
        {
            if (!double.TryParse(text, NumberStyles.Float, Inv, out value))
                return null;
            if (value < 0)
            {
                negative = true;
                value = -value;
            }
        }
        if (negative)
            value = -value;
        var limit = latitude ? 90 : 180;
        return Math.Abs(value) <= limit && value != 0 ? value : null;
    }

    /// <summary>Frequency in MHz from "146.940", "146.94 MHz", "146940 kHz" or "146940000" (Hz).</summary>
    public static double? ParseMHz(string text)
    {
        var m = Regex.Match(text.Replace(',', '.'), @"[-+]?\d+(\.\d+)?");
        if (!m.Success || !double.TryParse(m.Value, NumberStyles.Float, Inv, out var v) || v <= 0)
            return null;
        var lower = text.ToLowerInvariant();
        if (lower.Contains("khz"))
            v /= 1000;
        else if (lower.Contains("ghz"))
            v *= 1000;
        else if (v > 100_000)
            v /= 1_000_000; // Hz
        else if (v > 10_000)
            v /= 1000; // kHz
        return v is > 1 and < 10_000 ? Math.Round(v, 6) : null;
    }

    /// <summary>"+0.6", "-5", "+600 kHz", "-" (standard negative), "+" (standard positive) → MHz offset.</summary>
    public static double? ParseOffset(string text, double outputMHz)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;
        double Standard() => outputMHz switch { < 54 => 0.1, < 225 => 0.6, < 500 => 5.0, < 1000 => outputMHz < 900 ? -45 : 12, _ => 20 };
        if (text == "+")
            return Math.Abs(Standard());
        if (text == "-")
            return -Math.Abs(Standard());
        if (ParseMHz(text.TrimStart('+', '-')) is not { } v)
            return null;
        if (text.ToLowerInvariant().Contains("khz") || v > 50)
            v = text.ToLowerInvariant().Contains("khz") ? v : v / 1000;
        return text.StartsWith('-') ? -v : v;
    }

    private static double? ParseNumber(string text)
    {
        var m = Regex.Match(text.Replace(',', '.'), @"\d+(\.\d+)?");
        return m.Success && double.TryParse(m.Value, NumberStyles.Float, Inv, out var v) && v > 0 ? v : null;
    }

    /// <summary>"100", "100.0 Hz", "PL 100" → "CTCSS 100.0"; "D023", "023 DCS" → "DCS 023"; other text kept.</summary>
    public static string NormalizeTone(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text is "-" or "0" or "none" or "None" or "CSQ")
            return "";
        var upper = text.ToUpperInvariant();
        if (upper.Contains("DCS") || Regex.IsMatch(upper, @"^D\d{3}[NI]?$"))
            return "DCS " + Regex.Match(upper, @"\d{3}").Value;
        if (upper.Contains("NAC"))
            return upper;
        var m = Regex.Match(upper, @"\d{2,3}(\.\d)?");
        if (m.Success && double.TryParse(m.Value, NumberStyles.Float, Inv, out var hz) && hz is >= 60 and <= 260)
            return $"CTCSS {hz.ToString("0.0", Inv)}";
        return text;
    }
}
