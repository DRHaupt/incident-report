using Shared.Core.Update;

namespace Shared.Update;

/// <summary>
/// Unattended updates for IT and scripted roll-outs (and the installer test in CI), from any of the three programs:
/// <code>FieldToolkit.exe --update-from "E:\FieldSuite-Updates" --unattended</code>
/// The source is a folder or an https:// address of fieldsuite-update.json. The same checks apply as in the app
/// (newer version, signature, checksum); the install runs silently. Progress goes to update-cli.log in the updates folder.
/// Exit code: 0 install started, 1 no update, 2 error.
/// </summary>
public static class UpdateCommandLine
{
    /// <summary>Runs the update command if <paramref name="args"/> asks for it; returns its exit code, else null.</summary>
    public static int? TryRun(string[] args)
    {
        var from = Array.FindIndex(args, a => a.Equals("--update-from", StringComparison.OrdinalIgnoreCase));
        if (from < 0)
            return null;
        if (from + 1 >= args.Length || !args.Any(a => a.Equals("--unattended", StringComparison.OrdinalIgnoreCase)))
        {
            Log("Usage: --update-from <folder or https URL> --unattended");
            return 2;
        }
        try
        {
            return Run(args[from + 1]).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log("Failed: " + ex);
            return 2;
        }
    }

    private static async Task<int> Run(string location)
    {
        IUpdateSource source = Uri.TryCreate(location, UriKind.Absolute, out var url) && url.Scheme is "https" or "http"
            ? new UrlUpdateSource(url, new HttpClient())
            : new FolderUpdateSource(Path.GetFullPath(location));
        Log($"Checking {source.Name} (installed: {SuiteUpdater.InstalledVersion()?.ToString(3) ?? "not installed"})");
        using var cts = new CancellationTokenSource(TimeSpan.FromHours(1));
        var result = await SuiteUpdater.CheckAsync(cts.Token, new[] { source });
        foreach (var (name, message) in result.Report)
            Log($"  {name}: {message}");
        if (result.Best is not { } offer)
            return 1;
        var msi = await SuiteUpdater.DownloadAsync(offer, null, cts.Token);
        Log($"Verified {msi} ({offer.Trust}); starting silent install of {offer.Manifest.Version}");
        SuiteUpdater.StartInstall(msi, offer.Manifest.Version, relaunch: null, unattended: true);
        return 0;
    }

    private static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(SuiteUpdater.DownloadFolder);
            File.AppendAllText(Path.Combine(SuiteUpdater.DownloadFolder, "update-cli.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
