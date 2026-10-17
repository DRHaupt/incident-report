using FieldMap.Core.Geo;
using FieldMap.Core.Layers;
using FieldMap.Core.Osm;

namespace FieldMap.Core.Build;

/// <summary>A way kept from the OSM extract, with resolved coordinates.</summary>
public sealed class MapWay
{
    public long Id { get; init; }
    public long[] NodeIds { get; init; } = Array.Empty<long>();
    public GeoPoint[] Points { get; set; } = Array.Empty<GeoPoint>();
    public OsmTags Tags { get; init; } = OsmTags.Empty;
}

/// <summary>A polygon (outer rings and holes) from a closed way or a multipolygon relation.</summary>
public sealed class MapArea
{
    public long Id { get; init; }
    public string Kind { get; init; } = "";
    public string? Name { get; init; }
    public List<GeoPoint[]> Outers { get; } = new();
    public List<GeoPoint[]> Inners { get; } = new();
    public double AreaM2 { get; set; }
}

public sealed class Place
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public long Population { get; init; }
    public double Lat { get; init; }
    public double Lon { get; init; }

    /// <summary>1 = city … 5 = locality; smaller numbers are labelled first.</summary>
    public int Rank => Kind switch { "city" => 1, "town" => 2, "village" => 3, "suburb" or "hamlet" => 4, _ => 5 };
}

public sealed class AddressPoint
{
    public string Number { get; set; } = "";
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string Source { get; set; } = "OpenStreetMap";

    public string Label => (Unit.Length > 0 ? Unit + "-" : "") + $"{Number} {Street}".Trim();
}

/// <summary>A point of interest on one of the catalogue layers.</summary>
/// <summary>A named shop, restaurant, office, hotel… from OpenStreetMap: searchable, not drawn as a map layer.</summary>
public sealed class Business
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Plain-English type: "Restaurant", "Grocery store", "Hotel".</summary>
    public string Kind { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Hours { get; set; } = "";
    public string Website { get; set; } = "";
    public GeoPoint Point => new(Lat, Lon);
}

public sealed class Poi
{
    public long Id { get; set; }
    public string Layer { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public Dictionary<string, string> Props { get; set; } = new();

    /// <summary>Data source name (e.g. "OpenStreetMap", or an imported registry's name).</summary>
    public string Source { get; set; } = "OpenStreetMap";

    /// <summary>yyyy-MM-dd when the source says the entry was last checked, if known.</summary>
    public string? LastVerified { get; set; }

    /// <summary>"node/123", "way/456" for OSM-derived entries.</summary>
    public string? SourceId { get; set; }

    public GeoPoint Point => new(Lat, Lon);

    public string DisplayName => Name.Length > 0 ? Name : LayerCatalog.Find(Layer)?.KindLabel(Kind) ?? Kind;

    public string KindLabel => LayerCatalog.Find(Layer)?.KindLabel(Kind) ?? Kind;
}

/// <summary>Which vehicles a turn restriction applies to.</summary>
[Flags]
public enum RestrictionVehicles : byte
{
    None = 0,
    Car = 1,
    Truck = 2,
    Emergency = 4,
    All = Car | Truck | Emergency
}

/// <summary>An OSM turn restriction relation (type=restriction): from way, via node or ways, to way.</summary>
public sealed class OsmRestriction
{
    public long Id { get; init; }
    public long From { get; init; }
    public long To { get; init; }
    public long? ViaNode { get; init; }
    public long[] ViaWays { get; init; } = Array.Empty<long>();

    /// <summary>no_left_turn, no_right_turn, no_straight_on, no_u_turn, no_entry, no_exit, only_left_turn, only_right_turn, only_straight_on, only_u_turn.</summary>
    public string Kind { get; init; } = "";

    public RestrictionVehicles Vehicles { get; init; } = RestrictionVehicles.All;

    public bool IsOnly => Kind.StartsWith("only_", StringComparison.Ordinal);
}

/// <summary>Everything the builders need from one OSM extract.</summary>
public sealed class MapData
{
    public List<MapWay> Roads { get; } = new();
    public List<MapWay> Rail { get; } = new();
    public List<MapWay> Waterways { get; } = new();
    public List<MapWay> Coastlines { get; } = new();
    public List<MapWay> Boundaries { get; } = new();
    public List<MapArea> Water { get; } = new();
    public List<MapArea> Landuse { get; } = new();
    public List<MapArea> Hazards { get; } = new();
    public List<Place> Places { get; } = new();
    public List<Poi> Pois { get; } = new();
    public List<Business> Businesses { get; } = new();
    public List<AddressPoint> Addresses { get; } = new();

    /// <summary>Turn restrictions (no left turn, only straight on…), for routing.</summary>
    public List<OsmRestriction> Restrictions { get; } = new();

    /// <summary>Tags of barrier nodes (gates, bollards) that lie on roads, for routing.</summary>
    public Dictionary<long, OsmTags> Barriers { get; } = new();

    public GeoBounds Bounds { get; set; } = GeoBounds.Empty;

    /// <summary>Newest element timestamp is not read; the extract's own date comes from the download metadata.</summary>
    public string Summary =>
        $"{Roads.Count:N0} roads, {Rail.Count:N0} rail, {Water.Count:N0} water areas, {Landuse.Count:N0} land-use areas, " +
        $"{Places.Count:N0} places, {Pois.Count:N0} POIs, {Addresses.Count:N0} addresses";
}
