using FieldToolkit.Core.Kit;
using FieldToolkit.UI;
using Shared.UI;

namespace FieldToolkit.Tools.Kit;

/// <summary>Add or edit a tracked kit item. Quick buttons set common expiry periods without fiddling with a calendar.</summary>
public sealed class KitItemDialog : Form
{
    private readonly TextBox _name = new() { Width = 420 };
    private readonly ComboBox _category = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 420 };
    private readonly TextBox _location = new() { Width = 420 };
    private readonly TextBox _identifier = new() { Width = 420 };
    private readonly DateTimePicker _expires = new() { Format = DateTimePickerFormat.Long, ShowCheckBox = true, Width = 420 };
    private readonly NumericUpDown _warn = new() { Minimum = 0, Maximum = 365, Width = 120 };
    private readonly TextBox _notes = new() { Width = 420 };
    private readonly KitItem _item;

    public KitItemDialog(KitItem item, string title)
    {
        _item = item;
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        ClientSize = new Size(620, 660);
        Padding = new Padding(14);

        _name.Text = item.Name;
        _category.Items.AddRange(KitRules.DefaultCategories.Concat(AppServices.Kit.Items.Select(i => i.Category)).Where(c => c.Length > 0).Distinct().Cast<object>().ToArray());
        _category.Text = item.Category;
        _location.Text = item.Location;
        _identifier.Text = item.Identifier;
        _expires.Checked = item.Expires.HasValue;
        _expires.Value = (item.Expires ?? AppServices.Today.AddYears(1)).ToDateTime(TimeOnly.MinValue);
        _warn.Value = Math.Clamp(item.WarnDays, 0, 365);
        _notes.Text = item.Notes;
        foreach (var c in new Control[] { _name, _category, _location, _identifier, _expires, _warn, _notes })
            c.Styled(FontRole.Large);

        var stack = Ui.Stack(new Padding(0));
        void Field(string label, Control c)
        {
            stack.Controls.Add(Ui.Text(label, FontRole.Bold, padding: new Padding(0, 6, 0, 0)));
            stack.Controls.Add(c);
        }
        Field("Item (e.g. Trauma kit 2, Extinguisher – Unit 3, AED pads)", _name);
        Field("Category", _category);
        Field("Kept in / on (vehicle, bag, station)", _location);
        Field("Serial, lot or asset number", _identifier);
        Field("Expires / service due (untick for no date)", _expires);
        Button Plus(string text, int months) => Ui.Button(text, ButtonStyle.Secondary, () =>
        {
            _expires.Checked = true;
            _expires.Value = DateTime.Today.AddMonths(months);
        }, FontRole.Normal);
        var quick = Ui.ButtonRow(Plus("+6 months", 6), Plus("+1 year", 12), Plus("+2 years", 24), Plus("+5 years", 60));
        quick.Height = Ui.Scaled(48);
        quick.FixedSize();
        stack.Controls.Add(quick);
        Field("Warn this many days before", _warn);
        Field("Notes", _notes);

        var ok = Ui.Button("Save", ButtonStyle.Primary, Accept);
        var cancel = Ui.Button("Cancel", ButtonStyle.Secondary, () => DialogResult = DialogResult.Cancel);
        var buttons = Ui.ButtonRow(ok, cancel);
        buttons.Dock = DockStyle.Bottom;
        buttons.Height = Ui.TargetHeight + 8;
        Controls.Add(stack);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        UiMode.TrackDialog(this);
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            MessageBox.Show(this, "Give the item a name.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _item.Name = _name.Text.Trim();
        _item.Category = _category.Text.Trim();
        _item.Location = _location.Text.Trim();
        _item.Identifier = _identifier.Text.Trim();
        _item.Expires = _expires.Checked ? DateOnly.FromDateTime(_expires.Value) : null;
        _item.WarnDays = (int)_warn.Value;
        _item.Notes = _notes.Text.Trim();
        DialogResult = DialogResult.OK;
    }
}
