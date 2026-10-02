namespace IncidentReporting.UI;

/// <summary>Right-aligned toolbar toggle for switching between the standard and touch-friendly interface.</summary>
internal static class TouchModeButton
{
    public static ToolStripButton Create()
    {
        var button = new ToolStripButton
        {
            Alignment = ToolStripItemAlignment.Right,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            CheckOnClick = false
        };
        void Sync()
        {
            button.Checked = UiMode.Touch;
            button.Text = UiMode.Touch ? "Standard View" : "Touch Mode";
            button.ToolTipText = UiMode.Touch
                ? "Switch back to the standard interface (Ctrl+T)"
                : "Switch to a touch-friendly interface with larger text, rows and buttons (Ctrl+T)";
        }
        void OnChanged(object? s, EventArgs e) => Sync();
        button.Click += (_, _) => UiMode.SetTouch(!UiMode.Touch);
        UiMode.Changed += OnChanged;
        button.Disposed += (_, _) => UiMode.Changed -= OnChanged;
        Sync();
        return button;
    }
}
