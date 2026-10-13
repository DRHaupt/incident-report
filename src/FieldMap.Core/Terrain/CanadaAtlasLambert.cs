namespace FieldMap.Core.Terrain;

/// <summary>
/// EPSG:3979 "NAD83(CSRS) / Canada Atlas Lambert": Lambert conformal conic, standard parallels 49° and 77°, origin
/// 49° N 95° W, no false easting/northing, GRS80 ellipsoid. The projection of NRCan's Canadian Digital Elevation Model.
/// NAD83(CSRS) and WGS84 differ by about a metre, far below the 64 m elevation cells, so GPS positions are used as is.
/// </summary>
public static class CanadaAtlasLambert
{
    private const double A = 6378137.0;
    private const double F = 1 / 298.257222101;
    private static readonly double E = Math.Sqrt(F * (2 - F));
    private static readonly double Lon0 = -95.0 * Math.PI / 180;
    private static readonly double N;
    private static readonly double AF;
    private static readonly double Rho0;

    static CanadaAtlasLambert()
    {
        double phi1 = 49 * Math.PI / 180, phi2 = 77 * Math.PI / 180, phi0 = 49 * Math.PI / 180;
        double m1 = M(phi1), m2 = M(phi2);
        double t1 = T(phi1), t2 = T(phi2), t0 = T(phi0);
        N = (Math.Log(m1) - Math.Log(m2)) / (Math.Log(t1) - Math.Log(t2));
        AF = A * m1 / (N * Math.Pow(t1, N));
        Rho0 = AF * Math.Pow(t0, N);
    }

    private static double M(double phi) => Math.Cos(phi) / Math.Sqrt(1 - E * E * Math.Sin(phi) * Math.Sin(phi));

    private static double T(double phi)
    {
        var s = Math.Sin(phi);
        return Math.Tan(Math.PI / 4 - phi / 2) / Math.Pow((1 - E * s) / (1 + E * s), E / 2);
    }

    /// <summary>Latitude/longitude (degrees) to projected metres.</summary>
    public static (double X, double Y) Forward(double lat, double lon)
    {
        var rho = AF * Math.Pow(T(lat * Math.PI / 180), N);
        var theta = N * (lon * Math.PI / 180 - Lon0);
        return (rho * Math.Sin(theta), Rho0 - rho * Math.Cos(theta));
    }

    /// <summary>Projected metres to latitude/longitude (degrees).</summary>
    public static (double Lat, double Lon) Inverse(double x, double y)
    {
        var dy = Rho0 - y;
        var rho = Math.Sign(N) * Math.Sqrt(x * x + dy * dy);
        var theta = Math.Atan2(Math.Sign(N) * x, Math.Sign(N) * dy);
        var t = Math.Pow(rho / AF, 1 / N);
        var phi = Math.PI / 2 - 2 * Math.Atan(t);
        for (var i = 0; i < 10; i++)
        {
            var s = Math.Sin(phi);
            var next = Math.PI / 2 - 2 * Math.Atan(t * Math.Pow((1 - E * s) / (1 + E * s), E / 2));
            if (Math.Abs(next - phi) < 1e-12)
            {
                phi = next;
                break;
            }
            phi = next;
        }
        return (phi * 180 / Math.PI, (theta / N + Lon0) * 180 / Math.PI);
    }
}
