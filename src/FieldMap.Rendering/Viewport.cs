namespace FieldMap.Map;

/// <summary>
/// What part of the world the map shows: centre (Web Mercator world units, 0–1), zoom (256 × 2^zoom pixels per world
/// width, times the screen's pixel scale), and bearing (the compass direction at the top of the screen; 0 = north up).
/// All maths is in double precision; Skia only ever sees screen or tile-local coordinates.
/// </summary>
public readonly record struct Viewport(double CenterX, double CenterY, double Zoom, double Bearing, int Width, int Height, float PixelScale = 1f)
{
    public const double MinZoom = 2;
    public const double MaxZoom = 19.5;

    /// <summary>Screen pixels per world unit.</summary>
    public double Scale => 256.0 * Math.Pow(2, Zoom) * PixelScale;

    private double Rad => Bearing * Math.PI / 180;

    public (double X, double Y) WorldToScreen(double wx, double wy)
    {
        var dx = (wx - CenterX) * Scale;
        var dy = (wy - CenterY) * Scale;
        var c = Math.Cos(-Rad);
        var s = Math.Sin(-Rad);
        return (Width / 2.0 + dx * c - dy * s, Height / 2.0 + dx * s + dy * c);
    }

    public (double X, double Y) ScreenToWorld(double sx, double sy)
    {
        var dx = sx - Width / 2.0;
        var dy = sy - Height / 2.0;
        var c = Math.Cos(Rad);
        var s = Math.Sin(Rad);
        return (CenterX + (dx * c - dy * s) / Scale, CenterY + (dx * s + dy * c) / Scale);
    }

    public (double X, double Y) GeoToScreen(GeoPoint p)
    {
        var (wx, wy) = WebMercator.ToWorld(p.Lat, p.Lon);
        return WorldToScreen(wx, wy);
    }

    public GeoPoint ScreenToGeo(double sx, double sy)
    {
        var (wx, wy) = ScreenToWorld(sx, sy);
        return WebMercator.FromWorld(wx, wy);
    }

    public GeoPoint Center => WebMercator.FromWorld(CenterX, CenterY);

    /// <summary>World-unit bounding box of the (possibly rotated) screen.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) VisibleWorld()
    {
        var corners = new[] { ScreenToWorld(0, 0), ScreenToWorld(Width, 0), ScreenToWorld(0, Height), ScreenToWorld(Width, Height) };
        return (corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
    }

    public GeoBounds VisibleBounds()
    {
        var (minX, minY, maxX, maxY) = VisibleWorld();
        var nw = WebMercator.FromWorld(minX, minY);
        var se = WebMercator.FromWorld(maxX, maxY);
        return new GeoBounds(se.Lat, nw.Lon, nw.Lat, se.Lon);
    }

    /// <summary>Ground metres per screen pixel at the centre.</summary>
    public double MetersPerPixel => WebMercator.MetersPerWorldUnit(Center.Lat) / Scale;

    public static Viewport At(GeoPoint center, double zoom, double bearing, int width, int height, float pixelScale = 1f)
    {
        var (x, y) = WebMercator.ToWorld(center.Lat, center.Lon);
        return new Viewport(x, y, zoom, bearing, width, height, pixelScale);
    }

    /// <summary>Zoom that fits the bounds into the screen (with a margin).</summary>
    public static double ZoomToFit(GeoBounds b, int width, int height, float pixelScale = 1f, double margin = 60)
    {
        var (x0, y0) = WebMercator.ToWorld(b.MaxLat, b.MinLon);
        var (x1, y1) = WebMercator.ToWorld(b.MinLat, b.MaxLon);
        var w = Math.Max(1e-9, Math.Abs(x1 - x0));
        var h = Math.Max(1e-9, Math.Abs(y1 - y0));
        var zx = Math.Log2(Math.Max(1, width - margin * 2) / (256.0 * pixelScale * w));
        var zy = Math.Log2(Math.Max(1, height - margin * 2) / (256.0 * pixelScale * h));
        return Math.Clamp(Math.Min(zx, zy), MinZoom, 17);
    }
}
