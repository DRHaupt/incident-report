namespace FieldToolkit.Core.Location;

public sealed record UtmCoordinate(int Zone, char Band, double Easting, double Northing)
{
    public bool North => Band >= 'N';

    public override string ToString() => $"{Zone}{Band} {Math.Floor(Easting):0} {Math.Floor(Northing):0}";
}

/// <summary>
/// WGS84 ⇄ UTM using the Krüger series (as used by GeographicLib / the UTM article), accurate to well under a metre
/// within the zone. Includes the Norway and Svalbard zone exceptions. UTM covers 80°S to 84°N.
/// </summary>
public static class Utm
{
    private const double A = 6378137.0;
    private const double F = 1 / 298.257223563;
    private const double K0 = 0.9996;
    private const double E0 = 500000;
    private const string Bands = "CDEFGHJKLMNPQRSTUVWX";

    private static readonly double N = F / (2 - F);
    private static readonly double BigA = A / (1 + N) * (1 + N * N / 4 + Math.Pow(N, 4) / 64);
    private static readonly double[] Alpha = { N / 2 - 2 * N * N / 3 + 5 * Math.Pow(N, 3) / 16, 13 * N * N / 48 - 3 * Math.Pow(N, 3) / 5, 61 * Math.Pow(N, 3) / 240 };
    private static readonly double[] Beta = { N / 2 - 2 * N * N / 3 + 37 * Math.Pow(N, 3) / 96, N * N / 48 + Math.Pow(N, 3) / 15, 17 * Math.Pow(N, 3) / 480 };
    private static readonly double[] Delta = { 2 * N - 2 * N * N / 3 - 2 * Math.Pow(N, 3), 7 * N * N / 3 - 8 * Math.Pow(N, 3) / 5, 56 * Math.Pow(N, 3) / 15 };

    public static bool InRange(double lat) => lat >= -80 && lat < 84;

    public static int ZoneFor(double lat, double lon)
    {
        lon = NormalizeLon(lon);
        var zone = (int)Math.Floor((lon + 180) / 6) + 1;
        if (zone > 60) zone = 60;
        if (lat >= 56 && lat < 64 && lon >= 3 && lon < 12) return 32;
        if (lat >= 72 && lat < 84)
        {
            if (lon >= 0 && lon < 9) return 31;
            if (lon >= 9 && lon < 21) return 33;
            if (lon >= 21 && lon < 33) return 35;
            if (lon >= 33 && lon < 42) return 37;
        }
        return zone;
    }

    public static char BandFor(double lat) => lat >= 72 ? 'X' : Bands[(int)Math.Floor((lat + 80) / 8)];

    public static double BandSouthLatitude(char band) => band == 'X' ? 72 : -80 + Bands.IndexOf(band) * 8;

    private static double NormalizeLon(double lon) => ((lon + 180) % 360 + 360) % 360 - 180;

    private static double CentralMeridian(int zone) => (zone - 1) * 6 - 180 + 3;

    public static UtmCoordinate FromLatLon(double lat, double lon, int? forceZone = null)
    {
        if (!InRange(lat))
            throw new ArgumentOutOfRangeException(nameof(lat), "UTM covers latitudes from 80°S to 84°N.");
        var zone = forceZone ?? ZoneFor(lat, lon);
        var (e, n) = Forward(lat, lon, zone);
        return new UtmCoordinate(zone, BandFor(lat), e, n);
    }

    internal static (double E, double N) Forward(double lat, double lon, int zone)
    {
        var phi = lat * Math.PI / 180;
        var dLambda = (NormalizeLon(lon - CentralMeridian(zone))) * Math.PI / 180;
        var c = 2 * Math.Sqrt(N) / (1 + N);
        var t = Math.Sinh(Math.Atanh(Math.Sin(phi)) - c * Math.Atanh(c * Math.Sin(phi)));
        var xiP = Math.Atan2(t, Math.Cos(dLambda));
        var etaP = Math.Atanh(Math.Sin(dLambda) / Math.Sqrt(1 + t * t));
        double xi = xiP, eta = etaP;
        for (var j = 1; j <= 3; j++)
        {
            xi += Alpha[j - 1] * Math.Sin(2 * j * xiP) * Math.Cosh(2 * j * etaP);
            eta += Alpha[j - 1] * Math.Cos(2 * j * xiP) * Math.Sinh(2 * j * etaP);
        }
        var easting = E0 + K0 * BigA * eta;
        var northing = K0 * BigA * xi;
        if (lat < 0)
            northing += 10000000;
        return (easting, northing);
    }

    public static (double Lat, double Lon) ToLatLon(int zone, bool north, double easting, double northing)
    {
        var xi = (northing - (north ? 0 : 10000000)) / (K0 * BigA);
        var eta = (easting - E0) / (K0 * BigA);
        double xiP = xi, etaP = eta;
        for (var j = 1; j <= 3; j++)
        {
            xiP -= Beta[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
            etaP -= Beta[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
        }
        var chi = Math.Asin(Math.Sin(xiP) / Math.Cosh(etaP));
        var phi = chi;
        for (var j = 1; j <= 3; j++)
            phi += Delta[j - 1] * Math.Sin(2 * j * chi);
        var lambda = CentralMeridian(zone) * Math.PI / 180 + Math.Atan2(Math.Sinh(etaP), Math.Cos(xiP));
        return (phi * 180 / Math.PI, NormalizeLon(lambda * 180 / Math.PI));
    }
}
