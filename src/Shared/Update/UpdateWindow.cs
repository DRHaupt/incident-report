using System.Diagnostics;
using Shared.Core.Update;
using Shared.UI;

namespace Shared.Update;

/// <summary>Check for, download and install Field Suite updates, and choose where to look for them.</summary>
public sealed class UpdateWindow : Form
{
    private readonly WrapLabel _status;
    private readonly WrapLabel _result;
    private readonly ProgressBar _progress = new() { Height = 24, Visible = false, Maximum = 1000, Margin = new Padding(0, 6, 0, 6) };
    private readonly Button _check;
    private readonly Button _install;
    private readonly Button _skip;
    private readonly CheckBox _auto = new() { Text = "Check automatically when an app starts (about once a day)", AutoSize = true };
    private readonly CheckBox _usb = new() { Text = "Look on USB sticks and SD cards (a FieldSuite-Updates folder)", AutoSize = true };
    private readonly ListBox _sources = new() { Height = 150, IntegralHeight = false };
    private UpdateSettings _settings = SuiteUpdater.LoadSettings();
    private UpdateOffer? _offer;
    private CancellationTokenSource? _busy;

    /// <summary>Raised after the user skips a version or an install is starting (the banner can hide).</summary>
    public event EventHandler? OfferHandled;

    public UpdateWindow(UpdateOffer? offer = null)
    {
        Text = "Field Suite updates";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(600, 700);
        MinimumSize = new Size(460, 480);
        Padding = new Padding(14);

        _status = Ui.Text("", FontRole.Bold);
        _result = Ui.Text("");
        _check = Ui.Button("Check now", ButtonStyle.Primary, CheckOrCancel);
        _install = Ui.Button("Install", ButtonStyle.Success, Install);
        _skip = Ui.Button("Skip this version", ButtonStyle.Secondary, Skip);
        _install.Visible = _skip.Visible = false;

        var stack = Ui.Stack(new Padding(0));
        stack.Controls.Add(Ui.Text("Updates", FontRole.Heading));
        stack.Controls.Add(_status);
        stack.Controls.Add(_result);
        stack.Controls.Add(_progress);
        stack.Controls.Add(_check);
        stack.Controls.Add(_install);
        stack.Controls.Add(_skip);

        stack.Controls.Add(Ui.Text("Where to look", FontRole.Heading, padding: new Padding(0, 16, 0, 2)));
        stack.Controls.Add(_auto);
        stack.Controls.Add(_usb);
        stack.Controls.Add(_sources);
        stack.Controls.Add(Ui.ButtonRow(
            Ui.Button("Add folder / share…", ButtonStyle.Secondary, AddFolder, FontRole.Normal),
            Ui.Button("Add web address…", ButtonStyle.Secondary, AddUrl, FontRole.Normal)).Also(r => r.Height = Ui.TargetHeight + 8));
        stack.Controls.Add(Ui.ButtonRow(
            Ui.Button("Add GitHub…", ButtonStyle.Secondary, AddGitHub, FontRole.Normal),
            Ui.Button("Access token…", ButtonStyle.Secondary, SetToken, FontRole.Normal)).Also(r => r.Height = Ui.TargetHeight + 8));
        stack.Controls.Add(Ui.ButtonRow(
            Ui.Button("Turn on / off", ButtonStyle.Secondary, ToggleSource, FontRole.Normal),
            Ui.Button("Remove", ButtonStyle.Secondary, RemoveSource, FontRole.Normal)).Also(r => r.Height = Ui.TargetHeight + 8));
        stack.Controls.Add(Ui.Text(
            "No internet? Copy fieldsuite-update.json and the FieldSuite-….msi from a release into a folder named " +
            "FieldSuite-Updates on a USB stick (or a shared folder added above), plug it in and press Check now. " +
            "Your reports, maps, drawings and settings are kept when an update installs.", fore: Brand.Muted, padding: new Padding(0, 10, 0, 0)));
        stack.Controls.Add(Ui.Button("Open updates folder (downloads and install logs)", ButtonStyle.Secondary, OpenFolder, FontRole.Normal));

        var close = Ui.Button("Close", ButtonStyle.Secondary, Close);
        close.Dock = DockStyle.Bottom;
        Controls.Add(stack);
        Controls.Add(close);
        CancelButton = close;

        _auto.CheckedChanged += (_, _) => { _settings.AutoCheck = _auto.Checked; Save(); };
        _usb.CheckedChanged += (_, _) => { _settings.ScanRemovableDrives = _usb.Checked; Save(); };
        _auto.Checked = _settings.AutoCheck;
        _usb.Checked = _settings.ScanRemovableDrives;
        RefreshSources();
        RefreshStatus();
        UiMode.TrackDialog(this);
        FormClosing += (_, e) =>
        {
            if (_busy != null)
                _busy.Cancel();
        };
        if (offer != null)
            Load += (_, _) => ShowOffer(offer, null);
    }

