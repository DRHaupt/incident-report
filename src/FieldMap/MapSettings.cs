using FieldMap.Core.Drawings;
using FieldMap.Core.Layers;
using FieldMap.Map;
using Shared.Location;
using Shared.Settings;

namespace FieldMap;

public enum RestoreWindowsMode
{
    Ask,
    Always,
    Never
}

public sealed class MapSettings : ILocationSettings
{
    public bool TouchMode { get; set; } = true;
    public bool DaylightTheme { get; set; }
    public bool NightTheme { get; set; }
    public RestoreWindowsMode RestoreWindows { get; set; } = RestoreWindowsMode.Ask;

    /// <summary>Where map data is kept (may be a second drive or SD card). Empty = %LOCALAPPDATA%\FieldMap\MapData.</summary>
    public string DataFolder { get; set; } = "";

    // Position source (shared settings shape with Field Toolkit)
    public LocationSourceKind LocationSource { get; set; } = LocationSourceKind.Windows;
    public string SerialPortName { get; set; } = "";
    public int SerialBaud { get; set; } = 4800;
    public double? ManualLatitude { get; set; }
    public double? ManualLongitude { get; set; }

    /// <summary>Layer id → shown. Missing ids use the layer's default.</summary>
    public Dictionary<string, bool> Layers { get; set; } = new();
    public bool ShowBreadcrumbs { get; set; } = true;
    public bool ShowDrawings { get; set; } = true;
    public bool ShowLiveData { get; set; } = true;

    // Radio
    public bool ShowRepeaters { get; set; } = true;
    public bool ShowCellCoverage { get; set; }
    public bool ShowCoverage { get; set; } = true;
    public double CoverageRadiusKm { get; set; } = 50;
    public Dictionary<string, bool> LiveFeeds { get; set; } = new();
    public string Ab511Key { get; set; } = "";

    /// <summary>Map, aerial imagery, or imagery with roads and labels.</summary>
    public BaseMapMode BaseMode { get; set; } = BaseMapMode.Map;

    /// <summary>Selected imagery source id (an installed package "file:name" or an online source id).</summary>
    public string ImageryId { get; set; } = "";

    /// <summary>Keys or tokens for imagery sources that need one (by source id).</summary>
    public Dictionary<string, string> ImageryKeys { get; set; } = new();

    /// <summary>Online imagery sources whose terms the user has accepted.</summary>
    public List<string> AcceptedImageryTerms { get; set; } = new();

    /// <summary>APRS (amateur radio position reporting) by internet and/or a TNC.</summary>
    public AprsSettings Aprs { get; set; } = new();

    // Routing
    public string Profile { get; set; } = "Car";
    public double TruckHeight { get; set; } = 4.15;
    public double TruckWeight { get; set; } = 39.5;
    public double TruckLength { get; set; } = 23;
    public bool Voice { get; set; } = true;
    public bool AvoidClosures { get; set; } = true;

    // Drawing defaults
    public List<double> DefaultRings { get; set; } = new() { 500, 1000, 2000, 5000 };
    public ProtectiveShape HazmatShape { get; set; } = ProtectiveShape.Sector;
    public string UserName { get; set; } = "";

    /// <summary>"mgrs" or "decimal" for the radio read-out.</summary>
    public string ReadoutFormat { get; set; } = "mgrs";

    public int LauncherX { get; set; }
    public int LauncherY { get; set; }
    public int LauncherWidth { get; set; }
    public int LauncherHeight { get; set; }
    public bool LauncherMaximized { get; set; }

    public bool LayerOn(string id) => Layers.TryGetValue(id, out var on) ? on : LayerCatalog.Find(id)?.DefaultOn ?? false;

    public bool FeedOn(string id) => !LiveFeeds.TryGetValue(id, out var on) || on;

    public static string AppFolder => JsonSettings.AppDataFolder("FieldMap");

    public string EffectiveDataFolder => string.IsNullOrWhiteSpace(DataFolder) ? Path.Combine(AppFolder, "MapData") : DataFolder;

    private static string SettingsFile => Path.Combine(AppFolder, "settings.json");

    public static MapSettings Load() => JsonSettings.Load<MapSettings>(SettingsFile);

    public void Save()
    {
        try
        {
            JsonSettings.Save(SettingsFile, this);
        }
        catch (IOException)
        {
            // Not persisted; settings still apply for this session.
        }
    }
}

public enum TncConnection
{
    Serial,
    Tcp
}

public enum BeaconMode
{
    Smart,
    Fixed
}

public sealed class AprsSettings
{
    /// <summary>Draw APRS stations on the map.</summary>
    public bool Show { get; set; } = true;
    public bool ShowTracks { get; set; } = true;

    /// <summary>Stations not heard for this long are hidden.</summary>
    public int ShowMinutes { get; set; } = 60;

    /// <summary>Length of the drawn tracks.</summary>
    public int TrackMinutes { get; set; } = 120;

    // Internet (APRS-IS)
    public bool InternetEnabled { get; set; }
    public string Server { get; set; } = "noam.aprs2.net";
    public int ServerPort { get; set; } = 14580;
    public int RangeKm { get; set; } = 100;

    /// <summary>Extra APRS-IS filter terms, e.g. "b/VE7ABC*" (buddy) or "t/poimqstunw" (types).</summary>
    public string ExtraFilter { get; set; } = "";

    // Radio (KISS TNC)
    public bool RadioEnabled { get; set; }
    public TncConnection Connection { get; set; } = TncConnection.Serial;
    public string ComPort { get; set; } = "";
    public int Baud { get; set; } = 9600;
    public string TcpHost { get; set; } = "localhost";
    public int TcpPort { get; set; } = 8001;

    /// <summary>Text sent before KISS starts, for radios/TNCs that need a command (e.g. "KISS ON\rRESTART\r").</summary>
    public string InitText { get; set; } = "";

    // Identity and transmitting
    public string Callsign { get; set; } = "";

    /// <summary>APRS-IS passcode; only needed to send your position over the internet.</summary>
    public string Passcode { get; set; } = "";

    /// <summary>Receive messages to my call sign and acknowledge them; advertise messaging in the beacon.</summary>
    public bool MessagingEnabled { get; set; } = true;

    public bool BeaconEnabled { get; set; }
    public BeaconMode BeaconMode { get; set; } = BeaconMode.Smart;
    public FieldMap.Core.Aprs.SmartBeaconSettings Smart { get; set; } = new();
    public bool BeaconRadio { get; set; } = true;
    public bool BeaconInternet { get; set; } = true;
    public string BeaconSymbol { get; set; } = "/>";
    public string BeaconComment { get; set; } = "";
    public string BeaconPath { get; set; } = "WIDE1-1,WIDE2-1";
    public int BeaconMinutes { get; set; } = 10;
    public int BeaconMovingMinutes { get; set; } = 2;

    public char SymbolTable => BeaconSymbol.Length >= 2 ? BeaconSymbol[0] : '/';
    public char SymbolCode => BeaconSymbol.Length >= 2 ? BeaconSymbol[1] : '>';
}
