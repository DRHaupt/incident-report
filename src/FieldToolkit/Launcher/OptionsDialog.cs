using FieldToolkit.UI;
using Shared.UI;

namespace FieldToolkit.Launcher;

/// <summary>Toolkit settings: display modes, window restore, alerts and the content folder.</summary>
public sealed class OptionsDialog : Form
{
    private readonly CheckBox _touch = new() { Text = "Touch Mode (larger text and controls)", AutoSize = true };
    private readonly CheckBox _daylight = new() { Text = "Daylight theme (high contrast for sunlight)", AutoSize = true };
    private readonly CheckBox _sound = new() { Text = "Play a sound when a timer alerts", AutoSize = true };
    private readonly ComboBox _restore = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly TextBox _content = new() { Width = 360 };
    private readonly TextBox _user = new() { Width = 360 };

    public OptionsDialog()
    {
        Text = "Field Toolkit Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 560);
        Padding = new Padding(16);

        var s = AppServices.Settings;
        _touch.Checked = UiMode.Touch;
        _daylight.Checked = UiMode.Daylight;
        _sound.Checked = s.AlertSound;
        _restore.Items.AddRange(new object[] { "Ask on start", "Always restore", "Never restore" });
        _restore.SelectedIndex = (int)s.RestoreWindows;
        _content.Text = s.ContentFolder;
        _user.Text = s.UserName;

        var stack = Ui.Stack(new Padding(0));
        stack.Controls.Add(Ui.Text("Display", FontRole.Heading));
        stack.Controls.Add(_touch);
        stack.Controls.Add(_daylight);
        stack.Controls.Add(Ui.Text("Your name or call sign (filled in on checklists and the radio log)", FontRole.Heading, padding: new Padding(0, 12, 0, 2)));
        stack.Controls.Add(_user);
        stack.Controls.Add(Ui.Text("Windows and timers", FontRole.Heading, padding: new Padding(0, 12, 0, 2)));
        stack.Controls.Add(Ui.Text("Reopen the windows that were open when the toolkit last closed:"));
        stack.Controls.Add(_restore);
        stack.Controls.Add(_sound);
        stack.Controls.Add(Ui.Text("Content folder", FontRole.Heading, padding: new Padding(0, 12, 0, 2)));
        stack.Controls.Add(Ui.Text("Folder containing firstaid\\ and erg\\. Leave empty to use the Content folder next to the program. " +
                                   "Point this at a reviewed copy so edits survive program updates."));
        var contentRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        contentRow.Controls.Add(_content);
        contentRow.Controls.Add(Ui.Button("Browse…", ButtonStyle.Secondary, Browse).Also(b => b.Width = 110));
        stack.Controls.Add(contentRow);
        stack.Controls.Add(Ui.Text("In use: " + AppServices.ContentFolder, fore: Shared.Brand.Muted));
        stack.Controls.Add(Ui.Button("Open data folder (timers, settings, session)", ButtonStyle.Secondary, () =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ToolkitSettings.DataFolder) { UseShellExecute = true })));
        stack.Controls.Add(Ui.Text("Updates", FontRole.Heading, padding: new Padding(0, 12, 0, 2)));
        stack.Controls.Add(Ui.Button("Check for Field Suite updates…", ButtonStyle.Secondary, () => Shared.Update.UpdateNotifier.ShowWindow(this)));
        foreach (Control c in stack.Controls)
            c.Margin = new Padding(0, 2, 0, 4);

        var ok = Ui.Button("OK", ButtonStyle.Primary, Apply);
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

    private void Browse()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select the folder that contains firstaid\\ and erg\\", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _content.Text = dlg.SelectedPath;
    }

    private void Apply()
    {
        var folder = _content.Text.Trim();
        if (folder.Length > 0 && !Directory.Exists(Path.Combine(folder, "firstaid")))
        {
            MessageBox.Show(this, "That folder does not contain a firstaid subfolder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var s = AppServices.Settings;
        var contentChanged = !string.Equals(s.ContentFolder, folder, StringComparison.OrdinalIgnoreCase);
        s.ContentFolder = folder;
        s.AlertSound = _sound.Checked;
        s.UserName = _user.Text.Trim();
        s.RestoreWindows = (RestoreWindowsMode)_restore.SelectedIndex;
        s.Save();
        if (contentChanged)
        {
            AppServices.ReloadContent();
            MessageBox.Show(this, "The new content is used by windows opened from now on.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        DialogResult = DialogResult.OK;
        UiMode.SetTouch(_touch.Checked);
        UiMode.SetDaylight(_daylight.Checked);
    }
}
