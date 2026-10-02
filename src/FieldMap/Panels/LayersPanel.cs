using FieldMap.Core.Layers;
using FieldMap.Services;
using Shared;
using Shared.UI;

namespace FieldMap.Panels;

/// <summary>Big on/off toggles for every layer, with each layer's data source and date.</summary>
public sealed class LayersPanel : SidePanel
{
    public LayersPanel(Views.MapWindow map) : base(map, "Layers")
    {
        var s = AppServices.Settings;
        var data = AppServices.Data;
        if (!data.HasData)
            AddText("No map data is installed. Open Data & updates to install British Columbia and/or Alberta.", FontRole.Bold, Brand.Danger);
        else if (data.IsSample)
            AddText("SAMPLE DATA: fictitious places for trying the app.", FontRole.Bold, Brand.Danger);

        AddText("Facilities and features", FontRole.Heading);
        foreach (var layer in LayerCatalog.All)
        {
            var l = layer;
            var count = data.Regions.Sum(r => r.Manifest.Layers.TryGetValue(l.Id, out var info) ? info.Count : 0);
            var sources = data.Regions.SelectMany(r => r.Manifest.Layers.TryGetValue(l.Id, out var info) ? info.Sources : new List<string>()).Distinct().ToList();
            var dates = data.Regions.Select(r => r.Manifest.DataDate).Where(d => d != null).ToList();
            var date = dates.Count > 0 ? dates.Min()!.Value.ToString("yyyy-MM-dd") : "–";
            AddToggle($"{l.Title} ({count:N0})", s.LayerOn(l.Id), on => { s.Layers[l.Id] = on; Save(); },
                $"Source: {(sources.Count > 0 ? string.Join(", ", sources) : "–")} · data {date}" +
                (l.Id == LayerCatalog.Access ? " · also highlights seasonal and forest-service roads" : "") +
                (l.MinZoom > 12 ? $" · shown when zoomed in" : ""));
            if (l.Warning != null)
                AddText("⚠ " + l.Warning, FontRole.Bold, Brand.Danger);
        }

        AddText("Map", FontRole.Heading);
        AddToggle("Breadcrumb trail", s.ShowBreadcrumbs, on => { s.ShowBreadcrumbs = on; Save(); }, $"{AppServices.Breadcrumbs.Items.Count:N0} positions in the last 24 hours");
        AddToggle("Incident drawings", s.ShowDrawings, on => { s.ShowDrawings = on; Save(); }, $"{AppServices.Drawings.Items.Count} drawings");
        AddText("Terrain / hillshade: not installed (planned from NRCan MRDEM, Open Government Licence – Canada).", FontRole.Normal, Brand.Muted);
        foreach (var r in data.Regions)
        {
            var src = r.Manifest.Sources.FirstOrDefault();
            AddText($"Base map – {r.Manifest.RegionName}: {string.Join(", ", r.Manifest.Sources.Select(x => x.Name))}; built {r.Manifest.BuiltUtc:yyyy-MM-dd}, data {r.Manifest.DataDate:yyyy-MM-dd}" +
                    (src != null ? $" · {src.Licence}" : ""), FontRole.Normal, Brand.Muted);
        }

        AddText("Live data (only when connected)", FontRole.Heading);
        AddToggle("Show live data", s.ShowLiveData, on =>
        {
            s.ShowLiveData = on;
            Save();
            if (on)
                _ = AppServices.Live.RefreshAsync(true);
        }, "Road events, wildfire perimeters, evacuation orders and weather alerts. Nothing else depends on them.");
        foreach (var feed in AppServices.Live.Catalog.Feeds)
        {
            var f = feed;
            var result = AppServices.Live.Results.GetValueOrDefault(f.Id);
            var status = !f.Enabled || string.IsNullOrWhiteSpace(f.Url) ? "not configured (see Content/live-feeds.json)"
                : f.NeedsKey && string.IsNullOrWhiteSpace(s.Ab511Key) ? "needs a developer key (Data & updates → Settings)"
                : result != null ? $"{LiveDataService.Label(result)} · {result.Items.Count} items" : "not fetched yet";
            AddToggle(f.Name, s.FeedOn(f.Id), on => { s.LiveFeeds[f.Id] = on; Save(); if (on) _ = AppServices.Live.RefreshAsync(true); }, $"{status} · {f.Licence}");
        }
        AddButton("Refresh live data now", ButtonStyle.Secondary, () => _ = AppServices.Live.RefreshAsync(true));
    }

    private void Save()
    {
        AppServices.Settings.Save();
        Map.MapView.Invalidate();
    }

    private void AddToggle(string title, bool on, Action<bool> changed, string detail)
    {
        var box = new CheckBox
        {
            Text = title, Checked = on, AutoSize = false, Height = Ui.TargetHeight, Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleLeft,
            FlatStyle = FlatStyle.Flat, UseMnemonic = false, Margin = new Padding(0, 4, 0, 0), Padding = new Padding(10, 0, 0, 0)
        }.Styled(FontRole.Bold);
        box.FixedSize();
        void Sync()
        {
            box.Text = (box.Checked ? "✓  " : "○  ") + title;
            UiMode.SetColors(box, box.Checked ? Brand.Navy : Color.White, box.Checked ? Color.White : Brand.Navy);
        }
        box.CheckedChanged += (_, _) =>
        {
            Sync();
            changed(box.Checked);
        };
        Sync();
        Stack.Controls.Add(box);
        AddText(detail, FontRole.Normal, Brand.Muted);
    }
}
