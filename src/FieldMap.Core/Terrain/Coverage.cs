using FieldMap.Core.Geo;

namespace FieldMap.Core.Terrain;

/// <summary>Predicted signal at a place.</summary>
public enum CoverageClass : byte
{
    /// <summary>Below the receiver's sensitivity.</summary>
    None = 0,
    /// <summary>Above sensitivity but with less than the fade margin: may work, especially outdoors and moving.</summary>
    Marginal = 1,
    /// <summary>At least the fade margin above sensitivity.</summary>
    Likely = 2,
    /// <summary>Outside the computed radius.</summary>
    Outside = 255
}

/// <summary>What to predict: a transmitter (repeater) and the kind of radio listening to it.</summary>
public sealed record CoverageRequest
{
    public double Lat { get; init; }
    public double Lon { get; init; }
    /// <summary>Antenna height above ground at the site (metres).</summary>
    public double AntennaHeightM { get; init; } = 15;
    public double FrequencyMHz { get; init; } = 150;
    /// <summary>Effective radiated power (watts, relative to a dipole) towards the horizon.</summary>
    public double ErpWatts { get; init; } = 50;
    /// <summary>Receiving antenna height above ground: ~1.5 m handheld, ~2 m vehicle roof.</summary>
    public double ReceiverHeightM { get; init; } = 1.5;
    /// <summary>Receiving antenna gain (dBd): about −5 for a handheld at the hip/shoulder, 0 for a mobile whip.</summary>
    public double ReceiverGainDb { get; init; } = -5;
    /// <summary>Receiver sensitivity (dBm): −116 ≈ 0.35 µV, typical 12 dB SINAD for analogue FM.</summary>
    public double SensitivityDbm { get; init; } = -116;
    /// <summary>Margin above sensitivity counted as "likely" (allows for fading, buildings, trees, body loss).</summary>
    public double FadeMarginDb { get; init; } = 10;
    public double RadiusKm { get; init; } = 50;

    public static CoverageRequest Handheld(double lat, double lon) => new() { Lat = lat, Lon = lon };
    public static CoverageRequest Vehicle(double lat, double lon) => new() { Lat = lat, Lon = lon, ReceiverHeightM = 2.0, ReceiverGainDb = 0 };
}

/// <summary>A predicted coverage raster in Web Mercator (world 0–1 coordinates), one class per pixel.</summary>
public sealed class CoverageResult
{
    public required CoverageRequest Request { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public required byte[] Classes { get; init; }
    public double MinX { get; init; }
    public double MinY { get; init; }
    public double MaxX { get; init; }
    public double MaxY { get; init; }
    public GeoBounds Bounds { get; init; }
    /// <summary>Ground elevation at the site (metres), or null where the elevation model has no data.</summary>
    public double? SiteElevationM { get; init; }
    public double LikelyFraction { get; init; }
    public double MarginalFraction { get; init; }

    public CoverageClass ClassAt(double lat, double lon)
    {
        var (x, y) = WebMercator.ToWorld(lat, lon);
        if (x < MinX || x >= MaxX || y < MinY || y >= MaxY)
            return CoverageClass.Outside;
        var px = (int)((x - MinX) / (MaxX - MinX) * Width);
        var py = (int)((y - MinY) / (MaxY - MinY) * Height);
        return (CoverageClass)Classes[Math.Clamp(py, 0, Height - 1) * Width + Math.Clamp(px, 0, Width - 1)];
    }
}

/// <summary>
/// Predicts where a repeater can be heard, over the region's elevation model. For every direction it walks outwards
/// from the site cell by cell (64 m) and, at each cell, works out the link: free-space or ground-reflection loss for
/// the distance (whichever is larger), plus knife-edge diffraction over the highest obstruction seen from the antenna (with 4/3-earth curvature), compared
/// with the receiver's sensitivity. A single-obstacle model: good at "behind the ridge or not", optimistic behind several
/// ridges, and blind to trees and buildings – so it is an estimate to plan with, not a guarantee.
/// </summary>
public static class RadioCoverage
{
    private const double EarthRadius = 6371000;
    private const double KFactor = 4.0 / 3.0;

    public static CoverageResult Compute(TerrainGrid grid, CoverageRequest req, CancellationToken cancel = default)
    {
        var px = grid.PixelSize;
        var (sx, sy) = CanadaAtlasLambert.Forward(req.Lat, req.Lon);
        var (gx, gy) = grid.ToGrid(sx, sy);
        int cx = (int)Math.Round(gx), cy = (int)Math.Round(gy);
        var rc = Math.Max(4, (int)Math.Ceiling(req.RadiusKm * 1000 / px));
        var size = 2 * rc + 1;

        // Elevations of the whole window up front (ocean and no-data count as sea level).
        var elev = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            cancel.ThrowIfCancellationRequested();
            for (var x = 0; x < size; x++)
                elev[y * size + x] = grid.Cell(cx - rc + x, cy - rc + y) ?? 0;
        }
        var siteGround = grid.ElevationAtProjected(sx, sy);
        var antenna = (siteGround ?? elev[rc * size + rc]) + req.AntennaHeightM;

        var lambda = 299.792458 / req.FrequencyMHz;
        var eirpDbm = 10 * Math.Log10(req.ErpWatts * 1000) + 2.15;
        var fsplConst = 32.45 + 20 * Math.Log10(req.FrequencyMHz);
        var margins = new float[size * size];
        Array.Fill(margins, float.NegativeInfinity);
        margins[rc * size + rc] = float.PositiveInfinity;

