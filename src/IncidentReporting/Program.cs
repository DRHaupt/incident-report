using IncidentReporting.Services;
using IncidentReporting.UI;
using Shared.UI;

namespace IncidentReporting;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetDefaultFont(new Font("Segoe UI", 9f));
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show("An unexpected error occurred.\n\n" + e.Exception.Message, "Incident Report Manager",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        try
        {
            AppSession.Initialize();
            UiMode.Initialize(AppSession.Settings.TouchMode, AppSession.Settings.DaylightTheme);
            UiMode.Changed += (_, _) => SaveUiPreferences();
        }
        catch (Exception ex)
        {
            MessageBox.Show("The incident database could not be opened.\n\n" + ex.Message + "\n\n" +
                            "Check the database location under Tools > Options, or delete settings.json to restore defaults.",
                "Incident Report Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.Run(new MainForm());
    }

    private static void SaveUiPreferences()
    {
        AppSession.Settings.TouchMode = UiMode.Touch;
        AppSession.Settings.DaylightTheme = UiMode.Daylight;
        try
        {
            AppSession.Settings.Save();
        }
        catch (IOException)
        {
            // Preference not persisted; the mode still applies for this session.
        }
    }
}
