using System.IO.Ports;
using FieldToolkit.Core.Location;
using FieldToolkit.UI;
using Shared.UI;

namespace FieldToolkit.Tools.Location;

/// <summary>Chooses where the position comes from: Windows location, a serial GPS (COM port), or typed in.</summary>
public sealed class SourceDialog : Form
{
    private readonly RadioButton _windows = new() { Text = "Windows location (built-in GPS)", AutoSize = true };
    private readonly RadioButton _serial = new() { Text = "GPS receiver on a COM port (NMEA)", AutoSize = true };
    private readonly RadioButton _manual = new() { Text = "Enter position by hand", AutoSize = true };
    private readonly ComboBox _port = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 160 };
    private readonly ComboBox _baud = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TextBox _coords = new() { Width = 420 };

    public SourceDialog()
    {
        Text = "Position source";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 470);
        Padding = new Padding(14);

        var s = AppServices.Settings;
        _windows.Checked = s.LocationSource == LocationSourceKind.Windows;
        _serial.Checked = s.LocationSource == LocationSourceKind.Serial;
        _manual.Checked = s.LocationSource == LocationSourceKind.Manual;
        _port.Items.AddRange(SerialPort.GetPortNames().Distinct().Order().Cast<object>().ToArray());
        _port.Text = s.SerialPortName;
        foreach (var b in new[] { 4800, 9600, 19200, 38400, 57600, 115200 })
            _baud.Items.Add(b);
        _baud.SelectedItem = s.SerialBaud;
        if (_baud.SelectedIndex < 0)
            _baud.SelectedIndex = 0;
        if (s.ManualLatitude is { } lat && s.ManualLongitude is { } lon)
            _coords.Text = CoordinateFormat.Decimal(lat, lon);

        var stack = Ui.Stack(new Padding(0));
        stack.Controls.Add(_windows);
        stack.Controls.Add(Ui.Text("Uses the laptop's GPS through Windows. Location must be turned on for desktop apps in Windows Settings."));
        stack.Controls.Add(_serial);
        var serialRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        serialRow.Controls.Add(new Label { Text = "Port", AutoSize = true, Margin = new Padding(0, 8, 6, 0) });
        serialRow.Controls.Add(_port);
        serialRow.Controls.Add(new Label { Text = "Baud", AutoSize = true, Margin = new Padding(14, 8, 6, 0) });
        serialRow.Controls.Add(_baud);
        stack.Controls.Add(serialRow);
        stack.Controls.Add(Ui.Text("For receivers that appear as a COM port (common on Panasonic and Getac rugged laptops). 4800 or 9600 baud is typical."));
        stack.Controls.Add(_manual);
        stack.Controls.Add(_coords);
        stack.Controls.Add(Ui.Text("Decimal, DMS, degrees-minutes or MGRS. Shown as \"Manual\" so nobody mistakes it for a GPS fix."));
        foreach (Control c in stack.Controls)
            c.Margin = new Padding(0, 4, 0, 4);
        foreach (var box in new Control[] { _port, _baud, _coords })
            box.Styled(FontRole.Large);

        var ok = Ui.Button("OK", ButtonStyle.Primary, Accept);
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
        var s = AppServices.Settings;
        if (_manual.Checked)
        {
            if (!CoordinateFormat.TryParse(_coords.Text, out var lat, out var lon))
            {
                MessageBox.Show(this, "The coordinates could not be read.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            AppServices.Location.SetManual(lat, lon);
        }
        else
        {
            s.LocationSource = _serial.Checked ? LocationSourceKind.Serial : LocationSourceKind.Windows;
            s.SerialPortName = _port.Text.Trim();
            s.SerialBaud = _baud.SelectedItem is int b ? b : 4800;
            s.Save();
            AppServices.Location.Restart();
        }
        DialogResult = DialogResult.OK;
    }
}
