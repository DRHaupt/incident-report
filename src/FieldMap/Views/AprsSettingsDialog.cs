using System.IO.Ports;
using FieldMap.Core.Aprs;
using Shared;
using Shared.UI;

namespace FieldMap.Views;

/// <summary>
/// APRS settings: receive from the internet (APRS-IS) and/or a radio TNC, call sign and passcode, and the optional
/// position beacon. Receiving needs no licence; sending does.
/// </summary>
public sealed class AprsSettingsDialog : Form
{
    private static readonly (string Label, string Symbol)[] Symbols =
    {
        ("Car", "/>"), ("Ambulance", "/a"), ("Fire truck", "/f"), ("Police", "/P"), ("Truck", "/k"), ("SUV / 4x4", "\\k"), ("Jeep", "/j"),
        ("Van", "/v"), ("Person", "/["), ("Incident command post", "/c"), ("EOC", "/o"), ("Helicopter", "/X"), ("House", "/-"), ("Aid station", "/A")
    };

    private readonly CheckBox _internet = Check("Receive from the internet (APRS-IS)");
    private readonly TextBox _server = Box(300);
    private readonly NumericUpDown _serverPort = Number(1, 65535, 14580);
    private readonly NumericUpDown _range = Number(5, 1000, 100);
    private readonly TextBox _extra = Box(460);
    private readonly CheckBox _radio = Check("Receive from a radio TNC (KISS)");
    private readonly ComboBox _connection = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly ComboBox _com = new() { Width = 200 };
    private readonly ComboBox _baud = new() { Width = 160 };
    private readonly TextBox _host = Box(300);
    private readonly NumericUpDown _tcpPort = Number(1, 65535, 8001);
    private readonly TextBox _init = Box(460);
    private readonly TextBox _call = Box(200);
    private readonly TextBox _passcode = Box(200);
    private readonly CheckBox _beacon = Check("Send my position (beacon)");
    private readonly CheckBox _beaconRadio = Check("by radio");
    private readonly CheckBox _beaconInternet = Check("by internet (needs passcode)");
    private readonly ComboBox _symbol = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly TextBox _comment = Box(460);
    private readonly TextBox _path = Box(300);
    private readonly CheckBox _messaging = Check("Messaging: receive messages to my call sign and acknowledge them");
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly NumericUpDown _sbLow = Number(1, 60, 8);
    private readonly NumericUpDown _sbHigh = Number(20, 200, 90);
    private readonly NumericUpDown _sbSlow = Number(1, 120, 20);
    private readonly NumericUpDown _sbFast = Number(30, 1800, 180);
    private readonly NumericUpDown _sbAngle = Number(5, 90, 28);
    private readonly NumericUpDown _sbSlope = Number(0, 1000, 255);
    private readonly NumericUpDown _sbTurnTime = Number(5, 300, 15);
    private readonly Panel _smartBox = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
    private readonly Panel _fixedBox = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
    private readonly NumericUpDown _every = Number(1, 120, 10);
    private readonly NumericUpDown _everyMoving = Number(1, 60, 2);
    private readonly NumericUpDown _showMinutes = Number(5, 1440, 60);
    private readonly NumericUpDown _trackMinutes = Number(5, 1440, 120);

