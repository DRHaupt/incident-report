using FieldMap.Core.Drawings;
using FieldMap.Views;
using Shared;
using Shared.UI;

namespace FieldMap.Panels;

/// <summary>Incident drawing tools: tap the map to place pins, areas, closures, rings, measurements and hazmat zones.</summary>
public sealed class DrawPanel : SidePanel
{
    private static readonly (DrawingKind Kind, DrawingShape Shape, string Title)[] Tools =
    {
        (DrawingKind.Incident, DrawingShape.Point, "Incident pin"),
        (DrawingKind.Hazmat, DrawingShape.Point, "Hazmat zone"),
        (DrawingKind.RoadClosure, DrawingShape.Line, "Road closure"),
        (DrawingKind.Cordon, DrawingShape.Polygon, "Cordon"),
        (DrawingKind.Staging, DrawingShape.Polygon, "Staging area"),
        (DrawingKind.Rendezvous, DrawingShape.Point, "Rendezvous point"),
        (DrawingKind.LandingZone, DrawingShape.Point, "Landing zone"),
        (DrawingKind.Perimeter, DrawingShape.Polygon, "Perimeter"),
        (DrawingKind.RangeRings, DrawingShape.Point, "Range rings"),
        (DrawingKind.Unit, DrawingShape.Point, "Unit"),
        (DrawingKind.Measure, DrawingShape.Line, "Measure distance"),
        (DrawingKind.Measure, DrawingShape.Polygon, "Measure area"),
        (DrawingKind.Note, DrawingShape.Point, "Note")
    };

    private Drawing? _draft;
    private WrapLabel? _measure;

    public DrawPanel(MapWindow map) : base(map, "Draw")
    {
        BuildChooser();
    }

    private void BuildChooser()
    {
        Clear();
        Map.TapHandler = null;
        Map.MapView.Draft = null;
        Map.MapView.Invalidate();
        AddText("Choose what to draw, then tap the map.", FontRole.Bold);
        for (var i = 0; i < Tools.Length; i += 2)
            AddButtons(Tools.Skip(i).Take(2).Select(t => Btn(t.Title, t.Kind is DrawingKind.Incident or DrawingKind.RoadClosure or DrawingKind.Hazmat ? ButtonStyle.Danger : ButtonStyle.Secondary,
                () => Start(t.Kind, t.Shape, t.Title))).ToArray());
        AddButton("Incidents & drawings list", ButtonStyle.Primary, () => AppServices.Windows.Open(DrawingsWindow.ToolId));
        Refit();
    }

    private void Start(DrawingKind kind, DrawingShape shape, string title)
    {
        _draft = new Drawing { Kind = kind, Shape = shape, CreatedBy = AppServices.Settings.UserName };
        Clear();
        Title = title;
        AddText(shape == DrawingShape.Point ? "Tap the map where it is." :
            shape == DrawingShape.Line ? "Tap the map to add points along the line. Tap Finish when done (one point = a closure at that spot)." :
            "Tap the map around the area. Tap Finish when done.", FontRole.Large);
        _measure = AddText("", FontRole.Heading);
        if (shape != DrawingShape.Point)
        {
            AddButtons(Btn("Finish", ButtonStyle.Success, Finish), Btn("Undo point", ButtonStyle.Secondary, Undo));
            if (Map.MyPosition is { } me && Map.FixUsable)
                AddButton("Add my position", ButtonStyle.Secondary, () => AddPoint(me));
        }
        else if (Map.MyPosition is { } me && Map.FixUsable)
            AddButton("Use my position", ButtonStyle.Secondary, () => AddPoint(me));
        AddButton("Use map centre", ButtonStyle.Secondary, () => AddPoint(Map.MapView.Center));
        AddButton("Cancel", ButtonStyle.Secondary, BuildChooser);
        Map.TapHandler = AddPoint;
        Map.MapView.Draft = _draft;
        Refit();
    }

    private void AddPoint(GeoPoint p)
    {
        if (_draft == null)
            return;
        _draft.Points.Add(p);
        Map.MapView.Invalidate();
        if (_draft.Shape == DrawingShape.Point)
        {
            Finish();
            return;
        }
        UpdateMeasure();
    }

    private void Undo()
    {
        if (_draft is { Points.Count: > 0 })
        {
            _draft.Points.RemoveAt(_draft.Points.Count - 1);
            Map.MapView.Invalidate();
            UpdateMeasure();
        }
    }

    private void UpdateMeasure()
    {
        if (_draft == null || _measure == null)
            return;
        _measure.Text = _draft.Points.Count switch
        {
            0 => "",
            1 => "1 point",
            _ when _draft.Shape == DrawingShape.Line => $"{_draft.Points.Count} points · {GeoMath.FormatDistance(_draft.LengthMeters)}",
            _ => $"{_draft.Points.Count} points · area {Drawing.FormatArea(_draft.AreaSquareMeters)} · perimeter {GeoMath.FormatDistance(_draft.LengthMeters)}"
        };
    }

    private void Finish()
    {
        if (_draft == null || _draft.Points.Count == 0)
            return;
        var d = _draft;
        if (d.Shape == DrawingShape.Polygon && d.Points.Count < 3)
            d.Shape = d.Points.Count == 1 ? DrawingShape.Point : DrawingShape.Line;
        if (d.Shape == DrawingShape.Line && d.Points.Count == 1)
            d.Shape = DrawingShape.Point;
        Map.TapHandler = null;
        Map.MapView.Draft = null;
        _draft = null;
        if (Save(Map, d, edit: d.Kind != DrawingKind.Measure || true))
            Map.SelectDrawing(d);
        else
            BuildChooser();
    }

