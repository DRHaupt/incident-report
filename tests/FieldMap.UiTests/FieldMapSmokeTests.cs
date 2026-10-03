using System.Net;
using System.Net.Sockets;
using FieldMap;
using FieldMap.Core.Aprs;
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

        // APRS: a fake APRS-IS server and a fake KISS TNC on loopback feed stations into the map.
        RunAprs(follow);

        // Imagery: install a small synthetic package, switch to Imagery and Hybrid, and back.
        RunImagery(follow);

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
        AppServices.Settings.Aprs.InternetEnabled = false;
        AppServices.Settings.Aprs.RadioEnabled = false;
        AppServices.Aprs.Apply();
        AppServices.Windows.CloseAll();
        Pump(100);
        launcher.Close();
        Pump(100);
        Directory.Delete(dir, true);
    });

    private static void RunAprs(MapWindow map)
    {
        string Packet(string call, GeoPoint p, char table, char code, double? course, double? kmh, string comment) =>
            AprsBeacon.Packet(call, "WIDE1-1", AprsBeacon.Position(p.Lat, p.Lon, table, code, course, kmh, null, comment));
        var internetLines = new[]
        {
            Packet("VE7MED-9", SampleRegion.At(-100, 400), '/', 'a', 70, 60, "Medic 12"),
            Packet("VE7HKR-7", SampleRegion.At(300, 650), '\\', '!', null, null, "Injured hiker"),
            "VE7EOC>APRS,TCPIP*,qAC,TEST::VE7ABC-9 :Report to staging{42"
        };
        var fromApp = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var radioFrame = KissDecoder.Encode(Ax25.Encode(Packet("VE7FD-1", SampleRegion.At(250, 420), '/', 'f', 180, 40, "Engine 3")));

        var isListener = new TcpListener(IPAddress.Loopback, 0);
        isListener.Start();
        var tncListener = new TcpListener(IPAddress.Loopback, 0);
        tncListener.Start();
        using var stop = new CancellationTokenSource();
        var isServer = Task.Run(async () =>
        {
            using var c = await isListener.AcceptTcpClientAsync(stop.Token);
            using var s = c.GetStream();
            using var r = new StreamReader(s);
            var w = new StreamWriter(s) { NewLine = "\r\n", AutoFlush = true };
            await w.WriteLineAsync("# aprsc test");
            await r.ReadLineAsync(stop.Token);
            await w.WriteLineAsync("# logresp FMAP unverified, server TEST");
            foreach (var line in internetLines)
                await w.WriteLineAsync(line);
            while (!stop.IsCancellationRequested)
            {
                await w.WriteLineAsync("# keepalive");
                await Task.Delay(1000, stop.Token);
            }
        });
        var tncServer = Task.Run(async () =>
        {
            using var c = await tncListener.AcceptTcpClientAsync(stop.Token);
            using var s = c.GetStream();
            await s.WriteAsync(radioFrame, stop.Token);
            var decoder = new KissDecoder();
            var buf = new byte[2048];
            while (!stop.IsCancellationRequested)
            {
                var n = await s.ReadAsync(buf, stop.Token);
                if (n == 0)
                    break;
                foreach (var (_, data) in decoder.Push(buf.AsSpan(0, n)))
                    if (Ax25.Decode(data) is { } text)
                        fromApp.Enqueue(text);
            }
        });

        var a = AppServices.Settings.Aprs;
        a.Show = true;
        a.InternetEnabled = true;
        a.Server = "127.0.0.1";
        a.ServerPort = ((IPEndPoint)isListener.LocalEndpoint).Port;
        a.RadioEnabled = true;
        a.Connection = TncConnection.Tcp;
        a.TcpHost = "127.0.0.1";
        a.TcpPort = ((IPEndPoint)tncListener.LocalEndpoint).Port;
        a.Callsign = "VE7ABC-9";
        a.MessagingEnabled = true;
        a.BeaconEnabled = false;
        AppServices.Aprs.Apply();
        PumpUntil(() => AppServices.Aprs.Tracker.Count >= 3, 30000, "APRS stations from APRS-IS and the TNC");
        PumpUntil(() => AppServices.Aprs.ActiveEmergencies.Contains("VE7HKR-7"), 10000, "APRS emergency alert");
        Assert.Equal(AprsVia.Radio, AppServices.Aprs.Tracker.Find("VE7FD-1")!.Via);
        Assert.Equal(AprsVia.Internet, AppServices.Aprs.Tracker.Find("VE7MED-9")!.Via);

        map.ShowPanel(new AprsPanel(map));
        PumpUntil(() => PanelText(map).Contains("VE7MED-9") && PanelText(map).Contains("VE7FD-1"), 10000, "APRS station list");
        Assert.Contains("EMERGENCY", PanelText(map));
        map.SelectAprs("VE7MED-9", center: true);
        Pump(200);
        Assert.Contains("Ambulance", PanelText(map));
        Assert.Contains("Medic 12", PanelText(map));

        // Tapping the drawn symbol finds the station.
        map.MapView.SetView(SampleRegion.At(100, 500), 15.5, 0);
        map.MapView.Refresh();
        Pump(300);
        var pt = map.MapView.GeoToScreen(SampleRegion.At(250, 420));
        Assert.Equal("VE7FD-1", map.MapView.HitAprs(Point.Round(pt)));

        map.ShowPanel(new LayersPanel(map));
        Pump(100);
        Assert.Contains("APRS stations", PanelText(map));

        // Messaging: the message from VE7EOC arrived, was acknowledged by radio, and a reply goes out by radio.
        PumpUntil(() => AppServices.Aprs.Messenger.Unread == 1, 10000, "APRS message");
        PumpUntil(() => fromApp.Any(x => x.Contains(":VE7EOC   :ack42")), 10000, "acknowledgement sent to the TNC");
        map.ShowPanel(new AprsMessagesPanel(map, "VE7EOC"));
        Pump(200);
        Assert.Contains("Report to staging", PanelText(map));
        Assert.Equal(0, AppServices.Aprs.Messenger.Unread);
        var reply = AppServices.Aprs.Messenger.Send("VE7EOC", "On my way", DateTime.UtcNow);
        PumpUntil(() => fromApp.Any(x => x.StartsWith("VE7ABC-9>APZFMP,WIDE1-1,WIDE2-1::VE7EOC   :On my way{" + reply.MessageId)), 10000, "message sent to the TNC");
        map.ShowPanel(new AprsMessagesPanel(map, "VE7EOC"));
        Pump(200);
        Assert.Contains("On my way", PanelText(map));

        using (var settings = new AprsSettingsDialog())
        {
            settings.Show(map);
            Pump(200);
            settings.Close();
        }
        AprsLogWindow.ShowSingle(map);
        Pump(1500);
        foreach (var f in Application.OpenForms.OfType<AprsLogWindow>().ToList())
        {
            Assert.Contains("VE7FD-1", All(f).OfType<TextBox>().First().Text);
            f.Close();
        }
        map.ClosePanel();

        stop.Cancel();
        a.InternetEnabled = false;
        a.RadioEnabled = false;
        a.Callsign = "";
        AppServices.Aprs.Apply();
        isListener.Stop();
        tncListener.Stop();
        try
        {
            Task.WaitAll(new[] { isServer, tncServer }, 3000);
        }
        catch (AggregateException)
        {
            // Servers stop when cancelled.
        }
    }

    private static void RunImagery(MapWindow map)
    {
        var area = AppServices.Data.Bounds;
        var file = Path.Combine(Path.GetTempPath(), "fieldmap-ui-imagery-" + Guid.NewGuid().ToString("N")[..6] + ".mbtiles");
        using (var store = new FieldMap.Core.Imagery.RasterTileStore(file))
        {
            foreach (var z in new[] { 13, 14 })
                foreach (var (tz, x, y) in FieldMap.Core.Imagery.ImageryMath.Tiles(area, z))
                {
                    using var bmp = new SkiaSharp.SKBitmap(256, 256);
                    using (var c = new SkiaSharp.SKCanvas(bmp))
                        c.Clear(new SkiaSharp.SKColor(0x4A, 0x6B, 0x3A));
                    using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                    using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 70);
                    store.Put(tz, x, y, data.ToArray());
                }
            store.SetMetadata(new Dictionary<string, string> { ["name"] = "UI test imagery", ["attribution"] = "TEST IMAGERY" });
        }
        var package = AppServices.Imagery.Install(file);
        File.Delete(file);
        AppServices.Imagery.Apply();
        Assert.Contains(AppServices.Imagery.Available(), s => s.Name == "UI test imagery");
        AppServices.Imagery.Select(AppServices.Imagery.Available().First(s => s.Name == "UI test imagery"));
        foreach (var mode in new[] { FieldMap.Map.BaseMapMode.Imagery, FieldMap.Map.BaseMapMode.Hybrid })
        {
            AppServices.Imagery.SetMode(mode);
            map.MapView.SetView(SampleRegion.At(0, 560), 14.5, 0);
            map.MapView.Refresh();
            Pump(200);
            Assert.NotNull(AppServices.Imagery.Tiles);
        }
        map.ShowPanel(new LayersPanel(map));
        Pump(100);
        Assert.Contains("UI test imagery", PanelText(map));
        AppServices.Windows.Open(DataWindow.ToolId);
        Pump(200);
        AppServices.Imagery.SetMode(FieldMap.Map.BaseMapMode.Map);
        AppServices.Imagery.Delete(AppServices.Imagery.Packages().First(p => p.Name == "UI test imagery"));
        Assert.DoesNotContain(AppServices.Imagery.Available(), s => s.Name == "UI test imagery");
        map.ClosePanel();
    }
}

