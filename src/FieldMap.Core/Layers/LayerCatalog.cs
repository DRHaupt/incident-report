using FieldMap.Core.Osm;

namespace FieldMap.Core.Layers;

/// <summary>A point-of-interest layer shown in the Layers panel (AEDs, hospitals, hydrants…).</summary>
public sealed class LayerDef
{
    public required string Id { get; init; }
    public required string Title { get; init; }

    /// <summary>Short text drawn inside the map symbol ("AED", "H", "F").</summary>
    public required string Glyph { get; init; }

    /// <summary>Symbol colour in the standard theme (ARGB).</summary>
    public required uint Color { get; init; }

    /// <summary>Lowest zoom at which the symbols are drawn.</summary>
    public int MinZoom { get; init; } = 12;

    /// <summary>Shown when the layer is first installed.</summary>
    public bool DefaultOn { get; init; }

    /// <summary>Category words for search ("defib", "fire hall"…), already normalised.</summary>
    public string[] SearchWords { get; init; } = Array.Empty<string>();

    /// <summary>Warning shown with the layer (e.g. AED locations may be out of date).</summary>
    public string? Warning { get; init; }

    /// <summary>Plain-language description of which OSM tags feed the layer.</summary>
    public string TagsDescription { get; init; } = "";

    /// <summary>Returns the POI kind (e.g. "ed", "hydrant") for an OSM element, or null when it isn't in this layer.</summary>
    internal Func<OsmTags, string?> Classify { get; init; } = _ => null;

    /// <summary>Human label for a kind ("Hospital – emergency department").</summary>
    public Func<string, string> KindLabel { get; init; } = k => k;
}

/// <summary>Every POI layer, in Layers-panel order. Tag choices are documented in docs/FIELDMAP-DATA-SOURCES.md.</summary>
public static class LayerCatalog
{
    public const string Aed = "aed";
    public const string Hospital = "hospital";
    public const string School = "school";
    public const string Fire = "fire";
    public const string Police = "police";
    public const string Ambulance = "ambulance";
    public const string Air = "air";
    public const string Pharmacy = "pharmacy";
    public const string Fuel = "fuel";
    public const string Water = "water";
    public const string Shelter = "shelter";
    public const string Rest = "rest";
    public const string Crossings = "crossings";
    public const string Access = "access";
    public const string Hazard = "hazard";
    public const string Markers = "markers";

    public const string AedWarning = "AED locations may be out of date, moved or locked. Confirm with dispatch.";

