using System.Globalization;
using FieldMap.Core.Radio;
using Shared;
using Shared.Core.Geo;
using Shared.UI;

namespace FieldMap.Views;

/// <summary>Add or edit a repeater: position, frequencies, access tone, mode and the antenna details used for coverage.</summary>
public sealed class RepeaterEditDialog : Form
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly Repeater _r;
    private readonly ComboBox _source = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };
    private readonly TextBox _name = new() { Width = 460 };
    private readonly TextBox _call = new() { Width = 220 };
    private readonly TextBox _position = new() { Width = 460 };
    private readonly TextBox _output = new() { Width = 220 };
    private readonly TextBox _input = new() { Width = 220 };
    private readonly TextBox _tone = new() { Width = 220 };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 220 };
    private readonly TextBox _height = new() { Width = 220 };
    private readonly TextBox _erp = new() { Width = 220 };
    private readonly TextBox _owner = new() { Width = 460 };
    private readonly TextBox _notes = new() { Width = 460, Height = 70, Multiline = true, ScrollBars = ScrollBars.Vertical };

    private static readonly RepeaterSource[] Editable = { RepeaterSource.Own, RepeaterSource.Event, RepeaterSource.Amateur, RepeaterSource.RepeaterBook, RepeaterSource.Licensed };

    public RepeaterEditDialog(Repeater r, bool isNew)
    {
        _r = r;
        Text = isNew ? "Add repeater" : "Edit repeater";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 640);
        Padding = new Padding(14);

        var stack = Ui.Stack(new Padding(0));
        void Field(string label, Control c, string? hint = null)
        {
            stack.Controls.Add(Ui.Text(label, FontRole.Bold));
            c.Margin = new Padding(0, 0, 0, hint == null ? 8 : 0);
            stack.Controls.Add(c);
            if (hint != null)
                stack.Controls.Add(Ui.Text(hint, FontRole.Normal, Brand.Muted, new Padding(0, 0, 0, 8)));
        }
        foreach (var s in Editable)
            _source.Items.Add(Repeater.SourceLabel(s));
        _source.SelectedIndex = Math.Max(0, Array.IndexOf(Editable, r.Source));
        _name.Text = r.Name;
        _call.Text = r.CallSign;
        _position.Text = r.Lat == 0 && r.Lon == 0 ? "" : string.Create(Inv, $"{r.Lat:0.00000}, {r.Lon:0.00000}");
        _output.Text = r.OutputMHz > 0 ? r.OutputMHz.ToString("0.000###", Inv) : "";
        _input.Text = r.InputMHz?.ToString("0.000###", Inv) ?? "";
        _tone.Text = r.Tone;
        _mode.Items.AddRange(new object[] { "FM", "DMR", "P25", "NXDN", "D-STAR", "Fusion" });
        _mode.Text = r.Mode;
        _height.Text = r.AntennaHeightM?.ToString("0.#", Inv) ?? "";
        _erp.Text = r.ErpWatts?.ToString("0.#", Inv) ?? "";
        _owner.Text = r.Owner;
        _notes.Text = r.Notes;

        Field("Kind", _source);
        Field("Name / site", _name);
        Field("Call sign", _call);
        Field("Position", _position, "Decimal degrees (50.6745, -120.3273), degrees-minutes-seconds, UTM or MGRS.");
        Field("Output frequency (MHz) – what radios listen on", _output);
        Field("Input frequency (MHz) – what radios transmit on", _input, "Leave empty for simplex, or enter an offset such as +0.6 or -5.");
        Field("Access tone / code", _tone, "e.g. 100.0 (CTCSS), D023 (DCS), NAC 293, CC 1");
        Field("Mode", _mode);
        Field("Antenna height above ground (m)", _height, $"Used for coverage. Empty: {Services.CoverageService.DefaultAntennaHeightM:0} m is assumed.");
        Field("Effective radiated power (W)", _erp, $"Used for coverage. Empty: {Services.CoverageService.DefaultErpWatts:0} W is assumed.");
        Field("Owner / licensee / club", _owner);
        Field("Notes", _notes);

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
    }

    private void Accept()
    {
        string? problem = null;
        if (!CoordinateFormat.TryParse(_position.Text, out var lat, out var lon))
            problem = "Enter the repeater's position.";
        var output = RepeaterImport.ParseMHz(_output.Text);
        if (problem == null && output == null)
            problem = "Enter the output frequency in MHz.";
        double? input = null;
        var inText = _input.Text.Trim();
        if (problem == null && inText.Length > 0)
        {
            input = inText.StartsWith('+') || inText.StartsWith('-') || (RepeaterImport.ParseMHz(inText) is { } v && v < 30)
                ? RepeaterImport.ParseOffset(inText, output!.Value) is { } off ? Math.Round(output.Value + off, 5) : null
                : RepeaterImport.ParseMHz(inText);
            if (input == null)
                problem = "The input frequency isn't a frequency or offset.";
        }
        double? Num(TextBox t) => double.TryParse(t.Text.Trim().Replace(',', '.'), NumberStyles.Float, Inv, out var v) && v > 0 ? v : null;
        if (problem != null)
        {
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _r.Source = Editable[Math.Max(0, _source.SelectedIndex)];
        _r.Name = _name.Text.Trim();
        _r.CallSign = _call.Text.Trim().ToUpperInvariant();
        _r.Lat = lat;
        _r.Lon = lon;
        _r.OutputMHz = output!.Value;
        _r.InputMHz = input;
        _r.Tone = RepeaterImport.NormalizeTone(_tone.Text);
        _r.Mode = _mode.Text.Trim().Length > 0 ? _mode.Text.Trim() : "FM";
        _r.AntennaHeightM = Num(_height);
        _r.ErpWatts = Num(_erp);
        _r.Owner = _owner.Text.Trim();
        _r.Notes = _notes.Text.Trim();
        if (_r.SourceName.Length == 0)
            _r.SourceName = "Entered in Field Map";
        DialogResult = DialogResult.OK;
    }
}
