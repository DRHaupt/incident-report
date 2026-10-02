using System.Globalization;

namespace FieldToolkit.Core.Location;

/// <summary>
/// Minimal NMEA 0183 reader for GPS receivers exposed as a serial (COM) port, as on many rugged laptops. Combines GGA
/// (fix quality, satellites, HDOP, altitude) and RMC (validity, speed, course) sentences from any talker (GP, GN, GL…).
/// </summary>
public sealed class NmeaReader
{
    private int? _satellites;
    private double? _hdop;
    private double? _altitude;
    private double? _speed;
    private double? _course;

    /// <summary>Feeds one sentence; returns a fix when the sentence carries a valid position.</summary>
    public GeoFix? Feed(string sentence, DateTime nowUtc)
    {
        if (!TryParse(sentence, out var type, out var f))
            return null;
        var inv = CultureInfo.InvariantCulture;
        switch (type)
        {
            case "GGA" when f.Length >= 10:
            {
                if (f[6] == "0" || f[6].Length == 0)
                    return null; // no fix
                _satellites = int.TryParse(f[7], out var sats) ? sats : null;
                _hdop = double.TryParse(f[8], NumberStyles.Float, inv, out var h) ? h : null;
                _altitude = double.TryParse(f[9], NumberStyles.Float, inv, out var alt) ? alt : null;
                return Position(f[2], f[3], f[4], f[5], nowUtc);
            }
            case "RMC" when f.Length >= 9:
            {
                if (f[2] != "A")
                    return null; // V = void
                _speed = double.TryParse(f[7], NumberStyles.Float, inv, out var knots) ? knots * 0.514444 : null;
                _course = double.TryParse(f[8], NumberStyles.Float, inv, out var c) ? c : null;
                return Position(f[3], f[4], f[5], f[6], nowUtc);
            }
            default:
                return null;
        }
    }

    private GeoFix? Position(string lat, string ns, string lon, string ew, DateTime nowUtc)
    {
        var la = Angle(lat, 2);
        var lo = Angle(lon, 3);
        if (la == null || lo == null)
            return null;
        if (ns == "S") la = -la;
        if (ew == "W") lo = -lo;
        // Rough horizontal accuracy from HDOP (typical receiver UERE of about 5 m); shown as an estimate.
        var accuracy = _hdop is { } h ? h * 5 : (double?)null;
        return new GeoFix(la.Value, lo.Value, accuracy, _altitude, nowUtc, "GPS (serial NMEA)", _satellites, _hdop, _speed, _course);
    }

    /// <summary>"ddmm.mmmm" / "dddmm.mmmm" → decimal degrees.</summary>
    private static double? Angle(string text, int degreeDigits)
    {
        if (text.Length <= degreeDigits || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return null;
        var deg = double.Parse(text[..degreeDigits], CultureInfo.InvariantCulture);
        var min = double.Parse(text[degreeDigits..], CultureInfo.InvariantCulture);
        return min >= 60 ? null : deg + min / 60;
    }

    /// <summary>Validates "$..*hh" and splits it; returns the sentence type without the talker ID ("GGA").</summary>
    public static bool TryParse(string sentence, out string type, out string[] fields)
    {
        type = "";
        fields = Array.Empty<string>();
        var s = sentence.Trim();
        if (s.Length < 7 || s[0] != '$')
            return false;
        var star = s.LastIndexOf('*');
        if (star > 0)
        {
            if (star + 3 > s.Length || !byte.TryParse(s.AsSpan(star + 1, 2), NumberStyles.HexNumber, null, out var expected))
                return false;
            if (Checksum(s[1..star]) != expected)
                return false;
            s = s[..star];
        }
        fields = s[1..].Split(',');
        if (fields[0].Length < 5)
            return false;
        type = fields[0][^3..];
        return true;
    }

    public static byte Checksum(string body)
    {
        byte c = 0;
        foreach (var ch in body)
            c ^= (byte)ch;
        return c;
    }
}