    // ------------------------------------------------------------------ status

    private void RefreshStatus()
    {
        var installed = SuiteUpdater.InstalledVersion();
        var last = _settings.LastCheckUtc is { } t ? t.ToLocalTime().ToString("d MMM yyyy HH:mm") : "never";
        _status.Text = installed != null
            ? $"Installed: Field Suite {installed.ToString(3)} · Last checked: {last}"
            : $"This copy ({SuiteUpdater.ProgramVersion.ToString(3)}) was not installed with the Field Suite installer; installing an update puts Field Suite in Program Files. · Last checked: {last}";
    }

    private void ShowOffer(UpdateOffer? offer, UpdateCheckResult? result)
    {
        _offer = offer;
        var lines = new List<string>();
        if (offer != null)
        {
            var m = offer.Manifest;
            lines.Add($"Field Suite {m.Version} is available (published {m.PublishedUtc.ToLocalTime():d MMM yyyy}, {m.Size / 1048576.0:0} MB, from {offer.Source.Name}).");
            if (!string.IsNullOrWhiteSpace(m.Notes))
                lines.Add("What's new: " + m.Notes);
            if (offer.Trust == UpdateTrustResult.Unsigned)
                lines.Add("⚠ This update is not signed, and this installation has no release key to check it with. Install it only if you trust where it came from.");
        }
        else if (result != null)
        {
            lines.Add("No update available.");
        }
        if (result != null)
            lines.AddRange(result.Report.Select(r => $"• {r.Source}: {r.Message}"));
        _result.Text = string.Join(Environment.NewLine, lines);
        _install.Text = offer != null ? $"Install {offer.Manifest.Version}" : "Install";
        _install.Visible = _skip.Visible = offer != null;
        RefreshStatus();
    }

    // ------------------------------------------------------------------ check / download / install

    private async void CheckOrCancel()
    {
        if (_busy != null)
        {
            _busy.Cancel();
            return;
        }
        if (_settings.Sources.All(s => !s.Enabled) && !_settings.ScanRemovableDrives)
        {
            _result.Text = "No places to look: add a folder, web address or GitHub repository below.";
            return;
        }
        using var cts = Busy(TimeSpan.FromMinutes(2), "Checking…");
        try
        {
            var result = await SuiteUpdater.CheckAsync(cts.Token);
            _settings = SuiteUpdater.LoadSettings();
            ShowOffer(result.Best, result);
        }
        catch (OperationCanceledException)
        {
            _result.Text = "Check stopped.";
        }
        finally
        {
            Idle();
        }
    }

