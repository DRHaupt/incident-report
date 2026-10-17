using FieldMap.Core.Build;
using FieldMap.Core.Osm;

namespace FieldMap.Core.Search;

/// <summary>
/// Which named OpenStreetMap features count as businesses and places to search for (shops, restaurants, offices,
/// hotels, campgrounds, arenas…), with a plain-English type. Facilities the map shows as layers (hospitals,
/// pharmacies, fuel, schools…) are claimed by those layers first and never appear here twice.
/// </summary>
public static class BusinessKinds
{
    private static readonly Dictionary<string, string> Amenity = new()
    {
        ["restaurant"] = "Restaurant", ["fast_food"] = "Fast food", ["cafe"] = "Café", ["bar"] = "Bar", ["pub"] = "Pub",
        ["biergarten"] = "Beer garden", ["ice_cream"] = "Ice cream", ["food_court"] = "Food court", ["bank"] = "Bank",
        ["bureau_de_change"] = "Currency exchange", ["post_office"] = "Post office", ["library"] = "Library",
        ["community_centre"] = "Community centre", ["place_of_worship"] = "Place of worship", ["townhall"] = "Town hall",
        ["courthouse"] = "Courthouse", ["cinema"] = "Cinema", ["theatre"] = "Theatre", ["nightclub"] = "Nightclub",
        ["casino"] = "Casino", ["arts_centre"] = "Arts centre", ["events_venue"] = "Event venue", ["conference_centre"] = "Conference centre",
        ["exhibition_centre"] = "Exhibition centre", ["car_rental"] = "Car rental", ["car_wash"] = "Car wash", ["veterinary"] = "Veterinarian",
        ["dentist"] = "Dentist", ["doctors"] = "Doctor's office", ["clinic"] = "Clinic", ["childcare"] = "Childcare", ["kindergarten"] = "Preschool",
        ["marketplace"] = "Market", ["social_facility"] = "Social services", ["parking"] = "Parking", ["bus_station"] = "Bus station",
        ["ferry_terminal"] = "Ferry terminal", ["college"] = "College", ["university"] = "University", ["prison"] = "Correctional centre",
        ["fire_station"] = "Fire station", ["police"] = "Police", ["public_building"] = "Public building", ["vehicle_inspection"] = "Vehicle inspection"
    };

    private static readonly Dictionary<string, string> Shop = new()
    {
        ["supermarket"] = "Grocery store", ["convenience"] = "Convenience store", ["car_repair"] = "Auto repair", ["car"] = "Car dealer",
        ["car_parts"] = "Auto parts", ["tyres"] = "Tire shop", ["hardware"] = "Hardware store", ["doityourself"] = "Home improvement store",
        ["clothes"] = "Clothing store", ["shoes"] = "Shoe store", ["alcohol"] = "Liquor store", ["beverages"] = "Drinks store",
        ["bakery"] = "Bakery", ["butcher"] = "Butcher", ["beauty"] = "Beauty salon", ["hairdresser"] = "Hair salon",
        ["department_store"] = "Department store", ["mall"] = "Shopping mall", ["general"] = "General store", ["variety_store"] = "Dollar store",
        ["furniture"] = "Furniture store", ["electronics"] = "Electronics store", ["mobile_phone"] = "Phone store", ["computer"] = "Computer store",
        ["outdoor"] = "Outdoor store", ["sports"] = "Sporting goods", ["gift"] = "Gift shop", ["books"] = "Bookstore", ["florist"] = "Florist",
        ["optician"] = "Optician", ["pet"] = "Pet store", ["cannabis"] = "Cannabis store", ["laundry"] = "Laundromat", ["dry_cleaning"] = "Dry cleaner",
        ["jewelry"] = "Jewellery store", ["garden_centre"] = "Garden centre", ["farm"] = "Farm store", ["agrarian"] = "Farm supply",
        ["motorcycle"] = "Motorcycle dealer", ["bicycle"] = "Bike shop", ["boat"] = "Boat dealer", ["trade"] = "Trade supplier",
        ["chemist"] = "Drugstore", ["medical_supply"] = "Medical supply", ["hearing_aids"] = "Hearing clinic", ["massage"] = "Massage",
        ["tattoo"] = "Tattoo studio", ["funeral_directors"] = "Funeral home", ["storage_rental"] = "Storage", ["copyshop"] = "Print shop"
    };

