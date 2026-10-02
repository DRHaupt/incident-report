namespace FieldToolkit.Core.Location;

/// <summary>A WGS84 position fix.</summary>
public sealed record GeoFix(
    double Latitude,
    double Longitude,
    double? AccuracyMeters,
    double? AltitudeMeters,
    DateTime TimestampUtc,
    string Source,
    int? Satellites = null,
    double? Hdop = null,
    double? SpeedMetersPerSecond = null,
    double? CourseDegrees = null);

/// <summary>A named saved point (incident site, ambulance rendezvous point…).</summary>
public sealed class SavedPoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Note { get; set; } = "";
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
}
