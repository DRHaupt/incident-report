namespace Shared.UI;

/// <summary>Right-aligned toolbar toggles for the touch-friendly scale and the daylight (high-contrast) theme.</summary>
public static class UiModeButtons
{
    public static ToolStripButton Touch() => Create(
        () => UiMode.Touch, v => UiMode.SetTouch(v),
        on: "Standard View", off: "Touch Mode",
        tipOn: "Switch back to the standard interface (Ctrl+T)",
        tipOff: "Switch to a touch-friendly interface with larger text, rows and buttons (Ctrl+T)");

    public static ToolStripButton Daylight() => Create(
        () => UiMode.Daylight, v => UiMode.SetDaylight(v),
        on: "Standard Colors", off: "Daylight",
        tipOn: "Switch back to the standard colours (Ctrl+D)",
        tipOff: "High-contrast colours for direct sunlight (Ctrl+D)");

    private static ToolStripButton Create(Func<bool> get, Action<bool> set, string on, string off, string tipOn, string tipOff)
    {
        var button = new ToolStripButton
        {
            Alignment = ToolStripItemAlignment.Right,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            CheckOnClick = false
        };
        void Sync()
        {
            button.Checked = get();
            button.Text = get() ? on : off;
            button.ToolTipText = get() ? tipOn : tipOff;
        }
        void OnChanged(object? s, EventArgs e) => Sync();
        button.Click += (_, _) => set(!get());
        UiMode.Changed += OnChanged;
        button.Disposed += (_, _) => UiMode.Changed -= OnChanged;
        Sync();
        return button;
    }

    /// <summary>Handles Ctrl+T / Ctrl+D. Call from a form's ProcessCmdKey.</summary>
    public static bool HandleShortcut(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.T:
                UiMode.SetTouch(!UiMode.Touch);
                return true;
            case Keys.Control | Keys.D:
                UiMode.SetDaylight(!UiMode.Daylight);
                return true;
            default:
                return false;
        }
    }
}
