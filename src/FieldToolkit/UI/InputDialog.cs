using Shared.UI;

namespace FieldToolkit.UI;

/// <summary>A large single-line text prompt (works with the Windows touch keyboard).</summary>
public sealed class InputDialog : Form
{
    private readonly TextBox _box;

    private InputDialog(string title, string prompt, string value)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(480, 200);
        Padding = new Padding(16);

        _box = new TextBox { Text = value, Dock = DockStyle.Top };
        _box.Styled(FontRole.Large);
        var ok = Ui.Button("OK", ButtonStyle.Primary, () => { DialogResult = DialogResult.OK; });
        var cancel = Ui.Button("Cancel", ButtonStyle.Secondary, () => { DialogResult = DialogResult.Cancel; });
        var buttons = Ui.ButtonRow(ok, cancel);
        buttons.Dock = DockStyle.Bottom;
        buttons.Height = Ui.TargetHeight + 8;
        Controls.Add(buttons);
        Controls.Add(_box);
        Controls.Add(Ui.Text(prompt, FontRole.Bold).Also(l => l.Dock = DockStyle.Top));
        AcceptButton = ok;
        CancelButton = cancel;
        UiMode.TrackDialog(this);
        Shown += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(IWin32Window owner, string title, string prompt, string value = "")
    {
        using var d = new InputDialog(title, prompt, value);
        return d.ShowDialog(owner) == DialogResult.OK ? d._box.Text.Trim() : null;
    }
}

public static class ControlExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