    public static readonly IReadOnlyList<LayerDef> All = new[]
    {
        new LayerDef
        {
            Id = Aed, Title = "AEDs", Glyph = "AED", Color = 0xFF2E9E4F, MinZoom = 13, DefaultOn = true,
            SearchWords = new[] { "aed", "aeds", "defibrillator", "defibrillators", "defib" },
            Warning = AedWarning,
            TagsDescription = "emergency=defibrillator, plus imported registries",
            Classify = t => t.Is("emergency", "defibrillator") ? "aed" : null,
            KindLabel = _ => "AED (defibrillator)"
        },
        new LayerDef
        {
            Id = Hospital, Title = "Hospitals", Glyph = "H", Color = 0xFFD7263D, MinZoom = 9, DefaultOn = true,
            SearchWords = new[] { "hospital", "hospitals", "emergency department", "emergency room", "er", "ed" },
            TagsDescription = "amenity=hospital / healthcare=hospital; emergency=yes marks an emergency department",
            Classify = t => t.Is("amenity", "hospital") || t.Is("healthcare", "hospital")
                ? t["emergency"] switch { "yes" => "ed", "no" => "no-ed", _ => "hospital" }
                : null,
            KindLabel = k => k switch { "ed" => "Hospital – emergency department", "no-ed" => "Hospital – no emergency department", _ => "Hospital (ED not recorded)" }
        },
        new LayerDef
        {
            Id = School, Title = "Schools", Glyph = "S", Color = 0xFF8E6C2B, MinZoom = 13,
            SearchWords = new[] { "school", "schools", "college", "university" },
            TagsDescription = "amenity=school / college / university",
            Classify = t => t["amenity"] switch { "school" => "school", "college" => "college", "university" => "university", _ => null },
            KindLabel = k => k switch { "college" => "College", "university" => "University", _ => "School" }
        },
        new LayerDef
        {
            Id = Fire, Title = "Fire stations", Glyph = "F", Color = 0xFFE4572E, MinZoom = 11, DefaultOn = true,
            SearchWords = new[] { "fire station", "fire stations", "fire hall", "fire halls", "firehall", "fire department" },
            TagsDescription = "amenity=fire_station",
            Classify = t => t.Is("amenity", "fire_station") ? "fire" : null,
            KindLabel = _ => "Fire station"
        },
        new LayerDef
        {
            Id = Police, Title = "Police", Glyph = "P", Color = 0xFF2F5BBE, MinZoom = 11, DefaultOn = true,
            SearchWords = new[] { "police", "rcmp", "police station" },
            TagsDescription = "amenity=police",
            Classify = t => t.Is("amenity", "police") ? "police" : null,
            KindLabel = _ => "Police"
        },
        new LayerDef
        {
            Id = Ambulance, Title = "Ambulance stations", Glyph = "A", Color = 0xFFFFFFFF, MinZoom = 11, DefaultOn = true,
            SearchWords = new[] { "ambulance", "ambulance station", "bcehs", "ems station" },
            TagsDescription = "emergency=ambulance_station",
            Classify = t => t.Is("emergency", "ambulance_station") ? "ambulance" : null,
            KindLabel = _ => "Ambulance station"
        },
        new LayerDef
        {
            Id = Air, Title = "Helipads and airports", Glyph = "Hp", Color = 0xFF6C5CE7, MinZoom = 9,
            SearchWords = new[] { "helipad", "heliport", "airport", "airstrip", "aerodrome", "landing zone" },
            TagsDescription = "aeroway=helipad / heliport / aerodrome",
            Classify = t => t["aeroway"] switch { "helipad" or "heliport" => "helipad", "aerodrome" => "airport", _ => null },
            KindLabel = k => k == "airport" ? "Airport / airstrip" : "Helipad"
        },
        new LayerDef
        {
            Id = Pharmacy, Title = "Pharmacies", Glyph = "Rx", Color = 0xFF16A085, MinZoom = 13,
            SearchWords = new[] { "pharmacy", "pharmacies", "drugstore", "chemist" },
            TagsDescription = "amenity=pharmacy / healthcare=pharmacy",
            Classify = t => t.Is("amenity", "pharmacy") || t.Is("healthcare", "pharmacy") ? "pharmacy" : null,
            KindLabel = _ => "Pharmacy"
        },
        new LayerDef
        {
            Id = Fuel, Title = "Fuel", Glyph = "Gas", Color = 0xFFB8860B, MinZoom = 12,
            SearchWords = new[] { "fuel", "gas", "gas station", "petrol", "diesel" },
            TagsDescription = "amenity=fuel",
            Classify = t => t.Is("amenity", "fuel") ? "fuel" : null,
            KindLabel = _ => "Fuel station"
        },
        new LayerDef
        {
            Id = Water, Title = "Hydrants and water sources", Glyph = "W", Color = 0xFF1E90FF, MinZoom = 15,
            SearchWords = new[] { "hydrant", "hydrants", "fire hydrant", "water source", "water tank", "dry hydrant" },
            TagsDescription = "emergency=fire_hydrant / water_tank / suction_point / fire_water_pond",
            Classify = t => t["emergency"] switch
            {
                "fire_hydrant" => "hydrant",
                "water_tank" => "water_tank",
                "suction_point" => "suction_point",
                "fire_water_pond" => "pond",
                _ => null
            },
            KindLabel = k => k switch { "hydrant" => "Fire hydrant", "water_tank" => "Water tank", "suction_point" => "Suction point", "pond" => "Fire water pond", _ => k }
        },
        new LayerDef
        {
            Id = Shelter, Title = "Shelters and reception centres", Glyph = "Sh", Color = 0xFF9B59B6, MinZoom = 12,
            SearchWords = new[] { "shelter", "reception centre", "evacuation centre", "assembly point", "muster point" },
            TagsDescription = "social_facility=shelter, amenity=shelter, emergency=assembly_point, ESS facilities",
            Classify = t =>
                t.Is("emergency", "assembly_point") ? "assembly" :
                t.Is("social_facility", "shelter") ? "shelter" :
                t.Is("amenity", "shelter") && !t.IsAny("shelter_type", "picnic_shelter", "public_transport", "weather_shelter") ? "shelter" :
                t.Is("emergency", "reception_centre") ? "reception" : null,
            KindLabel = k => k switch { "assembly" => "Assembly point", "reception" => "Reception centre", _ => "Shelter" }
        },
        new LayerDef
        {
            Id = Rest, Title = "Rest areas, washrooms, pull-outs", Glyph = "R", Color = 0xFF7F8C8D, MinZoom = 11,
            SearchWords = new[] { "rest area", "rest stop", "washroom", "toilet", "toilets", "pullout", "pull out", "brake check" },
            TagsDescription = "highway=rest_area / services, amenity=toilets, parking=layby, highway=passing_place",
            Classify = t =>
                t.IsAny("highway", "rest_area", "services") ? "rest_area" :
                t.Is("amenity", "toilets") ? "toilets" :
                t.Is("parking", "layby") || t.Is("highway", "passing_place") ? "pullout" : null,
            KindLabel = k => k switch { "toilets" => "Washroom", "pullout" => "Pull-out", _ => "Rest area" }
        },
        new LayerDef
        {
            Id = Crossings, Title = "Bridges, rail crossings, tunnels", Glyph = "Br", Color = 0xFFF39C12, MinZoom = 13,
            SearchWords = new[] { "bridge", "bridges", "rail crossing", "level crossing", "tunnel" },
            TagsDescription = "road ways with bridge=* / tunnel=* (with maxheight, maxweight), railway=level_crossing / crossing",
            // Bridges and tunnels come from road ways in the builder; nodes are rail crossings.
            Classify = t => t.IsAny("railway", "level_crossing", "crossing") ? "rail_crossing" : null,
            KindLabel = k => k switch { "bridge" => "Bridge", "tunnel" => "Tunnel", _ => "Rail crossing" }
        },
        new LayerDef
        {
            Id = Access, Title = "Gates, seasonal and forest roads", Glyph = "G", Color = 0xFFC0392B, MinZoom = 12,
            SearchWords = new[] { "gate", "gates", "forest service road", "fsr", "seasonal road" },
            TagsDescription = "barrier=gate / lift_gate / swing_gate; roads with seasonal=yes, winter_road=yes, or FSR names (highlighted)",
            Classify = t => t.IsAny("barrier", "gate", "lift_gate", "swing_gate") ? "gate" : null,
            KindLabel = _ => "Gate"
        },
        new LayerDef
        {
            Id = Hazard, Title = "Avalanche and rockfall areas", Glyph = "!", Color = 0xFFE74C3C, MinZoom = 10,
            SearchWords = new[] { "avalanche", "rockfall", "rock fall", "hazard" },
            TagsDescription = "hazard=avalanche / rockfall / falling_rocks (where mapped; no open official layer found)",
            Classify = t => t["hazard"] switch
            {
                "avalanche" => "avalanche",
                "rockfall" or "falling_rocks" or "rock_slide" or "landslide" => "rockfall",
                _ => null
            },
            KindLabel = k => k == "avalanche" ? "Avalanche area" : "Rockfall / slide area"
        },
        new LayerDef
        {
            Id = Markers, Title = "Highway km markers and exits", Glyph = "km", Color = 0xFF27AE60, MinZoom = 12,
            SearchWords = new[] { "km marker", "kilometre marker", "exit", "milestone" },
            TagsDescription = "highway=milestone (distance), highway=motorway_junction (ref)",
            Classify = t => t.Is("highway", "milestone") ? "km" : t.Is("highway", "motorway_junction") ? "exit" : null,
            KindLabel = k => k == "exit" ? "Exit" : "Km marker"
        }
    };

    public static LayerDef? Find(string id) => All.FirstOrDefault(l => l.Id == id);

    /// <summary>The first layer that claims an element, with its kind.</summary>
    internal static (LayerDef Layer, string Kind)? Classify(OsmTags tags)
    {
        if (tags.Count == 0)
            return null;
        foreach (var layer in All)
            if (layer.Classify(tags) is { } kind)
                return (layer, kind);
        return null;
    }

    /// <summary>OSM keys copied onto POIs (shown in the details panel).</summary>
    internal static readonly string[] KeptKeys =
    {
        "name", "ref", "operator", "phone", "contact:phone", "opening_hours", "access", "emergency", "healthcare:speciality",
        "defibrillator:location", "description", "note", "check_date", "survey:date", "indoor", "level",
        "maxheight", "maxweight", "maxlength", "maxwidth", "distance", "fuel:diesel", "hgv", "addr:housenumber", "addr:street",
        "addr:city", "fire_hydrant:type", "water_tank:volume", "capacity", "shelter_type", "website", "aeroway", "icao", "iata"
    };
}
