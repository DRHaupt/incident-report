using FieldMap.Core.Data;
using FieldMap.Core.Sample;
using FieldMap.Framework;
using Shared;
using Shared.UI;

namespace FieldMap.Views;

/// <summary>
/// Map data management: data folder, installed regions (version, data date, sources), install/update with size shown
/// first, rollback, validation, offline USB packages, building from local files, AED imports and settings.
/// </summary>
public sealed class DataWindow : ToolWindow
{
    public const string ToolId = "data";

    private readonly FlowLayoutPanel _stack = Ui.Stack(new Padding(12));
    private readonly Panel _progressPanel = new() { Dock = DockStyle.Bottom, Height = 96, Visible = false, Padding = new Padding(10, 6, 10, 6), BackColor = Brand.Panel };
    private readonly Label _stage = new() { Dock = DockStyle.Top, Height = 30, ForeColor = Brand.Navy, AutoEllipsis = true };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 22, Maximum = 1000 };
    private readonly HashSet<string> _selected = new();
    private readonly Dictionary<string, List<SourceStatus>> _sizes = new();
    private CancellationTokenSource? _cancel;
    private string _log = "";
    private bool _busy;

    public DataWindow() : base(ToolRegistry.Find(ToolId)!)
    {
        var cancel = Ui.Button("Cancel", ButtonStyle.Danger, () => _cancel?.Cancel());
        cancel.Dock = DockStyle.Right;
        cancel.Width = 140;
        _stage.Styled(FontRole.Bold);
        _progressPanel.Controls.Add(_bar);
        _progressPanel.Controls.Add(_stage);
        _progressPanel.Controls.Add(cancel);
        Body.Controls.Add(_stack);
        Body.Controls.Add(_progressPanel);
        Build();
    }

    protected override void OnMapDataChanged() => Build();

    private UpdateService Service => new(AppServices.Store);

    private void Add(Control c) => _stack.Controls.Add(c);

    private void Line(string text, FontRole font = FontRole.Normal, Color? color = null) => Add(Ui.Text(text, font, color ?? Brand.Navy, new Padding(0, 3, 0, 3)));

    private Button Button(string text, ButtonStyle style, Action click)
    {
        var b = Ui.Button(text, style, click);
        b.Margin = new Padding(0, 3, 0, 3);
        b.Enabled = !_busy;
        return b;
    }

    private void Row(params Button[] buttons)
    {
        foreach (var b in buttons)
            b.Enabled = !_busy;
        var row = Ui.ButtonRow(buttons);
        row.Height = Ui.TargetHeight + 6;
        row.Margin = new Padding(0, 3, 0, 3);
        row.FixedSize();
        Add(row);
    }

    private void Build()
    {
        if (IsDisposed)
            return;
        _stack.SuspendLayout();
        foreach (Control c in _stack.Controls.Cast<Control>().ToList())
            c.Dispose();
        _stack.Controls.Clear();
        var store = AppServices.Store;

        Line("Data folder", FontRole.Heading);
        Line($"{store.Root}");
        Line("Map data can be large (≈ 1–1.5 GB per version for BC, ≈ 0.5–0.9 GB for Alberta; the previous version is kept for rollback). A second drive or SD card is fine.", FontRole.Normal, Brand.Muted);
        Row(Button("Choose data folder…", ButtonStyle.Secondary, ChooseFolder), Button("Settings…", ButtonStyle.Secondary, () => { using var d = new SettingsDialog(); d.ShowDialog(this); }));

        Line("Installed", FontRole.Heading);
        var installed = store.InstalledRegions().ToList();
        if (installed.Count == 0)
            Line("No map data installed.", FontRole.Bold, Brand.Danger);
        foreach (var id in installed)
        {
            var region = id;
            var m = store.CurrentManifest(id)!;
            var (_, message) = store.ResolveCurrent(id);
            var prev = store.PreviousVersion(id);
            Line($"{m.RegionName}{(m.IsSample ? " – SAMPLE DATA" : "")}", FontRole.Bold, m.IsSample ? Brand.Danger : Brand.Navy);
            Line($"Version {m.Version} · built {m.BuiltUtc.ToLocalTime():yyyy-MM-dd HH:mm} · data date {m.DataDate?.ToLocalTime():yyyy-MM-dd} · {store.SizeOnDisk(id) / 1_000_000.0:N0} MB on disk" +
                 (prev != null && prev != m.Version ? $" · previous {prev} kept" : ""));
            foreach (var s in m.Sources)
                Line($"• {s.Name} – {s.Licence} – downloaded {s.DownloadedUtc?.ToLocalTime():yyyy-MM-dd}", FontRole.Normal, Brand.Muted);
            if (message != null)
                Line("⚠ " + message, FontRole.Bold, Brand.Danger);
            Row(Button("Validate", ButtonStyle.Secondary, () => Validate(region)),
                Button("Roll back", ButtonStyle.Secondary, () => Rollback(region)),
                Button("Export USB package…", ButtonStyle.Secondary, () => _ = ExportAsync(region)),
                Button("Remove", ButtonStyle.Danger, () => Remove(region)));
        }

        Line("Update map data", FontRole.Heading);
        Line("Downloads the latest sources (resuming interrupted downloads, checking checksums), rebuilds tiles, routing and search, validates them, then switches over in one step. If anything fails, the map in use is not changed.", FontRole.Normal, Brand.Muted);
        var updatable = installed.Where(id => id != SampleRegion.RegionId && AppServices.Catalog.Find(id) != null).ToList();
        Add(Button(updatable.Count == 0 ? "Update map data (install a region first)" : $"Update map data ({string.Join(", ", updatable.Select(id => AppServices.Catalog.Find(id)!.Name))})",
            ButtonStyle.Accent, () => _ = UpdateAsync(updatable)));

        Line("Install regions", FontRole.Heading);
        foreach (var r in AppServices.Catalog.Regions)
        {
            var region = r;
            var size = _sizes.TryGetValue(r.Id, out var st) ? st.Sum(x => x.Bytes ?? 0) : r.ApproxDownloadBytes;
            var label = $"{(_selected.Contains(r.Id) ? "✓ " : "")}{r.Name} – download {(size / 1_000_000.0):N0} MB{(_sizes.ContainsKey(r.Id) ? "" : " (approx.)")}{(installed.Contains(r.Id) ? " – installed" : "")}";
            var b = Button(label, _selected.Contains(r.Id) ? ButtonStyle.Primary : ButtonStyle.Secondary, () =>
            {
                if (!_selected.Add(region.Id))
                    _selected.Remove(region.Id);
                Build();
            });
            b.TextAlign = ContentAlignment.MiddleLeft;
            Add(b);
            if (r.Notes.Length > 0)
                Line(r.Notes, FontRole.Normal, Brand.Muted);
            if (_sizes.TryGetValue(r.Id, out var statuses))
                foreach (var s in statuses)
                    Line($"• {s.Source.Name}: {(s.Bytes is { } bytes ? $"{bytes / 1_000_000.0:N0} MB" : "size unknown")}{(s.LastModifiedUtc is { } lm ? $", updated {lm:yyyy-MM-dd}" : "")}{(s.Error != null ? $" – {s.Error}" : "")}", FontRole.Normal, s.Error != null ? Brand.Danger : Brand.Muted);
        }
        Row(Button("Check sizes", ButtonStyle.Secondary, () => _ = CheckSizesAsync()),
            Button("Download, build and install", ButtonStyle.Primary, () => _ = UpdateAsync(_selected.ToList())));

        Line("Vehicles without internet", FontRole.Heading);
        Line("On a connected PC: install or update the region, then Export USB package. On the vehicle: Install from USB. Packages are checked file by file before anything changes.", FontRole.Normal, Brand.Muted);
        Row(Button("Install from USB…", ButtonStyle.Primary, () => _ = InstallPackageAsync()),
            Button("Build from files…", ButtonStyle.Secondary, () => _ = BuildFromFilesAsync()));

        Line("Imagery (satellite / aerial)", FontRole.Heading);
        Line("Imagery packages are MBTiles files of JPEG/PNG tiles (e.g. made with MapBuilder imagery, or exported from a service your organisation is licensed for). They work offline and show under Layers → Base map.", FontRole.Normal, Brand.Muted);
        var packages = AppServices.Imagery.Packages();
        if (packages.Count == 0)
            Line("No imagery packages installed.", FontRole.Normal, Brand.Muted);
        foreach (var pkg in packages)
        {
            var package = pkg;
            var bytes = File.Exists(pkg.FilePath) ? new FileInfo(pkg.FilePath!).Length : 0;
            Line($"{pkg.Name} – {bytes / 1_000_000.0:N0} MB, zoom {pkg.MinZoom}–{pkg.MaxZoom}" + (pkg.Attribution.Length > 0 ? $" · {pkg.Attribution}" : "") + (pkg.Licence.Length > 0 ? $" · {pkg.Licence}" : ""), FontRole.Bold);
            Row(Button("Remove", ButtonStyle.Danger, () =>
            {
                if (MessageBox.Show(this, $"Remove the imagery package {package.Name}?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                try
                {
                    AppServices.Imagery.Delete(package);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                Build();
            }));
        }
        Row(Button("Install imagery package…", ButtonStyle.Secondary, InstallImagery));

        Line("AEDs", FontRole.Heading);
        var aeds = UpdateService.AedImports(store);
        Line(aeds.Count == 0 ? "No AED registry imported. OpenStreetMap AEDs are always included." :
            "Imported: " + string.Join(", ", aeds.Select(a => $"{Path.GetFileName(a.Path)} ({a.SourceName})")), FontRole.Normal, Brand.Muted);
        Line("CSV columns: name, latitude, longitude, address, access, location, last_verified, source, id (or GeoJSON points). Imported AEDs replace OSM entries within 25 m and keep both sources.", FontRole.Normal, Brand.Muted);
        Row(Button("Import AED file…", ButtonStyle.Secondary, () => _ = ImportAedAsync()));

        Line("Sample data", FontRole.Heading);
        Line("A fictitious region for trying the app (red SAMPLE DATA banner).", FontRole.Normal, Brand.Muted);
        Row(Button(installed.Contains(SampleRegion.RegionId) ? "Rebuild sample" : "Install sample", ButtonStyle.Secondary, () => _ = InstallSampleAsync()),
            Button("Remove sample", ButtonStyle.Secondary, () => Remove(SampleRegion.RegionId)));

        if (_log.Length > 0)
        {
            Line("Last result", FontRole.Heading);
            var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 220, Text = _log.Replace("\n", Environment.NewLine), Margin = new Padding(0, 3, 0, 3) };
            Add(box);
        }
        UiMode.Apply(_stack);
        _stack.ResumeLayout(true);
    }

    // ------------------------------------------------------------------ operations

    private void InstallImagery()
    {
        using var d = new OpenFileDialog { Filter = "Imagery package (*.mbtiles)|*.mbtiles", Title = "Install imagery package" };
        if (d.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            var pkg = AppServices.Imagery.Install(d.FileName);
            AppServices.Imagery.Apply();
            _log = $"Installed imagery {pkg.Name} (zoom {pkg.MinZoom}–{pkg.MaxZoom}). Choose it under Layers → Base map.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            _log = "Could not install the imagery package: " + ex.Message;
        }
        Build();
    }

    private void SetBusy(bool busy, string stage = "")
    {
        _busy = busy;
        _progressPanel.Visible = busy;
        _stage.Text = stage;
        _bar.Value = 0;
        Build();
    }

    private IProgress<(string Stage, double Fraction)> Progress() => new Progress<(string Stage, double Fraction)>(p =>
    {
        if (IsDisposed)
            return;
        _stage.Text = p.Stage;
        _bar.Value = (int)Math.Clamp(p.Fraction * 1000, 0, 1000);
    });

    private void Finish(string log, bool reload)
    {
        _log = log;
        _busy = false;
        _progressPanel.Visible = false;
        if (reload)
            AppServices.ReloadData();
        Build();
    }

    private void ChooseFolder()
    {
        using var d = new FolderBrowserDialog { Description = "Folder for Field Map data (may be a second drive or SD card)", UseDescriptionForTitle = true, SelectedPath = AppServices.Store.Root };
        if (d.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            AppServices.UseDataFolder(d.SelectedPath);
            _log = $"Using data folder {d.SelectedPath}. Map data already in this folder was loaded.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, "That folder can't be used: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        Build();
    }

    private void Validate(string id)
    {
        var dir = AppServices.Store.CurrentDir(id);
        if (dir == null)
            return;
        Cursor = Cursors.WaitCursor;
        var report = RegionValidator.Validate(dir);
        Cursor = Cursors.Default;
        _log = (report.Ok ? "Validation passed.\n" : "Validation FAILED.\n") + report + "\n\nSpot-check these entries against the source:\n" + string.Join("\n", report.SpotChecks);
        Build();
    }

    private void Rollback(string id)
    {
        var prev = AppServices.Store.PreviousVersion(id);
        if (prev == null)
        {
            MessageBox.Show(this, "There is no previous version to go back to.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"Switch back to version {prev}? You can switch forward again the same way.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        var now = AppServices.Store.Rollback(id);
        Finish(now == null ? "Rollback was not possible (the previous version is missing or damaged)." : $"Now using version {now}.", reload: now != null);
    }

    private void Remove(string id)
    {
        if (!AppServices.Store.InstalledRegions().Contains(id))
            return;
        if (MessageBox.Show(this, $"Remove the {id} map data from this computer?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        AppServices.Data.Dispose();
        AppServices.Store.RemoveRegion(id);
        Finish($"Removed {id}.", reload: true);
    }

    private async Task CheckSizesAsync()
    {
        var ids = _selected.Count > 0 ? _selected.ToList() : AppServices.Catalog.Regions.Select(r => r.Id).ToList();
        SetBusy(true, "Asking the servers for sizes…");
        foreach (var id in ids)
            if (AppServices.Catalog.Find(id) is { } r)
                _sizes[id] = await Service.CheckSourcesAsync(r);
        Finish(_sizes.Values.SelectMany(x => x).Any(x => x.Error != null) ? "Some servers could not be reached (offline?). Sizes shown are estimates." : "Sizes updated.", reload: false);
    }

    private async Task UpdateAsync(List<string> ids)
    {
        if (ids.Count == 0)
        {
            MessageBox.Show(this, "Choose one or more regions first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var regions = ids.Select(id => AppServices.Catalog.Find(id)).OfType<RegionDefinition>().ToList();
        var total = regions.Sum(r => _sizes.TryGetValue(r.Id, out var s) ? s.Sum(x => x.Bytes ?? 0) : r.ApproxDownloadBytes);
        if (MessageBox.Show(this, $"Download up to {total / 1_000_000.0:N0} MB and rebuild {string.Join(", ", regions.Select(r => r.Name))}?\n\n" +
                                  "Building British Columbia takes 20–40 minutes and several GB of memory on a laptop. The current map stays usable until the new data is validated and switched in.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _cancel = new CancellationTokenSource();
        SetBusy(true, "Starting…");
        var log = new List<string>();
        var any = false;
        foreach (var r in regions)
        {
            try
            {
                var result = await Service.UpdateAsync(r, Progress(), _cancel.Token);
                log.Add($"{r.Name}: {result.Message}");
                if (result.Report != null)
                    log.Add(result.Report.ToString());
                any |= result.Success;
            }
            catch (OperationCanceledException)
            {
                log.Add($"{r.Name}: cancelled. The map data in use was not changed; downloads resume next time.");
                break;
            }
        }
        Finish(string.Join("\n", log), reload: any);
    }

    private async Task ExportAsync(string id)
    {
        using var d = new FolderBrowserDialog { Description = "Where to write the update package (e.g. a USB stick)", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK)
            return;
        SetBusy(true, "Writing update package…");
        try
        {
            var progress = Progress();
            var path = await Task.Run(() => UpdatePackage.Export(AppServices.Store, id, d.SelectedPath, new Progress<double>(f => progress.Report(("Writing update package", f)))));
            Finish($"Wrote {path} ({new FileInfo(path).Length / 1_000_000.0:N0} MB). On the vehicle: Data & updates → Install from USB.", reload: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Finish("Export failed: " + ex.Message, reload: false);
        }
    }

    private async Task InstallPackageAsync()
    {
        using var d = new OpenFileDialog { Filter = "Field Map update package (*.fmupdate)|*.fmupdate", Title = "Choose the update package (e.g. on the USB stick)" };
        if (d.ShowDialog(this) != DialogResult.OK)
            return;
        SetBusy(true, "Checking the package…");
        var progress = Progress();
        var result = await Task.Run(() => UpdatePackage.Install(AppServices.Store, d.FileName, progress));
        Finish(result.Message + (result.Report != null ? "\n" + result.Report : ""), reload: result.Success);
    }

    private async Task BuildFromFilesAsync()
    {
        var region = _selected.Select(id => AppServices.Catalog.Find(id)).OfType<RegionDefinition>().FirstOrDefault(r => r.Sources.Count(s => s.Kind == SourceKinds.OsmPbf) == 1);
        if (region == null)
        {
            MessageBox.Show(this, "Select British Columbia or Alberta under Install regions first, then choose its downloaded files.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var pbf = new OpenFileDialog { Filter = "OpenStreetMap extract (*.osm.pbf)|*.osm.pbf|OSM XML (*.osm)|*.osm", Title = $"OpenStreetMap extract for {region.Name}" };
        if (pbf.ShowDialog(this) != DialogResult.OK)
            return;
        using var oda = new OpenFileDialog { Filter = "Address points (*.zip;*.csv)|*.zip;*.csv", Title = "Address points (optional – Cancel to skip)" };
        var addresses = oda.ShowDialog(this) == DialogResult.OK ? new[] { oda.FileName } : Array.Empty<string>();
        _cancel = new CancellationTokenSource();
        SetBusy(true, "Building…");
        var progress = Progress();
        try
        {
            var files = UpdateService.LocalSources(region, pbf.FileName, addresses, Array.Empty<string>());
            var service = Service;
            var result = await Task.Run(() => service.BuildAndPublish(region, files, progress, _cancel.Token));
            Finish(result.Message + (result.Report != null ? "\n" + result.Report : ""), reload: result.Success);
        }
        catch (ArgumentException ex)
        {
            Finish(ex.Message, reload: false);
        }
    }

    private async Task ImportAedAsync()
    {
        using var d = new OpenFileDialog { Filter = "AED registry (*.csv;*.geojson;*.json)|*.csv;*.geojson;*.json" };
        if (d.ShowDialog(this) != DialogResult.OK)
            return;
        List<Core.Build.Poi> aeds;
        try
        {
            aeds = PoiImport.ReadAeds(d.FileName, Path.GetFileNameWithoutExtension(d.FileName));
        }
        catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, "The file could not be read: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (aeds.Count == 0)
        {
            MessageBox.Show(this, "No AEDs with valid coordinates were found. Check the column names (latitude, longitude…).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var source = InputDialog.Ask(this, "AED source", $"{aeds.Count} AEDs found. Name of this source (shown on every AED):", Path.GetFileNameWithoutExtension(d.FileName));
        if (string.IsNullOrWhiteSpace(source))
            return;
        UpdateService.AddAedImport(AppServices.Store, d.FileName, source);
        // Apply now: rebuild installed regions from the cached downloads (or the sample).
        SetBusy(true, "Merging AEDs…");
        var log = new List<string> { $"Imported {aeds.Count} AEDs from {Path.GetFileName(d.FileName)} as \"{source}\". They are merged into every future build." };
        var any = false;
        var progress = Progress();
        foreach (var id in AppServices.Store.InstalledRegions().ToList())
        {
            var service = Service;
            if (id == SampleRegion.RegionId)
            {
                var r = await Task.Run(() => service.InstallSample(progress));
                log.Add($"Sample: {r.Message}");
                any |= r.Success;
            }
            else if (AppServices.Catalog.Find(id) is { } def && service.CachedSources(def) is { } files)
            {
                var r = await Task.Run(() => service.BuildAndPublish(def, files, progress, CancellationToken.None));
                log.Add($"{def.Name}: {r.Message}");
                any |= r.Success;
            }
            else
            {
                log.Add($"{id}: the source downloads are not on this computer; the AEDs will be merged at the next Update map data.");
            }
        }
        Finish(string.Join("\n", log), reload: any);
    }

    private async Task InstallSampleAsync()
    {
        SetBusy(true, "Building the sample region…");
        var service = Service;
        var progress = Progress();
        var result = await Task.Run(() => service.InstallSample(progress));
        Finish(result.Message, reload: result.Success);
    }
}
