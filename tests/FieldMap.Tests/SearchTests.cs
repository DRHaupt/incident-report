using FieldMap.Core.Geo;
using FieldMap.Core.Sample;
using FieldMap.Core.Search;

namespace FieldMap.Tests;

[Collection("sample")]
public class SearchTests
{
    private readonly SampleFixture _f;

    public SearchTests(SampleFixture f) => _f = f;

    private SearchResult Top(string q) => _f.Search.Search(q, SampleRegion.Origin).First();

    [Theory]
    [InlineData("Main St & 5th Ave")]
    [InlineData("main and 5th")]
    [InlineData("5th Avenue / Main Street")]
    [InlineData("Mian St & Fifth Ave")]
    public void Intersections(string q)
    {
        var r = Top(q);
        Assert.Equal(ResultKind.Intersection, r.Kind);
        Assert.Contains("Main Street", r.Name);
        Assert.Contains("5th Avenue", r.Name);
        Assert.True(r.Point.DistanceTo(SampleRegion.Intersection(4, 4)) < 5);
    }

    [Theory]
    [InlineData("Sampleton General Hospital")]
    [InlineData("sampleton genral hosptal")]
    [InlineData("Sampleton Gen Hosp")]
    [InlineData("SAMPLETON GENERAL")]
    public void Facility_names_tolerate_typos_and_abbreviations(string q)
    {
        var r = Top(q);
        Assert.Equal(ResultKind.Poi, r.Kind);
        Assert.Equal("Sampleton General Hospital (SAMPLE)", r.Name);
        Assert.Equal("hospital", r.Layer);
    }

    [Fact]
    public void Places_and_streets()
    {
        Assert.Equal("Sampleton", Top("Sampleton").Name);
        Assert.Equal("Sampleton", Top("Samplton").Name);
        var street = _f.Search.Search("Hemlock St", SampleRegion.Origin).First(r => r.Kind == ResultKind.Street);
        Assert.Equal("Hemlock Street", street.Name);
        Assert.Contains("Sampleton", street.Detail);
        Assert.Equal("Sample Creek FSR", Top("sample creek forest service road").Name);
    }

    [Fact]
    public void Addresses_exact_and_interpolated()
    {
        var exact = Top("150 Main St");
        Assert.Equal(ResultKind.Address, exact.Kind);
        Assert.Equal("150 Main Street", exact.Name);
        Assert.False(exact.Approximate);
        var approx = Top("155 main street, sampleton");
        Assert.Equal(ResultKind.Address, approx.Kind);
        Assert.True(approx.Approximate);
        var a150 = exact.Point;
        Assert.True(approx.Point.DistanceTo(a150) < 40);
    }

    [Fact]
    public void Highway_km_markers_and_exits()
    {
        var km = Top("Hwy 901 km 106");
        Assert.Equal(ResultKind.KmMarker, km.Kind);
        Assert.False(km.Approximate);
        Assert.True(km.Point.DistanceTo(SampleRegion.At(0, -412)) < 5);
        var between = Top("highway 901 km 106.5");
        Assert.True(between.Approximate);
        Assert.True(between.Point.DistanceTo(SampleRegion.At(500, -412)) < 10);
        Assert.Equal(ResultKind.KmMarker, Top("km 110 hwy 901").Kind);
        var exit = Top("exit 106");
        Assert.Equal(ResultKind.Exit, exit.Kind);
        Assert.Contains("Sampleton", exit.Name);
    }

    [Fact]
    public void Coordinates_in_every_format()
    {
        var h = SampleRegion.HospitalPoint;
        foreach (var q in new[]
                 {
                     CoordinateFormat.Decimal(h.Lat, h.Lon),
                     CoordinateFormat.Dms(h.Lat, h.Lon),
                     CoordinateFormat.Ddm(h.Lat, h.Lon),
                     CoordinateFormat.Mgrs(h.Lat, h.Lon),
                     CoordinateFormat.Utm(h.Lat, h.Lon)
                 })
        {
            var r = Top(q);
            Assert.Equal(ResultKind.Coordinate, r.Kind);
            Assert.True(r.Point.DistanceTo(h) < 3, $"{q} → {r.Point}");
        }
    }

    [Fact]
    public void Category_words_offer_nearest_by_layer()
    {
        foreach (var (q, layer) in new[] { ("fire hall", "fire"), ("AED", "aed"), ("nearest hospital", "hospital"), ("defib", "aed"), ("hydrant", "water") })
        {
            var r = _f.Search.Search(q, SampleRegion.Origin).First(x => x.Kind == ResultKind.Category);
            Assert.Equal(layer, r.Layer);
        }
        var aeds = _f.Search.NearestPois("aed", SampleRegion.Origin, 3);
        Assert.Equal(3, aeds.Count);
        Assert.True(aeds[0].Point.DistanceTo(SampleRegion.Origin) <= aeds[1].Point.DistanceTo(SampleRegion.Origin));
    }

