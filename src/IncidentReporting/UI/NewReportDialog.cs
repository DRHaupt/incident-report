using IncidentReporting.Models;
using IncidentReporting.Services;
using IncidentReporting.Templates;

namespace IncidentReporting.UI;

/// <summary>Lets the user pick a report template by category.</summary>
public sealed class NewReportDialog : Form
{
    private const string AllCategories = "(All Forms)";

    private readonly ListBox _categories = new();
    private readonly ListView _forms = new();
    private readonly TextBox _filter = new();
    private readonly Label _description = new();
    private readonly Button _ok = new();

    public FormDefinition? SelectedForm { get; private set; }

    /// <summary>Category to select when the dialog opens; defaults to all forms.</summary>
    public string? InitialCategory { get; set; }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (InitialCategory != null && _categories.Items.IndexOf(InitialCategory) is var idx and >= 0)
            _categories.SelectedIndex = idx;
    }

    public NewReportDialog()
    {
        Text = "New Incident Report";
        Size = new Size(860, 560);
        MinimumSize = new Size(700, 420);
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(10) };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));

        main.Controls.Add(new Label { Text = "Category:", AutoSize = true, Margin = new Padding(0, 6, 0, 4) }, 0, 0);

        var filterRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(6, 0, 0, 0), WrapContents = false };
        filterRow.Controls.Add(new Label { Text = "Find form:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        _filter.Width = 260;
        _filter.TextChanged += (_, _) => LoadForms();
        filterRow.Controls.Add(_filter);
        main.Controls.Add(filterRow, 1, 0);

        _categories.Dock = DockStyle.Fill;
        _categories.IntegralHeight = false;
        _categories.Items.Add(AllCategories);
        foreach (var c in FormCatalog.Categories)
            _categories.Items.Add(c);
        _categories.SelectedIndexChanged += (_, _) => LoadForms();
        main.Controls.Add(_categories, 0, 1);

        _forms.Dock = DockStyle.Fill;
        _forms.View = View.Details;
        _forms.FullRowSelect = true;
        _forms.MultiSelect = false;
        _forms.HideSelection = false;
        _forms.Margin = new Padding(6, 3, 0, 3);
        _forms.Columns.Add("Form No.", 90);
        _forms.Columns.Add("Form Name", 300);
        _forms.Columns.Add("Category", 180);
        _forms.SelectedIndexChanged += (_, _) => OnFormSelected();
        _forms.DoubleClick += (_, _) => { if (SelectedForm != null) DialogResult = DialogResult.OK; };
        main.Controls.Add(_forms, 1, 1);

        _description.Dock = DockStyle.Fill;
        _description.BorderStyle = BorderStyle.FixedSingle;
        _description.BackColor = SystemColors.Info;
        _description.ForeColor = SystemColors.InfoText;
        _description.Padding = new Padding(6);
        _description.Margin = new Padding(0, 6, 0, 0);
        main.Controls.Add(_description, 0, 2);
        main.SetColumnSpan(_description, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8, 6, 8, 6) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        _ok.Text = "Create";
        _ok.Width = 90;
        _ok.DialogResult = DialogResult.OK;
        _ok.Enabled = false;
        var printBlank = new Button { Text = "Print Blank Form...", Width = 140 };
        printBlank.Click += (_, _) => PrintBlank();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(printBlank);

        Controls.Add(main);
        Controls.Add(buttons);
        AcceptButton = _ok;
        CancelButton = cancel;

        _categories.SelectedIndex = 0;
    }

    private void LoadForms()
    {
        var category = _categories.SelectedItem as string;
        var filter = _filter.Text.Trim();
        _forms.BeginUpdate();
        _forms.Items.Clear();
        foreach (var f in FormCatalog.All)
        {
            if (category != null && category != AllCategories && f.Category != category)
                continue;
            if (filter.Length > 0 &&
                !f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !f.Code.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !f.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            _forms.Items.Add(new ListViewItem(new[] { f.Code, f.Name, f.Category }) { Tag = f });
        }
        _forms.EndUpdate();
        if (_forms.Items.Count > 0)
            _forms.Items[0].Selected = true;
        OnFormSelected();
    }

    private void OnFormSelected()
    {
        SelectedForm = _forms.SelectedItems.Count > 0 ? _forms.SelectedItems[0].Tag as FormDefinition : null;
        _ok.Enabled = SelectedForm != null;
        if (SelectedForm == null)
        {
            _description.Text = "";
            return;
        }
        var fieldCount = SelectedForm.AllFields.Count();
        _description.Text = $"{SelectedForm.Code}  {SelectedForm.Name}\r\n{SelectedForm.Description}\r\n" +
                            $"{SelectedForm.Sections.Count} sections, {fieldCount} fields.";
    }

    private void PrintBlank()
    {
        if (SelectedForm == null)
            return;
        var blank = new IncidentReport { FormCode = SelectedForm.Code, ReportNumber = "____________" };
        using var printer = new ReportPrinter(blank, AppSession.Settings.OrganizationName);
        using var dlg = new PrintPreviewDialog { Document = printer.Document, Width = 900, Height = 1000, UseAntiAlias = true, ShowIcon = false };
        dlg.ShowDialog(this);
    }
}
