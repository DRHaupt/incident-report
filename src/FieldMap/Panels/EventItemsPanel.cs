using FieldMap.Core.Overlays;
using FieldMap.Views;
using Shared;
using Shared.UI;

namespace FieldMap.Panels;

/// <summary>
/// Everything in the imported event files, grouped by file and folder: filter by typing, choose a file, tap an item to
/// go to it on the map and see its details.
/// </summary>
public sealed class EventItemsPanel : SidePanel
{
    private const int MaxRows = 300;
    private readonly TextBox _filter = new() { Width = 300 };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    private Guid? _layer;
    private bool _pointsOnly;

    public EventItemsPanel(MapWindow map, Guid? layer = null, string filter = "") : base(map, "Event items")
    {
        _layer = layer;
        _filter.Text = filter;
        _filter.Styled(FontRole.Large);
        _filter.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Build();
        };
        AppServices.Overlays.Changed += OnChanged;
        Build();
    }

    public override void OnClosing()
    {
        _debounce.Stop();
        _debounce.Dispose();
        if (_filter.Parent == null)
            _filter.Dispose();
        AppServices.Overlays.Changed -= OnChanged;
        base.OnClosing();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
            BeginInvoke(Build);
    }

    private void Build()
    {
        var hadFocus = _filter.Focused;
        var caret = _filter.SelectionStart;
        Stack.SuspendLayout();
        // Keep the filter box itself; rebuild everything else.
        Stack.Controls.Remove(_filter);
        Clear();
        var lib = AppServices.Overlays;
        if (lib.Layers.Count == 0)
        {
            AddText("No event files imported. Import KML, KMZ, GPX, GeoJSON or CSV files from the event or agency.", FontRole.Bold);
            AddButton("Import map file…", ButtonStyle.Primary, () => Map.ChooseMapFiles());
            Stack.ResumeLayout(true);
            return;
        }

        AddText("Find", FontRole.Heading);
        _filter.Margin = new Padding(0, 0, 0, 6);
        Stack.Controls.Add(_filter);
        if (_layer != null && lib.Layers.All(l => l.Info.Id != _layer))
            _layer = null;

        // File choice: all, or one file.
        var choices = new List<Button> { Btn(_layer == null ? "✓ All files" : "All files", _layer == null ? ButtonStyle.Primary : ButtonStyle.Secondary, () => { _layer = null; Build(); }) };
        foreach (var l in lib.Layers)
        {
            var id = l.Info.Id;
            choices.Add(Btn((_layer == id ? "✓ " : "") + l.Info.Name, _layer == id ? ButtonStyle.Primary : ButtonStyle.Secondary, () => { _layer = id; Build(); }));
        }
        for (var i = 0; i < choices.Count; i += 2)
            AddButtons(choices.Skip(i).Take(2).ToArray());
        AddButton(_pointsOnly ? "✓ Points only (posts, tents, gates…)" : "Points only (posts, tents, gates…)", _pointsOnly ? ButtonStyle.Primary : ButtonStyle.Secondary,
            () => { _pointsOnly = !_pointsOnly; Build(); }, FontRole.Normal);

        var layers = lib.Layers.Where(l => _layer == null || l.Info.Id == _layer).ToList();
        var query = _filter.Text.Trim();
        var items = query.Length > 0
            ? OverlaySearch.Search(layers, query, Map.MyPositionOrCenter, 2000).Select(m => (m.Layer, m.Feature)).ToList()
            : layers.SelectMany(l => l.Content.Features.Select(f => (Layer: l, Feature: f))).ToList();
        if (_pointsOnly)
            items = items.Where(i => i.Feature.Geometry == OverlayGeometry.Point).ToList();
        items = items.Where(i => i.Feature.Parts.Count > 0 && i.Feature.Parts[0].Count > 0).ToList();

        var total = items.Count;
        AddText(query.Length > 0 ? $"{total:N0} match{(total == 1 ? "" : "es")} for \"{query}\"" : $"{total:N0} items", FontRole.Bold, Brand.Muted);
        var from = Map.MyPositionOrCenter;
        var groups = query.Length > 0
            ? new[] { ("", items) }.ToList() // matches stay in best-first order
            : items.GroupBy(i => (i.Layer.Info.Name, i.Feature.Folder))
                .OrderBy(g => g.Key.Name).ThenBy(g => g.Key.Folder)
                .Select(g => ($"{g.Key.Name}{(g.Key.Folder.Length > 0 ? " › " + g.Key.Folder : "")} ({g.Count()})",
                    g.OrderBy(i => i.Feature.Name.Length == 0).ThenBy(i => i.Feature.Name, StringComparer.CurrentCultureIgnoreCase).ToList()))
                .ToList();
        var shown = 0;
        foreach (var (title, list) in groups)
        {
            if (shown >= MaxRows)
                break;
            if (title.Length > 0)
                AddText(title, FontRole.Heading);
            foreach (var (layer, f) in list)
            {
                if (shown++ >= MaxRows)
                    break;
                var l = layer;
                var feature = f;
                var kind = f.Geometry switch { OverlayGeometry.Point => "Point", OverlayGeometry.Line => "Line", _ => "Area" };
                var detail = $"{kind} · {GeoMath.FormatDistance(f.Anchor.DistanceTo(from))}" +
                             (query.Length > 0 ? $" · {l.Info.Name}{(f.Folder.Length > 0 ? " › " + f.Folder : "")}" : "") +
                             (f.Description.Length > 0 ? " · " + Shorten(f.Description, 60) : "");
                AddRow(f.Name.Length > 0 ? f.Name : $"({kind.ToLowerInvariant()} without a name)", detail, () => Map.ShowOverlayFeature(l, feature), FeatureColor(f));
            }
        }
        if (total > MaxRows)
            AddText($"Showing the first {MaxRows} – type in Find or choose a file to narrow the list.", FontRole.Normal, Brand.Muted);
        Stack.ResumeLayout(true);
        if (hadFocus)
        {
            _filter.Focus();
            _filter.SelectionStart = Math.Min(caret, _filter.Text.Length);
        }
    }

    private static Color? FeatureColor(OverlayFeature f)
    {
        var argb = f.Geometry == OverlayGeometry.Polygon ? f.Fill ?? f.Stroke : f.Stroke ?? f.Fill;
        return argb is { } c && (c >> 24) > 0 ? Color.FromArgb(unchecked((int)(c | 0xFF000000))) : null;
    }

    private static string Shorten(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}

