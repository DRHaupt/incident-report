using IncidentReporting.Data;

namespace IncidentReporting.Services;

/// <summary>Application-wide services, created once at startup.</summary>
public static class AppSession
{
    public static AppSettings Settings { get; private set; } = new();
    public static ReportRepository Repository { get; private set; } = null!;

    public static string CurrentUser =>
        string.IsNullOrWhiteSpace(Settings.DefaultReporterName) ? Environment.UserName : Settings.DefaultReporterName;

    public static void Initialize()
    {
        Settings = AppSettings.Load();
        Repository = new ReportRepository(Settings.EffectiveDatabasePath);
    }

    public static void ReloadRepository() => Repository = new ReportRepository(Settings.EffectiveDatabasePath);
}
