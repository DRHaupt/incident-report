using System.Buffers.Binary;
using FieldMap.Core.Geo;
using FieldMap.Core.Terrain;

namespace FieldMap.Tests;

/// <summary>
/// Elevation: EPSG:3979 projection, TIFF LZW, reading a tiled GeoTIFF with overviews and extracting a region from it.
/// (Checked once against the real NRCan file: Calgary downtown 1048 m, Nose Hill 1217 m, English Bay 0 m.)
/// </summary>
public sealed class TerrainTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-terrain-" + Guid.NewGuid().ToString("N"));

    public TerrainTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(49.0, -95.0, 0, 0)]
    [InlineData(51.0447, -114.0719, double.NaN, double.NaN)]
    [InlineData(59.9, -138.9, double.NaN, double.NaN)]
    [InlineData(49.0, -123.0, double.NaN, double.NaN)]
    public void Lambert_projection_round_trips(double lat, double lon, double ex, double ey)
    {
        var (x, y) = CanadaAtlasLambert.Forward(lat, lon);
        if (!double.IsNaN(ex))
        {
            Assert.Equal(ex, x, 3);
            Assert.Equal(ey, y, 3);
        }
        var (lat2, lon2) = CanadaAtlasLambert.Inverse(x, y);
        Assert.Equal(lat, lat2, 8);
        Assert.Equal(lon, lon2, 8);
        // West of the central meridian is negative X; north of 49° is positive Y.
        if (lon < -95)
            Assert.True(x < 0);
    }

    [Fact]
    public void Lzw_round_trips_including_repeats_and_table_resets()
    {
        var rnd = new Random(3);
        foreach (var data in new[]
                 {
                     new byte[] { 1, 2, 3 },
                     Enumerable.Repeat((byte)7, 100_000).ToArray(),
                     Enumerable.Range(0, 300_000).Select(_ => (byte)rnd.Next(256)).ToArray(),
                     Enumerable.Range(0, 300_000).Select(i => (byte)(i / 50 % 7)).ToArray()
                 })
            Assert.Equal(data, TiffLzw.Decode(LzwEncode(data), data.Length));
    }

    [Fact]
    public async Task Extracts_a_region_from_a_tiled_geotiff_and_answers_elevations()
    {
        // A 256×256 grid of 64 m cells (16 m full-resolution grid with an overview), elevation = 100 + col + 2*row,
        // centred on Calgary. The overview (64 m) is what extraction at 64 m must pick.
        var (cx, cy) = CanadaAtlasLambert.Forward(51.0447, -114.0719);
        double ox = Math.Floor(cx / 64) * 64 - 128 * 64, oy = Math.Floor(cy / 64) * 64 + 128 * 64;
        var path = Path.Combine(_dir, "dem.tif");
        WriteGeoTiff(path, ox, oy, 64, 256, 256, (c, r) => (short)(100 + c + 2 * r));

        var output = Path.Combine(_dir, TerrainGrid.FileName);
        var reports = new List<double>();
        var bytes = await TerrainExtractor.ExtractAsync(new FileRangeSource(path), new GeoBounds(51.02, -114.10, 51.06, -114.04), 64, output, new Sync(reports.Add));
        Assert.True(bytes > 0);
        Assert.Equal(1.0, reports[^1], 3);

        using var grid = TerrainGrid.Open(output);
        Assert.Equal(64, grid.PixelSize, 6);
        // Exactly at a cell centre the value is the cell's.
        var (lat, lon) = CanadaAtlasLambert.Inverse(ox + 64 * 100.5, oy - 64 * 50.5);
        Assert.Equal(100 + 100 + 2 * 50, grid.Elevation(lat, lon)!.Value, 1);
        // Between cells it interpolates.
        (lat, lon) = CanadaAtlasLambert.Inverse(ox + 64 * 101, oy - 64 * 50.5);
        Assert.Equal(300.5, grid.Elevation(lat, lon)!.Value, 1);
        // No-data cells (the corner the writer leaves empty) give null.
        (lat, lon) = CanadaAtlasLambert.Inverse(ox + 64 * 2.5, oy - 64 * 2.5);
        Assert.Null(grid.Elevation(lat, lon));
        // Far outside the grid: null.
        Assert.Null(grid.Elevation(55, -120));
    }

    // ------------------------------------------------------------------ helpers

    private sealed class Sync : IProgress<double>
    {
        private readonly Action<double> _a;
        public Sync(Action<double> a) => _a = a;
        public void Report(double v) => _a(v);
    }

    /// <summary>TIFF LZW encoder (for the tests): MSB-first, early change, clear when the table fills.</summary>
    internal static byte[] LzwEncode(byte[] data)
    {
        var output = new List<byte>();
        long buf = 0;
        var bits = 0;
        var width = 9;
        void Put(int code)
        {
            buf = (buf << width) | (uint)code;
            bits += width;
            while (bits >= 8)
            {
                output.Add((byte)(buf >> (bits - 8)));
                bits -= 8;
            }
        }
        var table = new Dictionary<(int, byte), int>();
        var next = 258;
        Put(256);
        var w = -1;
        foreach (var b in data)
        {
            if (w < 0)
            {
                w = b;
                continue;
            }
            if (table.TryGetValue((w, b), out var code))
            {
                w = code;
                continue;
            }
            Put(w);
            table[(w, b)] = next++;
            // The decoder adds each entry one code later than the encoder, so the encoder widens one entry later too.
            if (next == 512 || next == 1024 || next == 2048)
                width++;
            if (next == 4094)
            {
                Put(256);
                table.Clear();
                next = 258;
                width = 9;
            }
            w = b;
        }
        if (w >= 0)
            Put(w);
        Put(257);
        if (bits > 0)
            output.Add((byte)(buf << (8 - bits)));
        return output.ToArray();
    }

    /// <summary>
    /// Writes a little-endian classic TIFF: a full-resolution image (pixel/4 size) and one overview at
    /// <paramref name="pixel"/>, 64×64 tiles, LZW, 16-bit signed, EPSG:3979, nodata −32767 in the top-left 4×4 cells.
    /// </summary>
    private static void WriteGeoTiff(string path, double originX, double originY, double pixel, int width, int height, Func<int, int, short> value)
    {
        const int tile = 64;
        var images = new[] { (W: width * 4, H: height * 4, Scale: 4), (W: width, H: height, Scale: 1) };
        using var f = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(f);
        w.Write((byte)'I');
        w.Write((byte)'I');
        w.Write((ushort)42);
        w.Write(0u); // first IFD, patched
        // Tile data first.
        var tileInfo = new List<(long[] Offsets, long[] Counts)>();
        foreach (var im in images)
        {
            int across = (im.W + tile - 1) / tile, down = (im.H + tile - 1) / tile;
            var offsets = new long[across * down];
            var counts = new long[across * down];
            for (var tr = 0; tr < down; tr++)
                for (var tc = 0; tc < across; tc++)
                {
                    var raw = new byte[tile * tile * 2];
                    for (var y = 0; y < tile; y++)
                        for (var x = 0; x < tile; x++)
                        {
                            int px = tc * tile + x, py = tr * tile + y;
                            int c = px / im.Scale, r = py / im.Scale;
                            short v = px >= im.W || py >= im.H ? (short)0 : c < 4 && r < 4 ? (short)-32767 : value(c, r);
                            BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan((y * tile + x) * 2), v);
                        }
                    var enc = LzwEncode(raw);
                    offsets[tr * across + tc] = f.Position;
                    counts[tr * across + tc] = enc.Length;
                    w.Write(enc);
                }
            tileInfo.Add((offsets, counts));
        }
        // IFDs.
        var ifdPositions = new List<long>();
        var nextPatch = 4L;
        for (var i = 0; i < images.Length; i++)
        {
            var im = images[i];
            var extra = new MemoryStream();
            var ew = new BinaryWriter(extra);
            var entries = new List<(ushort Tag, ushort Type, uint Count, uint Value, byte[]? Data)>();
            void Short(ushort tag, ushort v) => entries.Add((tag, 3, 1, v, null));
            void Long(ushort tag, uint v) => entries.Add((tag, 4, 1, v, null));
            void Data(ushort tag, ushort type, uint count, byte[] data) => entries.Add((tag, type, count, 0, data));
            if (i > 0)
                Long(254, 1);
            Long(256, (uint)im.W);
            Long(257, (uint)im.H);
            Short(258, 16);
            Short(259, 5);
            Short(262, 1);
            Short(277, 1);
            Short(322, tile);
            Short(323, tile);
            var (offs, cnts) = tileInfo[i];
            Data(324, 4, (uint)offs.Length, offs.SelectMany(o => BitConverter.GetBytes((uint)o)).ToArray());
            Data(325, 4, (uint)cnts.Length, cnts.SelectMany(o => BitConverter.GetBytes((uint)o)).ToArray());
            Short(339, 2);
            if (i == 0)
            {
                var px = pixel / 4;
                Data(33550, 12, 3, new[] { px, px, 0.0 }.SelectMany(BitConverter.GetBytes).ToArray());
                Data(33922, 12, 6, new[] { 0, 0, 0, originX, originY, 0.0 }.SelectMany(BitConverter.GetBytes).ToArray());
                Data(34735, 3, 8, new ushort[] { 1, 1, 0, 1, 3072, 0, 1, 3979 }.SelectMany(BitConverter.GetBytes).ToArray());
            }
            Data(42113, 2, 7, System.Text.Encoding.ASCII.GetBytes("-32767\0"));
            entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

            var ifdAt = f.Position;
            ifdPositions.Add(ifdAt);
            var dataAt = ifdAt + 2 + entries.Count * 12 + 4;
            w.Write((ushort)entries.Count);
            var blobs = new MemoryStream();
            foreach (var e in entries)
            {
                w.Write(e.Tag);
                w.Write(e.Type);
                w.Write(e.Count);
                if (e.Data == null)
                    w.Write(e.Value);
                else if (e.Data.Length <= 4)
                    w.Write(e.Data.Concat(new byte[4 - e.Data.Length]).ToArray());
                else
                {
                    w.Write((uint)(dataAt + blobs.Length));
                    blobs.Write(e.Data);
                    if (blobs.Length % 2 == 1)
                        blobs.WriteByte(0);
                }
            }
            var nextAt = f.Position;
            w.Write(0u);
            w.Write(blobs.ToArray());
            var end = f.Position;
            f.Position = nextPatch;
            w.Write((uint)ifdAt);
            f.Position = end;
            nextPatch = nextAt;
        }
    }
}

