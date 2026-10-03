using Shared;
using Shared.Location;
using Shared.UI;

namespace FieldMap.Views;

/// <summary>App settings: name for drawings, window restore, Alberta 511 key, default range rings, position source, APRS.</summary>
public sealed class SettingsDialog : Form
{
    private readonly TextBox _name = new() { Width = 420 };
    private readonly TextBox _key = new() { Width = 420 };
    private readonly TextBox _rings = new() { Width = 420 };
    private readonly ComboBox _restore = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };

    public SettingsDialog()
    {
        Text = "Field Map settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(500, 520);
        Padding = new Padding(14);
        var s = AppServices.Settings;
        _name.Text = s.UserName;
        _key.Text = s.Ab511Key;
        _rings.Text = string.Join(", ", s.DefaultRings);
        _restore.Items.AddRange(new object[] { "Ask", "Always", "Never" });
        _restore.SelectedIndex = (int)s.RestoreWindows;
        var stack = Ui.Stack(new Padding(0));
        void Field(string label, Control c, string? hint = null)
        {
            stack.Controls.Add(Ui.Text(label, FontRole.Bold));
            c.Margin = new Padding(0, 0, 0, 4);
            stack.Controls.Add(c);
            if (hint != null)
                stack.Controls.Add(Ui.Text(hint, FontRole.Normal, Brand.Muted));
        }
        Field("Your name or call sign (recorded on drawings)", _name);
        Field("Restore windows on start", _restore);
        Field("Default range rings (metres)", _rings);
        Field("Alberta 511 developer key (optional)", _key, "Free key from 511.alberta.ca/developers; without it the Alberta 511 live layer stays off.");
        var gps = Ui.Button("Position source (GPS)…", ButtonStyle.Secondary, () => { using var d = new SourceDialog(AppServices.Location); d.ShowDialog(this); });
        stack.Controls.Add(gps);
        var aprs = Ui.Button("APRS (amateur radio positions)…", ButtonStyle.Secondary, () => { using var d = new AprsSettingsDialog(); d.ShowDialog(this); });
        aprs.Margin = new Padding(0, 6, 0, 0);
        stack.Controls.Add(aprs);
        foreach (var c in new Control[] { _name, _key, _rings, _restore })
            c.Styled(FontRole.Large);
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
        var rings = _rings.Text.Split(',', ';', ' ').Where(x => x.Length > 0).Select(x => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1).ToList();
        if (rings.Count == 0 || rings.Any(r => r <= 0))
        {
            MessageBox.Show(this, "Enter ring radii in metres, separated by commas.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var s = AppServices.Settings;
        s.UserName = _name.Text.Trim();
        s.Ab511Key = _key.Text.Trim();
        s.DefaultRings = rings.OrderBy(r => r).ToList();
        s.RestoreWindows = (RestoreWindowsMode)Math.Max(0, _restore.SelectedIndex);
        s.Save();
        DialogResult = DialogResult.OK;
    }
}
