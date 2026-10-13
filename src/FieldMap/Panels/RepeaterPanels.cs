using System.Globalization;
using FieldMap.Core.Radio;
using FieldMap.Services;
using FieldMap.Views;
using Shared;
using Shared.Core.Geo;
using Shared.UI;

namespace FieldMap.Panels;

/// <summary>One repeater: how to use it, where it is, and its predicted coverage for a handheld or a vehicle radio.</summary>
public sealed class RepeaterPanel : SidePanel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly double[] Radii = { 25, 50, 80 };
    private readonly string _id;
    private string? _status;

    public RepeaterPanel(MapWindow map, string id) : base(map, "Repeater")
    {
        _id = id;
        Build();
        AppServices.Coverage.Changed += OnCoverage;
    }

    public override void OnClosing()
    {
        AppServices.Coverage.Changed -= OnCoverage;
        base.OnClosing();
    }

    private void OnCoverage(object? sender, EventArgs e)
    {
        if (!IsDisposed)
            BeginInvoke(Build);
    }

    private void Build()
    {
        Stack.SuspendLayout();
        Clear();
        if (AppServices.Repeaters.Find(_id) is not { } r)
        {
            AddText("This repeater was removed.", FontRole.Bold, Brand.Danger);
            Stack.ResumeLayout(true);
            return;
        }
        Title = r.Label;
        AddText(r.Label + (r.Name.Length > 0 && r.Name != r.Label ? " – " + r.Name : ""), FontRole.Heading);
        AddText(r.FrequencyText + (r.Mode.Length > 0 ? " · " + r.Mode : ""), FontRole.Large);
        if (r.InputMHz is { } input && Math.Abs(input - r.OutputMHz) > 0.0001)
            AddText($"Radios transmit on {input:0.000###} MHz.", FontRole.Normal);
        AddText($"{Repeater.SourceLabel(r.Source)}" + (r.Owner.Length > 0 ? $" · {r.Owner}" : "") + (r.SourceName.Length > 0 ? $" · {r.SourceName}" : ""), FontRole.Normal, Brand.Muted);
        var terrain = AppServices.Data.TerrainAt(r.Point);
        var ground = terrain?.Elevation(r.Point);
        AddText($"{r.Lat.ToString("0.00000", Inv)}, {r.Lon.ToString("0.00000", Inv)} · {CoordinateFormat.Mgrs(r.Lat, r.Lon)}" +
                (ground is { } g ? $" · ground {g:0} m" : ""), FontRole.Normal);
        AddText($"Antenna {(r.AntennaHeightM is { } h ? $"{h:0.#} m" : $"not recorded ({CoverageService.DefaultAntennaHeightM:0} m assumed)")} above ground · " +
                $"ERP {(r.ErpWatts is { } w ? $"{w:0.#} W" : $"not recorded ({CoverageService.DefaultErpWatts:0} W assumed)")}", FontRole.Normal, Brand.Muted);
        if (r.Notes.Length > 0)
            AddText(r.Notes, FontRole.Normal);

        AddText("Predicted coverage", FontRole.Heading);
        var coverage = AppServices.Coverage;
        var radius = AppServices.Settings.CoverageRadiusKm;
        if (terrain == null)
        {
            AddText(AppServices.Data.HasTerrain
                ? "No elevation data covers this repeater's position."
                : "Needs elevation data: run Update map data (Data & updates). It now includes elevation for BC and Alberta.", FontRole.Normal, Brand.Danger);
        }
        else if (coverage.Busy)
        {
            AddText("Working it out…", FontRole.Bold);
        }
        else
        {
            AddButtons(Btn("Handheld", ButtonStyle.Primary, () => Compute(r, false)),
                       Btn("Vehicle radio", ButtonStyle.Primary, () => Compute(r, true)));
            AddButton($"Area: {radius:0} km around the site (tap to change)", ButtonStyle.Secondary, () =>
            {
                var i = Array.IndexOf(Radii, radius);
                AppServices.Settings.CoverageRadiusKm = Radii[(i + 1) % Radii.Length];
                AppServices.Settings.Save();
                Build();
            }, FontRole.Normal);
        }
        if (coverage.For(r.Id) is { } item)
        {
            AddText($"Shown: {(item.Vehicle ? "vehicle radio" : "handheld")} – green likely, amber marginal, red unlikely. " +
                    $"Likely over {item.Result.LikelyFraction:P0} of the area, marginal over {item.Result.MarginalFraction:P0}.", FontRole.Bold);
            AddButton("Hide this coverage", ButtonStyle.Secondary, () => coverage.Remove(r.Id));
        }
        if (_status != null)
            AddText(_status, FontRole.Normal, Brand.Muted);
        AddText("An estimate from terrain only (64 m elevation, one main obstacle per path): trees, buildings and several ridges in a row " +
                "make real coverage smaller. Check it on the ground before relying on it.", FontRole.Normal, Brand.Muted);

        AddText("Repeater", FontRole.Heading);
        AddButtons(Btn("Copy details", ButtonStyle.Secondary, () => Copy(FindForm()!, Details(r))),
                   Btn("Directions to site", ButtonStyle.Secondary, () => Map.ShowPanel(new DirectionsPanel(Map, null, r.Point, r.Label))));
        AddButtons(Btn("Edit…", ButtonStyle.Secondary, () =>
        {
            using var d = new RepeaterEditDialog(r, isNew: false);
            if (d.ShowDialog(FindForm()) == DialogResult.OK)
            {
                AppServices.Repeaters.Save(r);
                coverage.Remove(r.Id); // parameters may have changed
                Build();
            }
        }), Btn("Delete", ButtonStyle.Danger, () =>
        {
            if (MessageBox.Show(FindForm(), $"Delete {r.Label} from the map?", "Repeaters", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            coverage.Remove(r.Id);
            AppServices.Repeaters.Remove(r.Id);
            Map.ShowPanel(new RepeatersPanel(Map));
        }));
        AddButton("All repeaters", ButtonStyle.Secondary, () => Map.ShowPanel(new RepeatersPanel(Map)));
        Stack.ResumeLayout(true);
    }

    private async void Compute(Repeater r, bool vehicle)
    {
        _status = null;
        AppServices.Settings.ShowCoverage = true;
        _status = await AppServices.Coverage.ComputeAsync(r, vehicle, AppServices.Settings.CoverageRadiusKm, AppServices.Data.TerrainAt(r.Point));
        if (!IsDisposed)
        {
            if (AppServices.Coverage.For(r.Id) is { } item)
                Map.MapView.ZoomToFit(item.Result.Bounds);
            Build();
        }
    }

    public static string Details(Repeater r) =>
        $"{r.Label}{(r.Name.Length > 0 && r.Name != r.Label ? " – " + r.Name : "")}\r\n{r.FrequencyText}{(r.Mode.Length > 0 ? " " + r.Mode : "")}\r\n" +
        (r.InputMHz is { } i ? $"Input {i.ToString("0.000###", Inv)} MHz\r\n" : "") +
        $"{r.Lat.ToString("0.00000", Inv)}, {r.Lon.ToString("0.00000", Inv)} ({CoordinateFormat.Mgrs(r.Lat, r.Lon)})" +
        (r.Owner.Length > 0 ? $"\r\n{r.Owner}" : "");
}

/// <summary>All repeaters: show/hide, add, import (own lists, RepeaterBook, ISED) and the nearest ones.</summary>
public sealed class RepeatersPanel : SidePanel
{
    public RepeatersPanel(MapWindow map) : base(map, "Repeaters")
    {
        Build();
    }

    private void Build()
    {
        Stack.SuspendLayout();
        Clear();
        var s = AppServices.Settings;
        var store = AppServices.Repeaters;
        AddToggle($"Show repeaters ({store.All.Count:N0})", s.ShowRepeaters, on => { s.ShowRepeaters = on; s.Save(); Map.MapView.Invalidate(); },
            "Licensed stations appear when zoomed in; our own and event repeaters always.");
        AddToggle($"Show predicted coverage ({AppServices.Coverage.Items.Count})", s.ShowCoverage, on => { s.ShowCoverage = on; s.Save(); Map.MapView.Invalidate(); },
            "Tap a repeater, then Handheld or Vehicle radio.");
        if (AppServices.Coverage.Items.Count > 0)
            AddButton("Clear all coverage", ButtonStyle.Secondary, () => { AppServices.Coverage.Clear(); Build(); });

        AddButton("Add a repeater at the map centre…", ButtonStyle.Primary, () => Add(Map.MapView.Center));
        AddText("Import", FontRole.Heading);
        AddButtons(Btn("My list (CSV)…", ButtonStyle.Secondary, () => Import(RepeaterSource.Amateur)),
                   Btn("RepeaterBook file…", ButtonStyle.Secondary, () => Import(RepeaterSource.RepeaterBook)));
        AddButton("ISED licensed land-mobile file (CSV)…", ButtonStyle.Secondary, () => Import(RepeaterSource.Licensed));
        AddText("CSV: any columns named like latitude, longitude, output (or frequency), input or offset, tone, call sign, name, mode, antenna height, ERP. " +
                "RepeaterBook: the JSON export from your own RepeaterBook account (their terms: personal, non-commercial use, no redistribution – check before sharing). " +
                "ISED: the land-mobile extract from ISED's Spectrum Management System downloads; stations with separate transmit and receive frequencies inside the installed map area are added. " +
                "Importing the same file again replaces what came from it.", FontRole.Normal, Brand.Muted);

        var bySource = store.All.GroupBy(r => r.Source).OrderBy(g => g.Key).ToList();
        if (bySource.Count > 0)
        {
            AddText("On the map", FontRole.Heading);
            foreach (var g in bySource)
            {
                var source = g.Key;
                AddButtons(Btn($"{Repeater.SourceLabel(source)}: {g.Count():N0}", ButtonStyle.Secondary, () => { }),
                           Btn("Remove all", ButtonStyle.Danger, () =>
                           {
                               if (MessageBox.Show(FindForm(), $"Remove all {g.Count():N0} {Repeater.SourceLabel(source)} repeaters from the map?", "Repeaters",
                                       MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                                   return;
                               AppServices.Repeaters.RemoveSource(source);
                               Build();
                           }));
            }
        }

        var from = Map.MyPositionOrCenter;
        var nearest = store.All.OrderBy(r => r.Point.DistanceTo(from)).Take(40).ToList();
        if (nearest.Count > 0)
        {
            AddText($"Nearest {(Map.FixUsable ? "to me" : "to the map centre")}", FontRole.Heading);
            foreach (var r in nearest)
            {
                var id = r.Id;
                var km = r.Point.DistanceTo(from) / 1000;
                AddRow($"{r.Label} – {r.FrequencyText}", $"{km:0.#} km · {Repeater.SourceLabel(r.Source)}{(r.Name.Length > 0 ? " · " + r.Name : "")}",
                    () => Map.SelectRepeater(id, center: true));
            }
        }
        Stack.ResumeLayout(true);
    }

    private void Add(GeoPoint at)
    {
        var r = new Repeater { Source = RepeaterSource.Event, Lat = at.Lat, Lon = at.Lon };
        using var d = new RepeaterEditDialog(r, isNew: true);
        if (d.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        AppServices.Repeaters.Save(r);
        Map.SelectRepeater(r.Id, center: true);
    }

    /// <summary>Opens the add dialog for a point (from What's here?).</summary>
    public static void AddAt(MapWindow map, GeoPoint at)
    {
        var r = new Repeater { Source = RepeaterSource.Event, Lat = at.Lat, Lon = at.Lon };
        using var d = new RepeaterEditDialog(r, isNew: true);
        if (d.ShowDialog(map) != DialogResult.OK)
            return;
        AppServices.Repeaters.Save(r);
        map.SelectRepeater(r.Id, center: false);
    }

    private void Import(RepeaterSource source)
    {
        using var d = new OpenFileDialog
        {
            Title = source switch { RepeaterSource.RepeaterBook => "RepeaterBook export (JSON)", RepeaterSource.Licensed => "ISED land-mobile data (CSV)", _ => "Repeater list (CSV)" },
            Filter = source == RepeaterSource.RepeaterBook ? "RepeaterBook export (*.json;*.csv)|*.json;*.csv|All files (*.*)|*.*" : "CSV files (*.csv;*.txt)|*.csv;*.txt|All files (*.*)|*.*"
        };
        if (d.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        var warnings = new List<string>();
        try
        {
            var area = AppServices.Data.HasData ? AppServices.Data.Bounds.Expand(20000) : (GeoBounds?)null;
            var list = source switch
            {
                RepeaterSource.RepeaterBook when d.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) => RepeaterImport.ReadRepeaterBookJson(d.FileName, warnings),
                RepeaterSource.Licensed => RepeaterImport.ReadIsedCsv(d.FileName, area, warnings),
                _ => RepeaterImport.ReadCsv(d.FileName, warnings)
            };
            var n = AppServices.Repeaters.ReplaceImport(source, Path.GetFileName(d.FileName), list);
            var note = warnings.Count > 0 ? $"\n\n{warnings.Count} note(s), e.g.: {warnings[0]}" : "";
            MessageBox.Show(FindForm(), $"{n:N0} repeaters imported from {Path.GetFileName(d.FileName)}.{note}", "Repeaters", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MessageBox.Show(FindForm(), $"The file could not be imported: {ex.Message}", "Repeaters", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        Build();
    }
}
