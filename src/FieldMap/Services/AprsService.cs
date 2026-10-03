using System.Collections.Concurrent;
using System.IO.Ports;
using FieldMap.Core.Aprs;
using Shared.Location;

namespace FieldMap.Services;

/// <summary>
/// APRS stations from the internet (APRS-IS) and/or a radio TNC (KISS over a COM port or TCP), shown on every map.
/// Receiving is the default; beaconing your own position is off until a call sign is set and it's switched on.
/// Network work happens on background threads; <see cref="Changed"/> and <see cref="EmergencyHeard"/> are raised on
/// the UI thread from the app clock.
/// </summary>
public sealed class AprsService : IDisposable
{
    private readonly MapSettings _settings;
    private readonly ConcurrentQueue<AprsStation> _emergencies = new();
    private readonly HashSet<string> _emergencyKeys = new(StringComparer.OrdinalIgnoreCase);
    private AprsIsClient? _internet;
    private KissTncClient? _radio;
    private int _seenVersion = -1;
    private GeoPoint? _filterCenter;
    private DateTime _lastFilterUpdate = DateTime.MinValue;
    private DateTime _lastPurge = DateTime.MinValue;
    private DateTime? _lastBeaconUtc;
    private GeoPoint? _lastBeaconPoint;
    private double? _lastBeaconCourse;
    private bool _beaconBusy;
    private readonly ConcurrentQueue<AprsMessage> _incoming = new();
    private readonly JsonListStore<AprsMessage> _messageStore;
    private int _savedMessages = -1;
    private DateTime _lastMessageSave;

    /// <param name="messagesFile">Where messages are kept between runs (null: memory only).</param>
    public AprsService(MapSettings settings, string? messagesFile = null)
    {
        _settings = settings;
        _messageStore = new JsonListStore<AprsMessage>(messagesFile);
        Messenger = new AprsMessenger(Transmit, _messageStore.Items);
    }

    /// <summary>APRS text messages to and from this station, and bulletins heard.</summary>
    public AprsMessenger Messenger { get; }

    /// <summary>A message for this station arrived (raised on the UI thread).</summary>
    public event EventHandler<AprsMessage>? MessageReceived;

    /// <summary>Messages changed (new, acknowledged, retried).</summary>
    public event EventHandler? MessagesChanged;

    public bool CanSendMessages => AprsBeacon.IsValidCallsign(Settings.Callsign) && (_radio is { CanTransmit: true } || _internet is { CanTransmit: true });

    /// <summary>Sends an information field on the requested links that are able to send. Returns the links used.</summary>
    private AprsVia Transmit(string info, AprsVia via)
    {
        var s = Settings;
        if (!AprsBeacon.IsValidCallsign(s.Callsign))
            return AprsVia.None;
        var call = s.Callsign.Trim().ToUpperInvariant();
        var sent = AprsVia.None;
        if (via.HasFlag(AprsVia.Radio) && _radio is { CanTransmit: true } radio)
        {
            Fire(radio.SendAsync(AprsBeacon.Packet(call, s.BeaconPath, info)));
            sent |= AprsVia.Radio;
        }
        if (via.HasFlag(AprsVia.Internet) && _internet is { CanTransmit: true } internet)
        {
            Fire(internet.SendAsync(AprsBeacon.Packet(call, "TCPIP*", info)));
            sent |= AprsVia.Internet;
        }
        return sent;
    }

    private static void Fire(Task t) => t.ContinueWith(x => _ = x.Exception, TaskScheduler.Default);

    public AprsStationTracker Tracker { get; } = new();

    /// <summary>The map area to receive around when there's no GPS fix (the active map window's centre).</summary>
    public Func<GeoPoint?> AreaCenter { get; set; } = () => null;

    public Func<GeoFix?> CurrentFix { get; set; } = () => null;

    /// <summary>Stations changed (throttled to once a second).</summary>
    public event EventHandler? Changed;

    /// <summary>A station sent an emergency (Mic-E "Emergency" or the emergency symbol).</summary>
    public event EventHandler<AprsStation>? EmergencyHeard;

    /// <summary>A link connected, disconnected or changed its status text.</summary>
    public event EventHandler? StatusChanged;

    public AprsSettings Settings => _settings.Aprs;
    public AprsIsClient? Internet => _internet;
    public KissTncClient? Radio => _radio;
    public bool Active => _internet != null || _radio != null;
    public string? LastBeaconResult { get; private set; }
    public DateTime? LastBeaconUtc => _lastBeaconUtc;

