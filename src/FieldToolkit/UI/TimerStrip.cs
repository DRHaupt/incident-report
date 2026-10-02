using FieldToolkit.Core.Timers;
using FieldToolkit.Tools.Timers;
using Shared;
using Shared.UI;

namespace FieldToolkit.UI;

/// <summary>
/// The strip at the bottom of every tool window showing running timers. Hidden when there are none. A timer that has
/// alerted turns red; tapping it acknowledges the alert, tapping any other timer opens the Timers tool.
/// </summary>
public sealed class TimerStrip : FlowLayoutPanel
{
    private const int MaxShown = 4;
    private readonly Dictionary<Guid, Button> _buttons = new();

    public TimerStrip()
    {
        Dock = DockStyle.Bottom;
        Height = 46;
        WrapContents = false;
        Padding = new Padding(4, 3, 4, 3);
        BackColor = Brand.Navy;
        this.FixedSize();
        AppServices.Timers.Changed += OnTimersChanged;
        AppServices.ClockTick += OnTick;
        UiMode.Changed += OnModeChanged;
        Rebuild();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            AppServices.Timers.Changed -= OnTimersChanged;
            AppServices.ClockTick -= OnTick;
            UiMode.Changed -= OnModeChanged;
        }
        base.Dispose(disposing);
    }

    private void OnModeChanged(object? s, EventArgs e) => Rebuild();

    private void OnTimersChanged(object? s, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        SuspendLayout();
        Controls.Clear();
        foreach (var b in _buttons.Values)
            b.Dispose();
        _buttons.Clear();

        var timers = AppServices.Timers.Timers
            .OrderByDescending(t => t.NeedsAttention)
            .ThenByDescending(t => t.Running)
            .Take(MaxShown)
            .ToList();
        Visible = timers.Count > 0;
        Height = Ui.Scaled(46);
        foreach (var t in timers)
        {
            var timer = t;
            var b = new Button
            {
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Height = Height - Padding.Vertical,
                Margin = new Padding(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                UseMnemonic = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.Styled(FontRole.Bold).FixedSize();
            b.Click += (_, _) =>
            {
                if (timer.NeedsAttention)
                    AppServices.Timers.Acknowledge(timer);
                else
                    AppServices.Windows.Open(TimersWindow.ToolId);
            };
            _buttons[t.Id] = b;
            Controls.Add(b);
            Update(timer, b);
        }
        var more = AppServices.Timers.Timers.Count - timers.Count;
        if (more > 0)
            Controls.Add(new Label { Text = $"+{more} more", AutoSize = true, ForeColor = Color.White, Margin = new Padding(6, 10, 0, 0) }.Styled(FontRole.Bold));
        UiMode.Apply(this);
        ResumeLayout(true);
    }

    private void OnTick(object? s, EventArgs e)
    {
        foreach (var t in AppServices.Timers.Timers)
            if (_buttons.TryGetValue(t.Id, out var b))
                Update(t, b);
    }

    private static void Update(FieldTimer t, Button b)
    {
        var text = $"{t.Label}  {t.Display(AppServices.Timers.Now)}" + (t.Running ? "" : " (paused)");
        if (b.Text != text)
            b.Text = text;
        var (back, fore) = t.NeedsAttention ? (Brand.Danger, Color.White)
            : t.IsExpired(AppServices.Timers.Now) ? (Brand.Amber, Brand.Navy)
            : (Color.FromArgb(0x41, 0x52, 0x6A), Color.White);
        if (b.BackColor != UiMode.Map(back, true))
            UiMode.SetColors(b, back, fore);
    }
}
