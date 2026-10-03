using FieldToolkit.Launcher;

namespace FieldToolkit;

internal static class Program
{
    private const string MutexName = @"Local\FieldToolkit.SingleInstance";
    private const string ActivateEventName = @"Local\FieldToolkit.Activate";

    [STAThread]
    private static void Main()
    {
        // One toolkit process holds every window and the timers; a second launch just brings it forward.
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
                // The other instance is shutting down; nothing to bring forward.
            }
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetDefaultFont(new Font("Segoe UI", 10f));
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        // Drag lists and pages with a finger, like a phone.
        Shared.UI.TouchScroll.Install();
        Application.ThreadException += (_, e) =>
            MessageBox.Show("An unexpected error occurred.\n\n" + e.Exception.Message, Branding.ProductName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        AppServices.Initialize();
        var launcher = new LauncherForm();

        using var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var registration = ThreadPool.RegisterWaitForSingleObject(activateEvent, (_, _) =>
        {
            if (launcher.IsHandleCreated)
                launcher.BeginInvoke(launcher.BringToFrontFromOtherInstance);
        }, null, Timeout.Infinite, executeOnlyOnce: false);

        Application.Run(launcher);
        registration.Unregister(null);
    }
}