    public string RadioName => Settings.Connection == TncConnection.Serial ? $"TNC {Settings.ComPort}" : $"TNC {Settings.TcpHost}:{Settings.TcpPort}";

    private List<AprsStation> _visible = new();
    private int _visibleVersion = -1;
    private DateTime _visibleAt;

    /// <summary>Stations with a position heard within the display window (cached until something changes).</summary>
    public IReadOnlyList<AprsStation> VisibleStations()
    {
        var v = Tracker.Version;
        if (v != _visibleVersion || DateTime.UtcNow - _visibleAt > TimeSpan.FromSeconds(30))
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(1, Settings.ShowMinutes));
            _visible = Tracker.Snapshot().Where(s => s.HasPosition && s.LastHeardUtc >= cutoff).ToList();
            _visibleVersion = v;
            _visibleAt = DateTime.UtcNow;
        }
        return _visible;
    }

    /// <summary>Starts or restarts the links to match the settings.</summary>
    public void Apply()
    {
        StopLinks();
        var s = Settings;
        if (s.InternetEnabled && !string.IsNullOrWhiteSpace(s.Server))
        {
            var center = FilterCenter();
            _filterCenter = center;
            var filter = center is { } c ? AprsIsClient.RangeFilter(c.Lat, c.Lon, s.RangeKm, s.ExtraFilter) : s.ExtraFilter.Trim();
            var login = AprsBeacon.IsValidCallsign(s.Callsign) ? s.Callsign.Trim().ToUpperInvariant() : ReceiveOnlyLogin();
            var passcode = AprsBeacon.IsValidCallsign(s.Callsign) ? s.Passcode : "";
            _internet = new AprsIsClient(s.Server.Trim(), s.ServerPort, login, passcode, filter, "FieldMap", Branding.Version);
            Hook(_internet);
            _internet.Start();
        }
        Messenger.MyCall = AprsBeacon.IsValidCallsign(s.Callsign) ? s.Callsign.Trim().ToUpperInvariant() : "";
        Messenger.Acknowledge = s.MessagingEnabled;
        if (s.RadioEnabled && (s.Connection == TncConnection.Tcp ? !string.IsNullOrWhiteSpace(s.TcpHost) : !string.IsNullOrWhiteSpace(s.ComPort)))
        {
            var open = s.Connection == TncConnection.Tcp ? KissTncClient.Tcp(s.TcpHost.Trim(), s.TcpPort) : SerialOpener(s.ComPort.Trim(), s.Baud);
            _radio = new KissTncClient(RadioName, open, s.InitText);
            Hook(_radio);
            _radio.Start();
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A unique receive-only login for APRS-IS (no call sign set). Servers accept it read-only.</summary>
    private static string ReceiveOnlyLogin() => "FMAP" + Random.Shared.Next(1000, 9999);

    private void Hook(AprsLink link)
    {
        link.PacketReceived += (_, line) => OnPacket(line, link.Via);
        link.StateChanged += (_, _) => _statusDirty = true;
    }

    private volatile bool _statusDirty;

    private void OnPacket(string line, AprsVia via)
    {
        // Background thread: the tracker is thread-safe.
        var packet = AprsParser.Parse(line);
        var station = Tracker.Process(packet, via, DateTime.UtcNow);
        if (station is { Emergency: true })
            _emergencies.Enqueue(station);
        if (Messenger.Handle(packet, via, DateTime.UtcNow) is { } message)
            _incoming.Enqueue(message);
    }

    private static Func<CancellationToken, Task<Stream>> SerialOpener(string portName, int baud) => _ =>
    {
        var port = new SerialPort(portName, baud) { DtrEnable = true, RtsEnable = true, ReadTimeout = SerialPort.InfiniteTimeout, WriteTimeout = 5000 };
        try
        {
            port.Open();
            return Task.FromResult<Stream>(new SerialStream(port));
        }
        catch
        {
            port.Dispose();
            throw;
        }
    };

    /// <summary>Once a second on the UI thread (app clock): change notification, emergencies, filter, beacon, purge.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        var v = Tracker.Version;
        if (v != _seenVersion)
        {
            _seenVersion = v;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        if (_statusDirty)
        {
            _statusDirty = false;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        while (_emergencies.TryDequeue(out var e))
            if (_emergencyKeys.Add(e.Key))
                EmergencyHeard?.Invoke(this, e);

        Messenger.Tick(now);
        while (_incoming.TryDequeue(out var m))
            MessageReceived?.Invoke(this, m);
        var mv = Messenger.Version;
        if (mv != _savedMessages && now - _lastMessageSave > TimeSpan.FromSeconds(2))
        {
            _savedMessages = mv;
            _lastMessageSave = now;
            Messenger.Persist(_messageStore.Save);
            MessagesChanged?.Invoke(this, EventArgs.Empty);
        }

        if (now - _lastPurge > TimeSpan.FromMinutes(1))
        {
            _lastPurge = now;
            Tracker.Purge(now, TimeSpan.FromHours(12), TimeSpan.FromMinutes(Math.Max(5, Settings.TrackMinutes)));
        }
        UpdateFilter(now);
        if (BeaconDue(now))
            _ = BeaconAsync(manual: false);
    }

    /// <summary>Clears the emergency flag so the same station alerts again if it sends another emergency.</summary>
    public void AcknowledgeEmergency(string key) => _emergencyKeys.Remove(key);

    public IReadOnlyCollection<string> ActiveEmergencies => _emergencyKeys;

    private GeoPoint? FilterCenter()
    {
        var fix = CurrentFix();
        if (fix != null && DateTime.UtcNow - fix.TimestampUtc < TimeSpan.FromMinutes(10))
            return new GeoPoint(fix.Latitude, fix.Longitude);
        return AreaCenter();
    }

    private void UpdateFilter(DateTime now)
    {
        if (_internet == null || now - _lastFilterUpdate < TimeSpan.FromMinutes(1))
            return;
        _lastFilterUpdate = now;
        var center = FilterCenter();
        if (center is not { } c)
            return;
        // Follow the area of interest once it has moved a quarter of the receive range.
        if (_filterCenter is { } old && old.DistanceTo(c) < Settings.RangeKm * 1000 / 4)
            return;
        _filterCenter = c;
        _ = _internet.SetFilterAsync(AprsIsClient.RangeFilter(c.Lat, c.Lon, Settings.RangeKm, Settings.ExtraFilter)).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ beacon

    /// <summary>Why a beacon can't be sent now, or null if it can.</summary>
    public string? BeaconProblem()
    {
        var s = Settings;
        if (!AprsBeacon.IsValidCallsign(s.Callsign))
            return "Set your amateur radio call sign in APRS settings.";
        var fix = UsableFix();
        if (fix == null)
            return "No current GPS position.";
        var radio = s.BeaconRadio && _radio is { CanTransmit: true };
        var internet = s.BeaconInternet && _internet is { CanTransmit: true };
        if (!radio && !internet)
            return s.BeaconInternet && _internet != null && !_internet.Verified
                ? "APRS-IS login is receive-only (passcode missing or wrong) and no TNC is open."
                : "No TNC open and APRS-IS isn't connected for sending.";
        return null;
    }

    private GeoFix? UsableFix()
    {
        var fix = CurrentFix();
        if (fix == null)
            return null;
        if (fix.Source.StartsWith("Entered", StringComparison.Ordinal))
            return fix;
        return DateTime.UtcNow - fix.TimestampUtc < LocationService.StaleAfter * 2 ? fix : null;
    }

    private bool BeaconDue(DateTime now)
    {
        var s = Settings;
        if (!s.BeaconEnabled || _beaconBusy || BeaconProblem() != null)
            return false;
        var fix = UsableFix()!;
        if (s.BeaconMode == BeaconMode.Smart)
            return SmartBeacon.Due(s.Smart, now, _lastBeaconUtc, _lastBeaconCourse, (fix.SpeedMetersPerSecond ?? 0) * 3.6, fix.CourseDegrees, out _);
        var moving = (fix.SpeedMetersPerSecond ?? 0) > 2;
        var interval = TimeSpan.FromMinutes(Math.Max(1, moving ? s.BeaconMovingMinutes : s.BeaconMinutes));
        if (_lastBeaconUtc is not { } last)
            return true;
        if (now - last >= interval)
            return true;
        // Moved a long way since the last beacon while on the stationary interval: send early (at most once a minute).
        return now - last > TimeSpan.FromMinutes(1) && _lastBeaconPoint is { } p && p.DistanceTo(new GeoPoint(fix.Latitude, fix.Longitude)) > 1000 && !moving;
    }

    /// <summary>Sends the position beacon on the enabled links. Returns a result for the user.</summary>
    public async Task<string> BeaconAsync(bool manual)
    {
        var problem = BeaconProblem();
        if (problem != null)
        {
            LastBeaconResult = problem;
            return problem;
        }
        _beaconBusy = true;
        try
        {
            var s = Settings;
            var fix = UsableFix()!;
            var info = AprsBeacon.Position(fix.Latitude, fix.Longitude, s.SymbolTable, s.SymbolCode, fix.CourseDegrees,
                fix.SpeedMetersPerSecond * 3.6, fix.AltitudeMeters, s.BeaconComment, messaging: s.MessagingEnabled);
            var call = s.Callsign.Trim().ToUpperInvariant();
            var sent = new List<string>();
            var failed = new List<string>();
            if (s.BeaconRadio && _radio is { CanTransmit: true } radio)
            {
                try
                {
                    await radio.SendAsync(AprsBeacon.Packet(call, s.BeaconPath, info));
                    sent.Add("radio");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or ObjectDisposedException)
                {
                    failed.Add("radio: " + ex.Message);
                }
            }
            if (s.BeaconInternet && _internet is { CanTransmit: true } internet)
            {
                try
                {
                    await internet.SendAsync(AprsBeacon.Packet(call, "TCPIP*", info));
                    sent.Add("internet");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    failed.Add("internet: " + ex.Message);
                }
            }
            _lastBeaconUtc = DateTime.UtcNow;
            _lastBeaconPoint = new GeoPoint(fix.Latitude, fix.Longitude);
            _lastBeaconCourse = fix.CourseDegrees;
            LastBeaconResult = (sent.Count > 0 ? $"Beacon sent by {string.Join(" and ", sent)} at {DateTime.Now:HH:mm}" : "Beacon not sent") +
                               (failed.Count > 0 ? " · failed: " + string.Join("; ", failed) : "");
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return LastBeaconResult;
        }
        finally
        {
            _beaconBusy = false;
        }
    }

    // ------------------------------------------------------------------ status

    /// <summary>Short text for the status bar, e.g. "APRS 42 · IS ✓ · TNC ✓".</summary>
    public string ShortStatus()
    {
        if (!Active)
            return "APRS off";
        var parts = new List<string> { $"APRS {Tracker.Count}" };
        if (_internet != null)
            parts.Add("IS " + Mark(_internet.State));
        if (_radio != null)
            parts.Add("TNC " + Mark(_radio.State));
        if (Messenger.Unread is var unread and > 0)
            parts.Add($"✉ {unread}");
        return string.Join(" · ", parts);
    }

    private static string Mark(AprsLinkState s) => s switch
    {
        AprsLinkState.Connected => "✓",
        AprsLinkState.Connecting => "…",
        AprsLinkState.Failed => "✗",
        _ => "–"
    };

    public bool AnyLinkFailed => _internet?.State == AprsLinkState.Failed || _radio?.State == AprsLinkState.Failed;

    private void StopLinks()
    {
        var links = new AprsLink?[] { _internet, _radio };
        _internet = null;
        _radio = null;
        foreach (var l in links)
            l?.Dispose();
    }

    /// <summary>A short description of the beacon schedule.</summary>
    public string BeaconScheduleText()
    {
        var s = Settings;
        return s.BeaconMode == BeaconMode.Smart
            ? $"SmartBeaconing: every {s.Smart.SlowRateSeconds / 60} min stopped, down to every {s.Smart.FastRateSeconds / 60.0:0.#} min at {s.Smart.HighSpeedKmh:0} km/h, and after turns"
            : $"every {s.BeaconMinutes} min ({s.BeaconMovingMinutes} min moving)";
    }

    public void Dispose()
    {
        StopLinks();
        Messenger.Persist(_messageStore.Save);
    }

    /// <summary>A serial port as a stream that closes the port when disposed.</summary>
    private sealed class SerialStream : Stream
    {
        private readonly SerialPort _port;
        private readonly Stream _inner;

        public SerialStream(SerialPort port)
        {
            _port = port;
            _inner = port.BaseStream;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.WriteAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _port.Close();
                }
                catch (IOException)
                {
                    // Device already gone (USB unplugged).
                }
                _port.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