/// <summary>A shop, restaurant, office, hotel… found by search.</summary>
public sealed class BusinessPanel : SidePanel
{
    public BusinessPanel(MapWindow map, Core.Build.Business b) : base(map, b.Name)
    {
        AddText(b.Name, FontRole.Large);
        AddText(b.Kind, FontRole.Bold, Brand.Muted);
        if (b.Address.Length > 0)
            AddText(b.Address);
        if (b.Phone.Length > 0)
            AddRow(b.Phone, "Phone – tap to copy", () => Map.CopyText(b.Phone, "Copied " + b.Phone));
        if (b.Hours.Length > 0)
            AddText("Hours: " + b.Hours);
        if (b.Website.Length > 0)
            AddText(b.Website, FontRole.Normal, Brand.Muted);
        var p = b.Point;
        AddText($"{CoordinateFormat.Decimal(p.Lat, p.Lon)} · MGRS {CoordinateFormat.Mgrs(p.Lat, p.Lon)}");
        if (map.MyPosition is { } me && map.FixUsable)
            AddText($"{GeoMath.FormatDistance(me.DistanceTo(p))} {GeoMath.FormatBearing(me.BearingTo(p))} from my position (straight line)", FontRole.Bold);
        AddText("From OpenStreetMap: names, hours and phone numbers may be out of date.", FontRole.Normal, Brand.Muted);
        AddButton("Directions here", ButtonStyle.Accent, () => Map.ShowPanel(new DirectionsPanel(Map, null, p, b.Name)));
        AddButtons(Btn("Copy for dispatch", ButtonStyle.Primary, () => Map.CopyText(
                       $"{b.Name} ({b.Kind}){(b.Address.Length > 0 ? ", " + b.Address : "")}\r\n{DispatchText.Ascii(CoordinateFormat.Decimal(p.Lat, p.Lon))} / MGRS {CoordinateFormat.Mgrs(p.Lat, p.Lon)}",
                       "Copied " + b.Name)),
                   Btn("What's here?", ButtonStyle.Secondary, () => Map.ShowWhatsHere(p, b.Name)));
    }
}
