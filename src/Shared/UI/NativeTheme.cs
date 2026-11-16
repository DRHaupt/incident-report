using System.Runtime.InteropServices;

namespace Shared.UI;

/// <summary>
/// The parts of a window Windows draws itself and WinForms colours can't reach: the title bar, scroll bars and list
/// headers. In the Night theme these use Windows' own dark versions (Windows 10 1809 and later); elsewhere they go
/// back to normal. Every call is best effort: on older Windows nothing changes and nothing fails.
/// </summary>
public static class NativeTheme
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeOld = 19; // Windows 10 1809–1909
    private const int LvmGetHeader = 0x101F;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    /// <summary>Dark or normal title bar (caption, minimise/maximise/close) for a window.</summary>
    public static void TitleBar(Form form, bool dark)
    {
        if (!Supported)
            return;
        WhenCreated(form, () =>
        {
            try
            {
                var value = dark ? 1 : 0;
                if (DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
                    DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkModeOld, ref value, sizeof(int));
                // Windows 10 only repaints the caption when the frame changes.
                const uint noMoveSizeZOrderActivate = 0x0001 | 0x0002 | 0x0004 | 0x0010, frameChanged = 0x0020;
                SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, 0, 0, noMoveSizeZOrderActivate | frameChanged);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        });
    }

    /// <summary>Dark or normal scroll bars for a list, tree, text box or scrolling panel.</summary>
    public static void Scrollbars(Control c, bool dark) =>
        Apply(c, () => SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : c is TreeView or ListView ? "Explorer" : null, null));

    /// <summary>Dark or normal column header strip of a details-view list (covers the area right of the last column).</summary>
    public static void ListHeader(ListView list, bool dark) =>
        Apply(list, () =>
        {
            var header = SendMessage(list.Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero)
                SetWindowTheme(header, dark ? "DarkMode_ItemsView" : null, null);
        });

    private static void Apply(Control c, Action action)
    {
        if (!Supported)
            return;
        WhenCreated(c, () =>
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        });
    }

    private static void WhenCreated(Control c, Action action)
    {
        if (c.IsHandleCreated)
        {
            action();
            return;
        }
        void Once(object? s, EventArgs e)
        {
            c.HandleCreated -= Once;
            action();
        }
        c.HandleCreated += Once;
    }
}
