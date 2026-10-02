using Shared.Settings;

namespace FieldToolkit;

public enum RestoreWindowsMode
{
    Ask,
    Always,
    Never
}

public sealed class ToolkitSettings
{
    public bool TouchMode { get; set; } = true;
    public bool DaylightTheme { get; set; }
    public RestoreWindowsMode RestoreWindows { get; set; } = RestoreWindowsMode.Ask;
    public bool AlertSound { get; set; } = true;

    /// <summary>Folder holding firstaid\ and erg\ content. Empty = the Content folder next to the executable.</summary>
    public string ContentFolder { get; set; } = "";

    public int LauncherX { get; set; }
    public int LauncherY { get; set; }
    public int LauncherWidth { get; set; }
    public int LauncherHeight { get; set; }
    public bool LauncherMaximized { get; set; }

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
