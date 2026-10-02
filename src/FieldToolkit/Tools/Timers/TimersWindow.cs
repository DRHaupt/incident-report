using FieldToolkit.Core.Timers;
using FieldToolkit.Framework;
using FieldToolkit.UI;
using Shared;
using Shared.UI;

namespace FieldToolkit.Tools.Timers;

/// <summary>All timers in the shared timer system: start stopwatches and countdowns, pause, restart, rename, delete.</summary>
public sealed class TimersWindow : ToolWindow
{
    public const string ToolId = "timers";

    private readonly FlowLayoutPanel _list;
    private readonly Dictionary<Guid, Label> _times = new();

    public TimersWindow() : base(ToolRegistry.Find(ToolId)!)
    {
        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 132, ColumnCount = 1, RowCount = 2, Padding = new Padding(8, 8, 8, 0) };
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var first = Ui.ButtonRow(
            Ui.Button("Start stopwatch", ButtonStyle.Primary, StartStopwatch),
            Ui.Button("Custom countdown…", ButtonStyle.Secondary, StartCustomCountdown));
        Button Preset(int minutes) => Ui.Button($"{minutes} min", ButtonStyle.Secondary,
            () => AppServices.Timers.StartCountdown($"{minutes}-minute countdown", TimeSpan.FromMinutes(minutes)));
        var presets = Ui.ButtonRow(Preset(1), Preset(2), Preset(5), Preset(10), Preset(15), Preset(20), Preset(30));
        first.Dock = presets.Dock = DockStyle.Fill;
        top.Controls.Add(first, 0, 0);
        top.Controls.Add(presets, 0, 1);

        _list = Ui.Stack(new Padding(8));
        Body.Controls.Add(_list);
        Body.Controls.Add(top);

        AppServices.Timers.Changed += OnTimersChanged;
        AppServices.ClockTick += OnTick;
        FormClosed += (_, _) =>
        {
            AppServices.Timers.Changed -= OnTimersChanged;
            AppServices.ClockTick -= OnTick;
        };
        Rebuild();
    }

    private void OnTimersChanged(object? s, EventArgs e) => Rebuild();

    private void StartStopwatch()
    {
        var n = AppServices.Timers.Timers.Count(t => t.Mode == TimerMode.Stopwatch) + 1;
        AppServices.Timers.StartStopwatch($"Stopwatch {n}");
    }

    private void StartCustomCountdown()
    {
        var text = InputDialog.Ask(this, "Countdown", "Minutes:", "10");
        if (text != null && double.TryParse(text, out var minutes) && minutes > 0 && minutes <= 24 * 60)
            AppServices.Timers.StartCountdown($"{minutes:0.#}-minute countdown", TimeSpan.FromMinutes(minutes));
    }

    private void Rebuild()
    {
        _list.SuspendLayout();
        foreach (Control c in _list.Controls.Cast<Control>().ToList())
            c.Dispose();
        _list.Controls.Clear();
        _times.Clear();
        if (AppServices.Timers.Timers.Count == 0)
            _list.Controls.Add(Ui.Text("No timers. Start a stopwatch or countdown above, or from a First Aid topic.", FontRole.Large, Brand.Muted));
        foreach (var t in AppServices.Timers.Timers)
            _list.Controls.Add(BuildRow(t));
        UiMode.Apply(_list);
        _list.ResumeLayout(true);
    }

    private Control BuildRow(FieldTimer t)
    {
        var alert = t.NeedsAttention;
        var row = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 3, Height = Ui.Scaled(170), Margin = new Padding(0, 0, 0, 8), Padding = new Padding(10, 6, 10, 6),
            BackColor = alert ? Brand.Danger : Brand.Panel
        };
        row.FixedSize();
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        var fore = alert ? Color.White : Brand.Navy;

        row.Controls.Add(new Label { Text = t.Label, Dock = DockStyle.Fill, ForeColor = fore, AutoEllipsis = true, UseMnemonic = false }.Styled(FontRole.Heading), 0, 0);
        var mode = t.Mode == TimerMode.Countdown ? $"Countdown {FieldTimer.Format(t.Duration)}" : "Stopwatch";
        var info = $"{mode} · started {t.StartedLocal:HH:mm}" + (t.Source.Length > 0 ? " · " + t.Source : "") + (t.Running ? "" : " · PAUSED");
        row.Controls.Add(new Label { Text = info, Dock = DockStyle.Fill, ForeColor = fore, AutoEllipsis = true, UseMnemonic = false }, 0, 1);
        var time = new Label { Text = t.Display(AppServices.Timers.Now), AutoSize = true, ForeColor = fore, Anchor = AnchorStyles.Right }.Styled(FontRole.Display);
        row.Controls.Add(time, 1, 0);
        row.SetRowSpan(time, 2);
        _times[t.Id] = time;

        var buttons = new List<Button>();
        if (alert)
            buttons.Add(Ui.Button("Acknowledge", ButtonStyle.Accent, () => AppServices.Timers.Acknowledge(t)));
        buttons.Add(t.Running
            ? Ui.Button("Pause", ButtonStyle.Secondary, () => AppServices.Timers.Pause(t))
            : Ui.Button("Resume", ButtonStyle.Primary, () => AppServices.Timers.Resume(t)));
        buttons.Add(Ui.Button("Restart", ButtonStyle.Secondary, () => AppServices.Timers.Restart(t)));
        buttons.Add(Ui.Button("Rename", ButtonStyle.Secondary, () =>
        {
            var name = InputDialog.Ask(this, "Rename timer", "Timer name:", t.Label);
            if (!string.IsNullOrWhiteSpace(name))
                AppServices.Timers.Rename(t, name);
        }));
        buttons.Add(Ui.Button("Delete", ButtonStyle.Danger, () =>
        {
            if (MessageBox.Show(this, $"Delete the timer \"{t.Label}\" ({t.Display(AppServices.Timers.Now)})?", "Delete timer",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                AppServices.Timers.Remove(t);
        }));
        var bar = Ui.ButtonRow(buttons.ToArray());
        bar.Dock = DockStyle.Fill;
        row.Controls.Add(bar, 0, 2);
        row.SetColumnSpan(bar, 2);
        return row;
    }

    private void OnTick(object? s, EventArgs e)
    {
        foreach (var t in AppServices.Timers.Timers)
            if (_times.TryGetValue(t.Id, out var label))
            {
                var text = t.Display(AppServices.Timers.Now);
                if (label.Text != text)
                    label.Text = text;
            }
    }
}
