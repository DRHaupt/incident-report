using FieldToolkit.Core.Location;
using FieldToolkit.UI;
using Shared;
using Shared.UI;

namespace FieldToolkit.Tools.Location;

/// <summary>Add or edit a saved point. Coordinates can be typed in any common format or taken from the current fix.</summary>
public sealed class PointDialog : Form
{
    private readonly TextBox _name = new() { Dock = DockStyle.Top };
    private readonly TextBox _coords = new() { Dock = DockStyle.Top };
    private readonly TextBox _note = new() { Dock = DockStyle.Top };
    private readonly WrapLabel _preview = new();
    private double _lat, _lon;
    private bool _valid;

    public PointDialog(string title, SavedPoint? point, GeoFix? current)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(600, 460);
        Padding = new Padding(14);

        foreach (var box in new[] { _name, _coords, _note })
            box.Styled(FontRole.Large);
        _name.Text = point?.Name ?? "";
        _note.Text = point?.Note ?? "";
        if (point != null)
            _coords.Text = CoordinateFormat.Decimal(point.Latitude, point.Longitude);
        else if (current != null)
            _coords.Text = CoordinateFormat.Decimal(current.Latitude, current.Longitude);
        _coords.TextChanged += (_, _) => UpdatePreview();

        var stack = Ui.Stack(new Padding(0));
        stack.Controls.Add(Ui.Text("Name (e.g. Incident site, Ambulance RVP)", FontRole.Bold));
        stack.Controls.Add(_name);
        stack.Controls.Add(Ui.Text("Coordinates – decimal, DMS, degrees-minutes or MGRS grid", FontRole.Bold, padding: new Padding(0, 10, 0, 2)));
        stack.Controls.Add(_coords);
        stack.Controls.Add(_preview);
        if (current != null)
        {
            var here = Ui.Button("Use current position", ButtonStyle.Secondary, () => _coords.Text = CoordinateFormat.Decimal(current.Latitude, current.Longitude));
            stack.Controls.Add(here);
        }
        stack.Controls.Add(Ui.Text("Note", FontRole.Bold, padding: new Padding(0, 10, 0, 2)));
        stack.Controls.Add(_note);

        var ok = Ui.Button("Save", ButtonStyle.Primary, Accept);
        var cancel = Ui.Button("Cancel", ButtonStyle.Secondary, () => DialogResult = DialogResult.Cancel);
        var buttons = Ui.ButtonRow(ok, cancel);
        buttons.Dock = DockStyle.Bottom;
        buttons.Height = Ui.TargetHeight + 8;
        Controls.Add(stack);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        UiMode.TrackDialog(this);
        UpdatePreview();
        Shown += (_, _) => _name.Focus();
    }

    public SavedPoint? Result { get; private set; }

    private void UpdatePreview()
    {
        _valid = CoordinateFormat.TryParse(_coords.Text, out _lat, out _lon);
        _preview.Text = _valid
            ? $"✓ {CoordinateFormat.Dms(_lat, _lon)}   ·   {CoordinateFormat.Mgrs(_lat, _lon)}"
            : "Not recognised yet. Examples: 51.50735, -0.12776  ·  51°30′26″N 0°07′40″W  ·  30U XC 99319 10158";
        _preview.ForeColor = _valid ? Brand.Success : Brand.Danger;
    }

    private void Accept()
    {
        if (!_valid)
        {
            MessageBox.Show(this, "The coordinates could not be read.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Result = new SavedPoint
        {
            Name = string.IsNullOrWhiteSpace(_name.Text) ? $"Point {DateTime.Now:HH:mm}" : _name.Text.Trim(),
            Latitude = _lat,
            Longitude = _lon,
            Note = _note.Text.Trim()
        };
        DialogResult = DialogResult.OK;
    }
}
