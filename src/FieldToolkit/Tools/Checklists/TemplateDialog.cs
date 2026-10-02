using FieldToolkit.Core.Checklists;
using FieldToolkit.UI;
using Shared.UI;

namespace FieldToolkit.Tools.Checklists;

/// <summary>Edit a checklist: name, the question asked at the start, and the items (one per line, "# " for headings).</summary>
public sealed class TemplateDialog : Form
{
    private readonly TextBox _name = new() { Dock = DockStyle.Top };
    private readonly TextBox _prompt = new() { Dock = DockStyle.Top };
    private readonly TextBox _items = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly ChecklistTemplate _template;

    public TemplateDialog(ChecklistTemplate template)
    {
        _template = template;
        Text = "Edit checklist";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = new Size(640, 640);
        MinimumSize = new Size(420, 420);
        Padding = new Padding(14);

        _name.Text = template.Name;
        _prompt.Text = template.ReferencePrompt;
        _items.Text = ChecklistText.Format(template.Lines);
        _name.Styled(FontRole.Large);
        _prompt.Styled(FontRole.Large);
        _items.Styled(FontRole.Large);

        var top = new VStack { Dock = DockStyle.Top };
        top.Controls.Add(Ui.Text("Checklist name", FontRole.Bold));
        top.Controls.Add(_name);
        top.Controls.Add(Ui.Text("Ask at the start for (optional, e.g. \"Vehicle / call sign\")", FontRole.Bold, padding: new Padding(0, 8, 0, 2)));
        top.Controls.Add(_prompt);
        top.Controls.Add(Ui.Text("Items – one per line. Start a line with # for a section heading.", FontRole.Bold, padding: new Padding(0, 8, 0, 2)));

        var ok = Ui.Button("Save", ButtonStyle.Primary, Accept);
        var cancel = Ui.Button("Cancel", ButtonStyle.Secondary, () => DialogResult = DialogResult.Cancel);
        var buttons = Ui.ButtonRow(ok, cancel);
        buttons.Dock = DockStyle.Bottom;
        buttons.Height = Ui.TargetHeight + 8;
        Controls.Add(_items);
        Controls.Add(top);
        Controls.Add(buttons);
        CancelButton = cancel;
        UiMode.TrackDialog(this);
    }

    private void Accept()
    {
        var lines = ChecklistText.Parse(_items.Text);
        if (string.IsNullOrWhiteSpace(_name.Text) || lines.All(l => l.Heading))
        {
            MessageBox.Show(this, "Give the checklist a name and at least one item.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _template.Name = _name.Text.Trim();
        _template.ReferencePrompt = _prompt.Text.Trim();
        _template.Lines = lines;
        DialogResult = DialogResult.OK;
    }
}
