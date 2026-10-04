using System.Diagnostics;
using FieldMap.Core.Build;
using FieldMap.Core.Geo;
using FieldMap.Core.Osm;
using FieldMap.Core.Tiles;

namespace FieldMap.Tests;

/// <summary>
/// Province-scale tile building: a long boundary line and a big detailed lake must not make every tile re-process
/// their whole geometry (which made a BC/Alberta build sit on "Building map tiles" for hours).
/// </summary>
public sealed class TileScaleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-tilescale-" + Guid.NewGuid().ToString("N"));

    public TileScaleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A wobbly closed ring around a centre: <paramref name="points"/> vertices, radius in degrees.</summary>
    private static GeoPoint[] Ring(double lat, double lon, double radiusDeg, int points)
    {
        var ring = new GeoPoint[points + 1];
        for (var i = 0; i < points; i++)
        {
            var a = 2 * Math.PI * i / points;
            var r = radiusDeg * (1 + 0.02 * Math.Sin(a * 97));
            ring[i] = new GeoPoint(lat + r * Math.Sin(a) * 0.6, lon + r * Math.Cos(a));
        }
        ring[points] = ring[0];
        return ring;
    }

    [Fact]
    public void Large_detailed_features_build_quickly_and_correctly()
    {
        var data = new MapData();
        var centre = new GeoPoint(53.0, -120.0);
        // A 'province boundary' 6 degrees across with 60,000 vertices.
        data.Boundaries.Add(new MapWay { Id = 1, Points = Ring(centre.Lat, centre.Lon, 3.0, 60_000), Tags = new OsmTags(new Dictionary<string, string> { ["admin_level"] = "4" }) });
        // A big lake with a detailed shore (20,000 vertices) and an island.
        var lake = new MapArea { Id = 2, Kind = "water", Name = "Big Lake", AreaM2 = 5e9 };
        lake.Outers.Add(Ring(centre.Lat, centre.Lon, 0.5, 20_000));
        lake.Inners.Add(Ring(centre.Lat + 0.1, centre.Lon + 0.1, 0.03, 400));
        data.Water.Add(lake);
        // A town's worth of streets away from the lake.
        var town = new GeoPoint(centre.Lat - 1.5, centre.Lon - 1.0);
        var id = 10;
        for (var i = 0; i < 60; i++)
            for (var j = 0; j < 60; j++)
            {
                var a = new GeoPoint(town.Lat + i * 0.002, town.Lon + j * 0.003);
                data.Roads.Add(new MapWay
                {
                    Id = id++, Points = new[] { a, new GeoPoint(a.Lat, a.Lon + 0.003), new GeoPoint(a.Lat + 0.002, a.Lon + 0.003) },
                    Tags = new OsmTags(new Dictionary<string, string> { ["highway"] = "residential", ["name"] = $"Street {i}" })
                });
            }
        data.Bounds = new GeoBounds(centre.Lat - 3, centre.Lon - 3.2, centre.Lat + 3, centre.Lon + 3.2);

        var path = Path.Combine(_dir, "tiles.mbtiles");
        var reports = new List<double>();
        var watch = Stopwatch.StartNew();
        var count = TileBuilder.BuildMbTiles(data, path, new Dictionary<string, string> { ["name"] = "scale" }, new SyncProgress(reports.Add));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(90), $"Tile build took {watch.Elapsed} for {count:N0} tiles");
        Assert.True(count > 1000, "tiles: " + count);
        // Progress moves steadily rather than sitting at 0 until the end.
        Assert.Equal(1.0, reports[^1], 3);
        Assert.True(reports.Count(r => r > 0 && r < 1) >= 5, "progress reports: " + reports.Count);
        Assert.True(reports.Zip(reports.Skip(1)).All(p => p.Second >= p.First), "progress went backwards");

        using var tiles = new MbTilesReader(path);
        VectorTile? At(GeoPoint p, int z = 14)
        {
            var (x, y) = WebMercator.Tile(p.Lat, p.Lon, z);
            return tiles.Get(z, x, y);
        }

        // Middle of the lake: the tile is all water.
        var mid = At(new GeoPoint(centre.Lat - 0.1, centre.Lon - 0.1));
        Assert.NotNull(mid);
        var water = Assert.Single(mid!.Layer(TileSchema.Water)!.Features);
        Assert.Equal("Big Lake", water.Str("name"));
        // On the island (hole): no water there.
        var island = At(new GeoPoint(centre.Lat + 0.1, centre.Lon + 0.1));
        Assert.True(island == null || island.Layer(TileSchema.Water) == null);
        // On the boundary line, east side.
        var east = At(new GeoPoint(centre.Lat, centre.Lon + 3.0 * (1 + 0.02 * Math.Sin(0))));
        Assert.NotNull(east?.Layer(TileSchema.Boundaries));
        // Inside the boundary but far from everything: no tile at z14.
        Assert.Null(At(new GeoPoint(centre.Lat + 1.5, centre.Lon + 1.5)));
        // The town's streets.
        var streets = At(new GeoPoint(town.Lat + 0.05, town.Lon + 0.05));
        Assert.Contains(streets!.Layer(TileSchema.Roads)!.Features, f => f.Str("class") == "minor");
        // Low zoom: one tile with the whole boundary and lake.
        var low = At(centre, 5)!;
        Assert.NotNull(low.Layer(TileSchema.Boundaries));
        Assert.NotNull(low.Layer(TileSchema.Water));
    }

    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _report;
        public SyncProgress(Action<double> report) => _report = report;
        public void Report(double value) => _report(value);
    }
}

public class LocalityIndexTests
{
    [Fact]
    public void Ring_search_matches_a_full_scan_and_respects_the_distance_limit()
    {
        var rnd = new Random(7);
        var places = new List<FieldMap.Core.Build.Place>();
        string[] kinds = { "city", "town", "village", "hamlet", "suburb" };
        for (var i = 0; i < 2000; i++)
            places.Add(new FieldMap.Core.Build.Place { Name = "P" + i, Kind = kinds[rnd.Next(kinds.Length)], Lat = 49 + rnd.NextDouble() * 3, Lon = -124 + rnd.NextDouble() * 4 });
        var index = new FieldMap.Core.Search.LocalityIndex(places);

        string? Brute(double lat, double lon, double maxKm)
        {
            FieldMap.Core.Build.Place? best = null;
            var bestScore = double.MaxValue;
            foreach (var p in places)
            {
                var d = Shared.Core.Geo.GeoMath.Distance(lat, lon, p.Lat, p.Lon) / 1000;
                var score = d * p.Rank switch { 1 => 0.5, 2 => 0.7, 3 => 0.85, _ => 1 };
                if (d <= maxKm && score < bestScore)
                {
                    best = p;
                    bestScore = score;
                }
            }
            return best?.Name;
        }

        for (var i = 0; i < 3000; i++)
        {
            var lat = 48.5 + rnd.NextDouble() * 4;
            var lon = -124.5 + rnd.NextDouble() * 5;
            var max = i % 3 == 0 ? 5 : 30;
            Assert.Equal(Brute(lat, lon, max), index.Nearest(lat, lon, max));
        }
        Assert.Null(index.Nearest(10, 10));
        Assert.Null(new FieldMap.Core.Search.LocalityIndex(Array.Empty<FieldMap.Core.Build.Place>()).Nearest(49, -123));
    }
}