    public AprsSettingsDialog()
    {
        Text = "APRS settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(Math.Min(600, screen.Width - 40), Math.Min(760, screen.Height - 60));
        MinimumSize = new Size(420, 360);
        Padding = new Padding(14, 8, 14, 8);

        var s = AppServices.Settings.Aprs;
        _internet.Checked = s.InternetEnabled;
        _server.Text = s.Server;
        _serverPort.Value = Math.Clamp(s.ServerPort, 1, 65535);
        _range.Value = Math.Clamp(s.RangeKm, 5, 1000);
        _extra.Text = s.ExtraFilter;
        _radio.Checked = s.RadioEnabled;
        _connection.Items.AddRange(new object[] { "Serial / USB / Bluetooth COM port", "TCP (e.g. Direwolf, KISS over network)" });
        _connection.SelectedIndex = s.Connection == TncConnection.Tcp ? 1 : 0;
        try
        {
            _com.Items.AddRange(SerialPort.GetPortNames().Distinct().OrderBy(x => x).Cast<object>().ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // No port list; the name can still be typed.
        }
        _com.Text = s.ComPort;
        _baud.Items.AddRange(new object[] { "1200", "4800", "9600", "19200", "38400", "57600", "115200" });
        _baud.Text = s.Baud.ToString();
        _host.Text = s.TcpHost;
        _tcpPort.Value = Math.Clamp(s.TcpPort, 1, 65535);
        _init.Text = s.InitText;
        _call.Text = s.Callsign;
        _passcode.Text = s.Passcode;
        _passcode.UseSystemPasswordChar = true;
        _beacon.Checked = s.BeaconEnabled;
        _beaconRadio.Checked = s.BeaconRadio;
        _beaconInternet.Checked = s.BeaconInternet;
        foreach (var (label, symbol) in Symbols)
            _symbol.Items.Add(label);
        var idx = Array.FindIndex(Symbols, x => x.Symbol == s.BeaconSymbol);
        _symbol.SelectedIndex = idx >= 0 ? idx : 0;
        _comment.Text = s.BeaconComment;
        _path.Text = s.BeaconPath;
        _messaging.Checked = s.MessagingEnabled;
        _mode.Items.AddRange(new object[] { "SmartBeaconing (by speed and turns)", "Fixed interval" });
        _mode.SelectedIndex = s.BeaconMode == BeaconMode.Fixed ? 1 : 0;
        var sb = s.Smart;
        _sbLow.Value = (decimal)Math.Clamp(sb.LowSpeedKmh, 1, 60);
        _sbHigh.Value = (decimal)Math.Clamp(sb.HighSpeedKmh, 20, 200);
        _sbSlow.Value = Math.Clamp(sb.SlowRateSeconds / 60, 1, 120);
        _sbFast.Value = Math.Clamp(sb.FastRateSeconds, 30, 1800);
        _sbAngle.Value = (decimal)Math.Clamp(sb.MinTurnAngle, 5, 90);
        _sbSlope.Value = (decimal)Math.Clamp(sb.TurnSlope, 0, 1000);
        _sbTurnTime.Value = Math.Clamp(sb.MinTurnSeconds, 5, 300);
        _every.Value = Math.Clamp(s.BeaconMinutes, 1, 120);
        _everyMoving.Value = Math.Clamp(s.BeaconMovingMinutes, 1, 60);
        _showMinutes.Value = Math.Clamp(s.ShowMinutes, 5, 1440);
        _trackMinutes.Value = Math.Clamp(s.TrackMinutes, 5, 1440);

        var stack = Ui.Stack(new Padding(0, 0, 8, 0));
        void Head(string text) => stack.Controls.Add(Ui.Text(text, FontRole.Heading, Brand.Navy, new Padding(0, 12, 0, 2)));
        void Hint(string text) => stack.Controls.Add(Ui.Text(text, FontRole.Normal, Brand.Muted));
        void Field(string label, Control c, string? hint = null)
        {
            stack.Controls.Add(Ui.Text(label, FontRole.Bold));
            c.Margin = new Padding(0, 0, 0, 6);
            stack.Controls.Add(c);
            if (hint != null)
                Hint(hint);
        }

        stack.Controls.Add(Ui.Text("APRS shows amateur radio stations that report their position: other responders' vehicles, " +
            "search teams, weather stations, digipeaters. It is unencrypted and public. Receiving needs no licence. Sending your " +
            "position needs an amateur radio licence (in Canada, ISED Basic Qualification) and your own call sign.", FontRole.Normal, Brand.Navy));

        Head("Internet (APRS-IS)");
        stack.Controls.Add(_internet);
        Field("Server", _server, "noam.aprs2.net picks a North American server of the volunteer APRS-IS network (rotate.aprs2.net: worldwide). Port 14580 sends only the area you ask for.");
        Field("Port", _serverPort);
        Field("Receive range around my position or the map (km)", _range);
        Field("Extra filter (optional)", _extra, "APRS-IS filter terms, e.g. b/VE7ABC* to always include stations starting VE7ABC.");

        Head("Radio (TNC)");
        stack.Controls.Add(_radio);
        Field("Connection", _connection, "KISS mode TNCs: hardware TNCs, radios with a built-in TNC in KISS mode, Bluetooth TNCs paired as a COM port, or a sound-card modem such as Direwolf.");
        Field("COM port", _com);
        Field("Speed (baud)", _baud, "Use the TNC's serial speed. Bluetooth and USB-virtual ports usually ignore it.");
        Field("TCP host", _host);
        Field("TCP port", _tcpPort, "Direwolf's KISS TCP port is 8001 by default.");
        Field("Text to put the TNC in KISS mode (optional)", _init, @"Sent once when opening, for TNCs that start in text mode. Use \r for Enter, e.g. KISS ON\rRESTART\r. See your TNC's manual.");

        Head("Call sign and sending");
        Field("Your amateur radio call sign, with SSID (e.g. VE7ABC-9)", _call, "Without a call sign Field Map only receives. SSID -9 is the usual choice for a vehicle.");
        Field("APRS-IS passcode (only to send by internet)", _passcode, "Issued for your call sign by APRS-IS software authors and server operators. Stored in this computer's Field Map settings.");
        stack.Controls.Add(_messaging);
        Hint("Acknowledging is a transmission. Your beacon tells other stations you can receive messages.");
        stack.Controls.Add(_beacon);
        var how = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        how.Controls.Add(_beaconRadio);
        how.Controls.Add(_beaconInternet);
        stack.Controls.Add(how);
        Field("Symbol", _symbol);
        Field("Comment (optional, e.g. unit name)", _comment);
        Field("Radio path", _path, "WIDE1-1,WIDE2-1 is the usual path in North America. Use WIDE2-1 or no path where digipeaters are busy.");
        Field("When to send", _mode, "SmartBeaconing sends often at speed, rarely when stopped, and right after turns, so your track follows the road without crowding the channel.");

        // The two schedules' fields live in their own stacks, shown for the chosen mode.
        FlowLayoutPanel SubStack(Panel host)
        {
            var sub = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
            host.Controls.Add(sub);
            return sub;
        }
        var smart = SubStack(_smartBox);
        var fixedStack = SubStack(_fixedBox);
        void SubField(FlowLayoutPanel target, string label, Control c, string? hint = null)
        {
            target.Controls.Add(Ui.Text(label, FontRole.Bold));
            c.Margin = new Padding(0, 0, 0, 6);
            target.Controls.Add(c);
            if (hint != null)
                target.Controls.Add(Ui.Text(hint, FontRole.Normal, Brand.Muted));
        }
        SubField(smart, "Stopped below (km/h)", _sbLow);
        SubField(smart, "Every (minutes) when stopped", _sbSlow);
        SubField(smart, "Fastest at (km/h)", _sbHigh);
        SubField(smart, "Every (seconds) at that speed", _sbFast, "In between, the interval grows as you slow down.");
        SubField(smart, "Turn that triggers a beacon (degrees, at speed)", _sbAngle);
        SubField(smart, "Turn slope", _sbSlope, "Extra turn needed at low speed: angle + slope ÷ speed in mph (255 is the usual value).");
        SubField(smart, "At most one turn beacon every (seconds)", _sbTurnTime);
        SubField(fixedStack, "Every (minutes, stopped)", _every);
        SubField(fixedStack, "Every (minutes, moving)", _everyMoving, "Don't beacon more often than you need: the radio channel is shared by everyone.");
        stack.Controls.Add(_smartBox);
        stack.Controls.Add(_fixedBox);
        _mode.SelectedIndexChanged += (_, _) => SyncMode();
        SyncMode();

        Head("Map");
        Field("Show stations heard in the last (minutes)", _showMinutes);
        Field("Track length (minutes)", _trackMinutes);

        void SyncEnabled()
        {
            var tcp = _connection.SelectedIndex == 1;
            _com.Enabled = _baud.Enabled = !tcp;
            _host.Enabled = _tcpPort.Enabled = tcp;
            var canSend = AprsBeacon.IsValidCallsign(_call.Text);
            _beacon.Enabled = _messaging.Enabled = canSend;
            _beaconRadio.Enabled = _beaconInternet.Enabled = canSend && _beacon.Checked;
        }
        _connection.SelectedIndexChanged += (_, _) => SyncEnabled();
        _call.TextChanged += (_, _) => SyncEnabled();
        _beacon.CheckedChanged += (_, _) => SyncEnabled();
        SyncEnabled();

        foreach (var c in new Control[] { _server, _extra, _connection, _com, _baud, _host, _init, _call, _passcode, _symbol, _comment, _path, _serverPort, _range, _tcpPort, _every, _everyMoving, _showMinutes, _trackMinutes, _mode, _sbLow, _sbHigh, _sbSlow, _sbFast, _sbAngle, _sbSlope, _sbTurnTime })
            c.Styled(FontRole.Large);

        var ok = Ui.Button("Save and connect", ButtonStyle.Primary, Accept);
        var cancel = Ui.Button("Cancel", ButtonStyle.Secondary, () => DialogResult = DialogResult.Cancel);
        var buttons = Ui.ButtonRow(ok, cancel);
        buttons.Dock = DockStyle.Bottom;
        buttons.Height = Ui.TargetHeight + 8;
        stack.Dock = DockStyle.Fill;
        stack.AutoScroll = true;
        Controls.Add(stack);
        Controls.Add(buttons);
        CancelButton = cancel;
        UiMode.TrackDialog(this);
        Load += (_, _) => KeepOnScreen(this);
    }

    /// <summary>Keeps the window on screen after Touch Mode scaling (small Toughbook screens).</summary>
    internal static void KeepOnScreen(Form f)
    {
        var wa = Screen.FromControl(f).WorkingArea;
        var w = Math.Min(f.Width, wa.Width);
        var h = Math.Min(f.Height, wa.Height);
        f.Bounds = new Rectangle(Math.Clamp(f.Left, wa.Left, wa.Right - w), Math.Clamp(f.Top, wa.Top, wa.Bottom - h), w, h);
    }

    private void SyncMode()
    {
        _smartBox.Visible = _mode.SelectedIndex == 0;
        _fixedBox.Visible = _mode.SelectedIndex == 1;
    }

    private static CheckBox Check(string text) => new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0, 4, 12, 4) }.Styled(FontRole.Bold);
    private static TextBox Box(int width) => new() { Width = width };
    private static NumericUpDown Number(int min, int max, int value) => new() { Minimum = min, Maximum = max, Value = value, Width = 160 };

    private void Accept()
    {
        var call = _call.Text.Trim().ToUpperInvariant();
        if (call.Length > 0 && !AprsBeacon.IsValidCallsign(call))
        {
            MessageBox.Show(this, "That doesn't look like an amateur radio call sign (letters and digits, optional -SSID from 0 to 15).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_radio.Checked && _connection.SelectedIndex == 0 && string.IsNullOrWhiteSpace(_com.Text))
        {
            MessageBox.Show(this, "Choose the TNC's COM port.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_radio.Checked && _connection.SelectedIndex == 0 && string.Equals(_com.Text.Trim(), AppServices.Settings.SerialPortName, StringComparison.OrdinalIgnoreCase)
            && AppServices.Settings.LocationSource == Shared.Location.LocationSourceKind.Serial)
        {
            MessageBox.Show(this, $"{_com.Text} is already used by the GPS. A COM port can only be opened by one program or function at a time.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var s = AppServices.Settings.Aprs;
        s.InternetEnabled = _internet.Checked;
        s.Server = _server.Text.Trim();
        s.ServerPort = (int)_serverPort.Value;
        s.RangeKm = (int)_range.Value;
        s.ExtraFilter = _extra.Text.Trim();
        s.RadioEnabled = _radio.Checked;
        s.Connection = _connection.SelectedIndex == 1 ? TncConnection.Tcp : TncConnection.Serial;
        s.ComPort = _com.Text.Trim();
        s.Baud = int.TryParse(_baud.Text, out var b) && b > 0 ? b : 9600;
        s.TcpHost = _host.Text.Trim();
        s.TcpPort = (int)_tcpPort.Value;
        s.InitText = _init.Text;
        s.Callsign = call;
        s.Passcode = _passcode.Text.Trim();
        s.BeaconEnabled = _beacon.Checked && AprsBeacon.IsValidCallsign(call);
        s.BeaconRadio = _beaconRadio.Checked;
        s.BeaconInternet = _beaconInternet.Checked;
        s.BeaconSymbol = Symbols[Math.Max(0, _symbol.SelectedIndex)].Symbol;
        s.BeaconComment = _comment.Text.Trim();
        s.BeaconPath = _path.Text.Trim();
        s.MessagingEnabled = _messaging.Checked;
        s.BeaconMode = _mode.SelectedIndex == 1 ? BeaconMode.Fixed : BeaconMode.Smart;
        s.Smart = new SmartBeaconSettings
        {
            LowSpeedKmh = (double)_sbLow.Value,
            HighSpeedKmh = Math.Max((double)_sbHigh.Value, (double)_sbLow.Value + 1),
            SlowRateSeconds = (int)_sbSlow.Value * 60,
            FastRateSeconds = (int)_sbFast.Value,
            MinTurnAngle = (double)_sbAngle.Value,
            TurnSlope = (double)_sbSlope.Value,
            MinTurnSeconds = (int)_sbTurnTime.Value
        };
        s.BeaconMinutes = (int)_every.Value;
        s.BeaconMovingMinutes = (int)_everyMoving.Value;
        s.ShowMinutes = (int)_showMinutes.Value;
        s.TrackMinutes = (int)_trackMinutes.Value;
        AppServices.Settings.Save();
        AppServices.Aprs.Apply();
        DialogResult = DialogResult.OK;
    }
}

/// <summary>The most recent APRS packets as received, for checking a TNC or the internet feed.</summary>
public sealed class AprsLogWindow : Form
{
    private static AprsLogWindow? _open;
    private readonly TextBox _text = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
    private readonly CheckBox _pause = new() { Text = "Pause", AutoSize = true, Margin = new Padding(8, 12, 8, 4) };
    private int _version = -1;

    public static void ShowSingle(IWin32Window? owner)
    {
        if (_open is { IsDisposed: false })
        {
            _open.Activate();
            return;
        }
        _open = new AprsLogWindow();
        _open.Show(owner);
    }

    private AprsLogWindow()
    {
        Text = "APRS packet log";
        Icon = Branding.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(900, screen.Width - 40), Math.Min(560, screen.Height - 60));
        _text.Font = new Font(FontFamily.GenericMonospace, 10f);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Ui.TargetHeight + 12, Padding = new Padding(4), BackColor = Brand.Panel };
        var copy = Ui.Button("Copy all", ButtonStyle.Secondary, () =>
        {
            try
            {
                Clipboard.SetText(_text.Text.Length > 0 ? _text.Text : " ");
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Clipboard busy.
            }
        });
        copy.Width = Ui.Scaled(140);
        bar.Controls.Add(copy);
        bar.Controls.Add(_pause.Styled(FontRole.Bold));
        Controls.Add(_text);
        Controls.Add(bar);
        AppServices.ClockTick += OnTick;
        FormClosed += (_, _) => AppServices.ClockTick -= OnTick;
        UiMode.TrackDialog(this);
        Load += (_, _) => AprsSettingsDialog.KeepOnScreen(this);
        UpdateLog(true);
    }

    private void OnTick(object? sender, EventArgs e) => UpdateLog(false);

    private void UpdateLog(bool force)
    {
        var v = AppServices.Aprs.Tracker.Version;
        if (_pause.Checked || (!force && v == _version))
            return;
        _version = v;
        var lines = AppServices.Aprs.Tracker.LogSnapshot().TakeLast(400)
            .Select(e => $"{e.TimeUtc.ToLocalTime():HH:mm:ss} {(e.Via == Core.Aprs.AprsVia.Radio ? "RF" : "IS")} {e.Raw}\r\n         {(e.Error ? "! " : "")}{e.Summary}");
        _text.Text = string.Join("\r\n", lines);
        _text.SelectionStart = _text.TextLength;
        _text.ScrollToCaret();
    }
}
