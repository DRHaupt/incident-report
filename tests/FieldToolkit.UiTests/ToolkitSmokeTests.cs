using FieldToolkit.Core.Checklists;
using CheckState = FieldToolkit.Core.Checklists.CheckState;
using FieldToolkit.Core.Kit;
using Shared.Core.Geo;
using FieldToolkit.Core.Radio;
using Shared.Core.Radio;
using Shared.Core.Layout;
using FieldToolkit.Framework;
using FieldToolkit.Launcher;
using FieldToolkit.Tools.Cpr;
using FieldToolkit.Tools.FirstAid;
using FieldToolkit.Tools.Checklists;
using FieldToolkit.Tools.Hazmat;
using FieldToolkit.Tools.Kit;
using FieldToolkit.Tools.Location;
using Shared.Location;
using FieldToolkit.Tools.Radio;
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

        // Hazmat: every guide page, and a material page for every kind of entry (Table 1 / Table 3 references,
        // water-reactive, polymerizable, no ID number) plus one material per guide.
        Assert.False(AppServices.Erg.IsSample);
        Assert.Empty(AppServices.Erg.Problems);
        var materials = AppServices.Erg.Materials.Where(m => m.Highlighted).Take(25)
            .Concat(AppServices.Erg.Materials.Where(m => m.WaterReactive).Take(10))
            .Concat(AppServices.Erg.Materials.Where(m => m.Polymerizable).Take(5))
            .Concat(AppServices.Erg.Materials.Where(m => m.Id.Length == 0))
            .Concat(AppServices.Erg.Materials.GroupBy(m => m.Guide).Select(g => g.First()))
            .Concat(AppServices.Erg.Materials.Where(m => m.Id is "1005" or "1017" or "1203"))
            .Distinct()
            .ToList();
        foreach (var m in materials)
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
        foreach (var (touch, theme) in new[] { (true, UiTheme.Standard), (true, UiTheme.Daylight), (false, UiTheme.Night), (true, UiTheme.Night), (false, UiTheme.Daylight), (false, UiTheme.Standard) })
        {
            UiMode.SetTouch(touch);
            UiMode.SetTheme(theme);
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
        Pump();

        OpenLaterTools();

        AppServices.Windows.CloseAll();
        AppServices.Timers.Clear();
        Pump();
        launcher.Close();
        Pump();
    });

    /// <summary>Location, checklists, kit tracker and radio, with data in every list, in both themes.</summary>
    private static void OpenLaterTools()
    {
        // Location: a manual position (CI has no GPS), saved points with distance/bearing.
        AppServices.Settings.LocationSource = LocationSourceKind.Manual;
        AppServices.Settings.ManualLatitude = 51.50735;
        AppServices.Settings.ManualLongitude = -0.12776;
        AppServices.Location.Restart();
        AppServices.Points.Items.Clear();
        AppServices.Points.Items.Add(new SavedPoint { Name = "Ambulance RVP", Latitude = 51.51, Longitude = -0.10, Note = "UI test" });
        AppServices.Points.Save();
        var location = AppServices.Windows.Open(LocationWindow.ToolId)!;
        Pump(300);
        Assert.NotNull(AppServices.Location.Fix);
        Assert.Contains("51.50735", LocationWindow.DispatchText(AppServices.Location.Fix!));

        // Checklists: home, an in-progress run with every state, and a completed run.
        var template = AppServices.ChecklistTemplates.Items.First();
        var run = ChecklistRun.Start(template, "UI test", "Unit 1", DateTime.UtcNow);
        var items = run.Items.ToList();
        run.Set(items[0], CheckState.Ok, DateTime.UtcNow);
        run.Set(items[1], CheckState.Fault, DateTime.UtcNow, "Test fault");
        run.Set(items[2], CheckState.NotApplicable, DateTime.UtcNow);
        AppServices.ChecklistRuns.Items.Add(run);
        AppServices.ChecklistRuns.Save();
        var checklists = AppServices.Windows.Open(ChecklistsWindow.ToolId)!;
        var runWindow = AppServices.Windows.Open(ChecklistsWindow.ToolId, "run:" + run.Id)!;
        Pump(200);
        Assert.Equal("run:" + run.Id, runWindow.SaveState());

        // Kit: expired, due-soon and in-date items; the launcher shows the warning.
        AppServices.Kit.Items.Clear();
        AppServices.Kit.Items.Add(new KitItem { Name = "AED pads", Category = "AED / defibrillator", Expires = AppServices.Today.AddDays(-2) });
        AppServices.Kit.Items.Add(new KitItem { Name = "Trauma kit", Expires = AppServices.Today.AddDays(10) });
        AppServices.Kit.Items.Add(new KitItem { Name = "Extinguisher", Expires = AppServices.Today.AddYears(1) });
        AppServices.Kit.Save();
        var kit = AppServices.Windows.Open(KitWindow.ToolId, "attention")!;
        Pump(200);

        // Radio: every tab, with channels, call signs and log entries.
        AppServices.Channels.Items.Clear();
        AppServices.Channels.Items.Add(new RadioChannel { Channel = "1", Name = "Control", RxFrequency = "456.000" });
        AppServices.Channels.Save();
        AppServices.CallSigns.Items.Clear();
        AppServices.CallSigns.Items.Add(new CallSign { Sign = "Control", Who = "Event control" });
        AppServices.CallSigns.Save();
        AppServices.RadioLog.Items.Add(new LogEntry { TimeUtc = DateTime.UtcNow, From = "Unit 1", To = "Control", Text = "UI test entry" });
        AppServices.RadioLog.Save();
        foreach (var tab in new[] { "Phonetic", "Phrases", "Channels", "CallSigns", "Log" })
        {
            var radio = AppServices.Windows.Open(RadioWindow.ToolId, tab)!;
            Pump(100);
            Assert.Equal(tab, radio.SaveState());
        }

        foreach (var (touch, theme) in new[] { (true, UiTheme.Daylight), (true, UiTheme.Night), (false, UiTheme.Standard) })
        {
            UiMode.SetTouch(touch);
            UiMode.SetTheme(theme);
            Pump(150);
        }
        AppServices.Windows.Arrange(ArrangeMode.Grid);
        Pump(100);
        Assert.Equal(9, AppServices.Windows.Windows.Count);

        // Leave the runner's data folder tidy.
        AppServices.ChecklistRuns.Items.Remove(run);
        AppServices.ChecklistRuns.Save();
        AppServices.RadioLog.Items.Clear();
        AppServices.RadioLog.Save();
        GC.KeepAlive(new object[] { location, checklists, kit });
    }
}
