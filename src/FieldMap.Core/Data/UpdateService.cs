using System.IO.Compression;
using FieldMap.Core.Sample;

namespace FieldMap.Core.Data;

public sealed record UpdateResult(bool Success, string Message, string? Version = null, ValidationReport? Report = null);

public sealed record SourceStatus(SourceDefinition Source, long? Bytes, DateTime? LastModifiedUtc, string? Error);

/// <summary>
/// "Update map data": download the latest sources (resuming, checksummed), build tiles/routing/search into a staging
/// folder, validate, then publish with an atomic pointer swap. Any failure leaves the installed version untouched.
/// </summary>
public sealed class UpdateService
{
    private readonly RegionStore _store;
    private readonly Downloader _downloader;

    public UpdateService(RegionStore store, HttpClient? http = null)
    {
        _store = store;
        _downloader = new Downloader(http ?? Downloader.CreateClient());
    }

    public Downloader Downloader => _downloader;

    /// <summary>Asks each server for the current size and date (for "show size before downloading").</summary>
    public async Task<List<SourceStatus>> CheckSourcesAsync(RegionDefinition region, CancellationToken cancel = default)
    {
        var list = new List<SourceStatus>();
        foreach (var s in region.Sources.Where(s => s.Enabled))
        {
            if (s.Kind == SourceKinds.Elevation)
            {
                // Only the region's part of the national file is fetched; its size is in the catalogue.
                list.Add(new SourceStatus(s, s.ApproxBytes, null, null));
                continue;
            }
            try
            {
                var info = await _downloader.HeadAsync(s.Url, cancel).ConfigureAwait(false);
                list.Add(new SourceStatus(s, info.Length ?? s.ApproxBytes, info.LastModifiedUtc, null));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                list.Add(new SourceStatus(s, s.ApproxBytes, null, ex.Message));
            }
        }
        return list;
    }

    /// <summary>Downloads are shared by all regions (file names come from the URLs), so BC, Alberta and the combined package reuse them.</summary>
    public string DownloadDir(string regionId) => _store.DownloadsDir;

    /// <summary>The full update for one region.</summary>
    public async Task<UpdateResult> UpdateAsync(RegionDefinition region, IProgress<(string Stage, double Fraction)>? progress = null, CancellationToken cancel = default)
    {
        var sources = region.Sources.Where(s => s.Enabled).ToList();
        var results = new SourceFile?[sources.Count];
        var errors = new Exception?[sources.Count];
        var meter = new DownloadMeter(progress);

        // The files come from different organisations' servers. Each server gets one connection at a time (Geofabrik in
        // particular caps its bandwidth and blocks heavy users), but different servers download at the same time, so a
        // slow one no longer holds up the rest.
        var byHost = sources.Select((s, i) => (Source: s, Index: i))
            .GroupBy(x => Uri.TryCreate(x.Source.Url, UriKind.Absolute, out var u) ? u.Host : x.Source.Url);
        await Task.WhenAll(byHost.Select(async group =>
        {
            foreach (var (source, i) in group)
            {
                if (cancel.IsCancellationRequested)
                    return;
                try
                {
                    results[i] = await DownloadSourceAsync(region.Id, source, meter.For(source.Name), cancel).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException && !cancel.IsCancellationRequested)
                {
                    errors[i] = ex;
                }
                finally
                {
                    meter.Finished(source.Name);
                }
            }
        })).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();

        var files = new List<SourceFile>();
        var warnings = new List<string>();
        for (var i = 0; i < sources.Count; i++)
        {
            if (results[i] is { } f)
                files.Add(f);
            else if (errors[i] is { } ex)
            {
                if (sources[i].Required)
                    return new UpdateResult(false, $"Could not download {sources[i].Name}: {ex.Message}\nThe map data in use was not changed. Try again to resume the download.");
                warnings.Add($"{sources[i].Name} was skipped ({ex.Message}).");
            }
        }
        var result = await Task.Run(() => BuildAndPublish(region, files, progress, cancel), cancel).ConfigureAwait(false);
        return warnings.Count == 0 ? result : result with { Message = result.Message + "\n" + string.Join("\n", warnings) };
    }

