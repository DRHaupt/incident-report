using IncidentReporting.Services;
using IncidentReporting.UI;
using Shared.UI;

namespace IncidentReporting;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Unattended update for IT / scripted roll-outs: --update-from <folder or URL> --unattended
        if (Shared.Update.UpdateCommandLine.TryRun(args) is { } exitCode)
        {
            Environment.ExitCode = exitCode;
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetDefaultFont(new Font("Segoe UI", 9f));
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        // Drag lists and pages with a finger, like a phone.
        Shared.UI.TouchScroll.Install();
        Application.ThreadException += (_, e) =>
            MessageBox.Show("An unexpected error occurred.\n\n" + e.Exception.Message, "Incident Report Manager",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        try
        {
            AppSession.Initialize();
            UiMode.Initialize(AppSession.Settings.TouchMode, AppSession.Settings.DaylightTheme, night: AppSession.Settings.NightTheme);
            UiMode.Changed += (_, _) => SaveUiPreferences();
        }
        catch (Exception ex)
        {
            MessageBox.Show("The incident database could not be opened.\n\n" + ex.Message + "\n\n" +
                            "Check the database location under Tools > Options, or delete settings.json to restore defaults.",
                "Incident Report Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var main = new MainForm();
        Shared.Update.UpdateNotifier.Attach(main);
        Application.Run(main);
    }

    private static void SaveUiPreferences()
    {
        AppSession.Settings.TouchMode = UiMode.Touch;
        AppSession.Settings.DaylightTheme = UiMode.Daylight;
        AppSession.Settings.NightTheme = UiMode.Night;
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
