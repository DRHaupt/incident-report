using System.Security.Cryptography;

namespace Shared.Core.Update;

/// <summary>Kinds of update source the user can configure.</summary>
public enum UpdateSourceKind
{
    /// <summary>A folder: network share or local path.</summary>
    Folder,
    /// <summary>https:// address of a fieldsuite-update.json.</summary>
    Url,
    /// <summary>owner/repository on GitHub (latest release).</summary>
    GitHub
}

public sealed class UpdateSourceSetting
{
    public UpdateSourceKind Kind { get; set; }
    /// <summary>Folder path, URL, or owner/repository.</summary>
    public string Location { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>GitHub access token, encrypted for this Windows user (never stored in plain text).</summary>
    public string? ProtectedToken { get; set; }
}

/// <summary>How and where this computer looks for Field Suite updates (shared by all three apps).</summary>
public sealed class UpdateSettings
{
    public const string DefaultRepository = "DRHaupt/incident-report";

    /// <summary>Look for updates in the background when an app starts (at most every <see cref="CheckEveryHours"/>).</summary>
    public bool AutoCheck { get; set; } = true;
    public int CheckEveryHours { get; set; } = 24;
    /// <summary>Look in a FieldSuite-Updates folder on any USB stick or SD card.</summary>
    public bool ScanRemovableDrives { get; set; } = true;
    public List<UpdateSourceSetting> Sources { get; set; } = new()
    {
        new UpdateSourceSetting { Kind = UpdateSourceKind.GitHub, Location = DefaultRepository }
    };
    public DateTime? LastCheckUtc { get; set; }
    /// <summary>A version the user chose to skip: no reminder for it (a newer one still shows).</summary>
    public string? SkippedVersion { get; set; }

    public bool CheckDue(DateTime nowUtc) =>
        AutoCheck && (LastCheckUtc is not { } last || nowUtc - last >= TimeSpan.FromHours(Math.Max(1, CheckEveryHours)) || last > nowUtc);
}

/// <summary>An update that can be installed.</summary>
public sealed record UpdateOffer(UpdateManifest Manifest, IUpdateSource Source, UpdateTrustResult Trust)
{
    public Version Version => Manifest.ParsedVersion;
}

/// <summary>What one check found, source by source.</summary>
public sealed class UpdateCheckResult
{
    public UpdateOffer? Best { get; set; }
    /// <summary>Per source: what was found, or why it couldn't be used.</summary>
    public List<(string Source, string Message)> Report { get; } = new();
}

/// <summary>Finds the newest acceptable update across sources, and downloads it with its checksum verified.</summary>
public static class UpdateChecker
{
    /// <summary>
    /// Asks every source (in order) for its newest release. Keeps the highest version newer than
    /// <paramref name="installed"/> that passes <paramref name="trust"/>; with equal versions the earlier source wins,
    /// so a USB stick or share is preferred over the internet.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(IEnumerable<IUpdateSource> sources, Version? installed, UpdateTrust trust, CancellationToken ct)
    {
        var result = new UpdateCheckResult();
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            UpdateManifest? m;
            try
            {
                m = await source.GetManifestAsync(ct);
            }
            catch (UpdateSourceException ex)
            {
                result.Report.Add((source.Name, ex.Message));
                continue;
            }
            if (m == null)
            {
                result.Report.Add((source.Name, "No update here."));
                continue;
            }
            var verdict = trust.Check(m);
            if (verdict == UpdateTrustResult.Rejected)
            {
                result.Report.Add((source.Name, $"{m.Version} ignored: not signed with this installation's release key."));
                continue;
            }
            if (installed != null && m.ParsedVersion <= installed)
            {
                result.Report.Add((source.Name, $"{m.Version} – already up to date."));
                continue;
            }
            result.Report.Add((source.Name, $"{m.Version} available{(verdict == UpdateTrustResult.Unsigned ? " (not signed)" : "")}."));
            if (result.Best == null || m.ParsedVersion > result.Best.Version)
                result.Best = new UpdateOffer(m, source, verdict);
        }
        return result;
    }

    /// <summary>
    /// Downloads (or copies) the installer into <paramref name="folder"/>, checking its size and SHA-256 against the
    /// manifest. A file already there with the right checksum is reused. Returns the verified path.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateOffer offer, string folder, IProgress<double>? progress, CancellationToken ct)
    {
        var m = offer.Manifest;
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, m.File);
        if (File.Exists(target) && new FileInfo(target).Length == m.Size && HashFile(target) == m.Sha256.ToLowerInvariant())
        {
            progress?.Report(1);
            return target;
        }

        var partial = target + ".partial";
        try
        {
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var input = await offer.Source.OpenInstallerAsync(m, ct))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        total += read;
                        if (total > m.Size)
                            throw new InvalidDataException("The installer is bigger than its description says; download stopped.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        progress?.Report((double)total / m.Size);
                    }
                    if (total != m.Size)
                        throw new InvalidDataException($"The download is incomplete ({total:N0} of {m.Size:N0} bytes).");
                }
                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (actual != m.Sha256.ToLowerInvariant())
                    throw new InvalidDataException("The installer's checksum doesn't match: the file is damaged or not the published one.");
            }
            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    public static string HashFile(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>Removes downloaded installers other than <paramref name="keep"/> (they're 100+ MB each).</summary>
    public static void CleanDownloads(string folder, string? keep = null)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (var f in Directory.EnumerateFiles(folder))
            if ((f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) &&
                !string.Equals(Path.GetFileName(f), keep, StringComparison.OrdinalIgnoreCase))
                TryDelete(f);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
