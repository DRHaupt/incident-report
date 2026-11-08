using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace FieldMap.Core.Data;

public sealed record RemoteInfo(long? Length, DateTime? LastModifiedUtc, string? ETag);

/// <summary>
/// Downloads large files with resume: data goes to "file.part" with a small "file.part.json" describing what is being
/// downloaded. After an interruption the next attempt asks the server for the remaining bytes (HTTP Range + If-Range),
/// and starts over only if the file changed on the server. The checksum is verified before the file is renamed into place.
/// </summary>
public sealed class Downloader
{
    private sealed class PartInfo
    {
        public string Url { get; set; } = "";
        public string? ETag { get; set; }
        public DateTime? LastModifiedUtc { get; set; }
        public long? Length { get; set; }
    }

    private readonly HttpClient _http;

    public Downloader(HttpClient http) => _http = http;

    public HttpClient Http => _http;

    public static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FieldMap/1.0 (offline emergency map; +https://github.com/DRHaupt/incident-report)");
        return http;
    }

    public async Task<RemoteInfo> HeadAsync(string url, CancellationToken cancel = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var req = new HttpRequestMessage(HttpMethod.Head, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return new RemoteInfo(resp.Content.Headers.ContentLength, resp.Content.Headers.LastModified?.UtcDateTime, resp.Headers.ETag?.Tag);
    }

    public async Task<string> GetStringAsync(string url, CancellationToken cancel = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        return await _http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destination"/>, resuming a previous partial download. When
    /// <paramref name="expectedHash"/> is given (hex MD5 or SHA-256, chosen by <paramref name="hashType"/>) a mismatch
    /// deletes the partial file and throws <see cref="InvalidDataException"/>. Returns what the server reported.
    /// </summary>
    public async Task<RemoteInfo> DownloadAsync(string url, string destination, string? expectedHash = null, string hashType = "sha256",
        IProgress<(long Done, long? Total)>? progress = null, CancellationToken cancel = default, Action<long>? onBytes = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var part = destination + ".part";
        var metaPath = part + ".json";
        PartInfo? meta = null;
        if (File.Exists(part) && File.Exists(metaPath))
        {
            try
            {
                meta = Json.Deserialize<PartInfo>(File.ReadAllText(metaPath));
                if (meta.Url != url)
                    meta = null;
            }
            catch (Exception)
            {
                meta = null;
            }
        }
        if (meta == null)
        {
            File.Delete(part);
            File.Delete(metaPath);
        }

        var existing = meta != null && File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
        {
            req.Headers.Range = new RangeHeaderValue(existing, null);
            if (meta!.ETag != null)
                req.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(meta.ETag));
            else if (meta.LastModifiedUtc is { } lm)
                req.Headers.IfRange = new RangeConditionHeaderValue(new DateTimeOffset(lm, TimeSpan.Zero));
        }
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            // Already complete (or the server changed): verify what we have, else start over next time.
            meta!.Length ??= existing;
        }
        else
        {
            resp.EnsureSuccessStatusCode();
            var resumed = resp.StatusCode == HttpStatusCode.PartialContent && existing > 0;
            if (!resumed)
                existing = 0;
            var total = resumed ? (resp.Content.Headers.ContentRange?.Length ?? existing + resp.Content.Headers.ContentLength) : resp.Content.Headers.ContentLength;
            meta = new PartInfo
            {
                Url = url,
                ETag = resp.Headers.ETag?.Tag ?? (resumed ? meta?.ETag : null),
                LastModifiedUtc = resp.Content.Headers.LastModified?.UtcDateTime ?? (resumed ? meta?.LastModifiedUtc : null),
                Length = total
            };
            File.WriteAllText(metaPath, Json.Serialize(meta));
            await using (var input = await resp.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            await using (var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buffer = new byte[1 << 18];
                var done = existing;
                var lastReport = DateTime.MinValue;
                int n;
                while ((n = await input.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
                    done += n;
                    onBytes?.Invoke(n);
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
                    {
                        lastReport = DateTime.UtcNow;
                        progress?.Report((done, total));
                    }
                }
                progress?.Report((done, total));
            }
        }

        var length = new FileInfo(part).Length;
        if (meta.Length is { } expected && length != expected)
            throw new IOException($"Download of {Path.GetFileName(destination)} is incomplete ({length:N0} of {expected:N0} bytes). Try again to resume.");
        if (expectedHash != null)
        {
            var actual = HashFile(part, hashType);
            if (!actual.Equals(expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                File.Delete(metaPath);
                throw new InvalidDataException($"{Path.GetFileName(destination)} failed its {hashType.ToUpperInvariant()} checksum; the download was discarded.");
            }
        }
        File.Move(part, destination, overwrite: true);
        File.Delete(metaPath);
        return new RemoteInfo(length, meta.LastModifiedUtc, meta.ETag);
    }

    public static string HashFile(string path, string hashType)
    {
        using var s = File.OpenRead(path);
        var bytes = hashType.Equals("md5", StringComparison.OrdinalIgnoreCase) ? MD5.HashData(s) : SHA256.HashData(s);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>The hash in a checksum file such as Geofabrik's ".md5" ("d41d8cd9...  alberta-latest.osm.pbf").</summary>
    public static string ParseChecksumFile(string content) =>
        content.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant()
        ?? throw new InvalidDataException("Empty checksum file.");
}
