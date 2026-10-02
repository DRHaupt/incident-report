using Shared.Location;
using Shared.Settings;

namespace FieldToolkit;

public enum RestoreWindowsMode
{
    Ask,
    Always,
    Never
}

/// <summary>A one-tap named stopwatch on the Timers tool.</summary>
public sealed class TimerPreset
{
    public string Name { get; set; } = "";
    public int RemindEveryMinutes { get; set; }
}

public sealed class ToolkitSettings : ILocationSettings
{
    public bool TouchMode { get; set; } = true;
    public bool DaylightTheme { get; set; }
    public bool NightTheme { get; set; }
    public RestoreWindowsMode RestoreWindows { get; set; } = RestoreWindowsMode.Ask;
    public bool AlertSound { get; set; } = true;

    /// <summary>Folder holding firstaid\ and erg\ content. Empty = the Content folder next to the executable.</summary>
    public string ContentFolder { get; set; } = "";

    public int LauncherX { get; set; }
    public int LauncherY { get; set; }
    public int LauncherWidth { get; set; }
    public int LauncherHeight { get; set; }
    public bool LauncherMaximized { get; set; }

    public LocationSourceKind LocationSource { get; set; } = LocationSourceKind.Windows;
    public string SerialPortName { get; set; } = "";
    public int SerialBaud { get; set; } = 4800;
    public double? ManualLatitude { get; set; }
    public double? ManualLongitude { get; set; }

    /// <summary>Format read out on the radio: "mgrs" or "decimal".</summary>
    public string ReadoutFormat { get; set; } = "mgrs";

    public List<TimerPreset> TimerPresets { get; set; } = new()
    {
        new() { Name = "Tourniquet", RemindEveryMinutes = 30 },
        new() { Name = "CPR", RemindEveryMinutes = 2 },
        new() { Name = "Road closure", RemindEveryMinutes = 15 },
        new() { Name = "Crew on duty", RemindEveryMinutes = 60 }
    };

    /// <summary>Name filled in as "checked by" on new checklists.</summary>
    public string UserName { get; set; } = "";

    public int CprRate { get; set; } = 110;
    public bool CprSound { get; set; } = true;

    public static string DataFolder => JsonSettings.AppDataFolder("FieldToolkit");

    private static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public string EffectiveContentFolder =>
        !string.IsNullOrWhiteSpace(ContentFolder) && Directory.Exists(ContentFolder)
            ? ContentFolder
            : Path.Combine(AppContext.BaseDirectory, "Content");

    public static ToolkitSettings Load() => JsonSettings.Load<ToolkitSettings>(SettingsFile);

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
