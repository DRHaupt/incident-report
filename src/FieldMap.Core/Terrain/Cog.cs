using System.Buffers.Binary;
using System.Net.Http.Headers;

namespace FieldMap.Core.Terrain;

/// <summary>Reads byte ranges of a file or a web resource (HTTP range requests).</summary>
public interface IRangeSource
{
    Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancel);
}

public sealed class FileRangeSource : IRangeSource
{
    private readonly string _path;
    public FileRangeSource(string path) => _path = path;

    public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancel)
    {
        await using var f = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        f.Position = offset;
        var buffer = new byte[(int)Math.Min(length, Math.Max(0, f.Length - offset))];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await f.ReadAsync(buffer.AsMemory(read), cancel);
            if (n == 0)
                break;
            read += n;
        }
        return buffer;
    }
}

public sealed class HttpRangeSource : IRangeSource
{
    private readonly HttpClient _http;
    private readonly Uri _url;

    public HttpRangeSource(HttpClient http, Uri url)
    {
        _http = http;
        _url = url;
    }

    public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancel)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, _url);
                req.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel);
                if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                    throw new IOException($"The elevation server answered {(int)resp.StatusCode} to a range request.");
                return await resp.Content.ReadAsByteArrayAsync(cancel);
            }
            catch (Exception ex) when (attempt < 4 && !cancel.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancel);
            }
        }
    }
}

/// <summary>One image (full resolution or an overview) of a tiled GeoTIFF.</summary>
public sealed class CogImage
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int TileWidth { get; init; }
    public int TileHeight { get; init; }
    public int Compression { get; init; }
    public int Predictor { get; init; } = 1;
    public int BitsPerSample { get; init; }
    public int SampleFormat { get; init; } = 1;
    public long TileOffsetsAt { get; init; }
    public long TileByteCountsAt { get; init; }
    public int TileOffsetType { get; init; }
    public int TileByteCountType { get; init; }
    public int TileCount { get; init; }
    public int TilesAcross => (Width + TileWidth - 1) / TileWidth;
    public int TilesDown => (Height + TileHeight - 1) / TileHeight;
}

/// <summary>
/// Minimal reader for cloud-optimised GeoTIFFs (classic or BigTIFF, little-endian, tiled, single band, 16-bit signed,
/// LZW or uncompressed) – the format of NRCan's Canadian Digital Elevation Model. Reads only the parts it needs, so a
/// 32 GB national file can be sampled over HTTP range requests.
/// </summary>
public sealed class CogFile
{
    private readonly IRangeSource _source;

    private CogFile(IRangeSource source) => _source = source;

    public List<CogImage> Images { get; } = new();
    /// <summary>Projected X/Y of the top-left corner of the full-resolution image, and its pixel size.</summary>
    public double OriginX { get; private set; }
    public double OriginY { get; private set; }
    public double PixelSize { get; private set; }
    public double? NoData { get; private set; }
    public int Epsg { get; private set; }

