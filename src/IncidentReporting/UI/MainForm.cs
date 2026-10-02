using System.Collections;
using System.Globalization;
using IncidentReporting.Data;
using IncidentReporting.Models;
using IncidentReporting.Services;
using IncidentReporting.Templates;

namespace IncidentReporting.UI;

public sealed class MainForm : Form
{
    private readonly TreeView _tree = new();
    private readonly ListView _list = new();
    private readonly WebBrowser _preview = new();
    private readonly SplitContainer _rightSplit = new();
    private readonly ToolStripTextBox _search = new();
    private readonly ToolStripStatusLabel _countLabel = new();
    private readonly ToolStripStatusLabel _dbLabel = new();
    private readonly ToolStripStatusLabel _userLabel = new();
    private readonly ToolStripMenuItem _previewMenu = new("&Preview Pane");
    private readonly System.Windows.Forms.Timer _searchDelay = new() { Interval = 300 };
    private readonly Dictionary<long, ReportEditorForm> _openEditors = new();
    private readonly HashSet<ReportEditorForm> _allEditors = new();

    private List<IncidentReport> _reports = new();
    private int _sortColumn = -1;
    private bool _sortDescending;

    private static ReportRepository Repo => AppSession.Repository;

    public MainForm()
    {
        Text = Branding.ProductName;
        Icon = Branding.AppIcon;
        Size = new Size(1280, 800);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildLayout();
        BuildTree();
        UpdateStatusBar();
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); RefreshList(); };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _tree.SelectedNode = _tree.Nodes[0];
        RefreshList();
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 5 };
        Controls.Add(split);

        _tree.Dock = DockStyle.Fill;
        _tree.HideSelection = false;
        _tree.FullRowSelect = true;
        _tree.ShowLines = false;
        _tree.ItemHeight = 22;
        _tree.BorderStyle = BorderStyle.None;
        _tree.AfterSelect += (_, _) => RefreshList();
        var treeHeader = new Label
        {
            Text = "  Report Views", Dock = DockStyle.Top, Height = 26, TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Branding.Navy, ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        split.Panel1.Controls.Add(_tree);
        split.Panel1.Controls.Add(treeHeader);
        split.Panel1.BackColor = SystemColors.Window;

        _rightSplit.Dock = DockStyle.Fill;
        _rightSplit.Orientation = Orientation.Horizontal;
        _rightSplit.SplitterWidth = 5;
        split.Panel2.Controls.Add(_rightSplit);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.HideSelection = false;
        _list.MultiSelect = true;
        _list.BorderStyle = BorderStyle.None;
        foreach (var (name, width, align) in new (string, int, HorizontalAlignment)[]
                 {
                     ("Report No.", 120, HorizontalAlignment.Left), ("Title", 260, HorizontalAlignment.Left),
                     ("Form", 200, HorizontalAlignment.Left), ("Incident Date", 95, HorizontalAlignment.Left),
                     ("Severity", 75, HorizontalAlignment.Left), ("Status", 95, HorizontalAlignment.Left),
                     ("Location", 160, HorizontalAlignment.Left), ("Reported By", 130, HorizontalAlignment.Left),
                     ("Last Modified", 125, HorizontalAlignment.Left)
                 })
            _list.Columns.Add(name, width, align);
        _list.ColumnClick += (_, e) => SortBy(e.Column);
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.SelectedIndexChanged += (_, _) => UpdatePreview();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; }
            if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
        };
        _list.ContextMenuStrip = BuildListContextMenu();
        _rightSplit.Panel1.Controls.Add(_list);

        _preview.Dock = DockStyle.Fill;
        _preview.AllowWebBrowserDrop = false;
        _preview.IsWebBrowserContextMenuEnabled = false;
        _preview.WebBrowserShortcutsEnabled = false;
        _preview.ScriptErrorsSuppressed = true;
        _preview.DocumentText = BlankPreview();
        _rightSplit.Panel2.Controls.Add(_preview);

        Controls.Add(BuildToolStrip());
        Controls.Add(BuildMenu());

        var status = new StatusStrip();
        _countLabel.Spring = true;
        _countLabel.TextAlign = ContentAlignment.MiddleLeft;
        _dbLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _userLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
        status.Items.AddRange(new ToolStripItem[] { _countLabel, _dbLabel, _userLabel });
        Controls.Add(status);

        Load += (_, _) =>
        {
            split.SplitterDistance = 230;
            _rightSplit.SplitterDistance = (int)(_rightSplit.Height * 0.55);
        };
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Item("&New Report...", Keys.Control | Keys.N, NewReport));
        file.DropDownItems.Add(Item("&Open Report", Keys.Control | Keys.O, OpenSelected));
        file.DropDownItems.Add(Item("Create &Follow-Up Report...", Keys.Control | Keys.Shift | Keys.N, () => { if (SelectedReport is { } r) CreateFollowUp(r); }));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Print Pre&view...", Keys.None, () => PrintSelected(preview: true)));
        file.DropDownItems.Add(Item("&Print...", Keys.Control | Keys.P, () => PrintSelected(preview: false)));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Export Report as &HTML...", Keys.None, ExportSelectedHtml));
        file.DropDownItems.Add(Item("Export List to &CSV...", Keys.None, ExportListCsv));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("&Back Up Database...", Keys.None, BackupDatabase));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("E&xit", Keys.Alt | Keys.F4, Close));

        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.Add(Item("&Delete Report", Keys.None, DeleteSelected));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("Select &All", Keys.Control | Keys.A, SelectAll));
        edit.DropDownItems.Add(Item("&Find...", Keys.Control | Keys.F, () => _search.Focus()));

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(Item("&Refresh", Keys.F5, () => { BuildTree(); RefreshList(); }));
        _previewMenu.Checked = true;
        _previewMenu.Click += (_, _) =>
        {
            _previewMenu.Checked = !_previewMenu.Checked;
            _rightSplit.Panel2Collapsed = !_previewMenu.Checked;
        };
        view.DropDownItems.Add(_previewMenu);

        var reports = new ToolStripMenuItem("&Reports");
        foreach (var category in FormCatalog.Categories)
        {
            var catItem = new ToolStripMenuItem(category);
            foreach (var f in FormCatalog.All.Where(f => f.Category == category))
            {
                var form = f;
                catItem.DropDownItems.Add($"{f.Code}  {f.Name}", null, (_, _) => CreateReport(form));
            }
            reports.DropDownItems.Add(catItem);
        }

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add(Item("&Options...", Keys.None, ShowOptions));
        tools.DropDownItems.Add(Item("Open &Data Folder", Keys.None, () =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppSettings.AppDataFolder) { UseShellExecute = true })));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Item("&About Incident Report Manager", Keys.None, () => { using var d = new AboutDialog(); d.ShowDialog(this); }));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, view, reports, tools, help });
        MainMenuStrip = menu;
        return menu;
    }

    private static ToolStripMenuItem Item(string text, Keys shortcut, Action action)
    {
        var item = new ToolStripMenuItem(text);
        if (shortcut != Keys.None)
            item.ShortcutKeys = shortcut;
        item.Click += (_, _) => action();
        return item;
    }

    private ToolStrip BuildToolStrip()
    {
        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 3, 6, 3), RenderMode = ToolStripRenderMode.System };
        ToolStripButton Btn(string text, string tip, Action a)
        {
            var b = new ToolStripButton(text) { ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.Text, Padding = new Padding(4, 0, 4, 0) };
            b.Click += (_, _) => a();
            return b;
        }

        tools.Items.Add(Btn("New Report", "Create a new incident report (Ctrl+N)", NewReport));
        tools.Items.Add(Btn("Open", "Open the selected report", OpenSelected));
        tools.Items.Add(Btn("Delete", "Delete the selected report(s)", DeleteSelected));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(Btn("Print Preview", "Preview the selected report", () => PrintSelected(preview: true)));
        tools.Items.Add(Btn("Print", "Print the selected report (Ctrl+P)", () => PrintSelected(preview: false)));
        tools.Items.Add(Btn("Export List", "Export the current list to CSV", ExportListCsv));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(Btn("Refresh", "Refresh (F5)", () => { BuildTree(); RefreshList(); }));

        _search.Alignment = ToolStripItemAlignment.Right;
        _search.Width = 240;
        _search.AutoSize = false;
        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.ToolTipText = "Search report number, title, location, reporter and all field contents";
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { _search.Clear(); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Enter) { _searchDelay.Stop(); RefreshList(); e.SuppressKeyPress = true; }
        };
        tools.Items.Add(_search);
        tools.Items.Add(new ToolStripLabel("Search:") { Alignment = ToolStripItemAlignment.Right });
        return tools;
    }

    private ContextMenuStrip BuildListContextMenu()
    {
        var cm = new ContextMenuStrip();
        cm.Items.Add("Open", null, (_, _) => OpenSelected());
        cm.Items.Add("Create Follow-Up Report...", null, (_, _) => { if (SelectedReport is { } r) CreateFollowUp(r); });
        cm.Items.Add(new ToolStripSeparator());
        cm.Items.Add("Print Preview", null, (_, _) => PrintSelected(preview: true));
        cm.Items.Add("Print...", null, (_, _) => PrintSelected(preview: false));
        cm.Items.Add("Export as HTML...", null, (_, _) => ExportSelectedHtml());
        cm.Items.Add(new ToolStripSeparator());
        cm.Items.Add("Delete", null, (_, _) => DeleteSelected());
        return cm;
    }

    // ------------------------------------------------------------------ navigation tree

    private sealed record TreeFilter(ReportStatus? Status = null, string? Category = null, int? LastDays = null, bool ThisYear = false);

    private void BuildTree()
    {
        var selectedPath = _tree.SelectedNode?.FullPath;
        var counts = Repo.CountByStatus();
        var total = counts.Values.Sum();

        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        _tree.Nodes.Add(new TreeNode($"All Reports ({total})") { Tag = new TreeFilter(), Name = "all" });

        var status = new TreeNode("By Status") { Tag = new TreeFilter() };
        foreach (var s in Enum.GetValues<ReportStatus>())
            status.Nodes.Add(new TreeNode($"{s.ToDisplay()} ({counts[s]})") { Tag = new TreeFilter(Status: s) });
        _tree.Nodes.Add(status);

        var date = new TreeNode("By Incident Date") { Tag = new TreeFilter() };
        date.Nodes.Add(new TreeNode("Today") { Tag = new TreeFilter(LastDays: 0) });
        date.Nodes.Add(new TreeNode("Last 7 Days") { Tag = new TreeFilter(LastDays: 7) });
        date.Nodes.Add(new TreeNode("Last 30 Days") { Tag = new TreeFilter(LastDays: 30) });
        date.Nodes.Add(new TreeNode("Last 90 Days") { Tag = new TreeFilter(LastDays: 90) });
        date.Nodes.Add(new TreeNode("This Year") { Tag = new TreeFilter(ThisYear: true) });
        _tree.Nodes.Add(date);

        var cats = new TreeNode("By Category") { Tag = new TreeFilter() };
        foreach (var c in FormCatalog.Categories)
            cats.Nodes.Add(new TreeNode(c) { Tag = new TreeFilter(Category: c) });
        _tree.Nodes.Add(cats);

        _tree.ExpandAll();
        _tree.EndUpdate();

        if (selectedPath != null)
        {
            // Labels carry counts, so match on the label text before the count.
            static string Stem(string p) => p.Split(" (")[0];
            var match = AllNodes(_tree.Nodes).FirstOrDefault(n => Stem(n.FullPath) == Stem(selectedPath));
            if (match != null)
                _tree.SelectedNode = match;
        }
        UpdateStatusBar();
    }

    private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes)
        {
            yield return n;
            foreach (var c in AllNodes(n.Nodes))
                yield return c;
        }
    }

    // ------------------------------------------------------------------ list

    private void RefreshList()
    {
        var filter = _tree.SelectedNode?.Tag as TreeFilter ?? new TreeFilter();
        var q = new ReportQuery { Status = filter.Status, Category = filter.Category, SearchText = _search.Text };
        if (filter.LastDays is { } days)
            q.From = DateTime.Today.AddDays(-days);
        if (filter.ThisYear)
            q.From = new DateTime(DateTime.Today.Year, 1, 1);

        try
        {
            _reports = Repo.Search(q);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Unable to read the report database.\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            _reports = new List<IncidentReport>();
        }

        var selectedIds = SelectedReports().Select(r => r.Id).ToHashSet();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in _reports)
        {
            var item = new ListViewItem(new[]
            {
                r.ReportNumber, r.Title, ReportFormatter.FormName(r.FormCode),
                r.IncidentDate?.ToShortDateString() ?? "", r.Severity, r.Status.ToDisplay(), r.Location, r.ReportedBy,
                r.ModifiedUtc.ToLocalTime().ToString("g")
            }) { Tag = r, UseItemStyleForSubItems = false };
            if (r.Severity is "Critical" or "High")
                item.SubItems[4].ForeColor = r.Severity == "Critical" ? Color.DarkRed : Color.FromArgb(0xB0, 0x50, 0x00);
            if (r.Status == ReportStatus.Draft)
                item.SubItems[5].ForeColor = SystemColors.GrayText;
            item.Selected = selectedIds.Contains(r.Id);
            _list.Items.Add(item);
        }
        if (_sortColumn >= 0)
            _list.ListViewItemSorter = new ItemComparer(_sortColumn, _sortDescending);
        _list.EndUpdate();
        UpdateStatusBar();
        UpdatePreview();
    }

    private void SortBy(int column)
    {
        _sortDescending = column == _sortColumn && !_sortDescending;
        _sortColumn = column;
        _list.ListViewItemSorter = new ItemComparer(column, _sortDescending);
        _list.Sort();
    }

    private sealed class ItemComparer : IComparer
    {
        private readonly int _column;
        private readonly bool _descending;
        public ItemComparer(int column, bool descending) { _column = column; _descending = descending; }

        public int Compare(object? x, object? y)
        {
            if (x is not ListViewItem a || y is not ListViewItem b || a.Tag is not IncidentReport ra || b.Tag is not IncidentReport rb)
                return 0;
            int result = _column switch
            {
                3 => Nullable.Compare(ra.IncidentDate, rb.IncidentDate),
                4 => SeverityRank(ra.Severity).CompareTo(SeverityRank(rb.Severity)),
                5 => ra.Status.CompareTo(rb.Status),
                8 => ra.ModifiedUtc.CompareTo(rb.ModifiedUtc),
                _ => string.Compare(a.SubItems[_column].Text, b.SubItems[_column].Text, StringComparison.CurrentCultureIgnoreCase)
            };
            return _descending ? -result : result;
        }

        private static int SeverityRank(string s) => Array.IndexOf(CommonSections.Severity, s);
    }

    private List<IncidentReport> SelectedReports() =>
        _list.SelectedItems.Cast<ListViewItem>().Select(i => (IncidentReport)i.Tag!).ToList();

    private IncidentReport? SelectedReport =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as IncidentReport : null;

    private void SelectAll()
    {
        if (_search.Focused)
        {
            _search.SelectAll();
            return;
        }
        _list.BeginUpdate();
        foreach (ListViewItem i in _list.Items)
            i.Selected = true;
        _list.EndUpdate();
    }

    private void UpdatePreview()
    {
        if (_rightSplit.Panel2Collapsed)
            return;
        var r = SelectedReport;
        _preview.DocumentText = r == null ? BlankPreview() : ExportService.BuildHtml(r, AppSession.Settings.OrganizationName);
    }

    private static string BlankPreview() =>
        "<html><head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"></head>" +
        "<body style=\"font-family:Segoe UI;font-size:9pt;color:#777;margin:0;text-align:center\">" +
        $"<div style=\"margin-top:40px\"><img src=\"{Branding.LogoDataUri(128)}\" width=\"96\" height=\"96\" alt=\"\"></div>" +
        $"<div style=\"font-size:13pt;font-weight:600;color:#2B3A4F;margin-top:10px\">{Branding.ProductName}</div>" +
        "<div style=\"margin-top:6px\">Select a report to preview it.</div></body></html>";

    private void UpdateStatusBar()
    {
        _countLabel.Text = $"{_list.Items.Count} report(s) shown";
        _dbLabel.Text = "Database: " + AppSession.Settings.EffectiveDatabasePath;
        _userLabel.Text = "User: " + AppSession.CurrentUser;
    }

    // ------------------------------------------------------------------ commands

    private void NewReport()
    {
        using var dlg = new NewReportDialog();
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedForm != null)
            CreateReport(dlg.SelectedForm);
    }

    /// <summary>Starts a new report linked to an existing one, copying its incident details.</summary>
    private void CreateFollowUp(IncidentReport source)
    {
        if (source.Id == 0)
            return;
        using var dlg = new NewReportDialog { InitialCategory = "Investigations & Follow-Up" };
        dlg.Text = $"Create Follow-Up Report for {source.ReportNumber}";
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedForm == null)
            return;
        CreateReport(dlg.SelectedForm, source);
    }

    private void CreateReport(FormDefinition form, IncidentReport? linkedFrom = null)
    {
        var s = AppSession.Settings;
        var today = DateTime.Today.ToString(FieldFormats.Date, CultureInfo.InvariantCulture);
        var report = new IncidentReport { FormCode = form.Code, Status = ReportStatus.Draft };
        report.Values[CommonKeys.IncidentDate] = today;
        report.Values["date_reported"] = today;
        if (!string.IsNullOrWhiteSpace(s.DefaultReporterName))
            report.Values[CommonKeys.ReporterName] = s.DefaultReporterName;
        if (!string.IsNullOrWhiteSpace(s.DefaultDepartment))
            report.Values[CommonKeys.Department] = s.DefaultDepartment;
        if (!string.IsNullOrWhiteSpace(s.DefaultLocation))
            report.Values[CommonKeys.Location] = s.DefaultLocation;

        if (linkedFrom != null)
        {
            // Carry the incident details across so the follow-up starts out describing the same event.
            foreach (var key in new[] { CommonKeys.Title, CommonKeys.IncidentDate, CommonKeys.IncidentTime, "shift", CommonKeys.Location, "specific_area", CommonKeys.Department, CommonKeys.Severity })
            {
                var v = linkedFrom.Get(key);
                if (v.Length > 0)
                    report.Values[key] = v;
            }
            if (form.AllFields.Any(f => f.Key == CommonKeys.RelatedReport))
                report.Values[CommonKeys.RelatedReport] = linkedFrom.ReportNumber;
        }
        ShowEditor(report, form);
    }

    private void OpenSelected()
    {
        if (SelectedReport is { } selected)
            OpenReport(selected.Id);
    }

    private void OpenReport(long id)
    {
        if (_openEditors.TryGetValue(id, out var existing))
        {
            existing.Activate();
            return;
        }
        var report = Repo.Get(id);
        if (report == null)
        {
            RefreshList();
            return;
        }
        var form = FormCatalog.Find(report.FormCode);
        if (form == null)
        {
            MessageBox.Show(this, $"The form template '{report.FormCode}' is not available in this version.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        ShowEditor(report, form);
    }

    private void ShowEditor(IncidentReport report, FormDefinition form)
    {
        var editor = new ReportEditorForm(report, form);
        if (report.Id != 0)
            _openEditors[report.Id] = editor;
        editor.ReportChanged += (_, _) =>
        {
            _openEditors[report.Id] = editor;
            foreach (var other in _allEditors)
                other.RefreshLinked();
            BuildTree();
            RefreshList();
        };
        editor.OpenReportRequested += (_, id) => OpenReport(id);
        editor.FollowUpRequested += (_, _) => CreateFollowUp(report);
        editor.FormClosed += (_, _) =>
        {
            if (report.Id != 0)
                _openEditors.Remove(report.Id);
            _allEditors.Remove(editor);
            editor.Dispose();
        };
        _allEditors.Add(editor);
        editor.Show(this);
    }

    private void DeleteSelected()
    {
        var selected = SelectedReports();
        if (selected.Count == 0)
            return;
        if (selected.Any(r => _openEditors.ContainsKey(r.Id)))
        {
            MessageBox.Show(this, "Close the report window before deleting the report.", "Delete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (AppSession.Settings.ConfirmOnDelete)
        {
            var msg = selected.Count == 1
                ? $"Permanently delete report {selected[0].ReportNumber}, including its attachments and history?"
                : $"Permanently delete {selected.Count} reports, including their attachments and history?";
            if (MessageBox.Show(this, msg, "Delete Report", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
        }
        foreach (var r in selected)
            Repo.Delete(r.Id);
        BuildTree();
        RefreshList();
    }

    private void PrintSelected(bool preview)
    {
        if (SelectedReport is not { } r)
            return;
        using var printer = new ReportPrinter(r, AppSession.Settings.OrganizationName);
        if (preview)
        {
            using var dlg = new PrintPreviewDialog { Document = printer.Document, Width = 900, Height = 1000, UseAntiAlias = true, ShowIcon = false };
            dlg.ShowDialog(this);
        }
        else
        {
            using var dlg = new PrintDialog { Document = printer.Document, UseEXDialog = true };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                printer.Document.Print();
        }
    }

    private void ExportSelectedHtml()
    {
        if (SelectedReport is not { } r)
            return;
        using var dlg = new SaveFileDialog { Filter = "HTML Document (*.html)|*.html", FileName = r.ReportNumber + ".html" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            File.WriteAllText(dlg.FileName, ExportService.BuildHtml(r, AppSession.Settings.OrganizationName));
    }

    private void ExportListCsv()
    {
        using var dlg = new SaveFileDialog { Filter = "CSV File (*.csv)|*.csv", FileName = $"Incident Reports {DateTime.Today:yyyy-MM-dd}.csv" };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        var rows = _list.Items.Cast<ListViewItem>().Select(i => (IncidentReport)i.Tag!);
        ExportService.ExportListToCsv(rows, dlg.FileName);
        MessageBox.Show(this, $"{_list.Items.Count} report(s) exported.", "Export List", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void BackupDatabase()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "Incident Database (*.db)|*.db",
            FileName = $"incidents-backup-{DateTime.Now:yyyyMMdd-HHmm}.db"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            Repo.Backup(dlg.FileName);
            MessageBox.Show(this, "Backup completed.", "Back Up Database", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Backup failed.\n\n" + ex.Message, "Back Up Database", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowOptions()
    {
        using var dlg = new OptionsDialog(AppSession.Settings);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        if (dlg.DatabaseChanged)
        {
            if (_allEditors.Count > 0)
                MessageBox.Show(this, "The new database location will be used for reports opened from now on.", "Options", MessageBoxButtons.OK, MessageBoxIcon.Information);
            try
            {
                AppSession.ReloadRepository();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Unable to open the selected database.\n\n" + ex.Message, "Options", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        BuildTree();
        RefreshList();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Close report windows first so each can prompt to save unsaved work.
        foreach (var editor in _allEditors.ToList())
        {
            editor.Close();
            if (!editor.IsDisposed && editor.Visible)
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnFormClosing(e);
    }
}
