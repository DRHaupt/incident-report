using FieldMap.Launcher;

namespace FieldMap;

internal static class Program
{
    private const string MutexName = @"Local\FieldMap.SingleInstance";
    private const string ActivateEventName = @"Local\FieldMap.Activate";

    [STAThread]
    private static void Main()
    {
        // One process holds every map window; a second launch (e.g. "Open in map" from Field Toolkit) brings it forward.
        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            try
            {
                using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                activate.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetDefaultFont(new Font("Segoe UI", 10f));
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show("An unexpected error occurred.\n\n" + e.Exception.Message, Branding.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);

        AppServices.Initialize();
        var launcher = new LauncherForm();
        using var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var registration = ThreadPool.RegisterWaitForSingleObject(activateEvent, (_, _) =>
        {
            if (launcher.IsHandleCreated)
                launcher.BeginInvoke(() =>
                {
                    launcher.BringToFrontFromOtherInstance();
                    AppServices.Exchange?.Check();
                });
        }, null, Timeout.Infinite, executeOnlyOnce: false);

        Application.Run(launcher);
        registration.Unregister(null);
        AppServices.Shutdown();
    }
}