    private async Task<SourceFile> DownloadSourceAsync(string regionId, SourceDefinition source, IProgress<(string Stage, double Fraction)>? progress, CancellationToken cancel)
    {
        if (source.Kind == SourceKinds.Elevation)
            return await DownloadElevationAsync(regionId, source, progress, cancel).ConfigureAwait(false);
        string? hash = null;
        if (source.ChecksumUrl != null)
            hash = Downloader.ParseChecksumFile(await _downloader.GetStringAsync(source.ChecksumUrl, cancel).ConfigureAwait(false));
        var dest = Path.Combine(DownloadDir(regionId), source.FileName);
        var stage = $"Downloading {source.Name}";
        var info = await _downloader.DownloadAsync(source.Url, dest, hash, source.ChecksumType ?? "sha256",
            new Progress<(long Done, long? Total)>(p => progress?.Report((stage, p.Total is > 0 ? (double)p.Done / p.Total.Value : 0))), cancel,
            bytes => (progress as DownloadMeter.Part)?.AddBytes(bytes)).ConfigureAwait(false);
        return new SourceFile(source, dest, Describe(source, dest, DateTime.UtcNow, info.LastModifiedUtc));
    }

    /// <summary>
    /// Fetches the region's part of the national elevation model once (it is static; delete the file in the downloads
    /// folder to fetch it again).
    /// </summary>
    internal async Task<SourceFile> DownloadElevationAsync(string regionId, SourceDefinition source, IProgress<(string Stage, double Fraction)>? progress, CancellationToken cancel)
    {
        var dest = Path.Combine(DownloadDir(regionId), source.LocalFileName(regionId));
        if (!File.Exists(dest))
        {
            if (source.Bounds is not { Length: 4 } b)
                throw new InvalidDataException("The elevation source has no area (bounds) in the region catalogue.");
            var stage = $"Downloading {source.Name}";
            progress?.Report((stage, 0));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try
            {
                await Terrain.TerrainExtractor.ExtractAsync(new Terrain.HttpRangeSource(_downloader.Http, new Uri(source.Url), progress is DownloadMeter.Part part ? part.AddBytes : null),
                    new Geo.GeoBounds(b[0], b[1], b[2], b[3]), source.PixelMetres, dest,
                    new Progress<double>(f => progress?.Report((stage, f))), cancel).ConfigureAwait(false);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                throw new IOException("The elevation file could not be written: " + ex.Message, ex);
            }
        }
        return new SourceFile(source, dest, Describe(source, dest, File.GetLastWriteTimeUtc(dest), null));
    }

