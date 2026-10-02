using IncidentReporting.Data;
using IncidentReporting.Models;
using IncidentReporting.Services;
using Shared.UI;

namespace IncidentReporting.UI;

/// <summary>Searchable list for choosing an existing report (used by report-link fields).</summary>
public sealed class ReportPickerDialog : Form
{
    private readonly TextBox _search = new() { Width = 300 };
    private readonly ListView _list = new();
    private readonly Button _ok = new() { Text = "OK", Width = 90, DialogResult = DialogResult.OK, Enabled = false };
    private readonly System.Windows.Forms.Timer _delay = new() { Interval = 250 };

    public IncidentReport? SelectedReport { get; private set; }

    public ReportPickerDialog()
    {
        Text = "Select Report";
        Size = new Size(820, 500);
        MinimumSize = new Size(600, 360);
        StartPosition = FormStartPosition.CenterParent;
        UiMode.TrackDialog(this);
        ShowIcon = false;
        ShowInTaskbar = false;
        MinimizeBox = false;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 6, 8, 0), WrapContents = false };
        top.Controls.Add(new Label { Text = "Search:", AutoSize = true, Margin = new Padding(0, 4, 4, 0) });
        top.Controls.Add(_search);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.GridLines = true;
        _list.Columns.Add("Report No.", 120);
        _list.Columns.Add("Title", 260);
        _list.Columns.Add("Form", 200);
        _list.Columns.Add("Incident Date", 95);
        _list.Columns.Add("Status", 90);
        _list.SelectedIndexChanged += (_, _) =>
        {
            SelectedReport = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as IncidentReport : null;
            _ok.Enabled = SelectedReport != null;
        };
        _list.DoubleClick += (_, _) => { if (SelectedReport != null) DialogResult = DialogResult.OK; };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8, 6, 8, 6) };
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);

        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 0) };
        listHost.Controls.Add(_list);
        Controls.Add(listHost);
        Controls.Add(buttons);
        Controls.Add(top);
        AcceptButton = _ok;
        CancelButton = cancel;

        _search.TextChanged += (_, _) => { _delay.Stop(); _delay.Start(); };
        _delay.Tick += (_, _) => { _delay.Stop(); LoadReports(); };
        LoadReports();
    }

    private void LoadReports()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in AppSession.Repository.Search(new ReportQuery { SearchText = _search.Text }).Take(500))
        {
            _list.Items.Add(new ListViewItem(new[]
            {
                r.ReportNumber, r.Title, ReportFormatter.FormName(r.FormCode), r.IncidentDate?.ToShortDateString() ?? "", r.Status.ToDisplay()
            }) { Tag = r });
        }
        _list.EndUpdate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _delay.Dispose();
        base.Dispose(disposing);
    }
}
