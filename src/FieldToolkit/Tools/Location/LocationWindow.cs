using Shared.Core.Geo;
using FieldToolkit.Framework;
using FieldToolkit.UI;
using Shared;
using Shared.UI;

using Shared.Location;

namespace FieldToolkit.Tools.Location;

/// <summary>
/// Current position in several formats (decimal, DMS, degrees-minutes, MGRS, UTM) with a radio read-out and "copy for
/// dispatch", plus saved points with live distance and bearing.
/// </summary>
public sealed class LocationWindow : ToolWindow
{
    public const string ToolId = "location";

    private readonly VStack _status = new() { Dock = DockStyle.Top, Padding = new Padding(10, 6, 10, 6) };
    private readonly WrapLabel _statusText;
    private readonly FlowLayoutPanel _stack;
    private readonly Dictionary<string, WrapLabel> _values = new();
    private readonly WrapLabel _spoken;
    private readonly Button _readoutToggle;
    private readonly FlowLayoutPanel _pointList = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
    private readonly Dictionary<Guid, (WrapLabel Text, BearingArrow Arrow)> _pointRows = new();
    private readonly System.Windows.Forms.Timer _age = new() { Interval = 1000 };
    private DateTime _confirmUntil;

    private static readonly (string Key, string Caption)[] Formats =
    {
        ("decimal", "Latitude / longitude"),
        ("dms", "Degrees, minutes, seconds"),
        ("ddm", "Degrees, decimal minutes"),
        ("mgrs", "Grid reference (MGRS)"),
        ("utm", "UTM")
    };

    public LocationWindow(string? state) : base(ToolRegistry.Find(ToolId)!)
    {
        _statusText = Ui.Text("", FontRole.Bold, Color.White);
        _status.Controls.Add(_statusText);

        _stack = Ui.Stack(new Padding(12, 6, 12, 12));
        foreach (var (key, caption) in Formats)
        {
            _stack.Controls.Add(Ui.Text(caption, FontRole.Bold, Brand.Muted, new Padding(0, 6, 0, 0)));
            var value = Ui.Text("—", key is "decimal" or "mgrs" ? FontRole.Title : FontRole.Large, Brand.Navy);
            value.Cursor = Cursors.Hand;
            var k = key;
            value.Click += (_, _) => CopyOne(k);
            _values[key] = value;
            _stack.Controls.Add(value);
        }
        _stack.Controls.Add(Ui.Text("Tap any line to copy it.", fore: Brand.Muted));

        var readoutBox = new VStack { BackColor = Brand.Panel, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 10, 0, 4) };
        _readoutToggle = Ui.Button("", ButtonStyle.Secondary, ToggleReadout, FontRole.Normal);
        _readoutToggle.Height = Ui.Scaled(40);
        _spoken = Ui.Text("", FontRole.Large, Brand.Navy);
        readoutBox.Controls.Add(Ui.Text("Read out on the radio", FontRole.Heading, Brand.Navy));
        readoutBox.Controls.Add(_spoken);
        readoutBox.Controls.Add(_readoutToggle);
        _stack.Controls.Add(readoutBox);

        var actions = Ui.ButtonRow(
            Ui.Button("Copy for dispatch", ButtonStyle.Primary, CopyForDispatch, FontRole.Heading),
            Ui.Button("Save this point", ButtonStyle.Accent, SaveCurrent, FontRole.Heading),
            Ui.Button("Open in map", ButtonStyle.Secondary, OpenCurrentInMap, FontRole.Heading));
        actions.Height = Ui.Scaled(64);
        actions.FixedSize();
        actions.Margin = new Padding(0, 6, 0, 6);
        _stack.Controls.Add(actions);

