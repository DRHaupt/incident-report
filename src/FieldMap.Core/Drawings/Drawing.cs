using System.Globalization;
using System.Text;
using FieldMap.Core.Geo;

namespace FieldMap.Core.Drawings;

public enum DrawingKind
{
    Incident,
    Staging,
    Rendezvous,
    LandingZone,
    Cordon,
    Perimeter,
    RoadClosure,
    RangeRings,
    Measure,
    Hazmat,
    Note,
    Unit
}

public enum DrawingShape
{
    Point,
    Line,
    Polygon
}

/// <summary>Shape of the hazmat protective-action zone.</summary>
public enum ProtectiveShape
{
    /// <summary>A downwind sector (pie slice) of the given width.</summary>
    Sector,
    /// <summary>The ERG square: as wide as it is long, centred on the downwind line, starting at the spill.</summary>
    ErgSquare
}

/// <summary>An incident drawing: pin, staging area, cordon, road closure, range rings, measurement or hazmat zone.</summary>
public sealed class Drawing
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DrawingKind Kind { get; set; }
    public string Label { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>Points (one for pins, rings and hazmat; several for lines and areas).</summary>
    public List<GeoPoint> Points { get; set; } = new();

    public DrawingShape Shape { get; set; }

    /// <summary>Range rings (metres).</summary>
    public List<double> Rings { get; set; } = new();

    // Hazmat
    public double? IsolationMeters { get; set; }
    public double? ProtectiveMeters { get; set; }

    /// <summary>Direction the wind is blowing FROM (degrees true), as reported by weather stations.</summary>
    public double? WindFromDegrees { get; set; }
    public ProtectiveShape ProtectiveShape { get; set; } = ProtectiveShape.Sector;
    public double SectorWidthDegrees { get; set; } = 90;
    public string? UnNumber { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";

    /// <summary>Road closures (and closure areas) are avoided by routing while this is true.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Unit call sign or status for unit symbols ("E12 – on scene").</summary>
    public string? Unit { get; set; }

    public GeoPoint Anchor => Points.Count == 0 ? default : Shape == DrawingShape.Polygon ? Geometry.Centroid(Points) : Points[0];

    /// <summary>For areas (cordons, perimeters): routing avoids every road inside or crossing the area.</summary>
    public bool ClosesRoads { get; set; }

    public bool AffectsRouting => Active && (Kind == DrawingKind.RoadClosure || ClosesRoads);

    public static DrawingShape DefaultShape(DrawingKind kind) => kind switch
    {
        DrawingKind.Cordon or DrawingKind.Perimeter or DrawingKind.Staging => DrawingShape.Polygon,
        DrawingKind.RoadClosure or DrawingKind.Measure => DrawingShape.Line,
        _ => DrawingShape.Point
    };

    public static string KindLabel(DrawingKind kind) => kind switch
    {
        DrawingKind.Incident => "Incident",
        DrawingKind.Staging => "Staging area",
        DrawingKind.Rendezvous => "Rendezvous point",
        DrawingKind.LandingZone => "Landing zone",
        DrawingKind.Cordon => "Cordon",
        DrawingKind.Perimeter => "Perimeter",
        DrawingKind.RoadClosure => "Road closure",
        DrawingKind.RangeRings => "Range rings",
        DrawingKind.Measure => "Measurement",
        DrawingKind.Hazmat => "Hazmat zone",
        DrawingKind.Unit => "Unit",
        _ => "Note"
    };

    public string Title => Label.Length > 0 ? Label : KindLabel(Kind);

    // ------------------------------------------------------------------ measurements

    public double LengthMeters => Shape == DrawingShape.Line ? Geometry.Length(Points) :
        Shape == DrawingShape.Polygon && Points.Count > 2 ? Geometry.Length(Points.Append(Points[0]).ToList()) : 0;

    public double AreaSquareMeters => Shape == DrawingShape.Polygon && Points.Count >= 3 ? Geometry.Area(Points) : 0;

    public static string FormatArea(double m2) =>
        m2 < 10_000 ? $"{m2:N0} m²" : m2 < 1_000_000 ? $"{m2 / 10_000:0.00} ha ({m2 / 4046.86:0.0} acres)" : $"{m2 / 1_000_000:0.00} km² ({m2 / 10_000:N0} ha)";

    /// <summary>Downwind bearing (where the plume goes).</summary>
    public double? DownwindBearing => WindFromDegrees is { } w ? (w + 180) % 360 : null;

    /// <summary>Circle around the spill (initial isolation zone).</summary>
    public List<GeoPoint> IsolationRing() => IsolationMeters is { } r && Points.Count > 0 ? Geometry.Circle(Points[0], r) : new();

    /// <summary>The protective-action zone downwind of the spill.</summary>
    public List<GeoPoint> ProtectiveZone()
    {
        if (ProtectiveMeters is not { } d || DownwindBearing is not { } b || Points.Count == 0)
            return new();
        var c = Points[0];
        if (ProtectiveShape == ProtectiveShape.Sector)
            return Geometry.Sector(c, b, SectorWidthDegrees, d);
        // ERG square: from the spill, half the width to each side of the downwind line.
        var left = Geometry.Destination(c, b - 90, d / 2);
        var right = Geometry.Destination(c, b + 90, d / 2);
        return new List<GeoPoint> { left, Geometry.Destination(left, b, d), Geometry.Destination(right, b, d), right, left };
    }

    /// <summary>Outlines to draw/export for this drawing (circles and zones expanded into rings).</summary>
    public List<(string Name, List<GeoPoint> Ring)> DerivedRings()
    {
        var list = new List<(string, List<GeoPoint>)>();
        if (Points.Count == 0)
            return list;
        foreach (var r in Rings)
            list.Add(($"{FormatRadius(r)}", Geometry.Circle(Points[0], r)));
        if (Kind == DrawingKind.Hazmat)
        {
            if (IsolationRing() is { Count: > 0 } iso)
                list.Add(($"Isolation {FormatRadius(IsolationMeters!.Value)}", iso));
            if (ProtectiveZone() is { Count: > 0 } pz)
                list.Add(($"Protective action {FormatRadius(ProtectiveMeters!.Value)} downwind", pz));
        }
        return list;
    }

    public static string FormatRadius(double m) => m < 1000 ? $"{m:0} m" : $"{m / 1000:0.##} km";

    // ------------------------------------------------------------------ text

    /// <summary>Plain-ASCII description for dispatch, in the same style as the location block.</summary>
    public string DispatchText()
    {
        var sb = new StringBuilder();
        static string A(string s) => Shared.Core.Geo.DispatchText.Ascii(s);
        sb.AppendLine($"{KindLabel(Kind).ToUpperInvariant()}{(Label.Length > 0 ? " – " + Label : "")} (drawn {CreatedUtc.ToLocalTime():HH:mm dd MMM yyyy})");
        var a = Anchor;
        sb.AppendLine($"{(Shape == DrawingShape.Polygon ? "Centre" : Shape == DrawingShape.Line ? "Start" : "Location")}: {CoordinateFormat.Decimal(a.Lat, a.Lon)} (WGS84)");
        sb.AppendLine($"Grid (MGRS): {CoordinateFormat.Mgrs(a.Lat, a.Lon)}");
        if (Shape == DrawingShape.Line && Points.Count > 1)
        {
            var end = Points[^1];
            sb.AppendLine($"End: {CoordinateFormat.Decimal(end.Lat, end.Lon)} / {CoordinateFormat.Mgrs(end.Lat, end.Lon)}");
            sb.AppendLine($"Length: {GeoMath.FormatDistance(LengthMeters)}");
        }
        if (Shape == DrawingShape.Polygon && Points.Count >= 3)
            sb.AppendLine($"Area: {FormatArea(AreaSquareMeters)}, perimeter {GeoMath.FormatDistance(LengthMeters)}");
        if (Rings.Count > 0)
            sb.AppendLine("Rings: " + string.Join(", ", Rings.Select(FormatRadius)));
        if (Kind == DrawingKind.Hazmat)
        {
            if (UnNumber is { Length: > 0 })
                sb.AppendLine($"Material: UN{UnNumber}");
            if (IsolationMeters is { } iso)
                sb.AppendLine($"Isolate {FormatRadius(iso)} in all directions");
            if (ProtectiveMeters is { } pd && WindFromDegrees is { } wf)
                sb.AppendLine($"Protect {FormatRadius(pd)} downwind – wind from {wf:000} deg ({GeoMath.CompassPoint(wf)}), downwind {DownwindBearing:000} deg ({GeoMath.CompassPoint(DownwindBearing!.Value)})");
        }
        if (Unit is { Length: > 0 })
            sb.AppendLine("Unit: " + Unit);
        if (Notes.Length > 0)
            sb.AppendLine("Notes: " + A(Notes));
        return sb.ToString().TrimEnd();
    }

    public Drawing Clone() => Json.Deserialize<Drawing>(Json.Serialize(this));

    public void Touch() => ModifiedUtc = DateTime.UtcNow;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{KindLabel(Kind)}: {Title}");
}
