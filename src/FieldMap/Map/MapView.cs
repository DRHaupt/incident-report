using System.Runtime.InteropServices;
using FieldMap.Core.Build;
using FieldMap.Core.Drawings;
using FieldMap.Core.Routing;
using FieldMap.Services;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using Shared.Location;
using Shared.UI;

namespace FieldMap.Map;

public sealed class MapPointEventArgs : EventArgs
{
    public MapPointEventArgs(GeoPoint point, Point screen)
    {
        Point = point;
        Screen = screen;
    }

    public GeoPoint Point { get; }
    public Point Screen { get; }
}

/// <summary>
/// The interactive map: draws with <see cref="MapRenderer"/> and handles mouse and Windows touch gestures (drag to pan,
/// pinch to zoom, two-finger rotate, two-finger tap to zoom out, press-and-hold for the context menu).
/// </summary>
public sealed class MapView : SKControl
{
    private static TileCache? _sharedTiles;

    private double _centerX = 0.5, _centerY = 0.5, _zoom = 4, _bearing;
    private Point _downPoint;
    private bool _mouseDown, _dragging, _rotating;
    private double _rotateStartAngle, _rotateStartBearing;
    private RenderResult? _last;
    private long _gestureZoomStart;
    private double _gestureZoomBase;
    private double _gestureRotateBase;
    private Point _gesturePanLast;
    private DateTime _suppressClickUntil = DateTime.MinValue;

