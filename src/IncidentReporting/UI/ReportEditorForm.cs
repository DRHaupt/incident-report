using IncidentReporting.Data;
using IncidentReporting.Models;
using IncidentReporting.Services;

namespace IncidentReporting.UI;

/// <summary>Data-entry window for a single incident report. Builds its pages from the form template.</summary>
public sealed class ReportEditorForm : Form
{
    private const string AttachmentsPage = "Attachments";
    private const string HistoryPage = "History";
    private const string LinkedPage = "Linked Reports";

    private readonly IncidentReport _report;
    private readonly FormDefinition _form;
    private readonly Dictionary<string, IFieldEditor> _editors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Control> _pages = new();
    private readonly List<TableLayoutPanel> _sectionTables = new();

    private readonly ListBox _nav = new();
    private readonly Panel _content = new();
    private readonly Label _headerTitle = new();
    private readonly Label _headerMeta = new();
    private readonly ToolStripButton _btnSubmit = new("Submit");
    private readonly ToolStripButton _btnReview = new("Begin Review");
    private readonly ToolStripButton _btnClose = new("Close Report");
    private readonly ToolStripButton _btnReopen = new("Reopen");
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ListView _attachments = new();
    private readonly ListView _history = new();
    private readonly ListView _linked = new();
    private readonly Label _attachmentsNote = new();
    private readonly Button _btnAddAttachment = new();
    private readonly Button _btnRemoveAttachment = new();

    private bool _dirty;
    private bool _loading;

    public event EventHandler? ReportChanged;

    /// <summary>Raised when the user asks to open another report (e.g. from the Linked Reports page).</summary>
    public event EventHandler<long>? OpenReportRequested;

    /// <summary>Raised when the user asks to start a follow-up report linked to this one.</summary>
    public event EventHandler? FollowUpRequested;

    public long ReportId => _report.Id;

    private static ReportRepository Repo => AppSession.Repository;

