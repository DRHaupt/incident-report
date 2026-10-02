using Shared.UI;

namespace FieldToolkit.UI;

/// <summary>A large multi-line text editor for short lists.</summary>
public sealed class TextEditDialog : Form
{
    private readonly TextBox _box = new() { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical };

    private TextEditDialog(string title, string prompt, string text)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 440);
        Padding = new Padding(14);
        _box.Text = text;
        _box.Styled(FontRole.Large);
        var label = Ui.Text(prompt, FontRole.Bold);
        label.Dock = DockStyle.Top;
        var ok = Ui.Button("Save", ButtonStyle.Primary, () => DialogResult = DialogResult.OK);
        var cancel = Ui.Button("Cancel", ButtonStyle.Secondary, () => DialogResult = DialogResult.Cancel);
        var buttons = Ui.ButtonRow(ok, cancel);
        buttons.Dock = DockStyle.Bottom;
        buttons.Height = Ui.TargetHeight + 8;
        Controls.Add(_box);
        Controls.Add(label);
        Controls.Add(buttons);
        CancelButton = cancel;
        UiMode.TrackDialog(this);
    }

    public static string? Ask(IWin32Window owner, string title, string prompt, string text)
    {
        using var d = new TextEditDialog(title, prompt, text);
        return d.ShowDialog(owner) == DialogResult.OK ? d._box.Text : null;
    }
}
