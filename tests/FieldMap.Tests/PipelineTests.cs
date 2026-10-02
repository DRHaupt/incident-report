using FieldMap.Core.Build;
using FieldMap.Core.Data;
using FieldMap.Core.Layers;
using FieldMap.Core.Sample;
using FieldMap.Core.Tiles;

namespace FieldMap.Tests;

[Collection("sample")]
public class PipelineTests
{
    private readonly SampleFixture _f;

    public PipelineTests(SampleFixture f) => _f = f;

    [Fact]
    public void Sample_region_builds_and_validates()
    {
        var report = RegionValidator.Validate(_f.Dir);
        Assert.True(report.Ok, report.ToString());
        Assert.True(_f.Manifest.IsSample);
        Assert.Contains(_f.Manifest.Files, f => f.Path == RegionBuilder.TilesFile);
        Assert.Contains(_f.Manifest.Files, f => f.Path == RegionBuilder.GraphFile);
        Assert.Contains(_f.Manifest.Files, f => f.Path == RegionBuilder.SearchFile);
        Assert.NotEmpty(report.SpotChecks);
    }

    [Fact]
    public void Manifest_records_layers_sources_and_counts()
    {
        var m = _f.Manifest;
        Assert.Equal(4, m.Layers[LayerCatalog.Aed].Count);
        Assert.Equal(2, m.Layers[LayerCatalog.Hospital].Count);
        Assert.True(m.Layers[LayerCatalog.Water].Count >= 17); // 16 hydrants + tank
        Assert.All(LayerCatalog.All, l => Assert.True(m.Layers.ContainsKey(l.Id), l.Id));
        Assert.All(m.Layers.Values, l => Assert.NotEmpty(l.Sources));
        Assert.Single(m.Sources);
        Assert.True(m.Sources[0].IsSample);
        Assert.NotNull(m.DataDate);
        Assert.InRange(m.MinLat, 57.3, 57.5);
    }

    [Fact]
    public void Tiles_contain_the_expected_layers_at_town_zoom()
    {
        using var tiles = new MbTilesReader(Path.Combine(_f.Dir, RegionBuilder.TilesFile));
        var (x, y) = FieldMap.Core.Geo.WebMercator.Tile(SampleRegion.Origin.Lat, SampleRegion.Origin.Lon, 14);
        var tile = tiles.Get(14, x, y);
        Assert.NotNull(tile);
        var roads = tile!.Layer(TileSchema.Roads);
        Assert.NotNull(roads);
        Assert.Contains(roads!.Features, f => f.Str("name") == "Main Street");
        Assert.Contains(tile.Layers, l => l.Name == TileSchema.Landuse);
        // Highway visible at low zoom, residential streets not.
        var (x7, y7) = FieldMap.Core.Geo.WebMercator.Tile(SampleRegion.Origin.Lat, SampleRegion.Origin.Lon, 7);
        var low = tiles.Get(7, x7, y7)!;
        Assert.Contains(low.Layer(TileSchema.Roads)!.Features, f => f.Str("ref") == "901");
        Assert.DoesNotContain(low.Layer(TileSchema.Roads)!.Features, f => f.Str("class") == "minor");
        Assert.Equal(TileSchema.MaxZoom, tiles.MaxZoom);
    }

    [Fact]
    public void Multipolygon_relations_are_assembled_with_holes()
    {
        var path = Path.Combine(_f.Root, "mp.osm");
        SampleRegion.Write(path);
        var data = OsmExtractor.Extract(path);
        var marsh = data.Landuse.Single(a => a.Name == "Sample Marsh");
        Assert.Equal("wetland", marsh.Kind);
        Assert.Single(marsh.Outers);
        Assert.Single(marsh.Inners);
        Assert.True(marsh.AreaM2 > 300_000);
    }
}