    /// <summary>Fills defaults, asks for details, stores the drawing. Returns false when the user cancels.</summary>
    internal static bool Save(MapWindow map, Drawing d, bool edit = true)
    {
        var s = AppServices.Settings;
        if (d.Kind == DrawingKind.RangeRings && d.Rings.Count == 0)
            d.Rings = s.DefaultRings.ToList();
        if (d.Kind == DrawingKind.Hazmat)
        {
            d.IsolationMeters ??= 100;
            d.ProtectiveMeters ??= 800;
            d.WindFromDegrees ??= 270;
            d.ProtectiveShape = s.HazmatShape;
        }
        if (d.Kind == DrawingKind.RoadClosure)
            d.Active = true;
        if (string.IsNullOrEmpty(d.CreatedBy))
            d.CreatedBy = s.UserName;
        if (edit)
        {
            using var dialog = new DrawingEditDialog(d, isNew: true);
            if (dialog.ShowDialog(map) != DialogResult.OK)
                return false;
        }
        d.CreatedUtc = d.ModifiedUtc = DateTime.UtcNow;
        AppServices.Drawings.Items.Add(d);
        AppServices.NotifyDrawingsChanged();
        return true;
    }

    /// <summary>One-tap drawing at a point (from the map's context menu and "What's here?").</summary>
    public static void QuickAdd(MapWindow map, DrawingKind kind, GeoPoint p, string? label = null)
    {
        var d = new Drawing { Kind = kind, Shape = DrawingShape.Point, Points = { p }, Label = label ?? "" };
        if (Save(map, d))
            map.SelectDrawing(d);
    }

    public override void OnClosing()
    {
        Map.TapHandler = null;
        Map.MapView.Draft = null;
        Map.MapView.Invalidate();
    }
}

/// <summary>Details of one drawing with edit, copy, export and delete.</summary>
public sealed class DrawingDetailsPanel : SidePanel
{
    private readonly Drawing _d;

    public DrawingDetailsPanel(MapWindow map, Drawing d) : base(map, Drawing.KindLabel(d.Kind))
    {
        _d = d;
        Build();
    }

    private void Build()
    {
        Clear();
        var d = _d;
        AddText(d.Title, FontRole.Large);
        AddText($"{Drawing.KindLabel(d.Kind)} · drawn {d.CreatedUtc.ToLocalTime():HH:mm dd MMM yyyy}" + (d.CreatedBy.Length > 0 ? $" by {d.CreatedBy}" : "") +
                (d.ModifiedUtc - d.CreatedUtc > TimeSpan.FromSeconds(5) ? $" · changed {d.ModifiedUtc.ToLocalTime():HH:mm dd MMM}" : ""), FontRole.Normal, Brand.Muted);
        if (d.Shape == DrawingShape.Line && d.Points.Count > 1)
            AddText($"Length {GeoMath.FormatDistance(d.LengthMeters)}", FontRole.Bold);
        if (d.Shape == DrawingShape.Polygon && d.Points.Count > 2)
            AddText($"Area {Drawing.FormatArea(d.AreaSquareMeters)} · perimeter {GeoMath.FormatDistance(d.LengthMeters)}", FontRole.Bold);
        if (d.Notes.Length > 0)
            AddText(d.Notes);
        var a = d.Anchor;
        AddText($"{CoordinateFormat.Decimal(a.Lat, a.Lon)} · MGRS {CoordinateFormat.Mgrs(a.Lat, a.Lon)}");
        if (d.Kind == DrawingKind.RoadClosure || d.ClosesRoads)
        {
            AddText(d.Active ? "Active: directions avoid this closure." : "Inactive: directions ignore it.", FontRole.Bold, d.Active ? Brand.Danger : Brand.Muted);
            AddButton(d.Active ? "Reopen (stop avoiding)" : "Close again (avoid)", ButtonStyle.Secondary, () =>
            {
                d.Active = !d.Active;
                d.Touch();
                AppServices.NotifyDrawingsChanged();
                Build();
            });
        }
        AddButtons(Btn("Edit", ButtonStyle.Primary, Edit), Btn("Copy for dispatch", ButtonStyle.Primary, () => Map.CopyText(d.DispatchText(), "Copied " + d.Title)));
        AddButtons(Btn("Zoom to", ButtonStyle.Secondary, () => ZoomTo(Map, d)), Btn("Directions here", ButtonStyle.Accent, () => Map.ShowPanel(new DirectionsPanel(Map, null, d.Anchor, d.Title))));
        AddButtons(Btn("Send to Field Toolkit", ButtonStyle.Secondary, () => Map.SendToToolkit(d.Anchor, d.Title, d.Notes)), Btn("Delete", ButtonStyle.Danger, Delete));
        Refit();
    }

    public static void ZoomTo(MapWindow map, Drawing d)
    {
        var pts = d.Points.Concat(d.DerivedRings().SelectMany(r => r.Ring)).ToList();
        if (pts.Count == 1)
            map.CenterOn(pts[0], 16);
        else if (pts.Count > 1)
            map.MapView.ZoomToFit(GeoBounds.Of(pts).Expand(50));
    }

    private void Edit()
    {
        using var dialog = new DrawingEditDialog(_d, isNew: false);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _d.Touch();
            AppServices.NotifyDrawingsChanged();
            Build();
        }
    }

    private void Delete()
    {
        if (MessageBox.Show(this, $"Delete \"{_d.Title}\"?", Branding.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        AppServices.Drawings.Items.Remove(_d);
        AppServices.NotifyDrawingsChanged();
        Map.ClosePanel();
    }

    public override void OnClosing()
    {
        Map.MapView.SelectedDrawing = null;
        Map.MapView.Invalidate();
    }
}
