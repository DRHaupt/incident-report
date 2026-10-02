namespace FieldMap.Core.Geo;

/// <summary>A WGS84 position in decimal degrees.</summary>
public readonly record struct GeoPoint(double Lat, double Lon)
{
    public override string ToString() => CoordinateFormat.Decimal(Lat, Lon);

    public double DistanceTo(GeoPoint other) => GeoMath.Distance(Lat, Lon, other.Lat, other.Lon);

    public double BearingTo(GeoPoint other) => GeoMath.Bearing(Lat, Lon, other.Lat, other.Lon);
}

/// <summary>A latitude/longitude rectangle.</summary>
public readonly record struct GeoBounds(double MinLat, double MinLon, double MaxLat, double MaxLon)
{
    public static readonly GeoBounds Empty = new(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);

    public bool IsEmpty => MinLat > MaxLat;

    public GeoPoint Center => new((MinLat + MaxLat) / 2, (MinLon + MaxLon) / 2);

    public bool Contains(double lat, double lon) => lat >= MinLat && lat <= MaxLat && lon >= MinLon && lon <= MaxLon;

    public bool Intersects(GeoBounds o) => !(o.MinLat > MaxLat || o.MaxLat < MinLat || o.MinLon > MaxLon || o.MaxLon < MinLon);

    public GeoBounds Include(double lat, double lon) =>
        new(Math.Min(MinLat, lat), Math.Min(MinLon, lon), Math.Max(MaxLat, lat), Math.Max(MaxLon, lon));

    public GeoBounds Include(GeoBounds o) => o.IsEmpty ? this : IsEmpty ? o :
        new(Math.Min(MinLat, o.MinLat), Math.Min(MinLon, o.MinLon), Math.Max(MaxLat, o.MaxLat), Math.Max(MaxLon, o.MaxLon));

    /// <summary>Grown by <paramref name="meters"/> on every side.</summary>
    public GeoBounds Expand(double meters)
    {
        var dLat = meters / 111320.0;
        var dLon = meters / (111320.0 * Math.Max(0.05, Math.Cos(Center.Lat * Math.PI / 180)));
        return new(MinLat - dLat, MinLon - dLon, MaxLat + dLat, MaxLon + dLon);
    }

    public static GeoBounds Of(IEnumerable<GeoPoint> points)
    {
        var b = Empty;
        foreach (var p in points)
            b = b.Include(p.Lat, p.Lon);
        return b;
    }
}
