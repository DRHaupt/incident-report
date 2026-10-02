using System.Globalization;
using FieldMap.Core.Drawings;
using Shared;
using Shared.UI;

namespace FieldMap.Views;

/// <summary>Label, notes and the kind-specific settings of a drawing (rings, hazmat distances and wind, closure state).</summary>
public sealed class DrawingEditDialog : Form
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly Drawing _d;
    private readonly TextBox _label = new() { Width = 460 };
    private readonly TextBox _notes = new() { Width = 460, Height = 90, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _rings = new() { Width = 460 };
    private readonly TextBox _un = new() { Width = 160 };
    private readonly TextBox _iso = new() { Width = 160 };
    private readonly TextBox _protect = new() { Width = 160 };
    private readonly TextBox _wind = new() { Width = 160 };
    private readonly TextBox _unit = new() { Width = 460 };
    private readonly CheckBox _active = new() { AutoSize = true };
    private readonly CheckBox _closes = new() { AutoSize = true, Text = "Close the roads inside this area to routing" };
    private readonly CheckBox _sector = new() { AutoSize = true, Text = "Downwind sector (untick for the ERG square)" };

    public DrawingEditDialog(Drawing d, bool isNew)
    {
        _d = d;
        Text = (isNew ? "New " : "Edit ") + Drawing.KindLabel(d.Kind).ToLowerInvariant();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 560);
        Padding = new Padding(14);

        var stack = Ui.Stack(new Padding(0));
        void Field(string label, Control c)
        {
            stack.Controls.Add(Ui.Text(label, FontRole.Bold));
            c.Margin = new Padding(0, 0, 0, 8);
            stack.Controls.Add(c);
        }
        _label.Text = d.Label;
        _notes.Text = d.Notes;
        Field("Label", _label);
        Field("Notes", _notes);
        if (d.Kind == DrawingKind.RangeRings)
        {
            _rings.Text = string.Join(", ", d.Rings.Select(r => r.ToString("0", Inv)));
            Field("Ring radii in metres (e.g. 500, 1000, 2000)", _rings);
        }
        if (d.Kind == DrawingKind.Hazmat)
        {
            _un.Text = d.UnNumber ?? "";
            _iso.Text = (d.IsolationMeters ?? 100).ToString("0", Inv);
            _protect.Text = (d.ProtectiveMeters ?? 800).ToString("0", Inv);
            _wind.Text = (d.WindFromDegrees ?? 270).ToString("0", Inv);
            _sector.Checked = d.ProtectiveShape == ProtectiveShape.Sector;
            Field("UN/NA number (optional)", _un);
            Field("Initial isolation distance (m, all directions)", _iso);
            Field("Protective action distance downwind (m)", _protect);
            Field("Wind FROM (degrees true, or N, NE, SW…)", _wind);
            stack.Controls.Add(_sector);
            stack.Controls.Add(Ui.Text("Take the distances from the ERG (Field Toolkit → Hazmat Lookup). This tool only draws them.", FontRole.Normal, Brand.Muted));
        }
        if (d.Kind == DrawingKind.Unit)
        {
            _unit.Text = d.Unit ?? "";
            Field("Unit call sign and status (e.g. \"E12 – on scene\")", _unit);
        }
        if (d.Kind == DrawingKind.RoadClosure)
        {
            _active.Text = "Closure active (directions avoid it)";
            _active.Checked = d.Active;
            stack.Controls.Add(_active);
        }
        if (d.Shape == DrawingShape.Polygon)
        {
            _closes.Checked = d.ClosesRoads;
            stack.Controls.Add(_closes);
        }
        foreach (var t in new[] { _label, _rings, _un, _iso, _protect, _wind, _unit })
            t.Styled(FontRole.Large);

        var ok = Ui.Button("Save", ButtonStyle.Primary, Accept);
        var cancel = Ui.Button("Cancel", ButtonStyle.Secondary, () => DialogResult = DialogResult.Cancel);
        var buttons = Ui.ButtonRow(ok, cancel);
        buttons.Dock = DockStyle.Bottom;
        buttons.Height = Ui.TargetHeight + 8;
        Controls.Add(stack);
        Controls.Add(buttons);
        AcceptButton = d.Kind == DrawingKind.Note ? null : ok;
        CancelButton = cancel;
        UiMode.TrackDialog(this);
        Shown += (_, _) => _label.Focus();
    }

    private static readonly Dictionary<string, double> Compass = new(StringComparer.OrdinalIgnoreCase)
    {
        ["N"] = 0, ["NNE"] = 22.5, ["NE"] = 45, ["ENE"] = 67.5, ["E"] = 90, ["ESE"] = 112.5, ["SE"] = 135, ["SSE"] = 157.5,
        ["S"] = 180, ["SSW"] = 202.5, ["SW"] = 225, ["WSW"] = 247.5, ["W"] = 270, ["WNW"] = 292.5, ["NW"] = 315, ["NNW"] = 337.5
    };

    public static bool TryParseDirection(string text, out double degrees)
    {
        text = text.Trim().TrimEnd('°');
        if (Compass.TryGetValue(text, out degrees))
            return true;
        return double.TryParse(text, NumberStyles.Float, Inv, out degrees) && degrees is >= 0 and <= 360;
    }

    private void Accept()
    {
        var d = _d;
        if (d.Kind == DrawingKind.RangeRings)
        {
            var rings = _rings.Text.Split(',', ';', ' ').Where(x => x.Length > 0).Select(x => double.TryParse(x, NumberStyles.Float, Inv, out var v) ? v : -1).ToList();
            if (rings.Count == 0 || rings.Any(r => r <= 0 || r > 200_000))
            {
                Warn("Enter ring radii in metres, separated by commas.");
                return;
            }
            d.Rings = rings.OrderBy(r => r).ToList();
        }
        if (d.Kind == DrawingKind.Hazmat)
        {
            if (!double.TryParse(_iso.Text, NumberStyles.Float, Inv, out var iso) || iso is <= 0 or > 50_000 ||
                !double.TryParse(_protect.Text, NumberStyles.Float, Inv, out var pd) || pd is <= 0 or > 100_000)
            {
                Warn("Enter the isolation and protective distances in metres.");
                return;
            }
            if (!TryParseDirection(_wind.Text, out var wind))
            {
                Warn("Enter the wind direction it blows FROM, in degrees (0–360) or as a compass point (N, NE, SW…).");
                return;
            }
            d.IsolationMeters = iso;
            d.ProtectiveMeters = pd;
            d.WindFromDegrees = wind % 360;
            d.UnNumber = _un.Text.Trim().TrimStart('U', 'N', 'u', 'n').Trim() is { Length: > 0 } un ? un : null;
            d.ProtectiveShape = _sector.Checked ? ProtectiveShape.Sector : ProtectiveShape.ErgSquare;
            AppServices.Settings.HazmatShape = d.ProtectiveShape;
            AppServices.Settings.Save();
        }
        if (d.Kind == DrawingKind.Unit)
            d.Unit = _unit.Text.Trim();
        if (d.Kind == DrawingKind.RoadClosure)
            d.Active = _active.Checked;
        if (d.Shape == DrawingShape.Polygon)
            d.ClosesRoads = _closes.Checked;
        d.Label = _label.Text.Trim();
        d.Notes = _notes.Text.Trim();
        DialogResult = DialogResult.OK;
    }

    private void Warn(string text) => MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