    public MapView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        // The map pans and zooms with its own touch gestures.
        TouchScroll.Exclude(this);
        Tiles.TileLoaded += OnTileLoaded;
        AppServices.Data.Changed += OnDataChanged;
        AppServices.DrawingsChanged += OnRedraw;
        AppServices.Breadcrumbs.Changed += OnRedraw;
        AppServices.Live.Changed += OnRedraw;
        AppServices.Aprs.Changed += OnRedraw;
        AppServices.Imagery.Changed += OnRedraw;
        AppServices.Overlays.Changed += OnRedraw;
        UiMode.Changed += OnRedraw;
        Disposed += (_, _) =>
        {
            Tiles.TileLoaded -= OnTileLoaded;
            AppServices.Data.Changed -= OnDataChanged;
            AppServices.DrawingsChanged -= OnRedraw;
            AppServices.Breadcrumbs.Changed -= OnRedraw;
            AppServices.Live.Changed -= OnRedraw;
            AppServices.Aprs.Changed -= OnRedraw;
            AppServices.Imagery.Changed -= OnRedraw;
            AppServices.Overlays.Changed -= OnRedraw;
            UiMode.Changed -= OnRedraw;
        };
    }

    /// <summary>Decoded tiles shared by all map windows.</summary>
    public static TileCache Tiles => _sharedTiles ??= new TileCache();

    private void OnTileLoaded(object? sender, EventArgs e) => Invalidate();
    private void OnRedraw(object? sender, EventArgs e) => Invalidate();

    private void OnDataChanged(object? sender, EventArgs e)
    {
        Tiles.Clear();
        SelectedPoi = null;
        Invalidate();
    }

    // ------------------------------------------------------------------ view state

    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, Viewport.MinZoom, Viewport.MaxZoom);
            ViewChangedNow();
        }
    }

    /// <summary>Compass direction at the top of the map (0 = north up).</summary>
    public double Bearing
    {
        get => _bearing;
        set
        {
            _bearing = ((value % 360) + 360) % 360;
            ViewChangedNow();
        }
    }

    public GeoPoint Center
    {
        get => WebMercator.FromWorld(_centerX, _centerY);
        set
        {
            (_centerX, _centerY) = WebMercator.ToWorld(value.Lat, value.Lon);
            ViewChangedNow();
        }
    }

    public void SetView(GeoPoint center, double zoom, double? bearing = null)
    {
        (_centerX, _centerY) = WebMercator.ToWorld(center.Lat, center.Lon);
        _zoom = Math.Clamp(zoom, Viewport.MinZoom, Viewport.MaxZoom);
        if (bearing is { } b)
            _bearing = ((b % 360) + 360) % 360;
        ViewChangedNow();
    }

    public void ZoomToFit(GeoBounds bounds)
    {
        if (bounds.IsEmpty)
            return;
        SetView(bounds.Center, Viewport.ZoomToFit(bounds, Math.Max(200, Width), Math.Max(200, Height), PixelScale));
    }

    public float PixelScale => DeviceDpi / 96f;

    public float UiScale => PixelScale * (UiMode.Touch ? 1.3f : 1f);

    public Viewport Viewport => new(_centerX, _centerY, _zoom, _bearing, Math.Max(1, Width), Math.Max(1, Height), PixelScale);

    public event EventHandler? ViewChanged;

    /// <summary>The user panned/zoomed/rotated the map by hand (map windows stop following the GPS).</summary>
    public event EventHandler? UserMoved;

    public event EventHandler<MapPointEventArgs>? Tapped;
    public event EventHandler<MapPointEventArgs>? LongPressed;

    private void ViewChangedNow()
    {
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ scene inputs (set by the map window)

    public Route? Route { get; set; }
    public IReadOnlyList<Route> Alternatives { get; set; } = Array.Empty<Route>();
    public Drawing? Draft { get; set; }
    public Guid? SelectedDrawing { get; set; }
    public Poi? SelectedPoi { get; set; }
    public string? SelectedAprs { get; set; }
    public GeoPoint? Marker { get; set; }
    public GeoFix? Fix { get; set; }
    public bool FixStale { get; set; }
    public bool ShowControls { get; set; } = true;

    public MapScene BuildScene(bool synchronous = false) => new()
    {
        Regions = AppServices.Data.Regions.Select(r => r.Source).ToList(),
        Tiles = Tiles,
        Style = MapStyles.Current,
        LayerOn = AppServices.Settings.LayerOn,
        Pois = AppServices.Data.PoiIndex,
        SelectedPoi = SelectedPoi,
        Drawings = AppServices.Settings.ShowDrawings ? AppServices.Drawings.Items : Array.Empty<Drawing>(),
        Draft = Draft,
        SelectedDrawing = SelectedDrawing,
        Route = Route,
        Alternatives = Alternatives,
        Fix = Fix,
        FixStale = FixStale,
        Breadcrumbs = AppServices.Settings.ShowBreadcrumbs ? AppServices.Breadcrumbs.Items.Select(c => new GeoPoint(c.Lat, c.Lon)).ToList() : null,
        Live = AppServices.Settings.ShowLiveData ? AppServices.Live.ActiveFeeds.Select(f => AppServices.Live.Results.GetValueOrDefault(f.Id)).OfType<Core.Live.LiveResult>().ToList() : Array.Empty<Core.Live.LiveResult>(),
        Marker = Marker,
        Aprs = AppServices.Settings.Aprs.Show ? AppServices.Aprs.VisibleStations() : Array.Empty<Core.Aprs.AprsStation>(),
        AprsTracks = AppServices.Settings.Aprs.ShowTracks,
        AprsFadeAfter = TimeSpan.FromMinutes(Math.Max(1, AppServices.Settings.Aprs.ShowMinutes) / 2.0),
        SelectedAprs = SelectedAprs,
        NowUtc = DateTime.UtcNow,
        Overlays = AppServices.Overlays.VisibleLayers,
        Imagery = AppServices.Imagery.Tiles,
        BaseMode = AppServices.Imagery.Tiles != null ? AppServices.Settings.BaseMode : BaseMapMode.Map,
        Attribution = AppServices.Imagery.Tiles != null && AppServices.Imagery.Current is { Attribution.Length: > 0 } img
            ? $"{img.Attribution} · {AppServices.Data.Attribution}" : AppServices.Data.Attribution,
        Synchronous = synchronous,
        ShowControls = ShowControls,
        UiScale = UiScale
    };

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        var vp = new Viewport(_centerX, _centerY, _zoom, _bearing, e.Info.Width, e.Info.Height, PixelScale);
        try
        {
            _last = MapRenderer.Render(e.Surface.Canvas, vp, BuildScene());
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Data reloaded mid-frame; the next frame draws the new data.
            e.Surface.Canvas.Clear(MapStyles.Current.Background);
        }
    }

    /// <summary>
    /// Renders the area currently shown at <paramref name="factor"/> times the screen resolution, with every tile loaded,
    /// for PNG/PDF export and printing. Overlays (scale bar, north arrow, attribution) are included.
    /// </summary>
    public SKBitmap RenderSnapshot(float factor = 2f)
    {
        var w = Math.Max(1, (int)(Width * factor));
        var h = Math.Max(1, (int)(Height * factor));
        var vp = new Viewport(_centerX, _centerY, _zoom, _bearing, w, h, PixelScale * factor);
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        var scene = BuildScene(synchronous: true);
        MapRenderer.Render(canvas, vp, new MapScene
        {
            Regions = scene.Regions, Tiles = scene.Tiles, Style = scene.Style, LayerOn = scene.LayerOn, Pois = scene.Pois, SelectedPoi = null,
            Drawings = scene.Drawings, Route = scene.Route, Alternatives = scene.Alternatives, Fix = scene.Fix, FixStale = scene.FixStale,
            Breadcrumbs = scene.Breadcrumbs, Live = scene.Live, Marker = scene.Marker, Attribution = scene.Attribution, Synchronous = true,
            Aprs = scene.Aprs, AprsTracks = scene.AprsTracks, AprsFadeAfter = scene.AprsFadeAfter, NowUtc = scene.NowUtc,
            Imagery = scene.Imagery, BaseMode = scene.BaseMode, Overlays = scene.Overlays,
            ShowControls = true, UiScale = UiScale * factor
        });
        return bmp;
    }

    // ------------------------------------------------------------------ hit testing

    public GeoPoint ScreenToGeo(Point p) => Viewport.ScreenToGeo(p.X, p.Y);

    public PointF GeoToScreen(GeoPoint p)
    {
        var (x, y) = Viewport.GeoToScreen(p);
        return new PointF((float)x, (float)y);
    }

    public Poi? HitPoi(Point p)
    {
        if (_last == null)
            return null;
        foreach (var (rect, poi) in Enumerable.Reverse(_last.Pois))
            if (rect.Contains(p.X, p.Y))
                return poi;
        return null;
    }

    /// <summary>The APRS station (key) drawn under a screen point, if any.</summary>
    public string? HitAprs(Point p)
    {
        if (_last == null)
            return null;
        foreach (var (rect, key) in Enumerable.Reverse(_last.Aprs))
            if (rect.Contains(p.X, p.Y))
                return key;
        return null;
    }

    /// <summary>An event-layer point drawn under the screen point.</summary>
    public (Core.Overlays.OverlayLayer Layer, Core.Overlays.OverlayFeature Feature)? HitOverlayPoint(Point p)
    {
        if (_last == null)
            return null;
        foreach (var (rect, layer, feature) in Enumerable.Reverse(_last.OverlayPoints))
            if (rect.Contains(p.X, p.Y))
                return (layer, feature);
        return null;
    }

    /// <summary>An event-layer line or area near (or containing) the screen point.</summary>
    public (Core.Overlays.OverlayLayer Layer, Core.Overlays.OverlayFeature Feature)? HitOverlayShape(Point p)
    {
        var tol = 14 * UiScale;
        (Core.Overlays.OverlayLayer, Core.Overlays.OverlayFeature)? best = null;
        var bestD = double.MaxValue;
        var view = Viewport.VisibleBounds();
        foreach (var layer in AppServices.Overlays.VisibleLayers)
            foreach (var f in layer.Content.Features)
            {
                if (f.Geometry == Core.Overlays.OverlayGeometry.Point || !f.Bounds.Intersects(view))
                    continue;
                var dist = double.MaxValue;
                foreach (var part in f.Parts)
                {
                    var pts = part.Select(GeoToScreen).ToList();
                    var closed = f.Geometry == Core.Overlays.OverlayGeometry.Polygon;
                    for (var i = 0; i < pts.Count - (closed ? 0 : 1); i++)
                        dist = Math.Min(dist, SegDist(p, pts[i], pts[(i + 1) % pts.Count]));
                }
                if (f.Geometry == Core.Overlays.OverlayGeometry.Polygon && Inside(f.Parts[0].Select(GeoToScreen).ToList(), p))
                    dist = Math.Min(dist, tol * 0.95);
                if (dist <= tol && dist < bestD)
                {
                    bestD = dist;
                    best = (layer, f);
                }
            }
        return best;
    }

    public bool HitNorthArrow(Point p) => _last != null && _last.NorthArrow.Contains(p.X, p.Y);

    public Drawing? HitDrawing(Point p)
    {
        var tol = 22 * UiScale;
        Drawing? best = null;
        var bestD = double.MaxValue;
        foreach (var d in AppServices.Drawings.Items)
        {
            if (d.Points.Count == 0)
                continue;
            var pts = d.Points.Select(GeoToScreen).ToList();
            double dist;
            if (d.Shape == DrawingShape.Point || d.Kind is DrawingKind.Hazmat or DrawingKind.RangeRings)
            {
                // Pins are drawn above their point.
                var anchor = pts[0];
                dist = Math.Min(Dist(p, anchor), Dist(p, new PointF(anchor.X, anchor.Y - 24 * UiScale)));
            }
            else
            {
                dist = double.MaxValue;
                var count = d.Shape == DrawingShape.Polygon ? pts.Count : pts.Count - 1;
                for (var i = 0; i < count; i++)
                    dist = Math.Min(dist, SegDist(p, pts[i], pts[(i + 1) % pts.Count]));
                if (d.Shape == DrawingShape.Polygon && Inside(pts, p))
                    dist = Math.Min(dist, tol * 0.9);
            }
            if (dist <= tol && dist < bestD)
            {
                bestD = dist;
                best = d;
            }
        }
        return best;
    }

    private static double Dist(Point a, PointF b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double SegDist(Point p, PointF a, PointF b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        var t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        var cx = a.X + t * dx;
        var cy = a.Y + t * dy;
        return Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
    }

    private static bool Inside(List<PointF> poly, Point p)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    // ------------------------------------------------------------------ navigation helpers

    public void ZoomAround(Point screen, double newZoom)
    {
        var vp = Viewport;
        var (wx, wy) = vp.ScreenToWorld(screen.X, screen.Y);
        _zoom = Math.Clamp(newZoom, Viewport.MinZoom, Viewport.MaxZoom);
        var after = Viewport;
        var (nx, ny) = after.ScreenToWorld(screen.X, screen.Y);
        _centerX += wx - nx;
        _centerY += wy - ny;
        ViewChangedNow();
    }

    public void ZoomBy(double delta) => ZoomAround(new Point(Width / 2, Height / 2), _zoom + delta);

    public void PanBy(int dx, int dy)
    {
        var vp = Viewport;
        var (wx, wy) = vp.ScreenToWorld(Width / 2.0 - dx, Height / 2.0 - dy);
        _centerX = wx;
        _centerY = Math.Clamp(wy, 0, 1);
        ViewChangedNow();
    }

    public void RotateAround(Point screen, double newBearing)
    {
        var vp = Viewport;
        var (wx, wy) = vp.ScreenToWorld(screen.X, screen.Y);
        _bearing = ((newBearing % 360) + 360) % 360;
        var (nx, ny) = Viewport.ScreenToWorld(screen.X, screen.Y);
        _centerX += wx - nx;
        _centerY += wy - ny;
        ViewChangedNow();
    }

    // ------------------------------------------------------------------ mouse and keyboard

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left)
            return;
        _mouseDown = true;
        _dragging = false;
        _downPoint = e.Location;
        _rotating = ModifierKeys.HasFlag(Keys.Control);
        if (_rotating)
        {
            _rotateStartAngle = Math.Atan2(e.Y - Height / 2.0, e.X - Width / 2.0) * 180 / Math.PI;
            _rotateStartBearing = _bearing;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_mouseDown)
            return;
        var dx = e.X - _downPoint.X;
        var dy = e.Y - _downPoint.Y;
        if (!_dragging && dx * dx + dy * dy < 36)
            return;
        if (!_dragging)
        {
            _dragging = true;
            UserMoved?.Invoke(this, EventArgs.Empty);
        }
        if (_rotating)
        {
            var angle = Math.Atan2(e.Y - Height / 2.0, e.X - Width / 2.0) * 180 / Math.PI;
            Bearing = _rotateStartBearing - (angle - _rotateStartAngle);
            return;
        }
        PanBy(dx, dy);
        _downPoint = e.Location;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var wasDragging = _dragging;
        _mouseDown = false;
        _dragging = false;
        if (e.Button == MouseButtons.Right)
        {
            // Press-and-hold on a touchscreen arrives as a right click.
            LongPressed?.Invoke(this, new MapPointEventArgs(ScreenToGeo(e.Location), e.Location));
            return;
        }
        if (e.Button != MouseButtons.Left || wasDragging || DateTime.UtcNow < _suppressClickUntil)
            return;
        if (HitNorthArrow(e.Location))
        {
            Bearing = 0;
            UserMoved?.Invoke(this, EventArgs.Empty);
            return;
        }
        Tapped?.Invoke(this, new MapPointEventArgs(ScreenToGeo(e.Location), e.Location));
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || HitNorthArrow(e.Location))
            return;
        UserMoved?.Invoke(this, EventArgs.Empty);
        ZoomAround(e.Location, _zoom + 1);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        UserMoved?.Invoke(this, EventArgs.Empty);
        ZoomAround(e.Location, _zoom + e.Delta / 120.0 * 0.5);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = (int)(80 * UiScale);
        switch (e.KeyCode)
        {
            case Keys.Left: PanBy(step, 0); break;
            case Keys.Right: PanBy(-step, 0); break;
            case Keys.Up: PanBy(0, step); break;
            case Keys.Down: PanBy(0, -step); break;
            case Keys.Oemplus or Keys.Add: ZoomBy(1); break;
            case Keys.OemMinus or Keys.Subtract: ZoomBy(-1); break;
            default: return;
        }
        UserMoved?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ touch gestures (WM_GESTURE)

    private const int WM_GESTURENOTIFY = 0x011A;
    private const int WM_GESTURE = 0x0119;
    private const int GID_ZOOM = 3, GID_PAN = 4, GID_ROTATE = 5, GID_TWOFINGERTAP = 6, GID_PRESSANDTAP = 7;
    private const int GF_BEGIN = 1, GF_END = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct GESTURECONFIG
    {
        public uint dwID;
        public uint dwWant;
        public uint dwBlock;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GESTUREINFO
    {
        public uint cbSize;
        public uint dwFlags;
        public uint dwID;
        public IntPtr hwndTarget;
        public short x;
        public short y;
        public uint dwInstanceID;
        public uint dwSequenceID;
        public ulong ullArguments;
        public uint cbExtraArgs;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetGestureConfig(IntPtr hwnd, uint dwReserved, uint cIDs, [In] GESTURECONFIG[] pGestureConfig, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGestureInfo(IntPtr hGestureInfo, ref GESTUREINFO pGestureInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseGestureInfoHandle(IntPtr hGestureInfo);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_GESTURENOTIFY)
        {
            ConfigureGestures();
            base.WndProc(ref m);
            return;
        }
        if (m.Msg == WM_GESTURE && HandleGesture(m.LParam))
        {
            CloseGestureInfoHandle(m.LParam);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    private void ConfigureGestures()
    {
        try
        {
            var config = new[]
            {
                new GESTURECONFIG { dwID = GID_ZOOM, dwWant = 1 },
                new GESTURECONFIG { dwID = GID_ROTATE, dwWant = 1 },
                // Single-finger pan in every direction, with inertia, without the "gutter" that locks to one axis.
                new GESTURECONFIG { dwID = GID_PAN, dwWant = 0x01 | 0x02 | 0x04 | 0x10, dwBlock = 0x08 },
                new GESTURECONFIG { dwID = GID_TWOFINGERTAP, dwWant = 1 },
                new GESTURECONFIG { dwID = GID_PRESSANDTAP, dwWant = 1 }
            };
            SetGestureConfig(Handle, 0, (uint)config.Length, config, (uint)Marshal.SizeOf<GESTURECONFIG>());
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // No touch support: mouse handling still works.
        }
    }

    private bool HandleGesture(IntPtr handle)
    {
        var gi = new GESTUREINFO { cbSize = (uint)Marshal.SizeOf<GESTUREINFO>() };
        try
        {
            if (!GetGestureInfo(handle, ref gi))
                return false;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
        var p = PointToClient(new Point(gi.x, gi.y));
        var begin = (gi.dwFlags & GF_BEGIN) != 0;
        switch (gi.dwID)
        {
            case GID_ZOOM:
            {
                var distance = (long)(gi.ullArguments & 0xFFFFFFFF);
                if (begin || _gestureZoomStart <= 0)
                {
                    _gestureZoomStart = Math.Max(1, distance);
                    _gestureZoomBase = _zoom;
                    UserMoved?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    ZoomAround(p, _gestureZoomBase + Math.Log2((double)Math.Max(1, distance) / _gestureZoomStart));
                }
                if ((gi.dwFlags & GF_END) != 0)
                    _gestureZoomStart = 0;
                _suppressClickUntil = DateTime.UtcNow.AddMilliseconds(400);
                return true;
            }
            case GID_ROTATE:
            {
                // GID_ROTATE_ANGLE_FROM_ARGUMENT
                var angle = ((double)(gi.ullArguments & 0xFFFF) / 65535.0 * 4.0 * Math.PI - 2.0 * Math.PI) * 180 / Math.PI;
                if (begin)
                {
                    _gestureRotateBase = _bearing;
                    UserMoved?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    RotateAround(p, _gestureRotateBase - angle);
                }
                _suppressClickUntil = DateTime.UtcNow.AddMilliseconds(400);
                return true;
            }
            case GID_PAN:
            {
                if (begin)
                {
                    _gesturePanLast = p;
                    UserMoved?.Invoke(this, EventArgs.Empty);
                    return true;
                }
                PanBy(p.X - _gesturePanLast.X, p.Y - _gesturePanLast.Y);
                _gesturePanLast = p;
                _suppressClickUntil = DateTime.UtcNow.AddMilliseconds(300);
                return true;
            }
            case GID_TWOFINGERTAP:
                UserMoved?.Invoke(this, EventArgs.Empty);
                ZoomAround(p, _zoom - 1);
                return true;
            case GID_PRESSANDTAP:
                LongPressed?.Invoke(this, new MapPointEventArgs(ScreenToGeo(p), p));
                return true;
            default:
                return false;
        }
    }
}
