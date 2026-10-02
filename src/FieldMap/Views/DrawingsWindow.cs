using FieldMap.Core.Drawings;
using FieldMap.Framework;
using FieldMap.Panels;
using Shared;
using Shared.UI;

namespace FieldMap.Views;

/// <summary>All incident drawings: open on the map, edit, copy, delete, import and export (KML, GeoJSON, GPX).</summary>
public sealed class DrawingsWindow : ToolWindow
{
    public const string ToolId = "drawings";

    private readonly FlowLayoutPanel _list = Ui.Stack(new Padding(10));
    private bool _activeOnly;

    public DrawingsWindow() : base(ToolRegistry.Find(ToolId)!)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(6), BackColor = Brand.Panel, WrapContents = false };
        bar.Controls.Add(Bar("Import…", ButtonStyle.Secondary, Import));
        bar.Controls.Add(Bar("Export…", ButtonStyle.Secondary, Export));
        bar.Controls.Add(Bar("Copy all", ButtonStyle.Primary, CopyAll));
        bar.Controls.Add(Bar("Active only", ButtonStyle.Secondary, () => { _activeOnly = !_activeOnly; Rebuild(); }));
        bar.Controls.Add(Bar("Delete all…", ButtonStyle.Danger, DeleteAll));
        Body.Controls.Add(_list);
        Body.Controls.Add(bar);
        AppServices.DrawingsChanged += OnChanged;
        FormClosed += (_, _) => AppServices.DrawingsChanged -= OnChanged;
        Rebuild();
    }

    private void OnChanged(object? sender, EventArgs e) => Rebuild();

    private static Button Bar(string text, ButtonStyle style, Action click)
    {
        var b = Ui.Button(text, style, click);
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Padding = new Padding(12, 0, 12, 0);
        b.Margin = new Padding(3);
        return b;
    }

    private void Rebuild()
    {
        if (IsDisposed)
            return;
        _list.SuspendLayout();
        foreach (Control c in _list.Controls.Cast<Control>().ToList())
            c.Dispose();
        _list.Controls.Clear();
        var items = AppServices.Drawings.Items.Where(d => !_activeOnly || d.Active)
            .OrderBy(d => d.Kind == DrawingKind.Incident ? 0 : d.Kind is DrawingKind.RoadClosure or DrawingKind.Hazmat or DrawingKind.Cordon ? 1 : 2)
            .ThenByDescending(d => d.CreatedUtc).ToList();
        _list.Controls.Add(Ui.Text($"{items.Count} drawing{(items.Count == 1 ? "" : "s")}{(_activeOnly ? " (active only)" : "")} · kept on this computer, time-stamped", FontRole.Heading, Brand.Navy));
        if (items.Count == 0)
            _list.Controls.Add(Ui.Text("Nothing drawn yet. Use Draw in a map window, or long-press the map → Drop incident pin."));
        foreach (var d in items)
            _list.Controls.Add(Row(d));
        UiMode.Apply(_list);
        _list.ResumeLayout(true);
        SetTitle($"Incidents & drawings ({AppServices.Drawings.Items.Count})");
    }

    private Control Row(Drawing d)
    {
        var row = new TableLayoutPanel { ColumnCount = 5, RowCount = 2, Height = Ui.Scaled(76), Margin = new Padding(0, 3, 0, 3), Padding = new Padding(6), BackColor = Color.White };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++)
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var color = Map.MapRenderer.DrawingColor(d.Kind);
        var title = new Label { Text = $"{Drawing.KindLabel(d.Kind)}: {d.Title}{(d.Active ? "" : " (inactive)")}", Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false, ForeColor = Brand.Navy, TextAlign = ContentAlignment.MiddleLeft }.Styled(FontRole.Bold);
        var extra = d.Shape == DrawingShape.Line && d.Points.Count > 1 ? $" · {GeoMath.FormatDistance(d.LengthMeters)}" :
            d.Shape == DrawingShape.Polygon && d.Points.Count > 2 ? $" · {Drawing.FormatArea(d.AreaSquareMeters)}" : "";
        var detail = new Label { Text = $"{d.CreatedUtc.ToLocalTime():HH:mm dd MMM}{extra} · {CoordinateFormat.Mgrs(d.Anchor.Lat, d.Anchor.Lon)}", Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false, ForeColor = Brand.Muted, TextAlign = ContentAlignment.MiddleLeft };
        row.Controls.Add(title, 0, 0);
        row.Controls.Add(detail, 0, 1);
        Button B(string text, ButtonStyle style, Action a)
        {
            var b = Ui.Button(text, style, a);
            b.Dock = DockStyle.Fill;
            b.AutoSize = true;
            b.Padding = new Padding(8, 0, 8, 0);
            return b;
        }
        var show = B("Show", ButtonStyle.Primary, () => ShowOnMap(d));
        var edit = B("Edit", ButtonStyle.Secondary, () => Edit(d));
        var copy = B("Copy", ButtonStyle.Secondary, () => Copy(d.DispatchText()));
        var delete = B("Delete", ButtonStyle.Danger, () => Delete(d));
        row.Controls.Add(show, 1, 0); row.SetRowSpan(show, 2);
        row.Controls.Add(edit, 2, 0); row.SetRowSpan(edit, 2);
        row.Controls.Add(copy, 3, 0); row.SetRowSpan(copy, 2);
        row.Controls.Add(delete, 4, 0); row.SetRowSpan(delete, 2);
        var stripe = new Panel { Width = 6, Dock = DockStyle.Left, BackColor = Color.FromArgb(color.Red, color.Green, color.Blue) };
        row.Controls.Add(stripe);
        row.FixedSize();
        return row;
    }

    private static void ShowOnMap(Drawing d)
    {
        var map = AppServices.Windows.Windows.OfType<MapWindow>().LastOrDefault() ?? AppServices.Windows.Open(MapWindow.ToolId, "v1|0|0|10|0|0|0|Map") as MapWindow;
        if (map == null)
            return;
        WindowManager.BringForward(map);
        DrawingDetailsPanel.ZoomTo(map, d);
        map.SelectDrawing(d);
    }

    private void Edit(Drawing d)
    {
        using var dialog = new DrawingEditDialog(d, isNew: false);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            d.Touch();
            AppServices.NotifyDrawingsChanged();
        }
    }

    private void Delete(Drawing d)
    {
        if (MessageBox.Show(this, $"Delete \"{d.Title}\"?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        AppServices.Drawings.Items.Remove(d);
        AppServices.NotifyDrawingsChanged();
    }

    private void DeleteAll()
    {
        var n = AppServices.Drawings.Items.Count;
        if (n == 0 || MessageBox.Show(this, $"Delete all {n} drawings? Export them first if you need a record.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        AppServices.Drawings.Items.Clear();
        AppServices.NotifyDrawingsChanged();
    }

    private void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "The clipboard is busy. Try again.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CopyAll()
    {
        var items = AppServices.Drawings.Items.Where(d => !_activeOnly || d.Active).ToList();
        if (items.Count > 0)
            Copy(string.Join(Environment.NewLine + Environment.NewLine, items.Select(d => d.DispatchText())));
    }

    private void Import()
    {
        using var dialog = new OpenFileDialog { Filter = "Drawings (*.kml;*.geojson;*.json;*.gpx)|*.kml;*.geojson;*.json;*.gpx|All files|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            var imported = DrawingFormats.Import(dialog.FileName);
            var existing = AppServices.Drawings.Items.Select(d => d.Id).ToHashSet();
            var added = 0;
            foreach (var d in imported)
            {
                if (existing.Contains(d.Id))
                    d.Id = Guid.NewGuid();
                if (string.IsNullOrEmpty(d.Notes))
                    d.Notes = $"Imported from {Path.GetFileName(dialog.FileName)}";
                AppServices.Drawings.Items.Add(d);
                added++;
            }
            AppServices.NotifyDrawingsChanged();
            MessageBox.Show(this, $"Imported {added} drawing{(added == 1 ? "" : "s")}.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or FormatException or System.Xml.XmlException or System.Text.Json.JsonException or InvalidOperationException)
        {
            MessageBox.Show(this, "The file could not be imported: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Export()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "KML (Google Earth, CAD) (*.kml)|*.kml|GeoJSON (*.geojson)|*.geojson|GPX (GPS units) (*.gpx)|*.gpx",
            FileName = $"incident-drawings-{DateTime.Now:yyyyMMdd-HHmm}"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        DrawingFormats.Export(dialog.FileName, AppServices.Drawings.Items.Where(d => !_activeOnly || d.Active));
    }
}
