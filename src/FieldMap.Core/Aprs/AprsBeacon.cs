using System.Globalization;
using System.Text.RegularExpressions;

namespace FieldMap.Core.Aprs;

/// <summary>Builds outgoing APRS packets (own position beacon, objects for incident points).</summary>
public static partial class AprsBeacon
{
    /// <summary>
    /// Destination ("tocall") identifying the software. APZxxx is the block for experimental software; register a
    /// permanent one in the APRS device identification list before wide use.
    /// </summary>
    public const string ToCall = "APZFMP";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [GeneratedRegex(@"^[A-Z0-9]{1,6}(-([0-9]|1[0-5]))?$")]
    private static partial Regex CallRx();

    /// <summary>An amateur call sign with optional SSID 0–15 (at least one digit and one letter).</summary>
    public static bool IsValidCallsign(string call)
    {
        call = call.Trim().ToUpperInvariant();
        var basePart = call.Split('-')[0];
        return CallRx().IsMatch(call) && basePart.Any(char.IsDigit) && basePart.Any(char.IsLetter) && basePart.Length >= 3;
    }

    /// <summary>"4903.50N" – latitude in degrees and hundredths of minutes.</summary>
    public static string Latitude(double lat)
    {
        var hemi = lat < 0 ? 'S' : 'N';
        var (deg, min) = DegMin(Math.Abs(lat));
        return deg.ToString("00", Inv) + min.ToString("00.00", Inv) + hemi;
    }

    /// <summary>"07201.75W".</summary>
    public static string Longitude(double lon)
    {
        var hemi = lon < 0 ? 'W' : 'E';
        var (deg, min) = DegMin(Math.Abs(lon));
        return deg.ToString("000", Inv) + min.ToString("00.00", Inv) + hemi;
    }

    private static (int Deg, double Min) DegMin(double v)
    {
        var deg = (int)Math.Floor(v);
        var min = Math.Round((v - deg) * 60, 2);
        if (min >= 60)
        {
            deg++;
            min = 0;
        }
        return (deg, min);
    }

    /// <summary>Position report information field: "!DDMM.mmN/DDDMM.mmW>CSE/SPD/A=000000comment" (no messaging).</summary>
    public static string Position(double lat, double lon, char table, char code, double? courseDegrees, double? speedKmh, double? altitudeMeters, string comment)
    {
        var s = "!" + Latitude(lat) + table + Longitude(lon) + code;
        if (courseDegrees is { } c && speedKmh is { } v && v >= 3)
        {
            var course = (int)Math.Round(c) % 360;
            s += (course == 0 ? 360 : course).ToString("000", Inv) + "/" + Math.Min(999, (int)Math.Round(v / 1.852)).ToString("000", Inv);
        }
        if (altitudeMeters is { } a)
            s += "/A=" + ((int)Math.Round(a / 0.3048)).ToString(a < 0 ? "00000" : "000000", Inv);
        return s + Clean(comment, 43);
    }

    /// <summary>Object information field: ";NAME_____*DDHHMMzDDMM.mmN/DDDMM.mmW&gt;comment" (alive) or with '_' (killed).</summary>
    public static string Object(string name, DateTime utc, double lat, double lon, char table, char code, string comment, bool alive = true)
    {
        var n = Clean(name, 9).PadRight(9);
        return ";" + n + (alive ? '*' : '_') + utc.ToString("ddHHmm", Inv) + "z" + Latitude(lat) + table + Longitude(lon) + code + Clean(comment, 43);
    }

    /// <summary>The full TNC2 line, e.g. "VE7XYZ-9>APZFMP,WIDE1-1,WIDE2-1:!…".</summary>
    public static string Packet(string callsign, string path, string information)
    {
        var p = string.Join(",", path.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return callsign.Trim().ToUpperInvariant() + ">" + ToCall + (p.Length > 0 ? "," + p : "") + ":" + information;
    }

    /// <summary>APRS text is printable ASCII; strip anything else and limit the length.</summary>
    private static string Clean(string s, int max)
    {
        var chars = s.Where(ch => ch is >= ' ' and <= '~' && ch != '|' && ch != '~').Take(max).ToArray();
        return new string(chars);
    }
}