    public ReportEditorForm(IncidentReport report, FormDefinition form)
    {
        _report = report;
        _form = form;

        Text = "Incident Report";
        Size = new Size(1040, 760);
        MinimumSize = new Size(820, 540);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        Icon = Branding.AppIcon;

        BuildLayout();
        BuildPages();
        LoadValues();
        RefreshHeader();

        _nav.SelectedIndex = 0;
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        // Content area (added first so it fills the space left by docked chrome).
        _content.Dock = DockStyle.Fill;
        _content.BackColor = SystemColors.Window;
        Controls.Add(_content);

        var navHost = new Panel { Dock = DockStyle.Left, Width = 220, Padding = new Padding(6, 6, 0, 6), BackColor = SystemColors.Control };
        _nav.Dock = DockStyle.Fill;
        _nav.IntegralHeight = false;
        _nav.DrawMode = DrawMode.OwnerDrawFixed;
        _nav.ItemHeight = 22;
        _nav.DrawItem += DrawNavItem;
        _nav.SelectedIndexChanged += (_, _) => ShowPage(_nav.SelectedIndex);
        navHost.Controls.Add(_nav);
        Controls.Add(navHost);

        var splitter = new Splitter { Dock = DockStyle.Left, Width = 4 };
        Controls.Add(splitter);
        Controls.SetChildIndex(splitter, 1);

        // Bottom buttons.
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8) };
        var btnCancel = new Button { Text = "Close", Width = 90, Dock = DockStyle.Right };
        var btnSaveClose = new Button { Text = "Save && Close", Width = 110, Dock = DockStyle.Right };
        var btnSave = new Button { Text = "&Save", Width = 90, Dock = DockStyle.Right };
        var gap1 = new Panel { Width = 6, Dock = DockStyle.Right };
        var gap2 = new Panel { Width = 6, Dock = DockStyle.Right };
        var requiredNote = new Label
        {
            Text = "Fields marked with * are required before the report can be submitted.",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText
        };
        btnSave.Click += (_, _) => SaveReport();
        btnSaveClose.Click += (_, _) => { if (SaveReport()) Close(); };
        btnCancel.Click += (_, _) => Close();
        bottom.Controls.Add(requiredNote);
        bottom.Controls.Add(btnSave);
        bottom.Controls.Add(gap2);
        bottom.Controls.Add(btnSaveClose);
        bottom.Controls.Add(gap1);
        bottom.Controls.Add(btnCancel);
        CancelButton = btnCancel;
        Controls.Add(bottom);

        var status = new StatusStrip { SizingGrip = true };
        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        status.Items.Add(_statusLabel);
        Controls.Add(status);

        // Toolbar.
        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2), RenderMode = ToolStripRenderMode.System };
        var btnSaveTool = new ToolStripButton("Save") { ToolTipText = "Save (Ctrl+S)" };
        btnSaveTool.Click += (_, _) => SaveReport();
        var btnPreview = new ToolStripButton("Print Preview");
        btnPreview.Click += (_, _) => PrintPreview();
        var btnPrint = new ToolStripButton("Print...") { ToolTipText = "Print (Ctrl+P)" };
        btnPrint.Click += (_, _) => Print();
        var export = new ToolStripDropDownButton("Export");
        export.DropDownItems.Add("Export as HTML Document...", null, (_, _) => ExportHtml());
        export.DropDownItems.Add("Export Fields to CSV...", null, (_, _) => ExportCsv());
        _btnSubmit.ToolTipText = "Validate required fields and submit the report for review";
        _btnSubmit.Click += (_, _) => Submit();
        _btnReview.Click += (_, _) => ChangeStatus(ReportStatus.UnderReview, "Begin review of this report?");
        _btnClose.Click += (_, _) => ChangeStatus(ReportStatus.Closed, "Close this report? Closed reports are read-only until reopened.");
        _btnReopen.Click += (_, _) => ChangeStatus(ReportStatus.UnderReview, "Reopen this report for editing?");
        tools.Items.AddRange(new ToolStripItem[]
        {
            btnSaveTool, new ToolStripSeparator(), btnPreview, btnPrint, export, new ToolStripSeparator(),
            _btnSubmit, _btnReview, _btnClose, _btnReopen
        });
        Controls.Add(tools);

        // Header band.
        var header = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Branding.Navy, Padding = new Padding(14, 8, 14, 6) };
        _headerTitle.Dock = DockStyle.Top;
        _headerTitle.Height = 24;
        _headerTitle.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        _headerTitle.ForeColor = Color.White;
        _headerMeta.Dock = DockStyle.Top;
        _headerMeta.Height = 20;
        _headerMeta.ForeColor = Color.FromArgb(0xD0, 0xD8, 0xE4);
        header.Controls.Add(_headerMeta);
        header.Controls.Add(_headerTitle);
        header.Controls.Add(new PictureBox
        {
            Image = Branding.Logo48,
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Left,
            Width = 54,
            Padding = new Padding(0, 0, 12, 0)
        });
        Controls.Add(header);
    }

    private void DrawNavItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0)
            return;
        var text = _nav.Items[e.Index].ToString() ?? "";
        var selected = (e.State & DrawItemState.Selected) != 0;
        var bg = selected ? SystemColors.Highlight : SystemColors.Window;
        var fg = selected ? SystemColors.HighlightText : SystemColors.ControlText;
        using (var b = new SolidBrush(bg))
            e.Graphics.FillRectangle(b, e.Bounds);
        var isExtra = text is AttachmentsPage or HistoryPage or LinkedPage;
        if (isExtra && !selected)
            fg = SystemColors.GrayText;
        var rect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, _nav.Font, rect, fg, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void BuildPages()
    {
        foreach (var section in _form.Sections)
        {
            _nav.Items.Add(section.Title);
            var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false, Padding = new Padding(18, 12, 18, 18) };
            var table = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Location = new Point(18, 12),
                GrowStyle = TableLayoutPanelGrowStyle.AddRows
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var title = new Label
            {
                Text = section.Title,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Branding.Navy,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2)
            };
            table.Controls.Add(title, 0, 0);
            table.SetColumnSpan(title, 2);
            var rule = new Label { BorderStyle = BorderStyle.Fixed3D, Height = 2, Width = 700, Margin = new Padding(0, 2, 0, 10) };
            table.Controls.Add(rule, 0, 1);
            table.SetColumnSpan(rule, 2);

            var row = 2;
            foreach (var field in section.Fields)
            {
                var editor = FieldEditorFactory.Create(field);
                _editors[field.Key] = editor;
                editor.Changed += (_, _) => MarkDirty();

                var label = new Label
                {
                    Text = field.Label + (field.Required ? " *" : ""),
                    AutoSize = true,
                    MaximumSize = new Size(240, 0),
                    Padding = new Padding(0, 4, 0, 0),
                    Margin = new Padding(0, 3, 6, 3)
                };
                if (field.Required)
                    label.Font = new Font(label.Font, FontStyle.Bold);
                editor.Control.Margin = new Padding(0, 3, 0, 3);
                table.Controls.Add(label, 0, row);
                table.Controls.Add(editor.Control, 1, row);
                row++;

                if (!string.IsNullOrEmpty(field.Hint))
                {
                    var hint = new Label { Text = field.Hint, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 6), MaximumSize = new Size(560, 0) };
                    table.Controls.Add(hint, 1, row++);
                }
            }
            page.Controls.Add(table);
            _sectionTables.Add(table);
            _pages.Add(page);
            _content.Controls.Add(page);
        }

        _nav.Items.Add(AttachmentsPage);
        _pages.Add(BuildAttachmentsPage());
        _nav.Items.Add(LinkedPage);
        _pages.Add(BuildLinkedPage());
        _nav.Items.Add(HistoryPage);
        _pages.Add(BuildHistoryPage());
        foreach (var p in _pages.Skip(_form.Sections.Count))
            _content.Controls.Add(p);
    }

    private Control BuildAttachmentsPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(18, 12, 18, 12) };
        _attachments.Dock = DockStyle.Fill;
        _attachments.View = View.Details;
        _attachments.FullRowSelect = true;
        _attachments.GridLines = true;
        _attachments.HideSelection = false;
        _attachments.Columns.Add("File Name", 280);
        _attachments.Columns.Add("Description", 260);
        _attachments.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _attachments.Columns.Add("Added", 150);
        _attachments.DoubleClick += (_, _) => OpenAttachment();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0) };
        _btnAddAttachment.Text = "Add...";
        _btnAddAttachment.Width = 90;
        _btnAddAttachment.Click += (_, _) => AddAttachment();
        var btnOpen = new Button { Text = "Open", Width = 90 };
        btnOpen.Click += (_, _) => OpenAttachment();
        var btnSaveAs = new Button { Text = "Save As...", Width = 90 };
        btnSaveAs.Click += (_, _) => SaveAttachmentAs();
        _btnRemoveAttachment.Text = "Remove";
        _btnRemoveAttachment.Width = 90;
        _btnRemoveAttachment.Click += (_, _) => RemoveAttachment();
        buttons.Controls.AddRange(new Control[] { _btnAddAttachment, btnOpen, btnSaveAs, _btnRemoveAttachment });

        _attachmentsNote.Dock = DockStyle.Top;
        _attachmentsNote.Height = 46;
        _attachmentsNote.Text = "Photos, diagrams, statements and other supporting documents.";
        _attachmentsNote.Font = new Font("Segoe UI", 9f);

        var title = new Label { Text = AttachmentsPage, Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = Branding.Navy };
        page.Controls.Add(_attachments);
        page.Controls.Add(buttons);
        page.Controls.Add(_attachmentsNote);
        page.Controls.Add(title);
        return page;
    }

    private Control BuildLinkedPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(18, 12, 18, 12) };
        _linked.Dock = DockStyle.Fill;
        _linked.View = View.Details;
        _linked.FullRowSelect = true;
        _linked.GridLines = true;
        _linked.MultiSelect = false;
        _linked.Columns.Add("Relationship", 110);
        _linked.Columns.Add("Report No.", 120);
        _linked.Columns.Add("Form", 240);
        _linked.Columns.Add("Title", 260);
        _linked.Columns.Add("Status", 90);
        _linked.DoubleClick += (_, _) => OpenLinked();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(0, 6, 0, 0) };
        var btnOpen = new Button { Text = "Open", Width = 90 };
        btnOpen.Click += (_, _) => OpenLinked();
        var btnFollowUp = new Button { Text = "Create Follow-Up Report...", Width = 190 };
        btnFollowUp.Click += (_, _) =>
        {
            if (EnsureSaved("create a follow-up report"))
                FollowUpRequested?.Invoke(this, EventArgs.Empty);
        };
        buttons.Controls.AddRange(new Control[] { btnOpen, btnFollowUp });

        var note = new Label
        {
            Text = "Investigations, witness statements, corrective actions and other reports connected to this report.",
            Dock = DockStyle.Top, Height = 30
        };
        var title = new Label { Text = LinkedPage, Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = Branding.Navy };
        page.Controls.Add(_linked);
        page.Controls.Add(buttons);
        page.Controls.Add(note);
        page.Controls.Add(title);
        return page;
    }

    private void LoadLinked()
    {
        _linked.BeginUpdate();
        _linked.Items.Clear();
        if (_report.Id != 0)
        {
            var parentNumber = _report.Get(CommonKeys.RelatedReport);
            if (parentNumber.Length > 0 && Repo.GetByNumber(parentNumber) is { } parent)
                _linked.Items.Add(LinkedItem("Parent", parent));
            foreach (var child in Repo.GetLinkedTo(_report.ReportNumber))
                _linked.Items.Add(LinkedItem("Follow-Up", child));
        }
        _linked.EndUpdate();
    }

    private static ListViewItem LinkedItem(string relationship, IncidentReport r) =>
        new(new[] { relationship, r.ReportNumber, ReportFormatter.FormName(r.FormCode), r.Title, r.Status.ToDisplay() }) { Tag = r };

    private void OpenLinked()
    {
        if (_linked.SelectedItems.Count > 0 && _linked.SelectedItems[0].Tag is IncidentReport r)
            OpenReportRequested?.Invoke(this, r.Id);
    }

    public void RefreshLinked() => LoadLinked();

    private Control BuildHistoryPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(18, 12, 18, 12) };
        _history.Dock = DockStyle.Fill;
        _history.View = View.Details;
        _history.FullRowSelect = true;
        _history.GridLines = true;
        _history.Columns.Add("Date / Time", 150);
        _history.Columns.Add("User", 140);
        _history.Columns.Add("Action", 150);
        _history.Columns.Add("Details", 380);
        var title = new Label { Text = "Audit History", Dock = DockStyle.Top, Height = 34, Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = Branding.Navy };
        page.Controls.Add(_history);
        page.Controls.Add(title);
        return page;
    }

    private void ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Count)
            return;
        _content.SuspendLayout();
        for (var i = 0; i < _pages.Count; i++)
            _pages[i].Visible = i == index;
        _content.ResumeLayout();
        var name = _nav.Items[index].ToString();
        if (name == AttachmentsPage)
            LoadAttachments();
        else if (name == HistoryPage)
            LoadHistory();
        else if (name == LinkedPage)
            LoadLinked();
    }

    // ------------------------------------------------------------------ data

    private void LoadValues()
    {
        _loading = true;
        foreach (var (key, editor) in _editors)
            editor.Value = _report.Get(key);
        _loading = false;
        _dirty = false;
        ApplyReadOnly();
    }

    private void CollectValues()
    {
        foreach (var (key, editor) in _editors)
        {
            var v = editor.Value;
            if (string.IsNullOrEmpty(v))
                _report.Values.Remove(key);
            else
                _report.Values[key] = v;
        }
    }

    private void MarkDirty()
    {
        if (_loading || _dirty)
            return;
        _dirty = true;
        RefreshHeader();
    }

    private void RefreshHeader()
    {
        var number = _report.Id == 0 ? "(not yet saved)" : _report.ReportNumber;
        _headerTitle.Text = _form.Name;
        _headerMeta.Text = $"Report No. {number}     |     Status: {_report.Status.ToDisplay()}     |     Form {_form.Code}     |     {_form.Category}";
        Text = (_report.Id == 0 ? "New Report" : _report.ReportNumber) + " - " + _form.Name + (_dirty ? " *" : "");

        _btnSubmit.Enabled = _report.Status == ReportStatus.Draft;
        _btnReview.Enabled = _report.Status == ReportStatus.Submitted;
        _btnClose.Enabled = _report.Status is ReportStatus.Submitted or ReportStatus.UnderReview;
        _btnReopen.Enabled = _report.Status == ReportStatus.Closed;

        _statusLabel.Text = _report.Id == 0
            ? "New report - not yet saved."
            : $"Created {_report.CreatedUtc.ToLocalTime():g}     Last saved {_report.ModifiedUtc.ToLocalTime():g}" + (_dirty ? "     (unsaved changes)" : "");
    }

    private void ApplyReadOnly()
    {
        var readOnly = _report.Status == ReportStatus.Closed;
        foreach (var t in _sectionTables)
            t.Enabled = !readOnly;
        _btnAddAttachment.Enabled = !readOnly;
        _btnRemoveAttachment.Enabled = !readOnly;
    }

    private bool SaveReport()
    {
        if (_report.Status == ReportStatus.Closed)
        {
            MessageBox.Show(this, "This report is closed. Reopen it to make changes.", "Report Closed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        try
        {
            CollectValues();
            if (string.IsNullOrWhiteSpace(_report.Title))
            {
                MessageBox.Show(this, "Enter an incident title before saving.", "Save Report", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                FocusField(CommonKeys.Title);
                return false;
            }
            if (_report.Id == 0)
                _report.ReportNumber = Repo.NextReportNumber(AppSession.Settings.ReportNumberPrefix);
            Repo.Save(_report, _form.Category, AppSession.CurrentUser);
            _dirty = false;
            RefreshHeader();
            ReportChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The report could not be saved.\n\n" + ex.Message, "Save Report", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void Submit()
    {
        CollectValues();
        var missing = ReportFormatter.MissingRequired(_form, _report);
        if (missing.Count > 0)
        {
            var list = string.Join("\n", missing.Take(15).Select(m => "  - " + m));
            if (missing.Count > 15)
                list += $"\n  ... and {missing.Count - 15} more";
            MessageBox.Show(this, "The following required fields must be completed before submitting:\n\n" + list,
                "Required Fields Missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            var firstKey = _form.AllFields.First(f => f.Required && string.IsNullOrWhiteSpace(_report.Get(f.Key))).Key;
            FocusField(firstKey);
            return;
        }
        if (MessageBox.Show(this, "Submit this report for review?", "Submit Report", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        if (!SaveReport())
            return;
        Repo.SetStatus(_report, ReportStatus.Submitted, AppSession.CurrentUser);
        RefreshHeader();
        ReportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ChangeStatus(ReportStatus status, string prompt)
    {
        if (MessageBox.Show(this, prompt, "Change Status", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        if (_dirty && _report.Status != ReportStatus.Closed && !SaveReport())
            return;
        Repo.SetStatus(_report, status, AppSession.CurrentUser);
        ApplyReadOnly();
        RefreshHeader();
        ReportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FocusField(string key)
    {
        if (!_editors.TryGetValue(key, out var editor))
            return;
        var sectionIndex = _form.Sections.FindIndex(s => s.Fields.Any(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)));
        if (sectionIndex < 0)
            return;
        _nav.SelectedIndex = sectionIndex;
        editor.Control.Focus();
        if (_pages[sectionIndex] is ScrollableControl sc)
            sc.ScrollControlIntoView(editor.Control);
    }

    // ------------------------------------------------------------------ printing / export

    private IncidentReport Snapshot()
    {
        CollectValues();
        return _report;
    }

    private void PrintPreview()
    {
        using var printer = new ReportPrinter(Snapshot(), AppSession.Settings.OrganizationName);
        using var dlg = new PrintPreviewDialog { Document = printer.Document, Width = 900, Height = 1000, UseAntiAlias = true, ShowIcon = false };
        dlg.ShowDialog(this);
    }

    private void Print()
    {
        using var printer = new ReportPrinter(Snapshot(), AppSession.Settings.OrganizationName);
        using var dlg = new PrintDialog { Document = printer.Document, UseEXDialog = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            printer.Document.Print();
    }

    private void ExportHtml()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "HTML Document (*.html)|*.html",
            FileName = (_report.Id == 0 ? "New Report" : _report.ReportNumber) + ".html"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        File.WriteAllText(dlg.FileName, ExportService.BuildHtml(Snapshot(), AppSession.Settings.OrganizationName));
    }

    private void ExportCsv()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV File (*.csv)|*.csv",
            FileName = (_report.Id == 0 ? "New Report" : _report.ReportNumber) + ".csv"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            ExportService.ExportReportToCsv(Snapshot(), dlg.FileName);
    }

    // ------------------------------------------------------------------ attachments & history

    private bool EnsureSaved(string action)
    {
        if (_report.Id != 0)
            return true;
        if (MessageBox.Show(this, $"The report must be saved before you can {action}. Save now?", "Save Report",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return false;
        return SaveReport();
    }

    private void LoadAttachments()
    {
        _attachments.BeginUpdate();
        _attachments.Items.Clear();
        if (_report.Id != 0)
        {
            foreach (var a in Repo.GetAttachments(_report.Id))
            {
                var item = new ListViewItem(new[] { a.FileName, a.Description, FormatSize(a.Size), a.AddedUtc.ToLocalTime().ToString("g") }) { Tag = a };
                _attachments.Items.Add(item);
            }
        }
        _attachments.EndUpdate();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    private ReportRepository.AttachmentInfo? SelectedAttachment =>
        _attachments.SelectedItems.Count > 0 ? _attachments.SelectedItems[0].Tag as ReportRepository.AttachmentInfo : null;

    private void AddAttachment()
    {
        if (!EnsureSaved("add attachments"))
            return;
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Add Attachments",
            Filter = "All Supported Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.txt;*.rtf;*.msg;*.eml;*.mp4;*.mov|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        const long maxBytes = 50L * 1024 * 1024;
        foreach (var file in dlg.FileNames)
        {
            if (new FileInfo(file).Length > maxBytes)
            {
                MessageBox.Show(this, $"{Path.GetFileName(file)} exceeds the 50 MB attachment limit and was skipped.", "Add Attachments",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }
            Repo.AddAttachment(_report.Id, file, "", AppSession.CurrentUser);
        }
        LoadAttachments();
    }

    private void OpenAttachment()
    {
        if (SelectedAttachment is not { } a)
            return;
        var dir = Path.Combine(Path.GetTempPath(), "IncidentReportManager", a.Id.ToString());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, a.FileName);
        Repo.SaveAttachmentTo(a.Id, path);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Unable to open the file.\n\n" + ex.Message, "Open Attachment", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveAttachmentAs()
    {
        if (SelectedAttachment is not { } a)
            return;
        using var dlg = new SaveFileDialog { FileName = a.FileName, Filter = "All Files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            Repo.SaveAttachmentTo(a.Id, dlg.FileName);
    }

    private void RemoveAttachment()
    {
        if (SelectedAttachment is not { } a)
            return;
        if (MessageBox.Show(this, $"Remove attachment '{a.FileName}'?", "Remove Attachment", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        Repo.DeleteAttachment(_report.Id, a.Id, a.FileName, AppSession.CurrentUser);
        LoadAttachments();
    }

    private void LoadHistory()
    {
        _history.BeginUpdate();
        _history.Items.Clear();
        if (_report.Id != 0)
        {
            foreach (var h in Repo.GetAudit(_report.Id))
                _history.Items.Add(new ListViewItem(new[] { h.TimestampUtc.ToLocalTime().ToString("g"), h.User, h.Action, h.Details }));
        }
        _history.EndUpdate();
    }

    // ------------------------------------------------------------------ window events

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.S:
                SaveReport();
                return true;
            case Keys.Control | Keys.P:
                Print();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_dirty && _report.Status != ReportStatus.Closed && e.CloseReason == CloseReason.UserClosing)
        {
            var answer = MessageBox.Show(this, "Save changes to this report before closing?", "Unsaved Changes",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !SaveReport()))
                e.Cancel = true;
        }
        base.OnFormClosing(e);
    }
}
