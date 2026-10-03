using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Shared.Core.Update;

/// <summary>A place updates can come from.</summary>
public interface IUpdateSource
{
    /// <summary>Shown to the user ("USB stick E:", "GitHub DRHaupt/incident-report"…).</summary>
    string Name { get; }

    /// <summary>The newest release offered here, or null when there is none. Throws when the source can't be read.</summary>
    Task<UpdateManifest?> GetManifestAsync(CancellationToken ct);

    /// <summary>Opens the installer described by <paramref name="manifest"/> for reading.</summary>
    Task<Stream> OpenInstallerAsync(UpdateManifest manifest, CancellationToken ct);
}

/// <summary>Raised when a source can't be read; the message is written for the user.</summary>
public sealed class UpdateSourceException : Exception
{
    public UpdateSourceException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// A folder holding <c>fieldsuite-update.json</c> and the MSI: a USB stick, a network share or a local folder. The
/// files may also sit in a <c>FieldSuite-Updates</c> subfolder.
/// </summary>
public sealed class FolderUpdateSource : IUpdateSource
{
    public FolderUpdateSource(string folder, string? name = null)
    {
        Folder = folder;
        Name = name ?? folder;
    }

    public string Folder { get; }
    public string Name { get; }

    private string? ManifestFolder()
    {
        foreach (var dir in new[] { Folder, Path.Combine(Folder, SuiteInfo.UpdateFolderName) })
            if (File.Exists(Path.Combine(dir, UpdateManifest.FileName)))
                return dir;
        return null;
    }

    public async Task<UpdateManifest?> GetManifestAsync(CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(Folder))
                throw new UpdateSourceException("Folder not found or not reachable.");
            var dir = ManifestFolder();
            if (dir == null)
                return null;
            var m = UpdateManifest.Parse(await File.ReadAllTextAsync(Path.Combine(dir, UpdateManifest.FileName), ct));
            if (!File.Exists(Path.Combine(dir, m.File)))
                throw new UpdateSourceException($"{m.File} is missing next to the update description.");
            return m;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new UpdateSourceException(ex.Message, ex);
        }
    }

    public Task<Stream> OpenInstallerAsync(UpdateManifest manifest, CancellationToken ct)
    {
        var dir = ManifestFolder() ?? throw new UpdateSourceException("The update is no longer in this folder.");
        return Task.FromResult<Stream>(new FileStream(Path.Combine(dir, manifest.File), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true));
    }
}

/// <summary>A web address of a <c>fieldsuite-update.json</c>; the MSI is fetched from the same folder on the server.</summary>
public sealed class UrlUpdateSource : IUpdateSource
{
    private readonly HttpClient _http;

    public UrlUpdateSource(Uri manifestUrl, HttpClient http)
    {
        if (manifestUrl.Scheme != Uri.UriSchemeHttps && !manifestUrl.IsLoopback)
            throw new ArgumentException("Update addresses must use https://.");
        ManifestUrl = manifestUrl;
        _http = http;
    }

    public Uri ManifestUrl { get; }
    public string Name => ManifestUrl.Host;

    public async Task<UpdateManifest?> GetManifestAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(ManifestUrl, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new UpdateSourceException($"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            return UpdateManifest.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new UpdateSourceException(Http.Describe(ex), ex);
        }
    }

    public async Task<Stream> OpenInstallerAsync(UpdateManifest manifest, CancellationToken ct) =>
        await Http.OpenAsync(_http, new HttpRequestMessage(HttpMethod.Get, new Uri(ManifestUrl, manifest.File)), ct);
}

/// <summary>
/// The latest release of a GitHub repository, which the release build creates with the MSI and the manifest attached.
/// A private repository needs an access token with read access to its contents.
/// </summary>
public sealed class GitHubUpdateSource : IUpdateSource
{
    private readonly HttpClient _http;
    private readonly string? _token;
    private readonly Dictionary<string, string> _assets = new(StringComparer.OrdinalIgnoreCase);

