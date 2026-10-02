using IncidentReporting.Data;

namespace IncidentReporting.UI;

public sealed class OptionsDialog : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _org = new() { Width = 340 };
    private readonly TextBox _reporter = new() { Width = 340 };
    private readonly TextBox _department = new() { Width = 340 };
    private readonly TextBox _location = new() { Width = 340 };
    private readonly TextBox _prefix = new() { Width = 80, MaxLength = 8, CharacterCasing = CharacterCasing.Upper };
    private readonly TextBox _dbPath = new() { Width = 340 };
    private readonly CheckBox _confirmDelete = new() { Text = "Ask for confirmation before deleting reports", AutoSize = true };
    private readonly CheckBox _touchMode = new() { Text = "Touch-friendly interface (larger text, rows and buttons)", AutoSize = true };

    public bool DatabaseChanged { get; private set; }

    public OptionsDialog(AppSettings settings)
    {
        _settings = settings;
        Text = "Options";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        UiMode.TrackDialog(this);
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 344);

        var tabs = new TabControl { Height = 300, Dock = DockStyle.Top };
        var general = new TabPage("General") { Padding = new Padding(12) };
        var data = new TabPage("Data Storage") { Padding = new Padding(12) };
        tabs.TabPages.Add(general);
        tabs.TabPages.Add(data);

        var g = NewGrid();
        AddRow(g, "Organization name:", _org, "Printed at the top of every report.");
        AddRow(g, "Default reporter name:", _reporter, "Pre-fills 'Reported By' and is recorded in the audit history.");
        AddRow(g, "Default department:", _department);
        AddRow(g, "Default location:", _location);
        AddRow(g, "Report number prefix:", _prefix, "Example: IR-2026-00001");
        g.Controls.Add(_confirmDelete, 1, g.RowCount++);
        g.Controls.Add(_touchMode, 1, g.RowCount++);
        general.Controls.Add(g);

        var d = NewGrid();
        var browse = new Button { Text = "Browse...", Width = 90 };
        browse.Click += (_, _) => BrowseDatabase();
        var pathRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        pathRow.Controls.Add(_dbPath);
        pathRow.Controls.Add(browse);
        AddRow(d, "Database file:", pathRow,
            "Leave blank to use the default location. To share reports between workstations, point this to a file on a network share.");
        var defaultLbl = new Label { Text = "Default: " + Path.Combine(AppSettings.AppDataFolder, "incidents.db"), AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(380, 0) };
        d.Controls.Add(defaultLbl, 1, d.RowCount++);
        data.Controls.Add(d);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(6) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        var ok = new Button { Text = "OK", Width = 90 };
        ok.Click += (_, _) => Apply();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(buttons);
        Controls.Add(tabs);
        AcceptButton = ok;
        CancelButton = cancel;

        _org.Text = settings.OrganizationName;
        _reporter.Text = settings.DefaultReporterName;
        _department.Text = settings.DefaultDepartment;
        _location.Text = settings.DefaultLocation;
        _prefix.Text = settings.ReportNumberPrefix;
        _dbPath.Text = settings.DatabasePath;
        _confirmDelete.Checked = settings.ConfirmOnDelete;
        _touchMode.Checked = UiMode.Touch;
    }

    private static TableLayoutPanel NewGrid()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    private static void AddRow(TableLayoutPanel t, string label, Control control, string? note = null)
    {
        var row = t.RowCount++;
        t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 4, 0) }, 0, row);
        control.Margin = new Padding(0, 3, 0, note == null ? 5 : 0);
        t.Controls.Add(control, 1, row);
        if (note != null)
        {
            var n = new Label { Text = note, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(380, 0), Margin = new Padding(0, 1, 0, 6) };
            t.Controls.Add(n, 1, t.RowCount++);
        }
    }

    private void BrowseDatabase()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Select or Create Database File",
            Filter = "Incident Database (*.db)|*.db|All Files (*.*)|*.*",
            OverwritePrompt = false,
            FileName = string.IsNullOrWhiteSpace(_dbPath.Text) ? "incidents.db" : _dbPath.Text
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _dbPath.Text = dlg.FileName;
    }

    private void Apply()
    {
        if (string.IsNullOrWhiteSpace(_prefix.Text) || !_prefix.Text.All(char.IsLetterOrDigit))
        {
            MessageBox.Show(this, "The report number prefix must contain letters or digits only.", "Options", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DatabaseChanged = !string.Equals(_settings.DatabasePath, _dbPath.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        _settings.OrganizationName = _org.Text.Trim();
        _settings.DefaultReporterName = _reporter.Text.Trim();
        _settings.DefaultDepartment = _department.Text.Trim();
        _settings.DefaultLocation = _location.Text.Trim();
        _settings.ReportNumberPrefix = _prefix.Text.Trim();
        _settings.DatabasePath = _dbPath.Text.Trim();
        _settings.ConfirmOnDelete = _confirmDelete.Checked;
        _settings.Save();
        DialogResult = DialogResult.OK;
        UiMode.SetTouch(_touchMode.Checked);
    }
}
