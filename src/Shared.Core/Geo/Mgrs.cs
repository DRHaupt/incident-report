using System.Text.RegularExpressions;

namespace Shared.Core.Geo;

/// <summary>Military Grid Reference System (WGS84, "AA" lettering), 80°S–84°N.</summary>
public static class Mgrs
{
    private static readonly string[] ColumnSets = { "ABCDEFGH", "JKLMNPQR", "STUVWXYZ" };
    private const string RowLetters = "ABCDEFGHJKLMNPQRSTUV";

    /// <summary>"31N AA 66021 00000" at 1 m precision (<paramref name="digits"/> per easting/northing: 1–5).</summary>
    public static string FromLatLon(double lat, double lon, int digits = 5)
    {
        var utm = Utm.FromLatLon(lat, lon);
        var e100k = (int)Math.Floor(utm.Easting / 100000);
        var n100k = (int)Math.Floor(utm.Northing / 100000) % 20;
        var column = ColumnSets[(utm.Zone - 1) % 3][e100k - 1];
        var row = RowLetters[(n100k + (utm.Zone % 2 == 0 ? 5 : 0)) % 20];
        var divisor = Math.Pow(10, 5 - digits);
        var e = (int)Math.Floor(utm.Easting % 100000 / divisor);
        var n = (int)Math.Floor(utm.Northing % 100000 / divisor);
        var fmt = new string('0', digits);
        return $"{utm.Zone}{utm.Band} {column}{row} {e.ToString(fmt)} {n.ToString(fmt)}";
    }

    private static readonly Regex Pattern = new(@"^\s*(?<zone>\d{1,2})\s*(?<band>[C-HJ-NP-X])\s*(?<col>[A-HJ-NP-Z])\s*(?<row>[A-HJ-NP-V])\s*(?<digits>(\d\s*){0,10})\s*$", RegexOptions.IgnoreCase);

    public static bool LooksLikeMgrs(string text) => Pattern.IsMatch(text);

    /// <summary>Parses an MGRS reference; returns the south-west corner of the referenced square, moved to its centre.</summary>
    public static bool TryParse(string text, out double lat, out double lon)
    {
        lat = lon = 0;
        var m = Pattern.Match(text ?? "");
        if (!m.Success)
            return false;
        var zone = int.Parse(m.Groups["zone"].Value);
        var band = char.ToUpperInvariant(m.Groups["band"].Value[0]);
        var col = char.ToUpperInvariant(m.Groups["col"].Value[0]);
        var row = char.ToUpperInvariant(m.Groups["row"].Value[0]);
        var digits = Regex.Replace(m.Groups["digits"].Value, @"\s", "");
        if (zone is < 1 or > 60 || digits.Length % 2 != 0)
            return false;

        var colIndex = ColumnSets[(zone - 1) % 3].IndexOf(col);
        var rowIndex = RowLetters.IndexOf(row);
        if (colIndex < 0 || rowIndex < 0)
            return false;
        rowIndex = (rowIndex - (zone % 2 == 0 ? 5 : 0) + 20) % 20;

        var half = digits.Length / 2;
        var scale = half == 0 ? 100000 : Math.Pow(10, 5 - half);
        var e = half == 0 ? 0 : int.Parse(digits[..half]) * scale;
        var n = half == 0 ? 0 : int.Parse(digits[half..]) * scale;
        var easting = (colIndex + 1) * 100000 + e + scale / 2;
        var northing = rowIndex * 100000 + n + scale / 2;

        // The row letters repeat every 2,000 km: pick the cycle that falls inside the latitude band.
        var north = band >= 'N';
        var bandLat = Utm.BandSouthLatitude(band);
        var (_, bandNorthing) = Utm.Forward(bandLat, (zone - 1) * 6 - 180 + 3, zone);
        if (!north && bandLat == -80)
            bandNorthing = Utm.Forward(-80, (zone - 1) * 6 - 180 + 3, zone).N;
        while (northing < bandNorthing - 100000)
            northing += 2000000;

        (lat, lon) = Utm.ToLatLon(zone, north, easting, northing);
        return true;
    }
}
