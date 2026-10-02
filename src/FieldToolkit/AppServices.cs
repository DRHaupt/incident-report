using System.Media;
using System.Runtime.InteropServices;
using FieldToolkit.Core.FirstAid;
using FieldToolkit.Core.Hazmat;
using FieldToolkit.Core.Timers;
using FieldToolkit.Framework;
using Shared.UI;

namespace FieldToolkit;

/// <summary>Application-wide services shared by every window, created once at startup.</summary>
public static class AppServices
{
    private static readonly System.Windows.Forms.Timer Clock = new() { Interval = 250 };
    private static FirstAidLibrary? _firstAid;
    private static FirstAidSearch? _firstAidSearch;
    private static ErgDataset? _erg;

    public static ToolkitSettings Settings { get; private set; } = new();
    public static TimerService Timers { get; private set; } = null!;
    public static WindowManager Windows { get; private set; } = null!;

    /// <summary>Raised four times a second for live read-outs (timers). Handlers must be cheap.</summary>
    public static event EventHandler? ClockTick;

    /// <summary>Set by the launcher; brings it to the front.</summary>
    public static Action ShowLauncher { get; set; } = () => { };

    public static string ContentFolder => Settings.EffectiveContentFolder;

    public static FirstAidLibrary FirstAid => _firstAid ??= FirstAidLibrary.Load(Path.Combine(ContentFolder, "firstaid"));

    public static FirstAidSearch FirstAidSearch => _firstAidSearch ??= new FirstAidSearch(FirstAid);

    public static ErgDataset Erg => _erg ??= ErgRepository.LoadOrEmpty(Path.Combine(ContentFolder, "erg", "erg.json"));

    /// <summary>Re-reads content after the content folder changes or a reviewer edits files.</summary>
    public static void ReloadContent()
    {
        _firstAid = null;
        _firstAidSearch = null;
        _erg = null;
    }

    public static void Initialize()
    {
        Settings = ToolkitSettings.Load();
        UiMode.Initialize(Settings.TouchMode, Settings.DaylightTheme, baseFontSize: 10f);
        UiMode.Changed += (_, _) =>
        {
            Settings.TouchMode = UiMode.Touch;
            Settings.DaylightTheme = UiMode.Daylight;
            Settings.Save();
        };

        Timers = new TimerService(Path.Combine(ToolkitSettings.DataFolder, "timers.json"));
        Timers.Alert += OnTimerAlert;
        Windows = new WindowManager(Path.Combine(ToolkitSettings.DataFolder, "session.json"));

        Clock.Tick += (_, _) =>
        {
            Timers.Tick();
            ClockTick?.Invoke(null, EventArgs.Empty);
        };
        Clock.Start();
    }

    private static void OnTimerAlert(object? sender, TimerAlert alert)
    {
        if (Settings.AlertSound)
            SystemSounds.Exclamation.Play();
        // Flash every toolkit window's taskbar button so the alert is noticed even when minimized.
        foreach (Form f in Application.OpenForms)
            Flash(f);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;

    private static void Flash(Form form)
    {
        if (!form.IsHandleCreated)
            return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = form.Handle,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = 5
        };
        FlashWindowEx(ref info);
    }
}
