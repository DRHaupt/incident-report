using Shared.Settings;

namespace IncidentReporting.Data;

public sealed class AppSettings
{
    public string OrganizationName { get; set; } = "";
    public string DefaultReporterName { get; set; } = "";
    public string DefaultDepartment { get; set; } = "";
    public string DefaultLocation { get; set; } = "";
    public string ReportNumberPrefix { get; set; } = "IR";
    public string DatabasePath { get; set; } = "";
    public bool ConfirmOnDelete { get; set; } = true;
    public bool TouchMode { get; set; }
    public bool DaylightTheme { get; set; }
    public bool NightTheme { get; set; }

    public static string AppDataFolder => JsonSettings.AppDataFolder("IncidentReportManager");

    private static string SettingsFile => Path.Combine(AppDataFolder, "settings.json");

    public string EffectiveDatabasePath =>
        string.IsNullOrWhiteSpace(DatabasePath) ? Path.Combine(AppDataFolder, "incidents.db") : DatabasePath;

    public static AppSettings Load() => JsonSettings.Load<AppSettings>(SettingsFile);

    public void Save() => JsonSettings.Save(SettingsFile, this);
}
