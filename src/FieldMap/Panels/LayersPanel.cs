using FieldMap.Core.Layers;
using FieldMap.Map;
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

        BuildBaseMap();

        BuildEventLayers();

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
        AddText(data.HasTerrain
            ? "Elevation: installed (NRCan Canadian Digital Elevation Model, 64 m; Open Government Licence – Canada). Used for ground height in What's here? and for repeater coverage."
            : "Elevation: not installed – Update map data adds it (BC about 620 MB, Alberta about 310 MB).", FontRole.Normal, Brand.Muted);
        foreach (var r in data.Regions)
        {
            var src = r.Manifest.Sources.FirstOrDefault();
            AddText($"Base map – {r.Manifest.RegionName}: {string.Join(", ", r.Manifest.Sources.Select(x => x.Name))}; built {r.Manifest.BuiltUtc:yyyy-MM-dd}, data {r.Manifest.DataDate:yyyy-MM-dd}" +
                    (src != null ? $" · {src.Licence}" : ""), FontRole.Normal, Brand.Muted);
        }

        BuildRadio();

        AddText("APRS (amateur radio positions)", FontRole.Heading);
        var aprs = AppServices.Aprs;
        AddToggle($"APRS stations ({aprs.VisibleStations().Count})", s.Aprs.Show, on => { s.Aprs.Show = on; Save(); },
            aprs.Active ? aprs.ShortStatus() + " · positions are sent by the stations themselves" : "Off – turn on the internet feed or a radio TNC in APRS settings.");
        AddToggle("APRS tracks", s.Aprs.ShowTracks, on => { s.Aprs.ShowTracks = on; Save(); }, $"Last {s.Aprs.TrackMinutes} minutes of movement");
        AddButtons(Btn("APRS stations", ButtonStyle.Secondary, () => Map.ShowPanel(new AprsPanel(Map))),
                   Btn("APRS settings…", ButtonStyle.Secondary, () => { using var d = new Views.AprsSettingsDialog(); d.ShowDialog(FindForm()); }));

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

    // ------------------------------------------------------------------ radio

    private void BuildRadio()
    {
        var s = AppServices.Settings;
        var data = AppServices.Data;
        AddText("Radio and cell coverage", FontRole.Heading);
        AddToggle($"Repeaters ({AppServices.Repeaters.All.Count:N0})", s.ShowRepeaters, on => { s.ShowRepeaters = on; Save(); },
            "Our own, event, amateur, RepeaterBook and licensed (ISED) repeaters. Tap one for details and predicted coverage.");
        AddToggle($"Predicted repeater coverage ({AppServices.Coverage.Items.Count})", s.ShowCoverage, on => { s.ShowCoverage = on; Save(); },
            "Green likely, amber marginal, red unlikely – an estimate from terrain, not a guarantee.");
        var cell = data.CellCoverage;
        AddToggle("Cell coverage (LTE / 5G)", s.ShowCellCoverage && cell != null, on =>
        {
            if (on && cell == null)
            {
                MessageBox.Show(FindForm(), "No cell coverage map is installed yet. Import the Government of Canada coverage file (KML/KMZ) under Data & updates → Cell coverage.",
                    "Cell coverage", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Map.ShowPanel(new LayersPanel(Map));
                return;
            }
            s.ShowCellCoverage = on;
            Save();
        }, cell != null
            ? $"Purple 5G, blue LTE, teal 3G – all carriers combined, as reported by the carriers (not verified). {cell.Metadata.GetValueOrDefault("description", "")}"
            : "Not installed – Data & updates → Cell coverage.");
        AddButtons(Btn("Repeaters…", ButtonStyle.Secondary, () => Map.ShowPanel(new RepeatersPanel(Map))),
                   Btn("Data & updates", ButtonStyle.Secondary, () => AppServices.Windows.Open(Views.DataWindow.ToolId)));
    }

    // ------------------------------------------------------------------ event layers

    private static readonly float[] Opacities = { 1f, 0.8f, 0.5f, 0.3f };

    private void BuildEventLayers()
    {
        var lib = AppServices.Overlays;
        AddText("Event layers (imported map files)", FontRole.Heading);
        AddText("KML, KMZ (including site-plan images), GPX, GeoJSON or CSV from an event or agency. Drag files onto the map, or:", FontRole.Normal, Brand.Muted);
        if (lib.Layers.Count > 0)
            AddButtons(Btn("Import map file…", ButtonStyle.Primary, () => Map.ChooseMapFiles()),
                       Btn($"All items ({lib.Layers.Sum(l => l.Content.Features.Count):N0})", ButtonStyle.Secondary, () => Map.ShowPanel(new EventItemsPanel(Map))));
        else
            AddButton("Import map file…", ButtonStyle.Primary, () => Map.ChooseMapFiles());
        foreach (var l in lib.Layers)
        {
            var layer = l;
            AddToggle($"{layer.Info.Name}", layer.Info.Visible, on => { lib.SetVisible(layer.Info.Id, on); Map.MapView.Invalidate(); },
                $"{layer.Content.Summary} · {Path.GetFileName(layer.Info.OriginalPath)} · imported {layer.Info.ImportedUtc.ToLocalTime():yyyy-MM-dd HH:mm}" +
                (layer.Content.Warnings.Count > 0 ? $" · {layer.Content.Warnings.Count} note(s)" : ""));
            var buttons = new List<Button>
            {
                Btn("Items", ButtonStyle.Secondary, () => Map.ShowPanel(new EventItemsPanel(Map, layer.Info.Id))),
                Btn("Zoom to", ButtonStyle.Secondary, () =>
                {
                    if (!layer.Content.Bounds.IsEmpty)
                        Map.MapView.ZoomToFit(layer.Content.Bounds.Expand(100));
                    if (!layer.Info.Visible)
                        lib.SetVisible(layer.Info.Id, true);
                })
            };
            if (layer.Content.GroundOverlays.Count > 0)
                buttons.Add(Btn($"Image {layer.Info.ImageOpacity:P0}", ButtonStyle.Secondary, () =>
                {
                    var i = Array.FindIndex(Opacities, o => Math.Abs(o - layer.Info.ImageOpacity) < 0.01f);
                    lib.SetImageOpacity(layer.Info.Id, Opacities[(i + 1) % Opacities.Length]);
                    Map.ShowPanel(new LayersPanel(Map));
                }));
            buttons.Add(Btn("Remove", ButtonStyle.Danger, () =>
            {
                if (MessageBox.Show(FindForm(), $"Remove the event layer {layer.Info.Name}?", "Event layers", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                lib.Remove(layer.Info.Id);
                Map.ShowPanel(new LayersPanel(Map));
            }));
            AddButtons(buttons.ToArray());
        }
    }

    // ------------------------------------------------------------------ base map and imagery

    private CancellationTokenSource? _saving;

    private void BuildBaseMap()
    {
        var imagery = AppServices.Imagery;
        var s = AppServices.Settings;
        AddText("Base map", FontRole.Heading);
        var current = imagery.Current;
        var modes = new[] { (BaseMapMode.Map, "Map"), (BaseMapMode.Imagery, "Imagery (satellite / aerial)"), (BaseMapMode.Hybrid, "Imagery with roads and labels") };
        var boxes = new List<CheckBox>();
        foreach (var (mode, title) in modes)
        {
            var m = mode;
            var on = current != null ? s.BaseMode == m : m == BaseMapMode.Map;
            CheckBox? box = null;
            box = AddToggle(title, on, isOn =>
            {
                if (!isOn)
                {
                    // Radio behaviour: one is always selected.
                    if (boxes.All(b => !b.Checked))
                        box!.Checked = true;
                    return;
                }
                if (m != BaseMapMode.Map && imagery.Current == null)
                {
                    // Select Map first so unticking this one doesn't re-tick it.
                    boxes[0].Checked = true;
                    box!.Checked = false;
                    MessageBox.Show(FindForm(), imagery.Status + ".\n\nInstall an imagery package under Data & updates, or ask your administrator to enable an online imagery source.", "Imagery", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                foreach (var other in boxes.Where(b => b != box))
                    other.Checked = false;
                imagery.SetMode(m);
                Map.MapView.Invalidate();
            }, "");
            boxes.Add(box);
        }

        var available = imagery.Available();
        if (available.Count == 0)
        {
            AddText("No imagery is installed or set up. Field Map doesn't include imagery: free imagery that may be kept offline doesn't exist for all of BC and Alberta. " +
                    "Install a package your organisation is licensed to use (Data & updates → Install imagery package), or ask your administrator to enable an online source.", FontRole.Normal, Brand.Muted);
        }
        else
        {
            AddText("Imagery source", FontRole.Bold);
            foreach (var src in available)
            {
                var source = src;
                var selected = current?.Id == source.Id;
                var detail = $"{(source.IsOnline ? "Online" + (source.AllowOffline ? ", cached as viewed" : ", not kept offline") : "Installed, works offline")} · zoom {source.MinZoom}–{source.MaxZoom}" +
                             (source.Attribution.Length > 0 ? $" · {source.Attribution}" : "") + (source.Licence.Length > 0 ? $" · {source.Licence}" : "");
                AddRow((selected ? "● " : "○ ") + source.Name, detail, () => ChooseSource(source), selected ? Brand.Navy : Brand.Muted);
            }
            if (current != null)
                AddText(imagery.Status, FontRole.Normal, imagery.Status.Contains("offline") ? Brand.Danger : Brand.Muted);
            if (current is { IsOnline: true, AllowOffline: true })
                AddButton("Save imagery of this area for offline use…", ButtonStyle.Secondary, SaveArea);
        }
        foreach (var missingKey in imagery.Catalog.Sources.Where(x => x.Enabled && x.NeedsKey && imagery.Key(x).Length == 0))
        {
            var source = missingKey;
            AddButton($"Enter key for {source.Name}…", ButtonStyle.Secondary, () =>
            {
                var key = InputDialog.Ask(FindForm()!, source.Name, "Key or token for this imagery service:");
                if (string.IsNullOrWhiteSpace(key))
                    return;
                AppServices.Settings.ImageryKeys[source.Id] = key.Trim();
                AppServices.Settings.Save();
                Map.ShowPanel(new LayersPanel(Map));
            });
        }
    }

    private void ChooseSource(Core.Imagery.ImagerySource source)
    {
        var imagery = AppServices.Imagery;
        if (!imagery.TermsAccepted(source))
        {
            var answer = MessageBox.Show(FindForm(), $"{source.Name}\n\n{source.Terms}\n\nLicence: {source.Licence}\n\nUse this imagery under these terms?",
                "Imagery terms", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
                return;
            imagery.AcceptTerms(source);
        }
        imagery.Select(source);
        if (AppServices.Settings.BaseMode == BaseMapMode.Map)
            imagery.SetMode(BaseMapMode.Hybrid);
        Map.MapView.Invalidate();
        Map.ShowPanel(new LayersPanel(Map));
    }

    private async void SaveArea()
    {
        var imagery = AppServices.Imagery;
        if (imagery.Current is not { } source)
            return;
        var view = Map.MapView.Viewport.VisibleBounds();
        var minZoom = Math.Max(source.MinZoom, (int)Math.Floor(Map.MapView.Zoom) - 2);
        var maxZoom = Math.Min(source.MaxZoom, (int)Math.Floor(Map.MapView.Zoom) + 3);
        var count = Core.Imagery.ImageryMath.CountTiles(view, minZoom, maxZoom);
        if (MessageBox.Show(FindForm(), $"Save {count:N0} imagery tiles (zoom {minZoom}–{maxZoom}, about {count * 25 / 1024.0:0} MB) of the area on screen for offline use?\n\nZoom out first to save a larger area at less detail.",
                "Save imagery", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        _saving = new CancellationTokenSource();
        var status = AddText("Saving imagery…", FontRole.Bold);
        var cancel = AddButton("Stop saving", ButtonStyle.Danger, () => _saving?.Cancel());
        Refit();
        try
        {
            var r = await imagery.SaveAreaAsync(view, minZoom, maxZoom,
                new Progress<Core.Imagery.SeedProgress>(p => { if (!status.IsDisposed) status.Text = $"Saving imagery: {p.Done:N0} of {p.Total:N0} tiles ({p.Failed:N0} not available)"; }), _saving.Token);
            Map.Flash($"Saved {r.Fetched + r.Skipped:N0} imagery tiles for offline use");
        }
        catch (OperationCanceledException)
        {
            Map.Flash("Saving imagery stopped; what was saved is kept");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException)
        {
            MessageBox.Show(FindForm(), ex.Message, "Save imagery", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _saving = null;
            if (!IsDisposed)
                Map.ShowPanel(new LayersPanel(Map));
        }
    }

    public override void OnClosing() => _saving?.Cancel();
}