    public GitHubUpdateSource(string repository, string? token, HttpClient http, Uri? apiBase = null)
    {
        var parts = repository.Trim().Trim('/').Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))))
            throw new ArgumentException("Use the form owner/repository.");
        Repository = parts[0] + "/" + parts[1];
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _http = http;
        ApiBase = apiBase ?? new Uri("https://api.github.com/");
    }

    public string Repository { get; }
    public Uri ApiBase { get; }
    public string Name => "GitHub " + Repository;

    private HttpRequestMessage Request(Uri url, string accept)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        r.Headers.Accept.ParseAdd(accept);
        r.Headers.UserAgent.ParseAdd("FieldSuite-Updater");
        r.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (_token != null)
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return r;
    }

    public async Task<UpdateManifest?> GetManifestAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.SendAsync(Request(new Uri(ApiBase, $"repos/{Repository}/releases/latest"), "application/vnd.github+json"), ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                if (_token == null)
                    throw new UpdateSourceException("No release found. A private repository needs an access token (Update settings).");
                return null;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new UpdateSourceException("GitHub rejected the access token (expired or mistyped?).");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new UpdateSourceException("GitHub refused the request (token without access to this repository, or rate limit).");
            if (!response.IsSuccessStatusCode)
                throw new UpdateSourceException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            _assets.Clear();
            if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                    if (a.TryGetProperty("name", out var n) && a.TryGetProperty("url", out var u) && n.GetString() is { } name && u.GetString() is { } url)
                        _assets[name] = url;
            if (!_assets.TryGetValue(UpdateManifest.FileName, out var manifestUrl))
                return null;

            using var stream = await Http.OpenAsync(_http, Request(new Uri(manifestUrl), "application/octet-stream"), ct);
            using var reader = new StreamReader(stream);
            var m = UpdateManifest.Parse(await reader.ReadToEndAsync(ct));
            if (!_assets.ContainsKey(m.File))
                throw new UpdateSourceException($"The release has no {m.File}.");
            return m;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new UpdateSourceException(Http.Describe(ex), ex);
        }
    }

    public async Task<Stream> OpenInstallerAsync(UpdateManifest manifest, CancellationToken ct)
    {
        if (!_assets.TryGetValue(manifest.File, out var url))
            throw new UpdateSourceException("Check for updates again: the release has changed.");
        // The asset URL redirects to a signed download link; HttpClient doesn't forward the token to it.
        return await Http.OpenAsync(_http, Request(new Uri(url), "application/octet-stream"), ct);
    }
}

internal static class Http
{
    public static async Task<Stream> OpenAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new UpdateSourceException(Describe(ex), ex);
        }
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            response.Dispose();
            throw new UpdateSourceException($"Download failed: the server answered {code}.");
        }
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "Timed out (no internet connection?).",
        HttpRequestException { InnerException: System.Net.Sockets.SocketException } => "Not reachable (no internet connection?).",
        HttpRequestException h => "Not reachable: " + h.Message,
        UpdateSourceException u => u.Message,
        _ => ex.Message
    };
}

/// <summary>Turns the saved settings into the list of sources to ask, nearest first.</summary>
public static class UpdateSourceList
{
    /// <summary>Root folders of USB sticks and SD cards that are plugged in now.</summary>
    public static IEnumerable<string> RemovableRoots()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            bool ready;
            try
            {
                ready = d.DriveType == DriveType.Removable && d.IsReady;
            }
            catch (IOException)
            {
                ready = false;
            }
            if (ready)
                yield return d.RootDirectory.FullName;
        }
    }

    /// <param name="unprotectToken">Decrypts a stored GitHub token (Windows user encryption); null if it can't.</param>
    /// <param name="removableRoots">Drive roots to scan; defaults to the plugged-in removable drives.</param>
    public static List<IUpdateSource> Create(UpdateSettings settings, Func<string, string?> unprotectToken, HttpClient http, IEnumerable<string>? removableRoots = null)
    {
        var list = new List<IUpdateSource>();
        if (settings.ScanRemovableDrives)
            foreach (var root in removableRoots ?? RemovableRoots())
            {
                var dir = Path.Combine(root, SuiteInfo.UpdateFolderName);
                if (Directory.Exists(dir))
                    list.Add(new FolderUpdateSource(dir, "USB drive " + root.TrimEnd('\\', '/')));
            }
        var enabled = settings.Sources.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Location)).ToList();
        foreach (var s in enabled.Where(s => s.Kind == UpdateSourceKind.Folder))
            list.Add(new FolderUpdateSource(s.Location.Trim()));
        foreach (var s in enabled.Where(s => s.Kind == UpdateSourceKind.Url))
            if (Uri.TryCreate(s.Location.Trim(), UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.IsLoopback))
                list.Add(new UrlUpdateSource(url, http));
        foreach (var s in enabled.Where(s => s.Kind == UpdateSourceKind.GitHub))
        {
            try
            {
                list.Add(new GitHubUpdateSource(s.Location, s.ProtectedToken is { Length: > 0 } t ? unprotectToken(t) : null, http));
            }
            catch (ArgumentException)
            {
                // Mistyped repository: shown as invalid in the settings, skipped here.
            }
        }
        return list;
    }
}
