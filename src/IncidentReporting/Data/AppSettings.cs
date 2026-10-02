using System.Text.Json;

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

    public static string AppDataFolder
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IncidentReportManager");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string SettingsFile => Path.Combine(AppDataFolder, "settings.json");

    public string EffectiveDatabasePath =>
        string.IsNullOrWhiteSpace(DatabasePath) ? Path.Combine(AppDataFolder, "incidents.db") : DatabasePath;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Corrupt settings file: fall back to defaults rather than failing to start.
        }
        return new AppSettings();
    }

    public void Save()
    {
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