    private async void Install()
    {
        if (_offer is not { } offer || _busy != null)
            return;
        if (offer.Trust == UpdateTrustResult.Unsigned &&
            MessageBox.Show(this, $"Field Suite {offer.Manifest.Version} from {offer.Source.Name} is not signed.\n\nInstall it anyway?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        string msi;
        using (var cts = Busy(TimeSpan.FromHours(2), $"Downloading Field Suite {offer.Manifest.Version}…"))
        {
            try
            {
                _progress.Visible = true;
                var progress = new Progress<double>(p => _progress.Value = Math.Clamp((int)(p * 1000), 0, 1000));
                msi = await SuiteUpdater.DownloadAsync(offer, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                _result.Text = "Download stopped.";
                return;
            }
            catch (Exception ex) when (ex is UpdateSourceException or InvalidDataException or IOException or UnauthorizedAccessException or HttpRequestException)
            {
                _result.Text = "The update could not be downloaded: " + ex.Message;
                return;
            }
            finally
            {
                _progress.Visible = false;
                Idle();
            }
        }

        if (!CloseOtherPrograms())
            return;
        if (MessageBox.Show(this,
                $"Ready to install Field Suite {offer.Manifest.Version}.\n\n" +
                "This app closes, Windows asks for administrator permission, a progress window shows the install, and the app opens again when it is done. " +
                "Your data and settings are kept.\n\nInstall now?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        SuiteUpdater.StartInstall(msi, offer.Manifest.Version, Environment.ProcessPath, unattended: false);
        OfferHandled?.Invoke(this, EventArgs.Empty);
        Close();
        Application.Exit();
    }

    /// <summary>Asks the other Field Suite apps to close (they can still ask to save); false if any stays open.</summary>
    private bool CloseOtherPrograms()
    {
        var others = SuiteUpdater.OtherSuitePrograms();
        if (others.Count == 0)
            return true;
        var names = string.Join(", ", others.Select(SuiteUpdater.FriendlyName).Distinct());
        if (MessageBox.Show(this, $"{names} must close before the update installs.\n\nClose them now? (They may ask you to save first.)", Text,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            return false;
        foreach (var p in others)
        {
            try
            {
                p.CloseMainWindow();
            }
            catch (InvalidOperationException)
            {
            }
        }
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && others.Any(p => !HasExited(p)))
        {
            Application.DoEvents();
            Thread.Sleep(100);
        }
        var still = others.Where(p => !HasExited(p)).Select(SuiteUpdater.FriendlyName).Distinct().ToList();
        if (still.Count == 0)
            return true;
        MessageBox.Show(this, $"{string.Join(", ", still)} is still open. Close it, then press Install again.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private static bool HasExited(Process p)
    {
        try
        {
            return p.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void Skip()
    {
        if (_offer == null)
            return;
        _settings.SkippedVersion = _offer.Manifest.Version;
        Save();
        _result.Text = $"You won't be reminded about {_offer.Manifest.Version}. A newer version will still show.";
        _install.Visible = _skip.Visible = false;
        _offer = null;
        OfferHandled?.Invoke(this, EventArgs.Empty);
    }

    private CancellationTokenSource Busy(TimeSpan timeout, string message)
    {
        _busy = new CancellationTokenSource(timeout);
        _result.Text = message;
        _check.Text = "Stop";
        _install.Enabled = _skip.Enabled = false;
        return _busy;
    }

    private void Idle()
    {
        _busy = null;
        if (IsDisposed)
            return;
        _check.Text = "Check now";
        _install.Enabled = _skip.Enabled = true;
    }

    // ------------------------------------------------------------------ sources

    private void RefreshSources()
    {
        _sources.Items.Clear();
        foreach (var s in _settings.Sources)
        {
            var kind = s.Kind switch { UpdateSourceKind.Folder => "Folder", UpdateSourceKind.Url => "Web", _ => "GitHub" };
            var token = s.Kind == UpdateSourceKind.GitHub ? (s.ProtectedToken != null ? " (token saved)" : " (no token)") : "";
            _sources.Items.Add($"{(s.Enabled ? "" : "[off] ")}{kind}: {s.Location}{token}");
        }
    }

    private void Save()
    {
        SuiteUpdater.SaveSettings(_settings);
        RefreshSources();
    }

    private void Add(UpdateSourceKind kind, string location)
    {
        if (_settings.Sources.Any(s => s.Kind == kind && string.Equals(s.Location, location, StringComparison.OrdinalIgnoreCase)))
            return;
        _settings.Sources.Add(new UpdateSourceSetting { Kind = kind, Location = location });
        Save();
    }

    private void AddFolder()
    {
        using var d = new FolderBrowserDialog { Description = "Folder or network share holding fieldsuite-update.json and the installer", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) == DialogResult.OK)
            Add(UpdateSourceKind.Folder, d.SelectedPath);
    }

    private void AddUrl()
    {
        var url = InputDialog.Ask(this, "Web address", "https:// address of fieldsuite-update.json", "https://");
        if (string.IsNullOrWhiteSpace(url))
            return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
        {
            MessageBox.Show(this, "Enter a full https:// address.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Add(UpdateSourceKind.Url, u.AbsoluteUri);
    }

    private void AddGitHub()
    {
        var repo = InputDialog.Ask(this, "GitHub repository", "owner/repository whose latest release holds the installer", UpdateSettings.DefaultRepository);
        if (string.IsNullOrWhiteSpace(repo))
            return;
        try
        {
            _ = new GitHubUpdateSource(repo, null, new HttpClient());
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Add(UpdateSourceKind.GitHub, repo.Trim().Trim('/'));
    }

    private UpdateSourceSetting? Selected => _sources.SelectedIndex >= 0 && _sources.SelectedIndex < _settings.Sources.Count ? _settings.Sources[_sources.SelectedIndex] : null;

    private void SetToken()
    {
        var s = Selected is { Kind: UpdateSourceKind.GitHub } sel ? sel : _settings.Sources.FirstOrDefault(x => x.Kind == UpdateSourceKind.GitHub);
        if (s == null)
        {
            MessageBox.Show(this, "Add a GitHub repository first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var token = InputDialog.Ask(this, "GitHub access token",
            $"Token with read-only access to {s.Location} (needed while the repository is private). Leave empty to remove it.");
        if (token == null)
            return;
        s.ProtectedToken = token.Length == 0 ? null : SuiteUpdater.ProtectToken(token);
        Save();
    }

    private void ToggleSource()
    {
        if (Selected is not { } s)
            return;
        var index = _sources.SelectedIndex;
        s.Enabled = !s.Enabled;
        Save();
        _sources.SelectedIndex = index;
    }

    private void RemoveSource()
    {
        if (Selected is not { } s)
            return;
        _settings.Sources.Remove(s);
        Save();
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(SuiteUpdater.DownloadFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", SuiteUpdater.DownloadFolder) { UseShellExecute = true });
    }
}
