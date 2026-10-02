namespace Shared.Core.Geo;

/// <summary>The plain-ASCII location block both apps copy for dispatch systems, messages and the radio log.</summary>
public static class DispatchText
{
    /// <summary>Replaces the typographic prime marks used on screen with plain ASCII quotes.</summary>
    public static string Ascii(string s) => s.Replace('′', '\'').Replace('″', '"');

    /// <summary>
    /// "LOCATION – label at 14:03:22 02 Oct 2026", then lat/long, DMS, MGRS and the fix source. <paramref name="extraLines"/>
    /// (e.g. the nearest address) are added before the source line.
    /// </summary>
    public static string Block(GeoFix fix, string? label = null, IEnumerable<string>? extraLines = null, DateTime? now = null)
    {
        var lines = new List<string>
        {
            $"LOCATION{(label is { Length: > 0 } ? " – " + label : "")} at {fix.TimestampUtc.ToLocalTime():HH:mm:ss} {(now ?? DateTime.Now):dd MMM yyyy}",
            $"Lat/Long: {CoordinateFormat.Decimal(fix.Latitude, fix.Longitude)} (WGS84)",
            $"DMS: {Ascii(CoordinateFormat.Dms(fix.Latitude, fix.Longitude))}",
            $"Grid (MGRS): {CoordinateFormat.Mgrs(fix.Latitude, fix.Longitude)}"
        };
        if (extraLines != null)
            lines.AddRange(extraLines);
        var quality = fix.Source + (fix.AccuracyMeters is { } a ? $", accuracy about {a:0} m" : "");
        lines.Add($"Source: {quality}");
        return string.Join(Environment.NewLine, lines);
    }
}
