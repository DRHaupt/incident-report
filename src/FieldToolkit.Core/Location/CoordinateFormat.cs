using System.Globalization;
using System.Text.RegularExpressions;
using FieldToolkit.Core.Radio;

namespace FieldToolkit.Core.Location;

/// <summary>Formatting and parsing of WGS84 coordinates in the forms used on the radio and by dispatch.</summary>
public static class CoordinateFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"51.50735, -0.12776"</summary>
    public static string Decimal(double lat, double lon) => string.Create(Inv, $"{lat:0.00000}, {lon:0.00000}");

    /// <summary>"51°30.441′N 0°07.666′W" (degrees and decimal minutes).</summary>
    public static string Ddm(double lat, double lon) => $"{DdmPart(lat, 'N', 'S', 2)} {DdmPart(lon, 'E', 'W', 3)}";

    /// <summary>"51°30′26.5″N 0°07′39.9″W"</summary>
    public static string Dms(double lat, double lon) => $"{DmsPart(lat, 'N', 'S', 2)} {DmsPart(lon, 'E', 'W', 3)}";

    private static string DdmPart(double v, char pos, char neg, int degDigits)
    {
        var a = Math.Abs(v);
        var d = (int)a;
        var m = Math.Round((a - d) * 60, 3);
        if (m >= 60) { d++; m = 0; }
        return string.Create(Inv, $"{d}°{m:00.000}′{(v >= 0 ? pos : neg)}");
    }

    private static string DmsPart(double v, char pos, char neg, int degDigits)
    {
        var a = Math.Abs(v);
        var d = (int)a;
        var mFull = (a - d) * 60;
        var m = (int)mFull;
        var s = Math.Round((mFull - m) * 60, 1);
        if (s >= 60) { s = 0; m++; }
        if (m >= 60) { m = 0; d++; }
        return string.Create(Inv, $"{d}°{m:00}′{s:00.0}″{(v >= 0 ? pos : neg)}");
    }

    public static string Utm(double lat, double lon) =>
        Location.Utm.InRange(lat) ? Location.Utm.FromLatLon(lat, lon).ToString() : "n/a (polar)";

    public static string Mgrs(double lat, double lon) =>
        Location.Utm.InRange(lat) ? Location.Mgrs.FromLatLon(lat, lon) : "n/a (polar)";

    /// <summary>How to say a grid reference over the radio: "TREE WUN NOVEMBER / ALFA ALFA / SIX SIX ZE-RO TOO WUN / …".</summary>
    public static string SpokenMgrs(string mgrs) => string.Join(" / ", mgrs.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(group =>
        string.Join(" ", group.Select(ch => char.IsDigit(ch) ? Phonetic.Figures1By1(ch.ToString()) : Phonetic.Spell(ch.ToString()).ToUpperInvariant()))));

    /// <summary>Decimal latitude/longitude read figure by figure: "FIFE WUN DAY-SEE-MAL FIFE ZE-RO … NORTH".</summary>
    public static string SpokenDecimal(double lat, double lon)
    {
        string Part(double v, string pos, string neg) =>
            Phonetic.Figures1By1(Math.Abs(v).ToString("0.00000", Inv)) + " " + (v >= 0 ? pos : neg);
        return $"{Part(lat, "NORTH", "SOUTH")} / {Part(lon, "EAST", "WEST")}";
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>
    /// Accepts "51.5074, -0.1278", "51.5074N 0.1278W", "N51 30.443 W0 07.668", "51°30'26.6"N 0°07'40.1"W" and MGRS
    /// ("30U XC 99316 10163").
    /// </summary>
    public static bool TryParse(string text, out double lat, out double lon)
    {
        lat = lon = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.Trim();
        if (Location.Mgrs.LooksLikeMgrs(t))
            return Location.Mgrs.TryParse(t, out lat, out lon);

        // Normalise symbols to spaces and pull out hemisphere letters next to each half.
        var s = Regex.Replace(t.ToUpperInvariant(), "[°º˚′'’″\"]", " ");
        s = s.Replace("DEG", " ");
        var halves = SplitHalves(s);
        if (halves == null)
            return false;
        if (!TryParsePart(halves.Value.First, out lat, out var h1) || !TryParsePart(halves.Value.Second, out lon, out var h2))
            return false;
        // A hemisphere letter decides which half is which.
        if (h1 is 'E' or 'W' && h2 is 'N' or 'S')
            (lat, lon) = (lon, lat);
        return lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
    }

    private static (string First, string Second)? SplitHalves(string s)
    {
        var comma = s.IndexOf(',');
        if (comma > 0)
            return (s[..comma], s[(comma + 1)..]);
        // Hemisphere letters mark the halves: prefixed ("N51 30 W0 07") or suffixed ("51 30 N 0 07 W").
        var letters = Regex.Matches(s, "[NSEW]").Select(m => m.Index).ToList();
        if (letters.Count == 2)
        {
            var prefixed = s.TrimStart().StartsWith(s[letters[0]]);
            var cut = prefixed ? letters[1] : letters[0] + 1;
            return (s[..cut], s[cut..]);
        }
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
            return (parts[0], parts[1]);
        if (parts.Length % 2 == 0 && parts.All(p => double.TryParse(p, NumberStyles.Float, Inv, out _)))
            return (string.Join(' ', parts[..(parts.Length / 2)]), string.Join(' ', parts[(parts.Length / 2)..]));
        return null;
    }

    private static bool TryParsePart(string part, out double value, out char hemisphere)
    {
        value = 0;
        hemisphere = ' ';
        var p = part.Trim();
        var hm = Regex.Match(p, "[NSEW]");
        if (hm.Success)
        {
            hemisphere = hm.Value[0];
            p = p.Remove(hm.Index, 1);
        }
        var numbers = p.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (numbers.Length is 0 or > 3)
            return false;
        var vals = new double[numbers.Length];
        for (var i = 0; i < numbers.Length; i++)
            if (!double.TryParse(numbers[i], NumberStyles.Float, Inv, out vals[i]))
                return false;
        var negative = vals[0] < 0 || numbers[0].StartsWith('-');
        var deg = Math.Abs(vals[0]);
        if (vals.Length > 1 && (vals[1] < 0 || vals[1] >= 60)) return false;
        if (vals.Length > 2 && (vals[2] < 0 || vals[2] >= 60)) return false;
        value = deg + (vals.Length > 1 ? vals[1] / 60 : 0) + (vals.Length > 2 ? vals[2] / 3600 : 0);
        if (negative || hemisphere is 'S' or 'W')
            value = -value;
        return true;
    }
}
