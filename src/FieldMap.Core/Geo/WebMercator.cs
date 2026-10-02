namespace FieldMap.Core.Geo;

/// <summary>
/// Spherical Web Mercator (EPSG:3857) in "world units": the whole world is a 1×1 square, x to the east and y to the south,
/// so zoom level z has 2^z × 2^z tiles of 1/2^z units each.
/// </summary>
public static class WebMercator
{
    public const double MaxLatitude = 85.05112878;
    private const double EarthCircumference = 2 * Math.PI * 6378137.0;

    public static (double X, double Y) ToWorld(double lat, double lon)
    {
        lat = Math.Clamp(lat, -MaxLatitude, MaxLatitude);
        var x = (lon + 180) / 360;
        var s = Math.Sin(lat * Math.PI / 180);
        var y = 0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI);
        return (x, y);
    }

    public static (double X, double Y) ToWorld(GeoPoint p) => ToWorld(p.Lat, p.Lon);

    public static GeoPoint FromWorld(double x, double y)
    {
        var lon = x * 360 - 180;
        var n = Math.PI - 2 * Math.PI * y;
        var lat = 180 / Math.PI * Math.Atan(Math.Sinh(n));
        return new GeoPoint(lat, lon);
    }

    /// <summary>Ground metres per world unit at a latitude (Mercator stretches with 1/cos(lat)).</summary>
    public static double MetersPerWorldUnit(double lat) => EarthCircumference * Math.Cos(lat * Math.PI / 180);

    /// <summary>The tile containing a point at a zoom level.</summary>
    public static (int X, int Y) Tile(double lat, double lon, int zoom)
    {
        var (x, y) = ToWorld(lat, lon);
        var n = 1 << zoom;
        return (Math.Clamp((int)Math.Floor(x * n), 0, n - 1), Math.Clamp((int)Math.Floor(y * n), 0, n - 1));
    }

    /// <summary>World-unit bounds of a tile.</summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) TileBounds(int zoom, int x, int y)
    {
        var size = 1.0 / (1 << zoom);
        return (x * size, y * size, (x + 1) * size, (y + 1) * size);
    }

    public static GeoBounds TileGeoBounds(int zoom, int x, int y)
    {
        var (minX, minY, maxX, maxY) = TileBounds(zoom, x, y);
        var nw = FromWorld(minX, minY);
        var se = FromWorld(maxX, maxY);
        return new GeoBounds(se.Lat, nw.Lon, nw.Lat, se.Lon);
    }
}
