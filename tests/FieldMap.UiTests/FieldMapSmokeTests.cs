using FieldMap;
using FieldMap.Core.Drawings;
using FieldMap.Core.Layers;
using FieldMap.Core.Sample;
using FieldMap.Framework;
using FieldMap.Launcher;
using FieldMap.Panels;
using FieldMap.Views;
using Shared.Core.Layout;
using Shared.UI;

namespace FieldMap.UiTests;

/// <summary>
/// Opens every Field Map window and panel against the sample region, runs search, "What's here?", nearest-N, directions
/// and navigation, renders and exports snapshots, switches Touch and all three themes live, arranges and captures the
/// session. Any exception while building or painting a window fails the test.
/// </summary>
public class FieldMapSmokeTests
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

    private static void PumpUntil(Func<bool> condition, int timeoutMs, string what)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Timed out waiting for " + what);
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
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

    private static IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));

    private static string PanelText(MapWindow w) => string.Join(" | ", All(w.CurrentPanel!).Select(c => c.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

    [Fact]
    public void Every_window_and_panel_opens_and_survives_theme_switches() => RunSta(() =>
    {
        AppServices.Initialize();
        AppServices.StartupMessages.Clear();
        AppServices.Settings.RestoreWindows = RestoreWindowsMode.Never;
        AppServices.Settings.ShowLiveData = false;
        AppServices.Settings.Voice = false;
        UiMode.SetTouch(false);
        UiMode.SetTheme(UiTheme.Standard);
        if (!AppServices.Data.HasData)
        {
            Assert.True(new FieldMap.Core.Data.UpdateService(AppServices.Store).InstallSample().Success);
            AppServices.ReloadData();
        }
        Assert.True(AppServices.Data.HasData);
        // A manual GPS position in the sample town.
        AppServices.Location.SetManual(SampleRegion.Origin.Lat, SampleRegion.Origin.Lon);

        var launcher = new LauncherForm();
        launcher.Show();
        Pump(300);

        // Two map windows from the launcher tiles: one following the GPS, one for the incident.
        var follow = (MapWindow)AppServices.Windows.Open(MapWindow.ToolId, MapWindow.FollowState)!;
        var incident = (MapWindow)AppServices.Windows.Open(MapWindow.ToolId, MapWindow.IncidentState)!;
        Pump(500);
        follow.MapView.SetView(SampleRegion.At(0, 560), 15.5, 0);
        follow.MapView.Refresh();
        Pump(500);

        // Layers panel with every toggle.
        follow.ShowPanel(new LayersPanel(follow));
        Pump(100);
        Assert.Contains("AEDs", PanelText(follow));

        // Search: intersection, facility with typos, coordinates.
        follow.ShowPanel(new SearchPanel(follow, "Main St & 5th Ave"));
        PumpUntil(() => PanelText(follow).Contains("5th Avenue"), 20000, "search results");
        follow.ShowPanel(new SearchPanel(follow, "sampleton genral hosptal"));
        PumpUntil(() => PanelText(follow).Contains("Sampleton General Hospital"), 20000, "fuzzy search");

        // What's here.
        follow.ShowWhatsHere(SampleRegion.Intersection(4, 4), null);
        PumpUntil(() => PanelText(follow).Contains("Main Street"), 20000, "what's here");

        // Nearest AEDs by drive time (with walking time).
        follow.ShowPanel(new NearestPanel(follow, SampleRegion.Origin, LayerCatalog.Aed));
        PumpUntil(() => PanelText(follow).Contains("drive"), 60000, "nearest AEDs");

        // Directions to the hospital, then navigation.
        follow.ShowPanel(new DirectionsPanel(follow, null, SampleRegion.HospitalPoint, "Hospital"));
        PumpUntil(() => follow.Route != null, 60000, "route");
        Assert.True(follow.Route!.Steps.Count >= 2);
        follow.StartNavigation(follow.Route);
        Pump(200);
        Assert.True(follow.Navigating);
        follow.ClearRoute();

        // Drawings: closure, hazmat zone, range rings, cordon; details panel for each; drawing tools panel.
        var drawings = new[]
        {
            new Drawing { Kind = DrawingKind.Incident, Shape = DrawingShape.Point, Points = { SampleRegion.At(300, 800) }, Label = "Smoke test incident" },
            new Drawing { Kind = DrawingKind.RoadClosure, Shape = DrawingShape.Line, Points = { SampleRegion.At(SampleRegion.StreetX(4) - 30, -100), SampleRegion.At(SampleRegion.StreetX(4) + 30, -100) } },
            new Drawing { Kind = DrawingKind.Hazmat, Shape = DrawingShape.Point, Points = { SampleRegion.At(300, 800) }, IsolationMeters = 100, ProtectiveMeters = 500, WindFromDegrees = 250 },
            new Drawing { Kind = DrawingKind.RangeRings, Shape = DrawingShape.Point, Points = { SampleRegion.At(0, 0) }, Rings = { 200, 500 } },
            new Drawing { Kind = DrawingKind.Cordon, Shape = DrawingShape.Polygon, Points = { SampleRegion.At(200, 700), SampleRegion.At(420, 700), SampleRegion.At(420, 900) }, ClosesRoads = true }
        };
        AppServices.Drawings.Items.AddRange(drawings);
        AppServices.NotifyDrawingsChanged();
        foreach (var d in drawings)
        {
            follow.SelectDrawing(d);
            Pump(30);
        }
        follow.ShowPanel(new DrawPanel(follow));
        Pump(100);
        follow.ClosePanel();
        follow.MapView.Refresh();
        Pump(200);

        // Snapshot export.
        var dir = Path.Combine(Path.GetTempPath(), "fieldmap-ui-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        Snapshot.SavePng(follow.MapView, Path.Combine(dir, "map.png"));
        Snapshot.SavePdf(follow.MapView, Path.Combine(dir, "map.pdf"));
        Assert.True(new FileInfo(Path.Combine(dir, "map.png")).Length > 10_000);
        Assert.True(new FileInfo(Path.Combine(dir, "map.pdf")).Length > 10_000);

        // Other windows.
        AppServices.Windows.Open(DrawingsWindow.ToolId);
        AppServices.Windows.Open(DataWindow.ToolId);
        AppServices.Windows.Open(AboutWindow.ToolId);
        Pump(300);

        // Live mode switches with everything open (the map included).
        foreach (var (touch, theme) in new[] { (true, UiTheme.Night), (true, UiTheme.Daylight), (false, UiTheme.Night), (false, UiTheme.Standard) })
        {
            UiMode.SetTouch(touch);
            UiMode.SetTheme(theme);
            follow.MapView.Refresh();
            incident.MapView.Refresh();
            Pump(150);
        }

        // Arrange and session.
        var area = Screen.FromControl(launcher).WorkingArea;
        foreach (var mode in Enum.GetValues<ArrangeMode>())
        {
            AppServices.Windows.Arrange(mode);
            Pump(50);
            Assert.All(AppServices.Windows.Windows, w => Assert.True(area.Contains(w.Bounds), $"{mode}: {w.Text}"));
        }
        var session = AppServices.Windows.Capture();
        Assert.Equal(5, session.Windows.Count);
        Assert.StartsWith("v1|", session.Windows.First(w => w.ToolId == MapWindow.ToolId).State);

        // Reload (as after an update) with windows open.
        AppServices.ReloadData();
        Pump(300);
        follow.MapView.Refresh();

        // Tidy up.
        foreach (var d in drawings)
            AppServices.Drawings.Items.Remove(d);
        AppServices.NotifyDrawingsChanged();
        AppServices.Windows.CloseAll();
        Pump(100);
        launcher.Close();
        Pump(100);
        Directory.Delete(dir, true);
    });
}