public sealed class RadioCoverageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-coverage-" + Guid.NewGuid().ToString("N"));
    private static readonly GeoPoint Site = new(51.0, -116.0);

    public RadioCoverageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A 64 m grid, 2000×2000 cells (128 km) centred on the site, elevation from a function of metres east/north.</summary>
    private TerrainGrid Grid(Func<double, double, short?> elevation)
    {
        var (sx, sy) = CanadaAtlasLambert.Forward(Site.Lat, Site.Lon);
        double ox = sx - 1000 * 64, oy = sy + 1000 * 64;
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".sqlite");
        TerrainGrid.Write(path, ox, oy, 64, 2000, 2000, (c, r) => elevation((c + 0.5) * 64 + ox - sx, sy - (oy - (r + 0.5) * 64)));
        return TerrainGrid.Open(path);
    }

    private static GeoPoint At(double bearing, double km) => Geometry.Destination(Site, bearing, km * 1000);

    [Fact]
    public void Knife_edge_loss_matches_the_standard_curve()
    {
        Assert.Equal(0, RadioCoverage.KnifeEdgeLossDb(-1), 6);
        Assert.Equal(6.0, RadioCoverage.KnifeEdgeLossDb(0), 0);   // grazing: ~6 dB
        Assert.Equal(13.5, RadioCoverage.KnifeEdgeLossDb(1), 0);  // ν = 1: ~13–14 dB
        Assert.InRange(RadioCoverage.KnifeEdgeLossDb(2.4), 19.5, 21.5); // ~20.5 dB
    }

    [Fact]
    public void Ridge_shadows_the_far_side_but_not_the_open_side()
    {
        // Flat 1000 m plateau with a 600 m high north-south ridge 5 km east of the site.
        using var grid = Grid((east, north) => (short)(Math.Abs(east - 5000) < 200 ? 1600 : 1000));
        var req = CoverageRequest.Handheld(Site.Lat, Site.Lon) with { RadiusKm = 30, ErpWatts = 50, AntennaHeightM = 15 };
        var result = RadioCoverage.Compute(grid, req);

        Assert.Equal(1000, result.SiteElevationM!.Value, 0);
        Assert.Equal(CoverageClass.Likely, result.ClassAt(At(270, 10).Lat, At(270, 10).Lon)); // open side, 10 km
        Assert.Equal(CoverageClass.Likely, result.ClassAt(At(90, 3).Lat, At(90, 3).Lon));     // before the ridge
        Assert.Equal(CoverageClass.None, result.ClassAt(At(90, 10).Lat, At(90, 10).Lon));     // behind it, same distance
        Assert.Equal(CoverageClass.Outside, result.ClassAt(At(0, 40).Lat, At(0, 40).Lon));
        Assert.InRange(result.LikelyFraction, 0.15, 0.8); // open half mostly covered, shadowed half not
    }

    [Fact]
    public void Flat_ground_fades_out_beyond_the_radio_horizon()
    {
        // Sea-level plain; 30 m antenna and 1.5 m handheld: radio horizon ≈ 4.12 (√30 + √1.5) ≈ 27.6 km.
        using var grid = Grid((_, _) => 0);
        var req = CoverageRequest.Handheld(Site.Lat, Site.Lon) with { RadiusKm = 60, ErpWatts = 100, AntennaHeightM = 30 };
        var result = RadioCoverage.Compute(grid, req);
        Assert.Equal(CoverageClass.Likely, result.ClassAt(At(45, 8).Lat, At(45, 8).Lon));
        Assert.Equal(CoverageClass.None, result.ClassAt(At(45, 55).Lat, At(45, 55).Lon));
        // Coverage is the same in every direction on flat ground.
        foreach (var bearing in new[] { 0, 90, 180, 270 })
            Assert.Equal(result.ClassAt(At(45, 8).Lat, At(45, 8).Lon), result.ClassAt(At(bearing, 8).Lat, At(bearing, 8).Lon));
    }

    [Fact]
    public void More_power_or_a_vehicle_radio_reaches_further()
    {
        using var grid = Grid((east, north) => (short)(1000 + 150 * Math.Sin(east / 3000) * Math.Cos(north / 4000)));
        var low = RadioCoverage.Compute(grid, CoverageRequest.Handheld(Site.Lat, Site.Lon) with { ErpWatts = 5, RadiusKm = 40 });
        var high = RadioCoverage.Compute(grid, CoverageRequest.Handheld(Site.Lat, Site.Lon) with { ErpWatts = 200, RadiusKm = 40 });
        var vehicle = RadioCoverage.Compute(grid, CoverageRequest.Vehicle(Site.Lat, Site.Lon) with { ErpWatts = 5, RadiusKm = 40 });
        Assert.True(high.LikelyFraction > low.LikelyFraction);
        Assert.True(vehicle.LikelyFraction + vehicle.MarginalFraction > low.LikelyFraction + low.MarginalFraction);
    }
}