        _stack.Controls.Add(Ui.Text("Saved points", FontRole.Heading, Brand.Navy, new Padding(0, 12, 0, 2)));
        _stack.Controls.Add(_pointList);
        var pointButtons = Ui.ButtonRow(
            Ui.Button("Add point by coordinates…", ButtonStyle.Secondary, AddByCoordinates),
            Ui.Button("Position source…", ButtonStyle.Secondary, ChooseSource));
        pointButtons.Height = Ui.Scaled(56);
        pointButtons.FixedSize();
        _stack.Controls.Add(pointButtons);
        _stack.Resize += (_, _) => SizePointRows();

        Body.Controls.Add(_stack);
        Body.Controls.Add(_status);

        AppServices.Location.Changed += OnLocationChanged;
        AppServices.Points.Changed += OnPointsChanged;
        _age.Tick += (_, _) => UpdateStatus();
        FormClosed += (_, _) =>
        {
            AppServices.Location.Changed -= OnLocationChanged;
            AppServices.Points.Changed -= OnPointsChanged;
            AppServices.Location.Release();
            _age.Dispose();
        };
        AppServices.Location.Acquire();
        _age.Start();
        UpdateReadoutButton();
        RebuildPoints();
        OnLocationChanged(null, EventArgs.Empty);
    }

    private static GeoFix? Fix => AppServices.Location.Fix;

    private void OnLocationChanged(object? s, EventArgs e)
    {
        var fix = Fix;
        if (fix != null)
        {
            _values["decimal"].Text = CoordinateFormat.Decimal(fix.Latitude, fix.Longitude);
            _values["dms"].Text = CoordinateFormat.Dms(fix.Latitude, fix.Longitude);
            _values["ddm"].Text = CoordinateFormat.Ddm(fix.Latitude, fix.Longitude);
            _values["mgrs"].Text = CoordinateFormat.Mgrs(fix.Latitude, fix.Longitude);
            _values["utm"].Text = CoordinateFormat.Utm(fix.Latitude, fix.Longitude);
            _spoken.Text = Spoken(fix);
        }
        else
        {
            foreach (var v in _values.Values)
                v.Text = "—";
            _spoken.Text = "No position yet.";
        }
        UpdateStatus();
        UpdatePointDistances();
    }

    private static string Spoken(GeoFix fix) =>
        AppServices.Settings.ReadoutFormat == "decimal" || !Utm.InRange(fix.Latitude)
            ? CoordinateFormat.SpokenDecimal(fix.Latitude, fix.Longitude)
            : CoordinateFormat.SpokenMgrs(Mgrs.FromLatLon(fix.Latitude, fix.Longitude));

    private void UpdateStatus()
    {
        if (DateTime.UtcNow < _confirmUntil)
            return;
        var svc = AppServices.Location;
        var fix = svc.Fix;
        string text;
        Color back;
        if (fix != null && svc.State == LocationState.Ready)
        {
            var age = DateTime.UtcNow - fix.TimestampUtc;
            var stale = fix.Source != "Entered manually" && age > LocationService.StaleAfter;
            var parts = new List<string> { fix.Source };
            if (fix.AccuracyMeters is { } acc)
                parts.Add($"±{acc:0} m");
            if (fix.Satellites is { } sats)
                parts.Add($"{sats} satellites");
            if (fix.AltitudeMeters is { } alt)
                parts.Add($"alt {alt:0} m");
            parts.Add(fix.Source == "Entered manually" ? "MANUAL – not a GPS fix" : $"fix {fix.TimestampUtc.ToLocalTime():HH:mm:ss}" + (stale ? $" – {(int)age.TotalSeconds} s OLD" : ""));
            text = string.Join("  ·  ", parts);
            back = stale || fix.Source == "Entered manually" ? Brand.Amber : Brand.Success;
        }
        else
        {
            text = svc.Status + (fix != null ? $"  ·  last fix {fix.TimestampUtc.ToLocalTime():HH:mm:ss}" : "");
            back = svc.State == LocationState.Error ? Brand.Danger : Brand.Amber;
        }
        if (_statusText.Text != text)
            _statusText.Text = text;
        var fore = back == Brand.Amber ? Brand.Navy : Color.White;
        if (_status.BackColor != UiMode.Map(back, true))
        {
            UiMode.SetColors(_status, back, fore);
            UiMode.SetColors(_statusText, back, fore);
        }
    }

    // ------------------------------------------------------------------ copy and read-out

    private void ToggleReadout()
    {
        var s = AppServices.Settings;
        s.ReadoutFormat = s.ReadoutFormat == "mgrs" ? "decimal" : "mgrs";
        s.Save();
        UpdateReadoutButton();
        OnLocationChanged(null, EventArgs.Empty);
    }

    private void UpdateReadoutButton() =>
        _readoutToggle.Text = AppServices.Settings.ReadoutFormat == "mgrs" ? "Reading grid reference – switch to lat/long" : "Reading lat/long – switch to grid reference";

    /// <summary>Plain-ASCII block for pasting into dispatch systems, messages or the radio log.</summary>
    internal static string DispatchText(GeoFix fix, string? label = null) => Shared.Core.Geo.DispatchText.Block(fix, label);

    private void CopyForDispatch()
    {
        if (Fix is not { } fix)
        {
            MessageBox.Show(this, "There is no position to copy yet.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Copy(DispatchText(fix), "Location copied – paste it into the dispatch system or message.");
    }

    private void OpenCurrentInMap()
    {
        if (Fix is not { } fix)
        {
            MessageBox.Show(this, "There is no position to show yet.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ShowStatus(FieldMapLink.OpenInMap("My position", fix.Latitude, fix.Longitude, fix.Source));
    }

    private void ShowStatus(string message)
    {
        _statusText.Text = "✓ " + message;
        _confirmUntil = DateTime.UtcNow.AddSeconds(5);
    }

    private void CopyOne(string key)
    {
        if (Fix != null)
            Copy(_values[key].Text, "Copied " + _values[key].Text);
    }

    private void Copy(string text, string confirmation)
    {
        try
        {
            Clipboard.SetText(text);
            _statusText.Text = "✓ " + confirmation;
            _confirmUntil = DateTime.UtcNow.AddSeconds(4);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "The clipboard is busy. Try again.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ------------------------------------------------------------------ saved points

    private void SaveCurrent()
    {
        if (Fix is not { } fix)
        {
            AddByCoordinates();
            return;
        }
        var name = InputDialog.Ask(this, "Save point", "Name for this point (e.g. Incident site, Ambulance RVP):", $"Point {DateTime.Now:HH:mm}");
        if (name == null)
            return;
        AppServices.Points.Items.Add(new SavedPoint { Name = name.Length > 0 ? name : $"Point {DateTime.Now:HH:mm}", Latitude = fix.Latitude, Longitude = fix.Longitude, Note = fix.Source });
        AppServices.Points.Save();
    }

    private void AddByCoordinates()
    {
        using var d = new PointDialog("Add point", null, Fix);
        if (d.ShowDialog(this) == DialogResult.OK && d.Result != null)
        {
            AppServices.Points.Items.Add(d.Result);
            AppServices.Points.Save();
        }
    }

    private void ChooseSource()
    {
        using var d = new SourceDialog(AppServices.Location);
        d.ShowDialog(this);
    }

    private void OnPointsChanged(object? s, EventArgs e) => RebuildPoints();

    private void RebuildPoints()
    {
        _pointList.SuspendLayout();
        foreach (Control c in _pointList.Controls.Cast<Control>().ToList())
            c.Dispose();
        _pointList.Controls.Clear();
        _pointRows.Clear();
        if (AppServices.Points.Items.Count == 0)
            _pointList.Controls.Add(Ui.Text("No saved points. Use \"Save this point\" at the incident site, or add a rendezvous point by its coordinates.", fore: Brand.Muted));
        foreach (var p in AppServices.Points.Items)
            _pointList.Controls.Add(PointRow(p));
        UiMode.Apply(_pointList);
        _pointList.ResumeLayout(true);
        SizePointRows();
        UpdatePointDistances();
    }

    private Control PointRow(SavedPoint p)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, Height = Ui.Scaled(150), BackColor = Brand.Panel, Margin = new Padding(0, 0, 0, 6), Padding = new Padding(8, 4, 8, 4) };
        row.FixedSize();
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.Scaled(80)));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.Scaled(52)));
        var arrow = new BearingArrow { Dock = DockStyle.Fill };
        var text = Ui.Text("", FontRole.Normal);
        text.Dock = DockStyle.Fill;
        row.Controls.Add(arrow, 0, 0);
        row.Controls.Add(text, 1, 0);
        var buttons = Ui.ButtonRow(
            Ui.Button("Copy", ButtonStyle.Secondary, () => CopyPoint(p)),
            Ui.Button("Map", ButtonStyle.Secondary, () => ShowStatus(FieldMapLink.OpenInMap(p.Name, p.Latitude, p.Longitude, p.Note))),
            Ui.Button("Edit", ButtonStyle.Secondary, () => EditPoint(p)),
            Ui.Button("Delete", ButtonStyle.Danger, () => DeletePoint(p)));
        buttons.Dock = DockStyle.Fill;
        row.Controls.Add(buttons, 0, 1);
        row.SetColumnSpan(buttons, 2);
        _pointRows[p.Id] = (text, arrow);
        return row;
    }

    private void SizePointRows()
    {
        var width = _stack.ClientSize.Width - _stack.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        foreach (Control c in _pointList.Controls)
            c.Width = Math.Max(100, width);
    }

    private void UpdatePointDistances()
    {
        var fix = Fix;
        foreach (var p in AppServices.Points.Items)
        {
            if (!_pointRows.TryGetValue(p.Id, out var row))
                continue;
            var where = $"{p.Name}\n{CoordinateFormat.Decimal(p.Latitude, p.Longitude)}  ·  {CoordinateFormat.Mgrs(p.Latitude, p.Longitude)}";
            if (fix != null)
            {
                var d = GeoMath.Distance(fix.Latitude, fix.Longitude, p.Latitude, p.Longitude);
                var b = GeoMath.Bearing(fix.Latitude, fix.Longitude, p.Latitude, p.Longitude);
                where += $"\n{GeoMath.FormatDistance(d)}  ·  bearing {GeoMath.FormatBearing(b)} (true)";
                row.Arrow.Bearing = d < 1 ? null : b;
            }
            else
            {
                where += "\nDistance shown when a position is available";
                row.Arrow.Bearing = null;
            }
            if (p.Note.Length > 0)
                where += $"\n{p.Note}";
            if (row.Text.Text != where)
                row.Text.Text = where;
        }
    }

    private void CopyPoint(SavedPoint p)
    {
        var text = DispatchText(new GeoFix(p.Latitude, p.Longitude, null, null, p.SavedUtc, "Saved point"), p.Name);
        if (Fix is { } fix)
            text += Environment.NewLine + $"From current position: {GeoMath.FormatDistance(GeoMath.Distance(fix.Latitude, fix.Longitude, p.Latitude, p.Longitude))}, " +
                    $"bearing {GeoMath.FormatBearing(GeoMath.Bearing(fix.Latitude, fix.Longitude, p.Latitude, p.Longitude))} true";
        if (p.Note.Length > 0)
            text += Environment.NewLine + "Note: " + p.Note;
        Copy(text, $"Copied {p.Name}");
    }

    private void EditPoint(SavedPoint p)
    {
        using var d = new PointDialog("Edit point", p, Fix);
        if (d.ShowDialog(this) != DialogResult.OK || d.Result == null)
            return;
        p.Name = d.Result.Name;
        p.Latitude = d.Result.Latitude;
        p.Longitude = d.Result.Longitude;
        p.Note = d.Result.Note;
        AppServices.Points.Save();
    }

    private void DeletePoint(SavedPoint p)
    {
        if (MessageBox.Show(this, $"Delete the saved point \"{p.Name}\"?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        AppServices.Points.Items.Remove(p);
        AppServices.Points.Save();
    }
}
