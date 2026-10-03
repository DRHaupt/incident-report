using FieldMap.Core.Data;
using FieldMap.Core.Drawings;
using FieldMap.Core.Live;
using FieldMap.Framework;
using FieldMap.Services;
using Shared.Core.Interop;
using Shared.Location;
using Shared.UI;

namespace FieldMap;

/// <summary>Application-wide services shared by every window, created once at startup.</summary>
public static class AppServices
{
    private static readonly System.Windows.Forms.Timer Clock = new() { Interval = 1000 };
    private static LocationService? _location;
    private static bool _initialized;

    public static MapSettings Settings { get; private set; } = new();
    public static WindowManager Windows { get; private set; } = null!;
    public static RegionStore Store { get; private set; } = null!;
    public static MapDataService Data { get; private set; } = null!;
    public static RoutingService Routing { get; private set; } = null!;
    public static JsonListStore<Drawing> Drawings { get; private set; } = null!;
    public static Breadcrumbs Breadcrumbs { get; private set; } = null!;
    public static Speech Speech { get; } = new();
    public static ExchangeWatcher? Exchange { get; private set; }
    public static LiveDataService Live { get; private set; } = null!;
    public static AprsService Aprs { get; private set; } = null!;
    public static RegionCatalog Catalog { get; private set; } = new();

    /// <summary>Problems found at start-up (data folder unavailable, damaged data) to show once.</summary>
    public static List<string> StartupMessages { get; } = new();

    public static string ContentFolder => Path.Combine(AppContext.BaseDirectory, "Content");

    /// <summary>Current position (the source runs while a map window is open).</summary>
    public static LocationService Location
    {
        get
        {
            if (_location == null)
            {
                _location = new LocationService(Settings);
                _location.Changed += (_, _) =>
                {
                    if (_location.Fix is { } fix && _location.State == LocationState.Ready)
                        Breadcrumbs.Add(fix);
                };
            }
            return _location;
        }
    }

    /// <summary>Once a second: GPS staleness, navigation timers.</summary>
    public static event EventHandler? ClockTick;

    /// <summary>Raised when drawings are added, edited or removed (maps redraw, routing re-applies closures).</summary>
    public static event EventHandler? DrawingsChanged;

    /// <summary>A point arrived from Field Toolkit ("Open in map").</summary>
    public static event EventHandler<ExchangeMessage>? PointReceived;

    public static Action ShowLauncher { get; set; } = () => { };

    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        Settings = MapSettings.Load();
        UiMode.Initialize(Settings.TouchMode, Settings.DaylightTheme, baseFontSize: 10f, night: Settings.NightTheme);
        UiMode.Changed += (_, _) =>
        {
            Settings.TouchMode = UiMode.Touch;
            Settings.DaylightTheme = UiMode.Daylight;
            Settings.NightTheme = UiMode.Night;
            Settings.Save();
        };

        Catalog = RegionCatalog.LoadOrDefault(Path.Combine(ContentFolder, "regions.json"));
        Store = OpenStore(Settings.EffectiveDataFolder);
        Data = new MapDataService(Store);
        Drawings = new JsonListStore<Drawing>(Path.Combine(MapSettings.AppFolder, "drawings.json"));
        if (Drawings.LoadError != null)
            StartupMessages.Add(Drawings.LoadError);
        Drawings.Changed += (_, _) => DrawingsChanged?.Invoke(null, EventArgs.Empty);
        Routing = new RoutingService(Data, () => Settings.AvoidClosures ? Drawings.Items : Enumerable.Empty<Drawing>());
        Breadcrumbs = new Breadcrumbs(Path.Combine(MapSettings.AppFolder, "breadcrumbs.json"));
        Windows = new WindowManager(Path.Combine(MapSettings.AppFolder, "session.json"));

        Data.Load();
        StartupMessages.AddRange(Data.Messages);
        if (!Data.HasData && !Store.InstalledRegions().Any())
        {
            // First run without data: install the fictitious sample so every screen can be tried (red SAMPLE banner).
            var result = new UpdateService(Store).InstallSample();
            if (result.Success)
                Data.Load();
        }

        Live = new LiveDataService(LiveFeedCatalog.LoadOrEmpty(Path.Combine(ContentFolder, "live-feeds.json")), Path.Combine(Store.CacheDir, "live"), Settings);
        try
        {
            Exchange = new ExchangeWatcher(Shared.Core.Interop.Exchange.Inbox(Shared.Core.Interop.Exchange.ToFieldMap));
            Exchange.Received += (_, m) => PointReceived?.Invoke(null, m);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StartupMessages.Add("Field Toolkit hand-over is unavailable: " + ex.Message);
        }

        Aprs = new AprsService(Settings)
        {
            CurrentFix = () => _location is { State: LocationState.Ready } l ? l.Fix : null,
            AreaCenter = () => Windows.Windows.OfType<Views.MapWindow>().FirstOrDefault(w => !w.IsDisposed)?.MapView.Center
                               ?? (Data.HasData ? Data.Bounds.Center : null)
        };
        Aprs.Apply();

        Clock.Tick += (_, _) =>
        {
            Aprs.Tick();
            ClockTick?.Invoke(null, EventArgs.Empty);
        };
        Clock.Start();
    }

    /// <summary>Opens the data folder, falling back to the default when a removable drive isn't present.</summary>
    private static RegionStore OpenStore(string folder)
    {
        try
        {
            return new RegionStore(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var fallback = Path.Combine(MapSettings.AppFolder, "MapData");
            StartupMessages.Add($"The map data folder {folder} is not available ({ex.Message}). Using {fallback} until the drive is connected.");
            return new RegionStore(fallback);
        }
    }

    /// <summary>Switches to another data folder (e.g. an SD card) and reloads the map data.</summary>
    public static void UseDataFolder(string folder)
    {
        var store = new RegionStore(folder);
        Settings.DataFolder = folder;
        Settings.Save();
        Store = store;
        Data.UseStore(store);
    }

    public static void ReloadData() => Data.Load();

    public static void NotifyDrawingsChanged() => Drawings.Save();

    public static void Shutdown()
    {
        Breadcrumbs.Save();
        Live.Dispose();
        Aprs.Dispose();
        Exchange?.Dispose();
        Speech.Dispose();
        Data.Dispose();
    }
}
