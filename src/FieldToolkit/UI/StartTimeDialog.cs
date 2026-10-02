using Shared.UI;

namespace FieldToolkit.UI;

/// <summary>
/// "When did it start?" for back-dated timers (e.g. stroke symptom onset). Big quick choices plus minute steppers
/// instead of a small time picker, so it can be used with gloves.
/// </summary>
public sealed class StartTimeDialog : Form
{
    private DateTime _time = DateTime.Now;
    private readonly Label _display;

    private StartTimeDialog(string question)
    {
        Text = "Start time";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 430);
        Padding = new Padding(12);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

        var q = Ui.Text(question, FontRole.Heading);
        q.Dock = DockStyle.Fill;
        layout.Controls.Add(q, 0, 0);

        Button Ago(string text, int minutes) => Ui.Button(text, ButtonStyle.Secondary, () => Set(DateTime.Now.AddMinutes(-minutes)));
        var quick = Ui.ButtonRow(Ago("Now", 0), Ago("5 min ago", 5), Ago("15 min ago", 15), Ago("30 min ago", 30), Ago("1 h ago", 60));
        quick.Dock = DockStyle.Fill;
        layout.Controls.Add(quick, 0, 1);

        _display = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter }.Styled(FontRole.Display);
        layout.Controls.Add(_display, 0, 2);

        Button Step(string text, int minutes) => Ui.Button(text, ButtonStyle.Secondary, () => Set(_time.AddMinutes(minutes)));
        var steps = Ui.ButtonRow(Step("−10 min", -10), Step("−1 min", -1), Step("+1 min", 1), Step("+10 min", 10));
        steps.Dock = DockStyle.Fill;
        layout.Controls.Add(steps, 0, 3);

        var ok = Ui.Button("Start timer", ButtonStyle.Primary, () => DialogResult = DialogResult.OK);
        var cancel = Ui.Button("Cancel", ButtonStyle.Secondary, () => DialogResult = DialogResult.Cancel);
        var buttons = Ui.ButtonRow(ok, cancel);
        buttons.Dock = DockStyle.Fill;
        layout.Controls.Add(buttons, 0, 5);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        UiMode.TrackDialog(this);
        Set(_time);
    }

    private DateTime Chosen => _time;

    private void Set(DateTime t)
    {
        _time = t > DateTime.Now ? DateTime.Now : t;
        var ago = DateTime.Now - _time;
        _display.Text = _time.ToString("HH:mm") + (ago.TotalMinutes >= 1 ? $"  ({(int)ago.TotalMinutes} min ago)" : "  (now)");
    }

    /// <summary>The chosen start time in UTC, or null if cancelled.</summary>
    public static DateTime? Ask(IWin32Window owner, string question)
    {
        using var d = new StartTimeDialog(question);
        return d.ShowDialog(owner) == DialogResult.OK ? d.Chosen.ToUniversalTime() : null;
    }
}
