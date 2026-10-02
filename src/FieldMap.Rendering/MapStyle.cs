using SkiaSharp;

namespace FieldMap.Map;

/// <summary>
/// Map colours for the three themes. Standard is the dark, high-contrast CAD style; Daylight is white with black roads
/// and saturated signal colours for direct sunlight; Night is very dark with dim amber/red so it doesn't spoil night vision.
/// </summary>
public sealed class MapStyle
{
    public required SKColor Background { get; init; }
    public required SKColor Water { get; init; }
    public required SKColor WaterLine { get; init; }
    public required SKColor Park { get; init; }
    public required SKColor Glacier { get; init; }
    public required SKColor Residential { get; init; }
    public required SKColor Commercial { get; init; }
    public required SKColor Industrial { get; init; }
    public required SKColor Wetland { get; init; }
    public required SKColor Military { get; init; }
    public required SKColor Airport { get; init; }
    public required SKColor Hazard { get; init; }
    public required SKColor Casing { get; init; }
    public required SKColor Motorway { get; init; }
    public required SKColor Trunk { get; init; }
    public required SKColor Primary { get; init; }
    public required SKColor Secondary { get; init; }
    public required SKColor Tertiary { get; init; }
    public required SKColor Minor { get; init; }
    public required SKColor Service { get; init; }
    public required SKColor Track { get; init; }
    public required SKColor Path { get; init; }
    public required SKColor Ferry { get; init; }
    public required SKColor Seasonal { get; init; }
    public required SKColor Rail { get; init; }
    public required SKColor Boundary { get; init; }
    public required SKColor Label { get; init; }
    public required SKColor LabelHalo { get; init; }
    public required SKColor PlaceLabel { get; init; }
    public required SKColor ShieldBack { get; init; }
    public required SKColor ShieldText { get; init; }
    public required SKColor Gps { get; init; }
    public required SKColor GpsStale { get; init; }
    public required SKColor Breadcrumb { get; init; }
    public required SKColor Route { get; init; }
    public required SKColor RouteAlt { get; init; }
    public required SKColor Overlay { get; init; }
    public required SKColor OverlayText { get; init; }
    public required SKColor Selection { get; init; }

    /// <summary>Symbol colours are dimmed at night.</summary>
    public required float SymbolBrightness { get; init; }

    public string Name { get; init; } = "";

    public static readonly MapStyle Standard = new()
    {
        Name = "Standard (CAD dark)",
        Background = new SKColor(0x12, 0x16, 0x1C),
        Water = new SKColor(0x17, 0x34, 0x52),
        WaterLine = new SKColor(0x2E, 0x6A, 0x99),
        Park = new SKColor(0x15, 0x27, 0x1B),
        Glacier = new SKColor(0x2B, 0x33, 0x3C),
        Residential = new SKColor(0x1B, 0x20, 0x28),
        Commercial = new SKColor(0x24, 0x1E, 0x29),
        Industrial = new SKColor(0x22, 0x20, 0x1B),
        Wetland = new SKColor(0x16, 0x26, 0x2A),
        Military = new SKColor(0x2C, 0x1A, 0x1A),
        Airport = new SKColor(0x22, 0x26, 0x31),
        Hazard = new SKColor(0xE7, 0x4C, 0x3C),
        Casing = new SKColor(0x00, 0x00, 0x00),
        Motorway = new SKColor(0xF2, 0xA9, 0x00),
        Trunk = new SKColor(0xF0, 0xB8, 0x3C),
        Primary = new SKColor(0xE8, 0xD0, 0x8C),
        Secondary = new SKColor(0xD8, 0xD8, 0xD8),
        Tertiary = new SKColor(0xBC, 0xBC, 0xBC),
        Minor = new SKColor(0x8C, 0x92, 0x9C),
        Service = new SKColor(0x62, 0x68, 0x72),
        Track = new SKColor(0x9A, 0x82, 0x5A),
        Path = new SKColor(0x6A, 0x7A, 0x6A),
        Ferry = new SKColor(0x4F, 0x9D, 0xE0),
        Seasonal = new SKColor(0xFF, 0x7A, 0x1A),
        Rail = new SKColor(0x9A, 0x9A, 0x9A),
        Boundary = new SKColor(0xB0, 0x7C, 0xC6),
        Label = new SKColor(0xE6, 0xE6, 0xE6),
        LabelHalo = new SKColor(0x00, 0x00, 0x00),
        PlaceLabel = new SKColor(0xFF, 0xFF, 0xFF),
        ShieldBack = new SKColor(0xF2, 0xA9, 0x00),
        ShieldText = new SKColor(0x00, 0x00, 0x00),
        Gps = new SKColor(0x29, 0x9B, 0xFF),
        GpsStale = new SKColor(0x88, 0x88, 0x88),
        Breadcrumb = new SKColor(0x00, 0xD0, 0xFF),
        Route = new SKColor(0x00, 0xB4, 0xFF),
        RouteAlt = new SKColor(0x6F, 0x86, 0x9C),
        Overlay = new SKColor(0x10, 0x14, 0x1A, 0xE0),
        OverlayText = new SKColor(0xFF, 0xFF, 0xFF),
        Selection = new SKColor(0xFF, 0xFF, 0x00),
        SymbolBrightness = 1f
    };