    public static DataSourceInfo Describe(SourceDefinition s, string path, DateTime downloadedUtc, DateTime? dataDateUtc) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Licence = s.Licence,
        LicenceUrl = s.LicenceUrl,
        Url = s.Url,
        Attribution = s.Attribution,
        DownloadedUtc = downloadedUtc,
        DataDateUtc = dataDateUtc ?? File.GetLastWriteTimeUtc(path),
        SizeBytes = new FileInfo(path).Length,
        Sha256 = RegionManifest.Sha256Of(path)
    };

    /// <summary>The downloaded copies of a region's sources (for rebuilding offline, e.g. after an AED import).</summary>
    public List<SourceFile>? CachedSources(RegionDefinition region)
    {
        var files = new List<SourceFile>();
        foreach (var s in region.Sources.Where(s => s.Enabled))
        {
            var path = Path.Combine(DownloadDir(region.Id), s.LocalFileName(region.Id));
            if (File.Exists(path))
                files.Add(new SourceFile(s, path, Describe(s, path, File.GetLastWriteTimeUtc(path), File.GetLastWriteTimeUtc(path))));
            else if (s.Required)
                return null;
        }
        return files;
    }

    /// <summary>Source files the user already has (e.g. copied from another PC): the OSM extract, address CSVs and facility tables.</summary>
    public static List<SourceFile> LocalSources(RegionDefinition region, string? pbf, IEnumerable<string> addressFiles, IEnumerable<string> poiFiles)
    {
        var files = new List<SourceFile>();
        var osmDef = region.Sources.FirstOrDefault(s => s.Kind == SourceKinds.OsmPbf)
                     ?? new SourceDefinition { Id = "osm", Name = "OpenStreetMap", Kind = SourceKinds.OsmPbf, Licence = "ODbL 1.0", Attribution = "© OpenStreetMap contributors", Url = "file:///osm.pbf" };
        if (pbf == null)
            throw new ArgumentException("An OpenStreetMap extract (.osm.pbf) is required.");
        files.Add(new SourceFile(osmDef, pbf, Describe(osmDef, pbf, DateTime.UtcNow, File.GetLastWriteTimeUtc(pbf))));
        var addrDef = region.Sources.FirstOrDefault(s => s.Kind == SourceKinds.AddressCsv)
                      ?? new SourceDefinition { Id = "addresses", Name = "Address points", Kind = SourceKinds.AddressCsv, Url = "file:///addresses.csv" };
        foreach (var a in addressFiles)
            files.Add(new SourceFile(addrDef, a, Describe(addrDef, a, DateTime.UtcNow, File.GetLastWriteTimeUtc(a))));
        foreach (var spec in poiFiles)
        {
            var eq = spec.IndexOf('=');
            if (eq <= 0)
                throw new ArgumentException($"--poi expects <source id>=<file>, got '{spec}'.");
            var def = region.Sources.FirstOrDefault(s => s.Id == spec[..eq] && s.Kind == SourceKinds.PoiTable)
                      ?? throw new ArgumentException($"No facility source '{spec[..eq]}' in the region catalogue.");
            var path = spec[(eq + 1)..];
            files.Add(new SourceFile(def, path, Describe(def, path, DateTime.UtcNow, File.GetLastWriteTimeUtc(path))));
        }
        return files;
    }

    /// <summary>AED registries the organisation imported (kept in imports\aed and re-applied after every update).</summary>
    public static List<(string Path, string SourceName)> AedImports(RegionStore store)
    {
        var dir = Path.Combine(store.ImportsDir, "aed");
        if (!Directory.Exists(dir))
            return new();
        string[] usable = { ".csv", ".xlsx", ".geojson", ".json", ".kml", ".kmz", ".gpx" };
        return Directory.GetFiles(dir).Where(f => usable.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f).Select(f => (f, SourceNameFor(f))).ToList();
    }

    /// <summary>The source name stored next to an imported file (file.source.txt), else the file name.</summary>
    public static string SourceNameFor(string importFile)
    {
        var side = importFile + ".source.txt";
        return File.Exists(side) ? File.ReadAllText(side).Trim() : Path.GetFileNameWithoutExtension(importFile);
    }

    /// <summary>Copies an AED file into imports\aed (with its source name) so every future build merges it.</summary>
    public static string AddAedImport(RegionStore store, string file, string sourceName)
    {
        var dir = Path.Combine(store.ImportsDir, "aed");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(file));
        File.Copy(file, dest, overwrite: true);
        File.WriteAllText(dest + ".source.txt", sourceName);
        return dest;
    }

    public UpdateResult BuildAndPublish(RegionDefinition region, List<SourceFile> files, IProgress<(string Stage, double Fraction)>? progress, CancellationToken cancel)
    {
        string? staging = null;
        try
        {
            staging = _store.NewStagingDir(region.Id);
            RegionBuilder.Build(new BuildRequest
            {
                RegionId = region.Id,
                RegionName = region.Name,
                OutputDir = staging,
                Sources = files,
                AedImports = AedImports(_store)
            }, progress, cancel);
            return ValidateAndPublish(region.Id, staging, progress);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (staging != null)
                TryDelete(staging);
            return new UpdateResult(false, ex is OperationCanceledException
                ? "Update cancelled. The map data in use was not changed."
                : $"Building the map data failed: {ex.Message}\nThe map data in use was not changed.");
        }
    }

    internal UpdateResult ValidateAndPublish(string regionId, string staging, IProgress<(string Stage, double Fraction)>? progress)
    {
        progress?.Report(("Validating", 0));
        var report = RegionValidator.Validate(staging, _store.CurrentManifest(regionId));
        if (!report.Ok)
        {
            TryDelete(staging);
            return new UpdateResult(false, "The new map data failed validation and was not installed:\n" + string.Join("\n", report.Errors), null, report);
        }
        progress?.Report(("Installing", 0.5));
        var version = _store.Publish(regionId, staging);
        progress?.Report(("Installed", 1));
        return new UpdateResult(true, $"Map data updated to version {version}.", version, report);
    }

    /// <summary>Builds and installs the fictitious sample region.</summary>
    public UpdateResult InstallSample(IProgress<(string Stage, double Fraction)>? progress = null, CancellationToken cancel = default)
    {
        var staging = _store.NewStagingDir(SampleRegion.RegionId);
        try
        {
            RegionBuilder.BuildSample(staging, Path.Combine(_store.Root, "work"), progress, cancel, AedImports(_store));
            return ValidateAndPublish(SampleRegion.RegionId, staging, progress);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TryDelete(staging);
            return new UpdateResult(false, "Building the sample failed: " + ex.Message);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Offline update packages (*.fmupdate): a zip of one validated region version plus a package description, written on a
/// connected PC and installed from USB on vehicles that never go online.
/// </summary>
public static class UpdatePackage
{
    public const string Extension = ".fmupdate";
    private const string DescriptionEntry = "fieldmap-package.json";

    public sealed class Description
    {
        public int Format { get; set; } = 1;
        public string RegionId { get; set; } = "";
        public string RegionName { get; set; } = "";
        public string Version { get; set; } = "";
        public DateTime BuiltUtc { get; set; }
        public DateTime CreatedUtc { get; set; }
        public bool IsSample { get; set; }
        public List<ManifestFile> Files { get; set; } = new();
    }

    /// <summary>Writes the region's current version as a package. <paramref name="output"/> may be a folder or a file name.</summary>
    public static string Export(RegionStore store, string regionId, string output, IProgress<double>? progress = null)
    {
        var dir = store.CurrentDir(regionId) ?? throw new InvalidOperationException($"Region '{regionId}' is not installed.");
        var manifest = RegionManifest.Load(dir);
        var path = output.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            ? output
            : Path.Combine(output, $"FieldMap-{regionId}-{manifest.Version}{Extension}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            var desc = new Description
            {
                RegionId = regionId, RegionName = manifest.RegionName, Version = manifest.Version, BuiltUtc = manifest.BuiltUtc,
                CreatedUtc = DateTime.UtcNow, IsSample = manifest.IsSample, Files = manifest.Files
            };
            using (var w = new StreamWriter(zip.CreateEntry(DescriptionEntry).Open()))
                w.Write(Json.Serialize(desc));
            var files = manifest.Files.Select(f => f.Path).Append(RegionManifest.FileName).ToList();
            for (var i = 0; i < files.Count; i++)
            {
                var level = files[i].EndsWith(".mbtiles", StringComparison.OrdinalIgnoreCase) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                zip.CreateEntryFromFile(Path.Combine(dir, files[i]), "data/" + files[i], level);
                progress?.Report((i + 1.0) / files.Count);
            }
        }
        File.Move(temp, path, overwrite: true);
        return path;
    }

    public static Description ReadDescription(string packagePath)
    {
        using var zip = ZipFile.OpenRead(packagePath);
        var entry = zip.GetEntry(DescriptionEntry) ?? throw new InvalidDataException("This is not a Field Map update package.");
        using var r = new StreamReader(entry.Open());
        return Json.Deserialize<Description>(r.ReadToEnd());
    }

    /// <summary>Verifies every checksum, validates, and installs with the same atomic swap as an online update.</summary>
    public static UpdateResult Install(RegionStore store, string packagePath, IProgress<(string Stage, double Fraction)>? progress = null)
    {
        Description desc;
        try
        {
            desc = ReadDescription(packagePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException)
        {
            return new UpdateResult(false, "The package can't be read: " + ex.Message);
        }
        var staging = store.NewStagingDir(desc.RegionId);
        try
        {
            using (var zip = ZipFile.OpenRead(packagePath))
            {
                var entries = zip.Entries.Where(e => e.FullName.StartsWith("data/", StringComparison.Ordinal) && e.Name.Length > 0).ToList();
                for (var i = 0; i < entries.Count; i++)
                {
                    var rel = entries[i].FullName["data/".Length..];
                    var target = Path.GetFullPath(Path.Combine(staging, rel));
                    if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidDataException("The package contains an unsafe file path.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entries[i].ExtractToFile(target, overwrite: true);
                    progress?.Report(("Copying from package", (i + 1.0) / entries.Count));
                }
            }
            var manifest = RegionManifest.Load(staging);
            if (manifest.RegionId != desc.RegionId)
                throw new InvalidDataException("The package description and its data don't match.");
            progress?.Report(("Verifying checksums", 0));
            var problems = manifest.VerifyFiles(staging);
            if (problems.Count > 0)
                throw new InvalidDataException("Checksum verification failed: " + string.Join("; ", problems));
            var service = new UpdateService(store);
            var result = service.ValidateAndPublish(desc.RegionId, staging, progress);
            return result.Success ? result with { Message = $"Installed {desc.RegionName} map data (built {desc.BuiltUtc:yyyy-MM-dd}) from the package as version {result.Version}." } : result;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            try { Directory.Delete(staging, true); } catch (IOException) { }
            return new UpdateResult(false, $"The package was not installed: {ex.Message}\nThe map data in use was not changed.");
        }
    }
}

/// <summary>
/// Combines the progress of downloads running side by side into one line for the progress bar: which files are still
/// coming, how far along they are, and the total speed ("Downloading OpenStreetMap 45%, Addresses 20% – 11.8 MB/s").
/// </summary>
internal sealed class DownloadMeter
{
    private readonly IProgress<(string Stage, double Fraction)>? _target;
    private readonly object _lock = new();
    private readonly Dictionary<string, double> _fractions = new();
    private readonly HashSet<string> _done = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private long _bytes, _windowBytes;
    private double _windowStart, _speed, _lastReport = -1;

    public DownloadMeter(IProgress<(string Stage, double Fraction)>? target) => _target = target;

    public Part For(string name)
    {
        lock (_lock)
            _fractions[name] = 0;
        return new Part(this, name);
    }

    public void Finished(string name)
    {
        lock (_lock)
        {
            _fractions[name] = 1;
            _done.Add(name);
        }
        Report(force: true);
    }

    private void Update(string name, double fraction)
    {
        lock (_lock)
            if (!_done.Contains(name))
                _fractions[name] = Math.Clamp(fraction, 0, 1);
        Report(force: false);
    }

    private void Count(long bytes)
    {
        lock (_lock)
        {
            _bytes += bytes;
            _windowBytes += bytes;
        }
    }

    private void Report(bool force)
    {
        string text;
        double overall;
        lock (_lock)
        {
            var now = _clock.Elapsed.TotalSeconds;
            if (!force && now - _lastReport < 0.25)
                return;
            _lastReport = now;
            // Speed over the last ~2 seconds, so it follows changes without jumping around.
            if (now - _windowStart >= 2)
            {
                _speed = _windowBytes / (now - _windowStart);
                _windowBytes = 0;
                _windowStart = now;
            }
            var active = _fractions.Where(f => !_done.Contains(f.Key)).Select(f => $"{f.Key} {f.Value:P0}").ToList();
            overall = _fractions.Count == 0 ? 0 : _fractions.Values.Average();
            text = active.Count == 0 ? "Downloads finished" : "Downloading " + string.Join(", ", active);
            if (_speed > 0 && active.Count > 0)
                text += $" – {_speed / 1e6:0.0} MB/s";
        }
        _target?.Report((text, overall));
    }

    /// <summary>The progress of one file, passed to the code that downloads it.</summary>
    public sealed class Part : IProgress<(string Stage, double Fraction)>
    {
        private readonly DownloadMeter _meter;
        private readonly string _name;

        public Part(DownloadMeter meter, string name)
        {
            _meter = meter;
            _name = name;
        }

        public void Report((string Stage, double Fraction) value) => _meter.Update(_name, value.Fraction);

        public void AddBytes(long bytes) => _meter.Count(bytes);
    }
}