    public static async Task<CogFile> OpenAsync(IRangeSource source, CancellationToken cancel = default)
    {
        var cog = new CogFile(source);
        // COGs put every IFD in the first few hundred KB.
        var head = await source.ReadAsync(0, 1 << 20, cancel);
        if (head.Length < 16 || head[0] != 'I' || head[1] != 'I')
            throw new InvalidDataException("Not a little-endian TIFF.");
        var big = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2)) == 43;
        long ifd = big ? (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(8)) : BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        var first = true;
        while (ifd != 0)
        {
            if (ifd + 16 > head.Length)
                break; // overview IFDs beyond the header: the coarsest ones; not needed
            var entries = big ? (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan((int)ifd)) : BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan((int)ifd));
            var p = (int)ifd + (big ? 8 : 2);
            var size = big ? 20 : 12;
            var tags = new Dictionary<int, (int Type, long Count, long Value, int At)>();
            for (var i = 0; i < entries; i++, p += size)
            {
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(p));
                var type = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(p + 2));
                long count = big ? (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(p + 4)) : BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(p + 4));
                var valueAt = p + (big ? 12 : 8);
                long value = TypeSize(type) * count <= (big ? 8 : 4)
                    ? ReadInline(head, valueAt, type)
                    : big ? (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(valueAt)) : BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(valueAt));
                tags[tag] = (type, count, value, valueAt);
            }
            long V(int tag, long fallback = 0) => tags.TryGetValue(tag, out var t) ? t.Value : fallback;
            if (!tags.ContainsKey(322))
                throw new InvalidDataException("The elevation file is not tiled.");
            cog.Images.Add(new CogImage
            {
                Width = (int)V(256), Height = (int)V(257), TileWidth = (int)V(322), TileHeight = (int)V(323),
                Compression = (int)V(259, 1), Predictor = (int)V(317, 1), BitsPerSample = (int)V(258, 16), SampleFormat = (int)V(339, 1),
                TileOffsetsAt = V(324), TileByteCountsAt = V(325), TileOffsetType = tags[324].Type, TileByteCountType = tags[325].Type,
                TileCount = (int)tags[324].Count
            });
            if (first)
            {
                first = false;
                if (tags.TryGetValue(33550, out var scale) && tags.TryGetValue(33922, out var tie))
                {
                    cog.PixelSize = BitConverter.ToDouble(head, (int)scale.Value);
                    cog.OriginX = BitConverter.ToDouble(head, (int)tie.Value + 24) - BitConverter.ToDouble(head, (int)tie.Value) * cog.PixelSize;
                    cog.OriginY = BitConverter.ToDouble(head, (int)tie.Value + 32) + BitConverter.ToDouble(head, (int)tie.Value + 8) * cog.PixelSize;
                }
                if (tags.TryGetValue(42113, out var nd))
                {
                    var text = nd.Count <= (big ? 8 : 4)
                        ? System.Text.Encoding.ASCII.GetString(head, nd.At, (int)nd.Count)
                        : System.Text.Encoding.ASCII.GetString(head, (int)nd.Value, (int)nd.Count);
                    if (double.TryParse(text.TrimEnd('\0', ' '), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                        cog.NoData = v;
                }
                if (tags.TryGetValue(34735, out var geo))
                {
                    // GeoKeyDirectory: header (4 shorts) then key entries; ProjectedCSTypeGeoKey = 3072.
                    var at = (int)geo.Value;
                    var keys = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(at + 6));
                    for (var k = 0; k < keys; k++)
                    {
                        var e = at + 8 + k * 8;
                        if (BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(e)) == 3072)
                            cog.Epsg = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(e + 6));
                    }
                }
            }
            ifd = big ? (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(p)) : BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(p));
        }
        if (cog.Images.Count == 0 || cog.PixelSize <= 0)
            throw new InvalidDataException("The elevation file has no georeferenced image.");
        foreach (var im in cog.Images)
            if (im.BitsPerSample != 16 || im.SampleFormat != 2 || im.Compression is not (1 or 5) || im.Predictor != 1)
                throw new InvalidDataException("Unsupported elevation format (expected 16-bit signed, LZW or uncompressed).");
        return cog;
    }

    /// <summary>Pixel size of image <paramref name="index"/> (overviews halve the resolution each level).</summary>
    public double PixelSizeOf(int index) => PixelSize * Images[0].Width / (double)Images[index].Width;

    /// <summary>Byte offsets and lengths of every tile of an image.</summary>
    public async Task<(long[] Offsets, long[] Counts)> TileIndexAsync(int index, CancellationToken cancel)
    {
        var im = Images[index];
        return (await ReadArray(im.TileOffsetsAt, im.TileOffsetType, im.TileCount, cancel),
                await ReadArray(im.TileByteCountsAt, im.TileByteCountType, im.TileCount, cancel));
    }

    private async Task<long[]> ReadArray(long at, int type, int count, CancellationToken cancel)
    {
        var size = TypeSize(type);
        var bytes = await _source.ReadAsync(at, size * count, cancel);
        var result = new long[count];
        for (var i = 0; i < count; i++)
            result[i] = ReadInline(bytes, i * size, type);
        return result;
    }

    public Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancel) => _source.ReadAsync(offset, length, cancel);

    /// <summary>Decodes one stored tile into tile-sized 16-bit samples.</summary>
    public static short[] DecodeTile(CogImage im, byte[] data)
    {
        var raw = im.Compression == 5 ? TiffLzw.Decode(data, im.TileWidth * im.TileHeight * 2) : data;
        var samples = new short[im.TileWidth * im.TileHeight];
        Buffer.BlockCopy(raw, 0, samples, 0, Math.Min(raw.Length, samples.Length * 2));
        return samples;
    }

    private static int TypeSize(int type) => type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 16 or 17 or 12 => 8, _ => 4 };

    private static long ReadInline(byte[] b, int at, int type) => type switch
    {
        3 => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)),
        16 => (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at)),
        1 or 2 => b[at],
        _ => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at))
    };
}

/// <summary>TIFF LZW decoder (MSB-first codes, 9–12 bits, early change).</summary>
public static class TiffLzw
{
    public static byte[] Decode(byte[] input, int expectedLength)
    {
        var output = new byte[expectedLength];
        var outPos = 0;
        var prefix = new short[4096];
        var suffix = new byte[4096];
        var first = new byte[4096];
        var length = new short[4096];
        for (var i = 0; i < 256; i++)
        {
            suffix[i] = (byte)i;
            first[i] = (byte)i;
            length[i] = 1;
            prefix[i] = -1;
        }
        int next = 258, width = 9, old = -1;
        long bitBuf = 0;
        var bitCount = 0;
        var inPos = 0;
        var stack = new byte[4096];
        while (true)
        {
            while (bitCount < width && inPos < input.Length)
            {
                bitBuf = (bitBuf << 8) | input[inPos++];
                bitCount += 8;
            }
            if (bitCount < width)
                break;
            var code = (int)((bitBuf >> (bitCount - width)) & ((1 << width) - 1));
            bitCount -= width;
            if (code == 257)
                break;
            if (code == 256)
            {
                next = 258;
                width = 9;
                old = -1;
                continue;
            }
            int emit;
            if (code < next)
                emit = code;
            else if (code == next && old >= 0)
                emit = old; // KwKwK: emitted after the new entry is added below
            else
                throw new InvalidDataException("Damaged LZW data in the elevation file.");

            if (old >= 0 && next < 4096)
            {
                prefix[next] = (short)old;
                suffix[next] = code < next ? first[code] : first[old];
                first[next] = first[old];
                length[next] = (short)(length[old] + 1);
                next++;
            }
            var c = code < next ? code : emit;
            // Write the string for code c (walk prefixes backwards).
            var len = length[c];
            var sp = len;
            for (var k = c; k >= 0; k = prefix[k])
                stack[--sp] = suffix[k];
            var n = Math.Min(len, output.Length - outPos);
            Array.Copy(stack, 0, output, outPos, n);
            outPos += n;
            old = code;
            if (next + 1 >= (1 << width) && width < 12)
                width++;
            if (outPos >= output.Length)
                break;
        }
        return output;
    }
}