    public static readonly MapStyle Daylight = new()
    {
        Name = "Daylight",
        Background = new SKColor(0xFF, 0xFF, 0xFF),
        Water = new SKColor(0x7F, 0xC2, 0xF5),
        WaterLine = new SKColor(0x00, 0x5C, 0xC8),
        Park = new SKColor(0xC9, 0xEB, 0xC2),
        Glacier = new SKColor(0xE6, 0xF2, 0xFA),
        Residential = new SKColor(0xF0, 0xF0, 0xF0),
        Commercial = new SKColor(0xF5, 0xE6, 0xF0),
        Industrial = new SKColor(0xEE, 0xEA, 0xDE),
        Wetland = new SKColor(0xD6, 0xEC, 0xE6),
        Military = new SKColor(0xF5, 0xD5, 0xD5),
        Airport = new SKColor(0xE4, 0xE6, 0xEE),
        Hazard = new SKColor(0xC0, 0x00, 0x00),
        Casing = new SKColor(0x00, 0x00, 0x00),
        Motorway = new SKColor(0xFF, 0xC4, 0x00),
        Trunk = new SKColor(0xFF, 0xD2, 0x40),
        Primary = new SKColor(0xFF, 0xE8, 0x96),
        Secondary = new SKColor(0xFF, 0xFF, 0xFF),
        Tertiary = new SKColor(0xFF, 0xFF, 0xFF),
        Minor = new SKColor(0xFF, 0xFF, 0xFF),
        Service = new SKColor(0xFF, 0xFF, 0xFF),
        Track = new SKColor(0x8B, 0x5A, 0x00),
        Path = new SKColor(0x40, 0x40, 0x40),
        Ferry = new SKColor(0x00, 0x3C, 0xB4),
        Seasonal = new SKColor(0xE0, 0x50, 0x00),
        Rail = new SKColor(0x30, 0x30, 0x30),
        Boundary = new SKColor(0x80, 0x00, 0x80),
        Label = new SKColor(0x00, 0x00, 0x00),
        LabelHalo = new SKColor(0xFF, 0xFF, 0xFF),
        PlaceLabel = new SKColor(0x00, 0x00, 0x00),
        ShieldBack = new SKColor(0xFF, 0xC4, 0x00),
        ShieldText = new SKColor(0x00, 0x00, 0x00),
        Gps = new SKColor(0x00, 0x3C, 0xB4),
        GpsStale = new SKColor(0x60, 0x60, 0x60),
        Breadcrumb = new SKColor(0x00, 0x3C, 0xB4),
        Route = new SKColor(0x00, 0x50, 0xE0),
        RouteAlt = new SKColor(0x70, 0x70, 0x70),
        Overlay = new SKColor(0xFF, 0xFF, 0xFF, 0xF0),
        OverlayText = new SKColor(0x00, 0x00, 0x00),
        Selection = new SKColor(0xC0, 0x00, 0xC0),
        SymbolBrightness = 1f
    };