    [Fact]
    public void Whats_here_gives_address_intersection_road_and_locality()
    {
        var at150 = Top("150 Main St").Point;
        var here = _f.Search.Reverse(new GeoPoint(at150.Lat + 0.00005, at150.Lon));
        Assert.Equal("150 Main Street, Sampleton", here.Address);
        Assert.Equal("Main Street", here.Road);
        Assert.Equal("Sampleton", here.Locality);
        Assert.StartsWith("150 Main Street", here.Summary);

        var corner = _f.Search.Reverse(SampleRegion.Intersection(4, 4));
        Assert.Contains("Main Street", corner.Intersection);
        Assert.Contains("5th Avenue", corner.Intersection);
        Assert.True(corner.IntersectionDistance < 1);

        var wild = _f.Search.Reverse(SampleRegion.At(-3000, 3000));
        Assert.Null(wild.Address);
        Assert.NotNull(wild.Road);
    }
}

public class NormalizerAndParserTests
{
    [Theory]
    [InlineData("Lougheed Hwy", "lougheed highway")]
    [InlineData("LOUGHEED HIGHWAY", "lougheed highway")]
    [InlineData("St. Paul's Hospital", "saint pauls hospital")]
    [InlineData("Main St", "main street")]
    [InlineData("Deerfoot Trl NE", "deerfoot trail northeast")]
    [InlineData("Ft St John", "fort street john")]
    [InlineData("Montréal Ave", "montreal avenue")]
    [InlineData("Sample Creek FSR", "sample creek forest service road")]
    [InlineData("Foothills Medical Center", "foothills medical centre")]
    [InlineData("Fire Hall No. 1", "fire hall number 1")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Fact]
    public void Edit_distance_handles_transpositions() => Assert.Equal(1, TextNormalizer.EditDistance("kamloosp", "kamloops"));

    [Theory]
    [InlineData("Hwy 97 km 120", "97", 120)]
    [InlineData("highway 1 km 55.5", "1", 55.5)]
    [InlineData("97 km 12", "97", 12)]
    [InlineData("km 120 on hwy 5", "5", 120)]
    [InlineData("hwy 16A km 3", "16A", 3)]
    public void Highway_km(string q, string hwy, double km)
    {
        Assert.True(QueryParser.TryHighwayKm(q, out var h, out var k));
        Assert.Equal(hwy, h);
        Assert.Equal(km, k);
    }

    [Theory]
    [InlineData("150 Main St", "150", "Main St")]
    [InlineData("1201A Lougheed Hwy, Coquitlam", "1201A", "Lougheed Hwy")]
    [InlineData("#3-150 Main", "150", "Main")]
    public void Addresses(string q, string number, string street)
    {
        Assert.True(QueryParser.TryAddress(q, out var n, out var s));
        Assert.Equal(number, n);
        Assert.Equal(street, s);
    }

    [Theory]
    [InlineData("5th Ave")]
    [InlineData("1st Avenue")]
    public void Ordinal_streets_are_not_addresses(string q) => Assert.False(QueryParser.TryAddress(q, out _, out _));

    [Fact]
    public void Utm_round_trip()
    {
        Assert.True(QueryParser.TryUtm("10U 500000 5600000", out var p));
        var back = Utm.FromLatLon(p.Lat, p.Lon);
        Assert.Equal(10, back.Zone);
        Assert.InRange(back.Easting, 499999, 500001);
        Assert.InRange(back.Northing, 5599999, 5600001);
    }
}

[Collection("sample")]
public class MultiRegionSearchTests
{
    private readonly SampleFixture _f;

    public MultiRegionSearchTests(SampleFixture f) => _f = f;

    [Fact]
    public void Facility_ids_stay_unique_with_several_regions()
    {
        // BC and Alberta each number their facilities from 1. Loading the sample twice gives the same overlap.
        using var two = new SearchEngine();
        var db = Path.Combine(_f.Dir, FieldMap.Core.Data.RegionBuilder.SearchFile);
        two.AddRegion(db, null);
        two.AddRegion(db, null);
        var pois = two.LoadPois();
        Assert.Equal(pois.Count, pois.Select(p => p.Id).Distinct().Count());

        // A search hit's id must lead back to that same facility (identical hits in both copies are merged into one).
        var hits = two.Search("Sampleton General Hospital", SampleRegion.Origin).Where(r => r.Kind == ResultKind.Poi).ToList();
        Assert.NotEmpty(hits);
        foreach (var h in hits)
            Assert.Contains(pois, p => p.Id == h.PoiId && p.Name == h.Name);
        Assert.Contains(two.NearestPois("aed", SampleRegion.Origin, 10), p => p.Id >= 1_000_000_000L);
    }
}
