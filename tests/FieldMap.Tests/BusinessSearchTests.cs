using FieldMap.Core.Build;
using FieldMap.Core.Geo;
using FieldMap.Core.Osm;
using FieldMap.Core.Overlays;
using FieldMap.Core.Sample;
using FieldMap.Core.Search;

namespace FieldMap.Tests;

[Collection("sample")]
public class BusinessSearchTests
{
    private readonly SampleFixture _f;

    public BusinessSearchTests(SampleFixture f) => _f = f;

    private static OsmTags T(params (string, string)[] tags) => new(tags.ToDictionary(t => t.Item1, t => t.Item2));

    [Fact]
    public void Named_shops_restaurants_offices_and_hotels_are_businesses_with_plain_types()
    {
        Assert.Equal("Restaurant", BusinessKinds.Classify(T(("amenity", "restaurant"), ("name", "A"))));
        Assert.Equal("Grocery store", BusinessKinds.Classify(T(("shop", "supermarket"), ("name", "A"))));
        Assert.Equal("Bike shop", BusinessKinds.Classify(T(("shop", "bicycle"), ("name", "A"))));
        Assert.Equal("Insurance office", BusinessKinds.Classify(T(("office", "insurance"), ("name", "A"))));
        Assert.Equal("Campground", BusinessKinds.Classify(T(("tourism", "camp_site"), ("name", "A"))));
        Assert.Equal("Arena", BusinessKinds.Classify(T(("leisure", "ice_rink"), ("name", "A"))));
        Assert.Equal("Pottery studio shop", BusinessKinds.Classify(T(("shop", "pottery_studio"), ("name", "A"))));
        Assert.Null(BusinessKinds.Classify(T(("amenity", "restaurant")))); // no name
        Assert.Null(BusinessKinds.Classify(T(("shop", "vacant"), ("name", "A"))));
        Assert.Null(BusinessKinds.Classify(T(("highway", "residential"), ("name", "A"))));
    }

    [Fact]
    public void Businesses_are_extracted_from_nodes_and_building_outlines_but_layer_facilities_are_not_duplicated()
    {
        var osm = Path.Combine(_f.Root, "business-test.osm");
        SampleRegion.Write(osm);
        var data = OsmExtractor.Extract(new List<string> { osm }, null, default);
        var names = data.Businesses.Select(b => b.Name).ToList();
        Assert.Contains("Sampleton Diner (SAMPLE)", names);
        Assert.Contains("Sample Hardware (SAMPLE)", names); // closed way (building outline)
        Assert.DoesNotContain("Sample Drugs (SAMPLE)", names); // pharmacy: a map layer
        var diner = data.Businesses.First(b => b.Name.StartsWith("Sampleton Diner"));
        Assert.Equal("Restaurant", diner.Kind);
        Assert.Equal("212 Main Street", diner.Address);
        Assert.Equal("+1-555-0142", diner.Phone);
    }

    [Fact]
    public void Search_finds_businesses_by_name_and_by_type_with_details()
    {
        var byName = _f.Search.Search("sampleton diner", SampleRegion.Origin);
        var diner = byName.First(r => r.Kind == ResultKind.Business);
        Assert.StartsWith("Sampleton Diner", diner.Name);
        Assert.Contains("Restaurant", diner.Detail);
        var details = _f.Search.GetBusiness(diner.PoiId!.Value)!;
        Assert.Equal("Mo-Su 06:00-21:00", details.Hours);

        // Misspelt name.
        Assert.Contains(_f.Search.Search("valey foods", SampleRegion.Origin), r => r.Kind == ResultKind.Business && r.Name.StartsWith("Valley Foods"));
        // By type, singular or plural.
        Assert.Contains(_f.Search.Search("restaurants", SampleRegion.Origin), r => r.Kind == ResultKind.Business && r.Name.StartsWith("Sampleton Diner"));
        Assert.Contains(_f.Search.Search("grocery", SampleRegion.Origin), r => r.Name.StartsWith("Valley Foods"));
        Assert.Contains(_f.Search.Search("motel", SampleRegion.Origin), r => r.Name.StartsWith("Riverside Motel"));
        Assert.Null(_f.Search.GetBusiness(999_999));
    }

    [Fact]
    public void Event_layer_search_ranks_names_then_folders_attributes_and_descriptions()
    {
        var content = new MapFileContent { Name = "Festival" };
        OverlayFeature P(string name, string folder = "", string description = "", Dictionary<string, string>? props = null) =>
            new() { Name = name, Folder = folder, Description = description, Geometry = OverlayGeometry.Point, Parts = { new List<GeoPoint> { SampleRegion.At(0, 0) } }, Properties = props ?? new() };
        content.Features.Add(P("Medic 2"));
        content.Features.Add(P("Medic tent 12"));
        content.Features.Add(P("Gate C", "Gates"));
        content.Features.Add(P("Post 7", "First aid posts"));
        content.Features.Add(P("Water station", description: "Near the medic tent"));
        content.Features.Add(P("Stage", props: new() { ["radio"] = "Channel MEDIC" }));
        var layer = new OverlayLayer(new OverlayLayerInfo { Name = "Festival" }, content);

        var medic = OverlaySearch.Search(new[] { layer }, "medic");
        Assert.Equal(new[] { "Medic 2", "Medic tent 12" }, medic.Take(2).Select(m => m.Feature.Name).OrderBy(n => n).ToArray());
        Assert.Contains(medic, m => m.Feature.Name == "Stage" && m.MatchedIn == "radio");
        Assert.Contains(medic, m => m.Feature.Name == "Water station" && m.MatchedIn == "description");
        Assert.True(medic.First(m => m.Feature.Name == "Medic 2").Score > medic.First(m => m.Feature.Name == "Stage").Score);

        Assert.Equal("Post 7", OverlaySearch.Search(new[] { layer }, "first aid").First().Feature.Name);
        Assert.Equal("Gate C", OverlaySearch.Search(new[] { layer }, "gate c").First().Feature.Name);
        Assert.Equal("Medic 2", OverlaySearch.Search(new[] { layer }, "medik 2").First().Feature.Name); // close spelling
        Assert.Empty(OverlaySearch.Search(new[] { layer }, "helicopter"));
        Assert.Empty(OverlaySearch.Search(new[] { layer }, "  "));
    }
}