    public static readonly MapStyle Night = new()
    {
        Name = "Night",
        Background = new SKColor(0x05, 0x06, 0x08),
        Water = new SKColor(0x08, 0x14, 0x20),
        WaterLine = new SKColor(0x10, 0x2A, 0x40),
        Park = new SKColor(0x07, 0x0F, 0x09),
        Glacier = new SKColor(0x10, 0x13, 0x16),
        Residential = new SKColor(0x0B, 0x0C, 0x0E),
        Commercial = new SKColor(0x0E, 0x0B, 0x0E),
        Industrial = new SKColor(0x0D, 0x0C, 0x0A),
        Wetland = new SKColor(0x08, 0x10, 0x11),
        Military = new SKColor(0x12, 0x08, 0x08),
        Airport = new SKColor(0x0C, 0x0D, 0x11),
        Hazard = new SKColor(0x8A, 0x22, 0x1A),
        Casing = new SKColor(0x00, 0x00, 0x00),
        Motorway = new SKColor(0x8A, 0x58, 0x00),
        Trunk = new SKColor(0x7A, 0x52, 0x14),
        Primary = new SKColor(0x66, 0x50, 0x34),
        Secondary = new SKColor(0x52, 0x48, 0x3E),
        Tertiary = new SKColor(0x46, 0x3E, 0x36),
        Minor = new SKColor(0x38, 0x33, 0x2E),
        Service = new SKColor(0x2A, 0x27, 0x24),
        Track = new SKColor(0x4A, 0x3A, 0x22),
        Path = new SKColor(0x2E, 0x34, 0x2E),
        Ferry = new SKColor(0x22, 0x44, 0x66),
        Seasonal = new SKColor(0x8A, 0x3C, 0x0C),
        Rail = new SKColor(0x40, 0x3A, 0x34),
        Boundary = new SKColor(0x4A, 0x32, 0x52),
        Label = new SKColor(0x9A, 0x88, 0x6A),
        LabelHalo = new SKColor(0x00, 0x00, 0x00),
        PlaceLabel = new SKColor(0xB0, 0x9C, 0x78),
        ShieldBack = new SKColor(0x6A, 0x44, 0x00),
        ShieldText = new SKColor(0xD0, 0xB0, 0x80),
        Gps = new SKColor(0x2C, 0x5C, 0x9A),
        GpsStale = new SKColor(0x40, 0x40, 0x40),
        Breadcrumb = new SKColor(0x1A, 0x5A, 0x70),
        Route = new SKColor(0x1E, 0x5E, 0x8C),
        RouteAlt = new SKColor(0x30, 0x3A, 0x44),
        Overlay = new SKColor(0x08, 0x0A, 0x0C, 0xE8),
        OverlayText = new SKColor(0xC4, 0xB4, 0x96),
        Selection = new SKColor(0xA0, 0x80, 0x10),
        SymbolBrightness = 0.55f
    };

    public SKColor RoadColor(string cls) => cls switch
    {
        "motorway" => Motorway,
        "trunk" => Trunk,
        "primary" => Primary,
        "secondary" => Secondary,
        "tertiary" => Tertiary,
        "minor" => Minor,
        "service" => Service,
        "track" => Track,
        "ferry" => Ferry,
        _ => Path
    };

    public SKColor LanduseColor(string kind) => kind switch
    {
        "park" => Park,
        "glacier" => Glacier,
        "residential" => Residential,
        "commercial" => Commercial,
        "industrial" => Industrial,
        "wetland" => Wetland,
        "military" => Military,
        "airport" => Airport,
        _ => Residential
    };

    /// <summary>A symbol colour adjusted for the theme (dimmed at night).</summary>
    public SKColor Symbol(uint argb)
    {
        var c = new SKColor(argb);
        if (SymbolBrightness >= 1)
            return c;
        return new SKColor((byte)(c.Red * SymbolBrightness), (byte)(c.Green * SymbolBrightness), (byte)(c.Blue * SymbolBrightness), c.Alpha);
    }

    public SKColor Symbol(SKColor c) => SymbolBrightness >= 1 ? c :
        new SKColor((byte)(c.Red * SymbolBrightness), (byte)(c.Green * SymbolBrightness), (byte)(c.Blue * SymbolBrightness), c.Alpha);

    /// <summary>Road line width in screen pixels at a zoom level.</summary>
    public static float RoadWidth(string cls, double zoom)
    {
        static float Lerp(double z, double z0, float w0, double z1, float w1) =>
            (float)(z <= z0 ? w0 * Math.Pow(2, (z - z0) * 0.5) : z >= z1 ? w1 * Math.Pow(2, (z - z1) * 0.6) : w0 + (w1 - w0) * (z - z0) / (z1 - z0));
        return cls switch
        {
            "motorway" or "trunk" => Lerp(zoom, 6, 1.5f, 16, 11f),
            "primary" => Lerp(zoom, 8, 1.2f, 16, 9f),
            "secondary" => Lerp(zoom, 9, 1.0f, 16, 8f),
            "tertiary" => Lerp(zoom, 10, 1.0f, 16, 7f),
            "minor" => Lerp(zoom, 12, 0.8f, 17, 6f),
            "service" => Lerp(zoom, 13, 0.6f, 17, 3.5f),
            "track" => Lerp(zoom, 12, 0.8f, 17, 3f),
            "ferry" => 2f,
            _ => Lerp(zoom, 14, 0.6f, 18, 2f)
        };
    }
}
