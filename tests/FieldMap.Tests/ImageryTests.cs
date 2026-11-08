using FieldMap.Core.Geo;
using FieldMap.Core.Imagery;

namespace FieldMap.Tests;

public class ImageryTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46 };

    private static string TempFile(string name) => Path.Combine(Path.GetTempPath(), "fieldmap-imagery-" + Guid.NewGuid().ToString("N")[..8], name);

    [Fact]
    public void BuildsXyzAndWmsRequests()
    {
        var xyz = new ImagerySource { Kind = "xyz", Url = "https://example.test/{z}/{y}/{x}.jpg?token={key}" };
        Assert.Equal("https://example.test/12/1400/650.jpg?token=a%20b", xyz.TileUrl(12, 650, 1400, "a b"));
        var tms = new ImagerySource { Kind = "xyz", Url = "https://example.test/{z}/{x}/{-y}.png" };
        Assert.Equal("https://example.test/1/0/1.png", tms.TileUrl(1, 0, 0));

        var wms = new ImagerySource { Kind = "wms", Url = "https://example.test/wms", Layers = "ortho 2024", Format = "image/jpeg" };
        var url = wms.TileUrl(0, 0, 0);
        Assert.StartsWith("https://example.test/wms?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap&LAYERS=ortho%202024&STYLES=&SRS=EPSG:3857", url);
        Assert.Contains("BBOX=-20037508.343,-20037508.343,20037508.343,20037508.343", url);
        Assert.Contains("WIDTH=256&HEIGHT=256&FORMAT=image%2Fjpeg", url);
        var withQuery = new ImagerySource { Kind = "wms", Url = "https://example.test/wms?map=x", Layers = "a" };
        Assert.StartsWith("https://example.test/wms?map=x&SERVICE=WMS", withQuery.TileUrl(3, 1, 2));
    }

    [Fact]
    public void TileMathCoversTheArea()
    {
        // Downtown Vancouver at zoom 15.
        var area = new GeoBounds(49.27, -123.13, 49.29, -123.10);
        var tiles = ImageryMath.Tiles(area, 15).ToList();
        Assert.Equal(tiles.Count, ImageryMath.CountTiles(area, 15, 15));
        Assert.All(tiles, t => Assert.True(ImageryMath.TileBounds(t.Z, t.X, t.Y).Intersects(area)));
        Assert.Equal(1, ImageryMath.CountTiles(area, 0, 0));
        var b = ImageryMath.TileMercatorBounds(1, 1, 0);
        Assert.Equal(0, b.MinX, 3);
        Assert.Equal(0, b.MinY, 3);
        var limited = new ImagerySource { MinZoom = 10, MaxZoom = 14, Bounds = new[] { -124.0, 49.0, -122.0, 50.0 } };
        Assert.True(limited.Covers(15 - 1, tiles[0].X / 2, tiles[0].Y / 2));
        Assert.False(limited.Covers(15, tiles[0].X, tiles[0].Y));
        Assert.False(limited.Covers(12, 0, 0));
    }

    [Fact]
    public void RecognisesImagesAndRejectsErrorPages()
    {
        Assert.True(ImageryMath.IsImage(Png));
        Assert.True(ImageryMath.IsImage(Jpeg));
        Assert.False(ImageryMath.IsImage("<html>Service unavailable</html>"u8.ToArray()));
        Assert.False(ImageryMath.IsImage("<?xml version=\"1.0\"?><ServiceExceptionReport/>"u8.ToArray()));
    }

    [Fact]
    public void StoreRoundTripsTrimsAndDescribesItself()
    {
        var path = TempFile("area-saved.mbtiles");
        using (var store = new RasterTileStore(path))
        {
            for (var i = 0; i < 10; i++)
                store.Put(14, 2600 + i, 5600, Jpeg);
            store.Put(12, 650, 1400, Png);
            store.SetMetadata(new Dictionary<string, string> { ["name"] = "Staging area", ["attribution"] = "Imagery © Test", ["bounds"] = "-123.2,49.2,-123.0,49.3" });
            Assert.Equal(11, store.Count());
            Assert.Equal(Png, store.Get(12, 650, 1400));
            Assert.Null(store.Get(12, 651, 1400));
            Assert.True(store.Has(14, 2600, 5600));
            Assert.Equal(3, store.TrimTo(8));
            Assert.False(store.Has(14, 2600, 5600));
            Assert.Equal((12, 14), store.ZoomRange());
        }
        var d = RasterTileStore.Describe(path);
        Assert.Equal("Staging area (saved areas)", d.Name);
        Assert.Equal("mbtiles", d.Kind);
        Assert.Equal(12, d.MinZoom);
        Assert.Equal(14, d.MaxZoom);
        Assert.Equal("Imagery © Test", d.Attribution);
        Assert.Equal(new[] { -123.2, 49.2, -123.0, 49.3 }, d.Bounds);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public async Task SeedsAnAreaResumesAndCountsMissingTiles()
    {
        using var server = new TestHttpServer();
        var area = new GeoBounds(49.27, -123.13, 49.28, -123.12);
        var tiles = Enumerable.Range(13, 3).SelectMany(z => ImageryMath.Tiles(area, z)).ToList();
        // One tile missing (404) and one answering with an error page instead of an image.
        foreach (var t in tiles.Skip(2))
            server.Serve($"t/{t.Z}/{t.X}/{t.Y}.jpg", Jpeg);
        server.Serve($"t/{tiles[1].Z}/{tiles[1].X}/{tiles[1].Y}.jpg", "<html>error</html>"u8.ToArray());
        var source = new ImagerySource { Id = "test", Name = "Test imagery", Kind = "xyz", Url = server.BaseUrl + "t/{z}/{x}/{y}.jpg", AllowOffline = true, MaxZoom = 15, Attribution = "Test" };
        var path = TempFile("seed.mbtiles");
        using var http = new HttpClient();
        var fetcher = new ImageryFetcher(http, "FieldMap-test");
        using (var store = new RasterTileStore(path))
        {
            var r = await fetcher.SeedAsync(source, area, 13, 15, store, "", null, CancellationToken.None);
            Assert.Equal(tiles.Count, r.Total);
            Assert.Equal(tiles.Count - 2, r.Fetched);
            Assert.Equal(2, r.Failed);
            Assert.Equal(tiles.Count - 2, store.Count());
            // Run again: everything already saved is skipped.
            var again = await fetcher.SeedAsync(source, area, 13, 15, store, "", null, CancellationToken.None);
            Assert.Equal(tiles.Count - 2, again.Skipped);
            Assert.Equal(0, again.Fetched);
            Assert.Equal("Test imagery", store.Metadata()["name"]);
        }
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public async Task RefusesSourcesThatDontAllowOfflineUseAndHugeAreas()
    {
        using var http = new HttpClient();
        var fetcher = new ImageryFetcher(http, "FieldMap-test");
        var path = TempFile("refuse.mbtiles");
        using (var store = new RasterTileStore(path))
        {
            var online = new ImagerySource { Name = "View only", Url = "http://127.0.0.1:9/{z}/{x}/{y}", AllowOffline = false };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fetcher.SeedAsync(online, new GeoBounds(49, -124, 50, -123), 10, 12, store, "", null, CancellationToken.None));
            var allowed = new ImagerySource { Name = "Allowed", Url = "http://127.0.0.1:9/{z}/{x}/{y}", AllowOffline = true };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => fetcher.SeedAsync(allowed, new GeoBounds(48, -139, 60, -110), 10, 17, store, "", null, CancellationToken.None));
            Assert.Contains("limit", ex.Message);
        }
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Shipped_catalogue_only_turns_on_view_only_sources_with_terms_and_credit()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "FieldMap", "Content", "imagery-sources.json");
        var catalog = ImageryCatalog.LoadOrEmpty(path);
        Assert.NotEmpty(catalog.Sources);
        Assert.All(catalog.Sources, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Licence));
            Assert.False(string.IsNullOrWhiteSpace(s.Terms));
            if (!s.Enabled)
                return;
            // Listed out of the box: shown online only (never saved offline), no key, and the terms are asked first.
            Assert.True(s.IsOnline, s.Id);
            Assert.False(s.AllowOffline, $"{s.Id} must not be kept offline by default");
            Assert.False(s.NeedsKey, s.Id);
            Assert.False(string.IsNullOrWhiteSpace(s.Attribution), s.Id);
            Assert.StartsWith("https://", s.Url);
        });
        Assert.Contains(catalog.Sources, s => s.Enabled && s.MaxZoom >= 18); // detailed imagery when online
    }
}
