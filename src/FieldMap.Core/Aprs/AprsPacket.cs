namespace FieldMap.Core.Aprs;

public enum AprsPacketType
{
    Unknown,
    Position,
    MicE,
    Object,
    Item,
    Status,
    Message,
    Weather,
    Telemetry,
    Other
}

/// <summary>Where a packet was heard.</summary>
[Flags]
public enum AprsVia
{
    None = 0,
    Internet = 1,
    Radio = 2
}

/// <summary>Weather values from a weather report, in the units sent (APRS uses imperial units on the air).</summary>
public sealed class AprsWeather
{
    public int? WindDirection { get; set; }
    public double? WindSpeedMph { get; set; }
    public double? GustMph { get; set; }
    public double? TemperatureF { get; set; }
    public double? RainLastHourInches { get; set; }
    public double? Rain24hInches { get; set; }
    public double? RainSinceMidnightInches { get; set; }
    public int? HumidityPercent { get; set; }
    public double? PressureMbar { get; set; }

    public double? TemperatureC => TemperatureF is { } f ? (f - 32) * 5 / 9 : null;

    public override string ToString()
    {
        var parts = new List<string>();
        if (TemperatureC is { } c)
            parts.Add($"{c:0.#} °C");
        if (WindSpeedMph is { } w)
            parts.Add($"wind {(WindDirection is { } d ? GeoMath.CompassPoint(d) + " " : "")}{w * 1.609344:0} km/h" + (GustMph is { } g ? $" gust {g * 1.609344:0}" : ""));
        if (HumidityPercent is { } h)
            parts.Add($"{h} % RH");
        if (PressureMbar is { } p)
            parts.Add($"{p:0.0} hPa");
        if (RainLastHourInches is { } r && r > 0)
            parts.Add($"rain {r * 25.4:0.#} mm/h");
        return string.Join(" · ", parts);
    }
}

/// <summary>One decoded APRS packet. Fields that a packet doesn't carry stay null.</summary>
public sealed class AprsPacket
{
    /// <summary>The packet as text, "SOURCE>DEST,PATH:information".</summary>
    public string Raw { get; init; } = "";
    public string Source { get; init; } = "";
    public string Destination { get; init; } = "";
    public IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();
    public string Information { get; init; } = "";
    public AprsPacketType Type { get; set; }

    /// <summary>Object or item name; null for a station's own position.</summary>
    public string? ObjectName { get; set; }

    /// <summary>False when an object or item is deleted ("killed") by its owner.</summary>
    public bool Alive { get; set; } = true;

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>Position ambiguity: 0 = exact, 1–4 = digits blanked by the sender (about 185 m to 111 km).</summary>
    public int Ambiguity { get; set; }

    public char SymbolTable { get; set; } = '/';
    public char SymbolCode { get; set; } = '/';
    public double? CourseDegrees { get; set; }
    public double? SpeedKnots { get; set; }
    public double? AltitudeMeters { get; set; }
    public string Comment { get; set; } = "";
    public string? Status { get; set; }

    /// <summary>Mic-E message ("En Route", "In Service", "EMERGENCY"…).</summary>
    public string? MicEMessage { get; set; }

    public bool Emergency { get; set; }
    public AprsWeather? Weather { get; set; }

    /// <summary>Message addressee and text (type Message).</summary>
    public string? Addressee { get; set; }
    public string? MessageText { get; set; }
    public string? MessageId { get; set; }

    /// <summary>Why the packet could not be decoded, if it couldn't.</summary>
    public string? Error { get; set; }

    public bool HasPosition => Latitude is not null && Longitude is not null;

    /// <summary>The station or object this packet describes on the map.</summary>
    public string Key => ObjectName ?? Source;

    public double? SpeedKmh => SpeedKnots * 1.852;
}
