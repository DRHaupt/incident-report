
using Shared.Core.Layout;
using Shared.UI;

namespace FieldMap.Framework;

/// <summary>Opens, tracks, arranges and restores the tool windows.</summary>
public sealed class WindowManager
{
    private readonly List<ToolWindow> _windows = new();
    private readonly string _sessionFile;

    public WindowManager(string sessionFile) => _sessionFile = sessionFile;

    /// <summary>Raised when windows open, close, or change title or pin state.</summary>
    public event EventHandler? Changed;

    /// <summary>Open tool windows in the order they were opened.</summary>
    public IReadOnlyList<ToolWindow> Windows => _windows;

    /// <summary>The window arrange commands lay out around (the launcher's screen).</summary>
    public Func<Screen> CurrentScreen { get; set; } = () => Screen.PrimaryScreen!;

    internal void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Opens a tool. Single-instance tools bring their existing window forward instead.</summary>
    public ToolWindow? Open(string toolId, string? state = null, WindowSession? placement = null)
    {
        var tool = ToolRegistry.Find(toolId);
        if (tool == null)
            return null;

        if (tool.SingleInstance && _windows.FirstOrDefault(w => w.Tool.Id == toolId) is { } existing)
        {
            if (state != null && existing is IAcceptsState accepts)
                accepts.ApplyState(state);
            BringForward(existing);
            return existing;
        }

        var window = tool.Create(state);
        if (UiMode.Touch && placement == null)
            window.ClientSize = new Size((int)(window.ClientSize.Width * UiMode.Scale), (int)(window.ClientSize.Height * UiMode.Scale));
        FitToScreen(window, placement);
        window.FormClosed += (_, _) =>
        {
            _windows.Remove(window);
            NotifyChanged();
        };
        _windows.Add(window);
        window.Show();
        if (placement != null)
        {
            if (placement.Pinned)
                window.Pinned = true;
            if (placement.Maximized)
                window.WindowState = FormWindowState.Maximized;
            else if (placement.Minimized)
                window.WindowState = FormWindowState.Minimized;
        }
        NotifyChanged();
        return window;
    }

    private void FitToScreen(Form window, WindowSession? placement)
    {
        var areas = Screen.AllScreens.Select(s => s.WorkingArea).OrderByDescending(a => a == CurrentScreen().WorkingArea).ToList();
        var area = areas[0];
        if (placement is { Width: > 0, Height: > 0 })
        {
            window.StartPosition = FormStartPosition.Manual;
            window.Bounds = WindowLayout.EnsureVisible(new Rectangle(placement.X, placement.Y, placement.Width, placement.Height), areas, window.MinimumSize);
            return;
        }
        // New windows: never larger than the screen, cascaded from the top-left of the launcher's screen.
        var size = new Size(Math.Min(window.Width, area.Width), Math.Min(window.Height, area.Height));
        var offset = (_windows.Count % 6) * 36;
        window.StartPosition = FormStartPosition.Manual;
        window.Bounds = WindowLayout.EnsureVisible(
            new Rectangle(Math.Min(area.Left + 40 + offset, area.Right - size.Width), Math.Min(area.Top + 30 + offset, area.Bottom - size.Height), size.Width, size.Height),
            areas, window.MinimumSize);
    }

    public static void BringForward(Form window)
    {
        if (window.WindowState == FormWindowState.Minimized)
            window.WindowState = FormWindowState.Normal;
        window.Show();
        window.Activate();
    }

    /// <summary>Lays out all open tool windows on the launcher's screen.</summary>
    public void Arrange(ArrangeMode mode)
    {
        if (_windows.Count == 0)
            return;
        var area = CurrentScreen().WorkingArea;
        var minSize = new Size((int)(460 * UiMode.Scale), (int)(360 * UiMode.Scale));
        var targets = _windows.ToList();
        var rects = WindowLayout.Arrange(mode, targets.Count, area, minSize);
        for (var i = 0; i < targets.Count; i++)
        {
            var w = targets[i];
            w.WindowState = FormWindowState.Normal;
            w.Bounds = rects[i];
        }
        // Bring them forward in order so cascaded windows stack correctly.
        foreach (var w in targets)
            w.BringToFront();
        targets[^1].Activate();
    }

    public void MinimizeAll()
    {
        foreach (var w in _windows)
            w.WindowState = FormWindowState.Minimized;
    }

    public void ShowAll()
    {
        foreach (var w in _windows)
            if (w.WindowState == FormWindowState.Minimized)
                w.WindowState = FormWindowState.Normal;
    }

    public void CloseAll()
    {
        foreach (var w in _windows.ToList())
            w.Close();
    }

    // ------------------------------------------------------------------ session

    public SessionState Capture() => new()
    {
        SavedUtc = DateTime.UtcNow,
        Windows = _windows.Select(w =>
        {
            var b = w.WindowState == FormWindowState.Normal ? w.Bounds : w.RestoreBounds;
            return new WindowSession
            {
                ToolId = w.Tool.Id,
                State = w.SaveState(),
                X = b.X,
                Y = b.Y,
                Width = b.Width,
                Height = b.Height,
                Maximized = w.WindowState == FormWindowState.Maximized,
                Minimized = w.WindowState == FormWindowState.Minimized,
                Pinned = w.Pinned
            };
        }).ToList()
    };

    public void SaveSession()
    {
        try
        {
            Json.WriteFileAtomic(_sessionFile, Json.Serialize(Capture()));
        }
        catch (IOException)
        {
            // Session restore is a convenience; losing it is harmless.
        }
    }

    public SessionState? LoadSession()
    {
        try
        {
            if (!File.Exists(_sessionFile))
                return null;
            var s = Json.Deserialize<SessionState>(File.ReadAllText(_sessionFile));
            s.Windows.RemoveAll(w => ToolRegistry.Find(w.ToolId) == null);
            return s.Windows.Count > 0 ? s : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Restore(SessionState session)
    {
        foreach (var w in session.Windows)
            Open(w.ToolId, string.IsNullOrEmpty(w.State) ? null : w.State, w);
    }
}

/// <summary>A single-instance tool that can switch to new state when opened again (e.g. CPR helper preset).</summary>
public interface IAcceptsState
{
    void ApplyState(string state);
}
