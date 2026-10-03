using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Shared.Core;
using Shared.Core.Update;

namespace Shared.Update;

/// <summary>
/// Updates the installed Field Suite from inside any of its apps: finds a newer installer (USB stick, folder or share,
/// web address, GitHub release), downloads and verifies it, then hands it to Windows Installer, which upgrades the
/// programs in place (MajorUpgrade) and keeps every user's data. Settings are shared by the three apps.
/// </summary>
public static class SuiteUpdater
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FieldSuite");
    public static string SettingsPath => Path.Combine(DataFolder, "update-settings.json");

    /// <summary>Optional defaults for every user of the computer, set by IT: %ProgramData%\Field Suite\update-settings.json.</summary>
    public static string MachineSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Field Suite", "update-settings.json");

    public static string DownloadFolder => Path.Combine(DataFolder, "updates");
    private static string ResultPath => Path.Combine(DownloadFolder, "last-result.txt");

    /// <summary>Release keys next to the running program (written by the release build).</summary>
    public static UpdateTrust Trust => UpdateTrust.Load(Path.Combine(AppContext.BaseDirectory, UpdateTrust.FileName));

    // ------------------------------------------------------------------ settings

    public static UpdateSettings LoadSettings()
    {
        foreach (var path in new[] { SettingsPath, MachineSettingsPath })
        {
            try
            {
                if (File.Exists(path))
                    return Json.Deserialize<UpdateSettings>(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // Unreadable: try the next one, then defaults.
            }
        }
        return new UpdateSettings();
    }

    public static void SaveSettings(UpdateSettings settings)
    {
        try
        {
            Json.WriteFileAtomic(SettingsPath, Json.Serialize(settings));
        }
        catch (IOException)
        {
            // Another Field Suite app is saving at the same moment; the next save wins.
        }
    }

    /// <summary>Encrypts a token so only this Windows user on this computer can read it.</summary>
    public static string ProtectToken(string token) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));

    public static string? UnprotectToken(string protectedToken)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Saved by another user or computer: enter it again.
            return null;
        }
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FieldSuite.UpdateToken");

    // ------------------------------------------------------------------ versions

    /// <summary>The installed Field Suite version from Windows Installer; null when the suite wasn't installed with the MSI.</summary>
    public static Version? InstalledVersion()
    {
        try
        {
            var product = new StringBuilder(39);
            for (uint i = 0; MsiEnumRelatedProducts(SuiteInfo.UpgradeCode, 0, i, product) == 0; i++)
            {
                var len = 64u;
                var value = new StringBuilder((int)len);
                if (MsiGetProductInfo(product.ToString(), "VersionString", value, ref len) == 0 && SuiteInfo.ParseVersion(value.ToString()) is { } v)
                    return v;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return null;
    }

    /// <summary>Version of the running program (the release build stamps the suite version into every app).</summary>
    public static Version ProgramVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>What updates are compared against: the installed suite, else this program.</summary>
    public static Version CurrentVersion => InstalledVersion() ?? ProgramVersion;

    // ------------------------------------------------------------------ check and download

    public static List<IUpdateSource> Sources(UpdateSettings settings) => UpdateSourceList.Create(settings, UnprotectToken, Http);

    /// <summary>Checks every configured source (or just <paramref name="only"/>) and records when it last checked.</summary>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct, IEnumerable<IUpdateSource>? only = null)
    {
        var settings = LoadSettings();
        var sources = only?.ToList() ?? Sources(settings);
        var result = await Task.Run(() => UpdateChecker.CheckAsync(sources, CurrentVersion, Trust, ct), ct);
        if (only == null)
        {
            settings = LoadSettings();
            settings.LastCheckUtc = DateTime.UtcNow;
            SaveSettings(settings);
        }
        return result;
    }

    public static async Task<string> DownloadAsync(UpdateOffer offer, IProgress<double>? progress, CancellationToken ct)
    {
        var path = await Task.Run(() => UpdateChecker.DownloadAsync(offer, DownloadFolder, progress, ct), ct);
        UpdateChecker.CleanDownloads(DownloadFolder, Path.GetFileName(path));
        return path;
    }

    // ------------------------------------------------------------------ install

    /// <summary>Other Field Suite programs running now (they must close before the installer replaces their files).</summary>
    public static List<Process> OtherSuitePrograms()
    {
        var me = Environment.ProcessId;
        return SuiteInfo.ProcessNames.SelectMany(Process.GetProcessesByName).Where(p => p.Id != me).ToList();
    }

    public static string FriendlyName(Process p) => p.ProcessName switch
    {
        "IncidentReporting" => "Incident Report Manager",
        "FieldToolkit" => "Field Toolkit",
        "FieldMap" => "Field Map",
        var n => n
    };

    /// <summary>
    /// Starts the upgrade and returns at once; the caller must then exit. A small hidden PowerShell waits for this
    /// program to close, runs Windows Installer (Windows asks for administrator permission), records the result for
    /// the next start, and reopens <paramref name="relaunch"/>. Unattended runs install silently and reopen nothing.
    /// </summary>
    public static void StartInstall(string msiPath, string version, string? relaunch, bool unattended)
    {
        Directory.CreateDirectory(DownloadFolder);
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var log = Path.Combine(DownloadFolder, $"install-{version}.log");
        var ui = unattended ? "/qn" : "/passive";
        var script = string.Join("; ", new[]
        {
            "$q = [char]34",
            $"Wait-Process -Id {Environment.ProcessId} -Timeout 120 -ErrorAction SilentlyContinue",
            "$code = 1602",
            $"try {{ $p = Start-Process -FilePath 'msiexec.exe' -ArgumentList ('/i ' + $q + {Q(msiPath)} + $q + ' {ui} /norestart /l*v ' + $q + {Q(log)} + $q) -Verb RunAs -Wait -PassThru; $code = $p.ExitCode }} catch {{ $code = 1602 }}",
            $"Set-Content -LiteralPath {Q(ResultPath)} -Value ({Q(version + "|")} + $code)",
            relaunch != null && !unattended ? $"Start-Process -FilePath {Q(relaunch)}" : "$null = 0"
        });
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = DownloadFolder
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", script })
            psi.ArgumentList.Add(a);
        TryDelete(ResultPath);
        Process.Start(psi);
    }

    /// <summary>The outcome of the last update (once): version and Windows Installer exit code.</summary>
    public static (string Version, int Code)? TakeLastResult()
    {
        try
        {
            if (!File.Exists(ResultPath))
                return null;
            var parts = File.ReadAllText(ResultPath).Trim().Split('|');
            File.Delete(ResultPath);
            return parts.Length == 2 && int.TryParse(parts[1], out var code) ? (parts[0], code) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Plain words for a Windows Installer exit code.</summary>
    public static string DescribeResult(int code) => code switch
    {
        0 => "installed",
        3010 => "installed (restart Windows to finish)",
        1602 => "cancelled (permission was not given, or the install was stopped)",
        1618 => "not installed: another installation was running; try again in a few minutes",
        1603 => "failed (see the install log in the updates folder)",
        1638 => "not installed: this or a newer version is already installed",
        _ => $"failed (Windows Installer code {code})"
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, StringBuilder productCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfo(string product, string property, StringBuilder value, ref uint length);
}
