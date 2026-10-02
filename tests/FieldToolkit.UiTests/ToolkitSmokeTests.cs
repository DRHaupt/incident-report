using FieldToolkit.Core.Windows;
using FieldToolkit.Framework;
using FieldToolkit.Launcher;
using FieldToolkit.Tools.Cpr;
using FieldToolkit.Tools.FirstAid;
using FieldToolkit.Tools.Hazmat;
using FieldToolkit.Tools.Timers;
using Shared.UI;

namespace FieldToolkit.UiTests;

/// <summary>
/// Opens every tool, every first-aid topic/step/view and every hazmat page in real windows, switches Touch and Daylight
/// live, arranges and captures the session. Any exception thrown while building or painting a window fails the test.
/// </summary>
public class ToolkitSmokeTests
{
    private static void Pump(int milliseconds = 50)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            Application.DoEvents();
            Thread.Sleep(5);
        } while (DateTime.UtcNow < until);
    }

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(10)), "UI smoke test timed out.");
        if (error != null)
            throw new Exception("UI smoke test failed: " + error, error);
    }

    [Fact]
    public void Every_tool_topic_and_view_opens_and_survives_mode_switches() => RunSta(() =>
    {
        AppServices.Initialize();
        AppServices.Settings.RestoreWindows = RestoreWindowsMode.Never;
        AppServices.Settings.CprSound = false;
        AppServices.Settings.AlertSound = false;
        AppServices.Timers.Clear();
        UiMode.SetTouch(false);
        UiMode.SetDaylight(false);

        var launcher = new LauncherForm();
        launcher.Show();
        Pump(200);

        // First aid: every step of every topic, then every other view.
        Assert.Empty(AppServices.FirstAid.Problems);
        foreach (var topic in AppServices.FirstAid.Topics)
        {
            for (var step = 0; step < topic.Steps.Count; step++)
            {
                var w = AppServices.Windows.Open(FirstAidWindow.ToolId, $"{topic.Id}|{step}|Steps")!;
                Pump(10);
                Assert.Equal($"{topic.Id}|{step}|Steps", w.SaveState());
                w.Close();
            }
            foreach (var view in new[] { "All", "Warnings", "More" })
            {
                var w = AppServices.Windows.Open(FirstAidWindow.ToolId, $"{topic.Id}|0|{view}")!;
                Pump(10);
                w.Close();
            }
        }
        Pump();
        Assert.Empty(AppServices.Windows.Windows);

        // Hazmat: home, each material page, each guide page.
        foreach (var m in AppServices.Erg.Materials)
        {
            var w = AppServices.Windows.Open(HazmatWindow.ToolId, $"material:{m.Id}|{m.Name}")!;
            Pump(10);
            Assert.StartsWith("material:" + m.Id, w.SaveState());
            w.Close();
        }
        foreach (var g in AppServices.Erg.Guides)
        {
            var w = AppServices.Windows.Open(HazmatWindow.ToolId, "guide:" + g.Number)!;
            Pump(10);
            w.Close();
        }

        // Timers feed every window's timer strip and the launcher.
        AppServices.Timers.StartStopwatch("Smoke stopwatch", "UI test", remindEveryMinutes: 1);
        AppServices.Timers.StartCountdown("Smoke countdown", TimeSpan.FromSeconds(1));

        // Several windows at once, including two of the same tool.
        var windows = new List<ToolWindow>
        {
            AppServices.Windows.Open(FirstAidWindow.ToolId, "severe-bleeding|2|Steps")!,
            AppServices.Windows.Open(FirstAidWindow.ToolId, "stroke|0|Steps")!,
            AppServices.Windows.Open(HazmatWindow.ToolId)!,
            AppServices.Windows.Open(TimersWindow.ToolId)!,
            AppServices.Windows.Open(CprWindow.ToolId, "infant")!
        };
        Pump(1500); // countdown expires -> alert path runs
        Assert.Contains(AppServices.Timers.Timers, t => t.NeedsAttention);
        Assert.Same(windows[3], AppServices.Windows.Open(TimersWindow.ToolId)); // single instance

        // CPR metronome runs.
        var cpr = (CprWindow)windows[4];
        cpr.StartStop();
        Pump(1500);
        cpr.StartStop();
        Assert.InRange(cpr.Metronome.TotalCompressions, 2, 4);

        // Live mode switches with everything open.
        foreach (var (touch, daylight) in new[] { (true, false), (true, true), (false, true), (false, false) })
        {
            UiMode.SetTouch(touch);
            UiMode.SetDaylight(daylight);
            Pump(100);
        }

        // Arrange modes keep windows on the launcher's screen.
        var area = Screen.FromControl(launcher).WorkingArea;
        foreach (var mode in Enum.GetValues<ArrangeMode>())
        {
            AppServices.Windows.Arrange(mode);
            Pump(50);
            Assert.All(AppServices.Windows.Windows, w => Assert.True(area.Contains(w.Bounds), $"{mode}: {w.Text} {w.Bounds} outside {area}"));
        }

        // Pin and session capture.
        windows[0].Pinned = true;
        var session = AppServices.Windows.Capture();
        Assert.Equal(5, session.Windows.Count);
        Assert.True(session.Windows.Single(s => s.State == "severe-bleeding|2|Steps").Pinned);

        AppServices.Windows.CloseAll();
        Pump();
        AppServices.Windows.Restore(session);
        Pump(200);
        Assert.Equal(5, AppServices.Windows.Windows.Count);
        Assert.True(AppServices.Windows.Windows.Single(w => w.SaveState() == "severe-bleeding|2|Steps").Pinned);

        AppServices.Windows.CloseAll();
        AppServices.Timers.Clear();
        Pump();
        launcher.Close();
        Pump();
    });
}
