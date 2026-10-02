namespace FieldMap.Core.Routing;

public enum ProfileKind
{
    Car,
    Emergency,
    Truck,
    Walking
}

/// <summary>
/// How a vehicle uses the road network: which segments it may use and how fast. Truck dimensions are compared with the
/// height/weight/length limits tagged in OpenStreetMap; untagged limits are unknown, which directions say.
/// </summary>
public sealed class VehicleProfile
{
    public ProfileKind Kind { get; init; }
    public string Name { get; init; } = "";
    public double HeightMeters { get; init; }
    public double WeightTonnes { get; init; }
    public double LengthMeters { get; init; }

    /// <summary>Upper bound on speed (km/h), used by the A* estimate; must not be lower than any speed returned.</summary>
    public double MaxSpeedKmh => Kind switch { ProfileKind.Emergency => 135, ProfileKind.Walking => 5, ProfileKind.Truck => 100, _ => 130 };

    public static VehicleProfile Car { get; } = new() { Kind = ProfileKind.Car, Name = "Car" };
    public static VehicleProfile Emergency { get; } = new() { Kind = ProfileKind.Emergency, Name = "Emergency vehicle" };
    public static VehicleProfile Walking { get; } = new() { Kind = ProfileKind.Walking, Name = "Walking" };

    /// <summary>Typical North American tractor-trailer: 4.15 m high, 39.5 t, 23 m long.</summary>
    public static VehicleProfile Truck(double heightMeters = 4.15, double weightTonnes = 39.5, double lengthMeters = 23) =>
        new() { Kind = ProfileKind.Truck, Name = "Large truck / trailer", HeightMeters = heightMeters, WeightTonnes = weightTonnes, LengthMeters = lengthMeters };

    public bool IsWalking => Kind == ProfileKind.Walking;

    /// <summary>Speed in km/h on a segment in a direction, or 0 when the vehicle may not use it.</summary>
    public double Speed(RoadGraph g, int segment, bool forward)
    {
        var f = g.SegFlags[segment];
        if (IsWalking)
            return (f & SegmentFlags.NoWalk) != 0 ? 0 : (f & SegmentFlags.Ferry) != 0 ? g.SegSpeed[segment] : 5;

        if ((f & SegmentFlags.WalkOnly) != 0 || (f & SegmentFlags.NoMotor) != 0)
            return 0;
        if ((f & SegmentFlags.EmergencyAccess) != 0 && Kind != ProfileKind.Emergency)
            return 0;
        if (forward ? (f & SegmentFlags.OnewayBackward) != 0 : (f & SegmentFlags.OnewayForward) != 0)
            return 0;

        double speed = g.SegSpeed[segment];
        var cls = g.SegClass[segment];
        if ((f & SegmentFlags.Unpaved) != 0)
            speed = Math.Min(speed, cls == RoadClass.Track ? 25 : 50);
        switch (Kind)
        {
            case ProfileKind.Emergency:
                // Lights and siren: faster on through roads, about the same on local streets.
                if (cls <= RoadClass.Tertiary && (f & SegmentFlags.Ferry) == 0)
                    speed = Math.Min(130, speed * 1.15);
                break;
            case ProfileKind.Truck:
                if ((f & SegmentFlags.NoHgv) != 0)
                    return 0;
                if (g.SegMaxHeight[segment] > 0 && HeightMeters > g.SegMaxHeight[segment] / 100.0)
                    return 0;
                if (g.SegMaxWeight[segment] > 0 && WeightTonnes > g.SegMaxWeight[segment] / 10.0)
                    return 0;
                if (g.SegMaxLength[segment] > 0 && LengthMeters > g.SegMaxLength[segment] / 10.0)
                    return 0;
                speed = Math.Min(speed, 90);
                if (cls is RoadClass.Residential or RoadClass.Service or RoadClass.Track)
                    speed *= 0.6; // discourage residential shortcuts
                break;
        }
        return speed;
    }

    /// <summary>Whether the vehicle may pass a graph node (locked gates, bollards).</summary>
    public bool CanPass(RoadGraph g, int node)
    {
        var f = g.NodeFlag[node];
        if (IsWalking)
            return (f & NodeFlags.BlocksWalk) == 0;
        if ((f & NodeFlags.BlocksMotor) == 0)
            return true;
        return Kind == ProfileKind.Emergency && (f & NodeFlags.EmergencyPasses) != 0;
    }
}