        void Ray(int tx, int ty)
        {
            int dx = tx - rc, dy = ty - rc;
            var steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
            double stepX = (double)dx / steps, stepY = (double)dy / steps;
            var stepLen = Math.Sqrt(stepX * stepX + stepY * stepY) * px;
            // Dominant obstacle so far: highest elevation angle from the antenna.
            double obsAngle = double.NegativeInfinity, obsD = 0, obsH = 0;
            for (var i = 1; i <= steps; i++)
            {
                var x = (int)Math.Round(rc + stepX * i);
                var y = (int)Math.Round(rc + stepY * i);
                var idx = y * size + x;
                var d = stepLen * i;
                var drop = d * d / (2 * KFactor * EarthRadius);
                var ground = elev[idx] - drop;
                var rx = ground + req.ReceiverHeightM;

                double diffraction = 0;
                if (obsAngle > double.NegativeInfinity)
                {
                    var lineAtObs = antenna + (rx - antenna) * obsD / d;
                    var h = obsH - lineAtObs;
                    var d2 = d - obsD;
                    if (d2 > 1)
                    {
                        var v = h * Math.Sqrt(2 / lambda * (1 / obsD + 1 / d2));
                        diffraction = KnifeEdgeLossDb(v);
                    }
                }
                // Basic loss: free space close in, then the ground-reflection (plane-earth) law, which falls off much
                // faster for antennas near the ground. The transmitter's height counts above the receiver's ground,
                // so a mountain-top repeater covers the valley below it.
                var fspl = fsplConst + 20 * Math.Log10(d / 1000);
                var htEff = Math.Max(req.AntennaHeightM, antenna - elev[idx]);
                var planeEarth = 40 * Math.Log10(d) - 20 * Math.Log10(htEff * Math.Max(0.5, req.ReceiverHeightM));
                var pr = eirpDbm - Math.Max(fspl, planeEarth) - diffraction + req.ReceiverGainDb;
                var margin = (float)(pr - req.SensitivityDbm);
                if (margin > margins[idx])
                    margins[idx] = margin;

                var angle = (ground - antenna) / d;
                if (angle > obsAngle)
                {
                    obsAngle = angle;
                    obsD = d;
                    obsH = ground;
                }
            }
        }

        // One ray to every cell on the edge of the square window reaches every cell inside it.
        for (var t = 0; t < size; t++)
        {
            cancel.ThrowIfCancellationRequested();
            Ray(t, 0);
            Ray(t, size - 1);
            Ray(0, t);
            Ray(size - 1, t);
        }

        // Resample to Web Mercator, at about the elevation cell size, clipped to the radius.
        var radiusM = rc * px;
        var north = Geometry.Destination(new GeoPoint(req.Lat, req.Lon), 0, radiusM);
        var south = Geometry.Destination(new GeoPoint(req.Lat, req.Lon), 180, radiusM);
        var east = Geometry.Destination(new GeoPoint(req.Lat, req.Lon), 90, radiusM);
        var west = Geometry.Destination(new GeoPoint(req.Lat, req.Lon), 270, radiusM);
        var (minX, minY) = WebMercator.ToWorld(north.Lat, west.Lon);
        var (maxX, maxY) = WebMercator.ToWorld(south.Lat, east.Lon);
        var width = Math.Clamp(size, 64, 2048);
        var height = Math.Clamp((int)Math.Round(width * (maxY - minY) / (maxX - minX)), 64, 2048);
        var classes = new byte[width * height];
        long likely = 0, marginal = 0, inside = 0;
        for (var y = 0; y < height; y++)
        {
            cancel.ThrowIfCancellationRequested();
            var wy = minY + (y + 0.5) / height * (maxY - minY);
            for (var x = 0; x < width; x++)
            {
                var wx = minX + (x + 0.5) / width * (maxX - minX);
                var g = WebMercator.FromWorld(wx, wy);
                var (lx, ly) = CanadaAtlasLambert.Forward(g.Lat, g.Lon);
                var (cgx, cgy) = grid.ToGrid(lx, ly);
                int wxCell = (int)Math.Round(cgx) - (cx - rc), wyCell = (int)Math.Round(cgy) - (cy - rc);
                var ddx = wxCell - rc;
                var ddy = wyCell - rc;
                if (wxCell < 0 || wyCell < 0 || wxCell >= size || wyCell >= size || ddx * ddx + ddy * ddy > rc * rc)
                {
                    classes[y * width + x] = (byte)CoverageClass.Outside;
                    continue;
                }
                inside++;
                var m = margins[wyCell * size + wxCell];
                var c = m >= req.FadeMarginDb ? CoverageClass.Likely : m >= 0 ? CoverageClass.Marginal : CoverageClass.None;
                if (c == CoverageClass.Likely)
                    likely++;
                else if (c == CoverageClass.Marginal)
                    marginal++;
                classes[y * width + x] = (byte)c;
            }
        }
        return new CoverageResult
        {
            Request = req, Width = width, Height = height, Classes = classes, MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
            Bounds = new GeoBounds(south.Lat, west.Lon, north.Lat, east.Lon), SiteElevationM = siteGround,
            LikelyFraction = inside == 0 ? 0 : (double)likely / inside, MarginalFraction = inside == 0 ? 0 : (double)marginal / inside
        };
    }

    /// <summary>Single knife-edge diffraction loss (dB) for the Fresnel–Kirchhoff parameter ν (ITU-R P.526 approximation).</summary>
    public static double KnifeEdgeLossDb(double v) =>
        v <= -0.78 ? 0 : 6.9 + 20 * Math.Log10(Math.Sqrt((v - 0.1) * (v - 0.1) + 1) + v - 0.1);
}
