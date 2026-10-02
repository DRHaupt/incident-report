namespace Shared.Core.Geo;

/// <summary>Great-circle distance and bearing on the WGS84 mean sphere (accurate to about 0.5 %).</summary>
public static class GeoMath
{
    public const double EarthRadius = 6371008.8;

    private static double Rad(double d) => d * Math.PI / 180;
    private static double Deg(double r) => r * 180 / Math.PI;

    /// <summary>Distance in metres (haversine).</summary>
    public static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Initial bearing from point 1 to point 2, degrees true (0–360).</summary>
    public static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        var y = Math.Sin(Rad(lon2 - lon1)) * Math.Cos(Rad(lat2));
        var x = Math.Cos(Rad(lat1)) * Math.Sin(Rad(lat2)) - Math.Sin(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Cos(Rad(lon2 - lon1));
        return (Deg(Math.Atan2(y, x)) + 360) % 360;
    }

    private static readonly string[] Points16 = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };

    public static string CompassPoint(double bearing) => Points16[(int)Math.Round(((bearing % 360) + 360) % 360 / 22.5) % 16];

    /// <summary>"850 m (0.53 mi)" / "12.4 km (7.7 mi)".</summary>
    public static string FormatDistance(double meters)
    {
        var miles = meters / 1609.344;
        var metric = meters < 1000 ? $"{meters:0} m" : meters < 10000 ? $"{meters / 1000:0.00} km" : $"{meters / 1000:0.0} km";
        var imperial = miles < 0.1 ? $"{meters * 3.28084:0} ft" : miles < 10 ? $"{miles:0.00} mi" : $"{miles:0.0} mi";
        return $"{metric} ({imperial})";
    }

    /// <summary>"045° NE (true)".</summary>
    public static string FormatBearing(double bearing) => $"{Math.Round(bearing) % 360:000}° {CompassPoint(bearing)}";
}
