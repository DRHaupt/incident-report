using System.IO.Ports;
using FieldToolkit.Core.Location;
using Windows.Devices.Geolocation;

namespace FieldToolkit.Tools.Location;

public enum LocationState
{
    Starting,
    Waiting,
    Ready,
    Error
}

/// <summary>
/// The current position from the source chosen in settings: the Windows location service (built-in GNSS on most
/// Toughbooks), a GPS receiver on a COM port (NMEA), or a position entered by hand. Runs while any window uses it;
/// events are raised on the UI thread.
/// </summary>
public sealed class LocationService
{
    private readonly SynchronizationContext _ui;
    private int _users;
    private Geolocator? _geolocator;
    private SerialPort? _serial;
    private CancellationTokenSource? _serialCancel;

    public LocationService()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    }

    public GeoFix? Fix { get; private set; }
    public LocationState State { get; private set; } = LocationState.Starting;
    public string Status { get; private set; } = "Starting…";

    /// <summary>Fix older than this is shown as stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    public event EventHandler? Changed;

    public void Acquire()
    {
        if (_users++ == 0)
            Start();
    }

    public void Release()
    {
        if (_users > 0 && --_users == 0)
            Stop();
    }

    /// <summary>Applies a changed source setting.</summary>
    public void Restart()
    {
        Stop();
        Fix = null;
        if (_users > 0)
            Start();
        else
            Raise(LocationState.Starting, "Starting…");
    }

    private void Start()
    {
        var s = AppServices.Settings;
        switch (s.LocationSource)
        {
            case LocationSourceKind.Manual:
                if (s.ManualLatitude is { } lat && s.ManualLongitude is { } lon)
                {
                    Fix = new GeoFix(lat, lon, null, null, DateTime.UtcNow, "Entered manually");
                    Raise(LocationState.Ready, "Manual position (not from GPS)");
                }
                else
                {
                    Raise(LocationState.Error, "No manual position set. Tap Source to enter one.");
                }
                break;
            case LocationSourceKind.Serial:
                StartSerial(s.SerialPortName, s.SerialBaud);
                break;
            default:
                _ = StartWindowsAsync();
                break;
        }
    }

    private void Stop()
    {
        if (_geolocator != null)
        {
            _geolocator.PositionChanged -= OnPositionChanged;
            _geolocator.StatusChanged -= OnStatusChanged;
            _geolocator = null;
        }
        _serialCancel?.Cancel();
        try
        {
            _serial?.Close();
        }
        catch (IOException)
        {
        }
        _serial?.Dispose();
        _serial = null;
    }

    // ------------------------------------------------------------------ Windows location service

    private async Task StartWindowsAsync()
    {
        Raise(LocationState.Starting, "Asking Windows for location access…");
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                Raise(LocationState.Error, "Location access is off. In Windows Settings → Privacy & security → Location, turn on " +
                                           "Location services and \"Let desktop apps access your location\". Or tap Source to use a serial GPS.");
                return;
            }
            var g = new Geolocator { DesiredAccuracy = PositionAccuracy.High, ReportInterval = 1000, MovementThreshold = 0 };
            g.PositionChanged += OnPositionChanged;
            g.StatusChanged += OnStatusChanged;
            _geolocator = g;
            Raise(LocationState.Waiting, "Waiting for a position fix…");
        }
        catch (Exception ex)
        {
            Raise(LocationState.Error, "Windows location service unavailable: " + ex.Message);
        }
    }

    private void OnStatusChanged(Geolocator sender, StatusChangedEventArgs e)
    {
        switch (e.Status)
        {
            case PositionStatus.Disabled:
                Raise(LocationState.Error, "Location is turned off in Windows settings (Privacy & security → Location).");
                break;
            case PositionStatus.NotAvailable:
                Raise(LocationState.Error, "No location hardware found by Windows. Tap Source to use a serial GPS.");
                break;
            case PositionStatus.Initializing:
            case PositionStatus.NoData:
                if (Fix == null)
                    Raise(LocationState.Waiting, "Waiting for a position fix… (get a clear view of the sky)");
                break;
        }
    }

    private void OnPositionChanged(Geolocator sender, PositionChangedEventArgs e)
    {
        var c = e.Position.Coordinate;
        var p = c.Point.Position;
        var source = c.PositionSource switch
        {
            PositionSource.Satellite => "GPS (Windows)",
            PositionSource.WiFi => "Wi-Fi positioning (Windows)",
            PositionSource.Cellular => "Mobile network (Windows)",
            PositionSource.IPAddress => "IP address (approximate)",
            _ => "Windows location"
        };
        var fix = new GeoFix(
            p.Latitude,
            p.Longitude,
            c.Accuracy,
            c.AltitudeAccuracy.HasValue ? p.Altitude : null,
            c.Timestamp.UtcDateTime,
            source,
            Satellites: null,
            Hdop: c.SatelliteData?.HorizontalDilutionOfPrecision,
            SpeedMetersPerSecond: c.Speed is { } sp && !double.IsNaN(sp) ? sp : null,
            CourseDegrees: c.Heading is { } h && !double.IsNaN(h) ? h : null);
        Update(fix, LocationState.Ready, source);
    }

    // ------------------------------------------------------------------ serial NMEA receiver

    private void StartSerial(string port, int baud)
    {
        if (string.IsNullOrWhiteSpace(port))
        {
            Raise(LocationState.Error, "No COM port chosen for the serial GPS. Tap Source.");
            return;
        }
        _serialCancel = new CancellationTokenSource();
        var token = _serialCancel.Token;
        Raise(LocationState.Starting, $"Opening {port} at {baud} baud…");
        Task.Run(() =>
        {
            var reader = new NmeaReader();
            try
            {
                var serial = new SerialPort(port, baud) { ReadTimeout = 3000, NewLine = "\n" };
                serial.Open();
                _serial = serial;
                Raise(LocationState.Waiting, $"Listening on {port}… waiting for a fix");
                while (!token.IsCancellationRequested)
                {
                    string line;
                    try
                    {
                        line = serial.ReadLine();
                    }
                    catch (TimeoutException)
                    {
                        Raise(LocationState.Waiting, $"No data from {port}. Check the port and baud rate.");
                        continue;
                    }
                    if (reader.Feed(line, DateTime.UtcNow) is { } fix)
                        Update(fix, LocationState.Ready, $"GPS on {port}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                if (!token.IsCancellationRequested)
                    Raise(LocationState.Error, $"Could not read {port}: {ex.Message}");
            }
        }, token);
    }

    // ------------------------------------------------------------------ manual

    public void SetManual(double lat, double lon)
    {
        var s = AppServices.Settings;
        s.LocationSource = LocationSourceKind.Manual;
        s.ManualLatitude = lat;
        s.ManualLongitude = lon;
        s.Save();
        Restart();
    }

    // ------------------------------------------------------------------ events

    private void Update(GeoFix fix, LocationState state, string status) =>
        _ui.Post(_ =>
        {
            Fix = fix;
            State = state;
            Status = status;
            Changed?.Invoke(this, EventArgs.Empty);
        }, null);

    private void Raise(LocationState state, string status) =>
        _ui.Post(_ =>
        {
            State = state;
            Status = status;
            Changed?.Invoke(this, EventArgs.Empty);
        }, null);
}