    private static readonly Dictionary<string, string> Tourism = new()
    {
        ["hotel"] = "Hotel", ["motel"] = "Motel", ["hostel"] = "Hostel", ["guest_house"] = "Guest house", ["apartment"] = "Vacation rental",
        ["chalet"] = "Cabins", ["camp_site"] = "Campground", ["caravan_site"] = "RV park", ["alpine_hut"] = "Backcountry hut",
        ["wilderness_hut"] = "Backcountry hut", ["attraction"] = "Attraction", ["museum"] = "Museum", ["gallery"] = "Gallery", ["zoo"] = "Zoo",
        ["aquarium"] = "Aquarium", ["theme_park"] = "Theme park", ["viewpoint"] = "Viewpoint", ["information"] = "Visitor information",
        ["picnic_site"] = "Picnic site"
    };

    private static readonly Dictionary<string, string> Leisure = new()
    {
        ["sports_centre"] = "Sports centre", ["stadium"] = "Stadium", ["golf_course"] = "Golf course", ["ice_rink"] = "Arena",
        ["swimming_pool"] = "Pool", ["fitness_centre"] = "Gym", ["park"] = "Park", ["marina"] = "Marina", ["water_park"] = "Water park",
        ["nature_reserve"] = "Nature reserve", ["recreation_ground"] = "Recreation ground", ["sports_hall"] = "Sports hall",
        ["bowling_alley"] = "Bowling alley", ["miniature_golf"] = "Mini golf", ["horse_riding"] = "Riding centre", ["track"] = "Track",
        ["fairground"] = "Fairground", ["resort"] = "Resort"
    };

    /// <summary>The business type of a named feature, or null if it isn't one.</summary>
    public static string? Classify(OsmTags t)
    {
        if (t["name"] is not { Length: > 0 })
            return null;
        if (t["shop"] is { } shop && shop is not ("no" or "vacant" or "disused"))
            return Shop.TryGetValue(shop, out var s) ? s : Human(shop) + " shop";
        if (t["amenity"] is { } a && Amenity.TryGetValue(a, out var am))
            return am;
        if (t["tourism"] is { } tr && Tourism.TryGetValue(tr, out var to))
            return to;
        if (t["leisure"] is { } le && Leisure.TryGetValue(le, out var l))
            return l;
        if (t["office"] is { } o && o != "no")
            return o == "yes" ? "Office" : Human(o) + " office";
        if (t["craft"] is { } c && c != "no")
            return Human(c);
        if (t["healthcare"] is { } h && h != "no")
            return Human(h);
        if (t["club"] is { } cl && cl != "no")
            return "Club";
        return null;
    }

    /// <summary>"car_repair;tyres" → "Car repair".</summary>
    public static string Human(string value)
    {
        var v = value.Split(';')[0].Replace('_', ' ').Trim();
        return v.Length == 0 ? "Business" : char.ToUpperInvariant(v[0]) + v[1..];
    }

    internal static Business Make(OsmTags t, string kind, double lat, double lon)
    {
        var street = string.Join(" ", new[] { t["addr:housenumber"], t["addr:street"] }.Where(x => !string.IsNullOrEmpty(x)));
        var address = string.Join(", ", new[] { street, t["addr:city"] }.Where(x => !string.IsNullOrEmpty(x)));
        return new Business
        {
            Name = t["name"]!, Kind = kind, Lat = lat, Lon = lon, Address = address,
            Phone = t["phone"] ?? t["contact:phone"] ?? "", Hours = t["opening_hours"] ?? "", Website = t["website"] ?? t["contact:website"] ?? ""
        };
    }
}
