using System.ComponentModel;
using FieldToolkit.Core;
using Shared.Core.Geo;
using FieldToolkit.Core.Radio;
using Shared.Core.Radio;
using FieldToolkit.Framework;
using FieldToolkit.UI;
using Shared;
using Shared.UI;

namespace FieldToolkit.Tools.Radio;

/// <summary>
/// Radio &amp; comms reference: phonetic alphabet with a speller, standard phrases and report formats, the user's own
/// channel plan and call-sign list (editable, CSV import/export), and a quick time-stamped radio log.
/// </summary>
public sealed class RadioWindow : ToolWindow
{
    public const string ToolId = "radio";

    private enum Tab { Phonetic, Phrases, Channels, CallSigns, Log }

    private static readonly string[] TabNames = { "Phonetic", "Phrases", "Channels", "Call signs", "Log" };

    private readonly Button[] _tabs = new Button[5];
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private Tab _tab;

    public RadioWindow(string? state) : base(ToolRegistry.Find(ToolId)!)
    {
        var tabs = new TableLayoutPanel { Dock = DockStyle.Top, Height = 58, ColumnCount = 5, RowCount = 1, Padding = new Padding(8, 6, 8, 2) };
        tabs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++)
        {
            var tab = (Tab)i;
            tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            _tabs[i] = Ui.Button(TabNames[i], ButtonStyle.Secondary, () => ShowTab(tab));
            _tabs[i].Dock = DockStyle.Fill;
            _tabs[i].Margin = new Padding(2, 0, 2, 0);
            tabs.Controls.Add(_tabs[i], i, 0);
        }
        Body.Controls.Add(_content);
        Body.Controls.Add(tabs);
        FormClosed += (_, _) => AppServices.RadioLog.Changed -= OnLogChanged;
        ShowTab(Enum.TryParse<Tab>(state, out var t) ? t : Tab.Phonetic);
    }

    public override string SaveState() => _tab.ToString();

    private void ShowTab(Tab tab)
    {
        AppServices.RadioLog.Changed -= OnLogChanged;
        _tab = tab;
        for (var i = 0; i < _tabs.Length; i++)
            Ui.SetSelected(_tabs[i], i == (int)tab);
        SetTitle(tab == Tab.Phonetic ? Tool.Title : $"{TabNames[(int)tab]} – Radio");
        _content.SuspendLayout();
        foreach (Control c in _content.Controls.Cast<Control>().ToList())
            c.Dispose();
        _content.Controls.Clear();
        switch (tab)
        {
            case Tab.Phonetic: BuildPhonetic(); break;
            case Tab.Phrases: BuildPhrases(); break;
            case Tab.Channels: BuildGrid(AppServices.Channels, "Channel plan", new[] { ("Channel", "Ch", 60), ("Name", "Name", 140), ("RxFrequency", "RX MHz", 100), ("TxFrequency", "TX MHz", 100), ("Tone", "CTCSS/DCS", 90), ("Use", "Use", 160), ("Notes", "Notes", 200) }); break;
            case Tab.CallSigns: BuildGrid(AppServices.CallSigns, "Call signs", new[] { ("Sign", "Call sign", 110), ("Who", "Who / role", 170), ("Location", "Location / vehicle", 150), ("Channel", "Channel", 80), ("Phone", "Phone", 120), ("Notes", "Notes", 200) }); break;
            default: BuildLog(); break;
        }
        UiMode.Apply(_content);
        _content.ResumeLayout(true);
    }

    // ------------------------------------------------------------------ phonetic alphabet and speller

    private void BuildPhonetic()
    {
        var input = new TextBox { Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Type a name, registration or code to spell it" };
        input.Styled(FontRole.Large);
        var spelled = Ui.Text("", FontRole.Title, Brand.Navy);
        var said = Ui.Text("", FontRole.Large, Brand.Muted);
        var top = new VStack { Dock = DockStyle.Top, Padding = new Padding(10, 8, 10, 6) };
        top.Controls.Add(input);
        top.Controls.Add(spelled);
        top.Controls.Add(said);
        input.TextChanged += (_, _) =>
        {
            spelled.Text = Phonetic.Spell(input.Text);
            said.Text = input.Text.Length == 0 ? "" : "Say: " + Phonetic.Spell(input.Text, pronunciation: true);
        };

        var grid = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
        foreach (var e in Phonetic.Letters.Concat(Phonetic.Figures))
            grid.Controls.Add(LetterTile(e));
        _content.Controls.Add(grid);
        _content.Controls.Add(top);
        FocusLater(input);
    }

    private static Control LetterTile(PhoneticEntry e)
    {
        var tile = new VStack { Width = Ui.Scaled(170), BackColor = Brand.Panel, Margin = new Padding(4), Padding = new Padding(8, 4, 8, 6) };
        tile.Controls.Add(Ui.Text(e.Character, FontRole.Title, Brand.Navy));
        tile.Controls.Add(Ui.Text(e.Word, FontRole.Heading, Brand.Navy));
        tile.Controls.Add(Ui.Text(e.Pronunciation, fore: Brand.Muted));
        return tile;
    }

    // ------------------------------------------------------------------ standard phrases and formats

    private void BuildPhrases()
    {
        var r = AppServices.Radio;
        var stack = Ui.Stack(new Padding(12, 8, 12, 12));
        if (r.Tips.Count > 0)
        {
            var tips = new VStack { BackColor = Brand.Panel, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 0, 0, 8) };
            tips.Controls.Add(Ui.Text("Good radio practice", FontRole.Heading, Brand.Navy));
            foreach (var t in r.Tips)
                tips.Controls.Add(Ui.Text("•  " + t));
            stack.Controls.Add(tips);
        }
        foreach (var group in r.Prowords.GroupBy(p => p.Group))
        {
            stack.Controls.Add(Ui.Text(group.Key.Length > 0 ? group.Key : "Phrases", FontRole.Heading, group.Key == "Emergency" ? Brand.Danger : Brand.Navy, new Padding(0, 10, 0, 2)));
            foreach (var p in group)
            {
                var row = new VStack { Padding = new Padding(0, 2, 0, 4) };
                row.Controls.Add(Ui.Text(p.Word, FontRole.Heading, group.Key == "Emergency" ? Brand.Danger : Brand.Navy));
                row.Controls.Add(Ui.Text(p.Meaning, FontRole.Large));
                stack.Controls.Add(row);
            }
        }
        foreach (var f in r.Formats)
        {
            var box = new VStack { BackColor = Brand.Amber, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 14, 0, 4) };
            box.Controls.Add(Ui.Text(f.Name, FontRole.Heading, Brand.Navy));
            foreach (var l in f.Lines)
                box.Controls.Add(Ui.Text($"{l.Key}  –  {l.Meaning}", FontRole.Large, Brand.Navy));
            if (f.Source.Length > 0)
                box.Controls.Add(Ui.Text(f.Source, fore: Brand.Navy));
            stack.Controls.Add(box);
        }
        if (r.Source.Length > 0)
            stack.Controls.Add(Ui.Text(r.Source, fore: Brand.Muted, padding: new Padding(0, 12, 0, 0)));
        stack.Controls.Add(Ui.Text("Edit Content\\radio\\reference.json to match your organisation's procedure.", fore: Brand.Muted));
        _content.Controls.Add(stack);
    }

    // ------------------------------------------------------------------ editable tables

    private void BuildGrid<T>(JsonListStore<T> store, string title, (string Property, string Header, int Width)[] columns) where T : class, new()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AllowUserToResizeRows = false,
            RowHeadersWidth = 32,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            BorderStyle = BorderStyle.None,
            EditMode = DataGridViewEditMode.EditOnEnter
        };
        foreach (var (prop, header, width) in columns)
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = prop, HeaderText = header, Width = (int)(width * UiMode.Scale), MinimumWidth = 50 });
        grid.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        var binding = new BindingList<T>(store.Items) { AllowNew = true, AllowRemove = true };
        grid.DataSource = binding;
        void Save()
        {
            store.Items.RemoveAll(IsEmpty);
            store.Save();
        }
        grid.CellEndEdit += (_, _) => Save();
        grid.UserDeletedRow += (_, _) => Save();
        grid.DataError += (_, e) => e.ThrowException = false;

        var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 60, ColumnCount = 5, RowCount = 1, Padding = new Padding(8, 6, 8, 2) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++)
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        bar.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Brand.Navy }.Styled(FontRole.Heading), 0, 0);
        Button B(string text, ButtonStyle style, Action a)
        {
            var b = Ui.Button(text, style, a);
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(6, 0, 0, 0);
            return b;
        }
        bar.Controls.Add(B("Delete row", ButtonStyle.Danger, () =>
        {
            if (grid.CurrentRow is { IsNewRow: false } row && row.DataBoundItem is T item &&
                MessageBox.Show(this, "Delete the selected row?", title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                binding.Remove(item);
                Save();
            }
        }), 1, 0);
        bar.Controls.Add(B("Copy", ButtonStyle.Secondary, () => CopyText(Csv.Write(store.Items).Replace(",", "\t"), $"{title} copied (paste into a spreadsheet or message).")), 2, 0);
        bar.Controls.Add(B("Import CSV…", ButtonStyle.Secondary, () => ImportCsv(store, binding, title)), 3, 0);
        bar.Controls.Add(B("Export CSV…", ButtonStyle.Secondary, () => ExportCsv(store, title)), 4, 0);

        var hint = Ui.Text("Tap a cell to edit; type in the last row to add. Changes are saved as you go.", fore: Brand.Muted, padding: new Padding(10, 2, 10, 2));
        hint.Dock = DockStyle.Bottom;
        _content.Controls.Add(grid);
        _content.Controls.Add(hint);
        _content.Controls.Add(bar);
    }

    private static bool IsEmpty<T>(T item) =>
        typeof(T).GetProperties().Where(p => p.PropertyType == typeof(string)).All(p => string.IsNullOrWhiteSpace((string?)p.GetValue(item)));

    private void ImportCsv<T>(JsonListStore<T> store, BindingList<T> binding, string title) where T : new()
    {
        using var dlg = new OpenFileDialog { Title = $"Import {title}", Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            var rows = Csv.Read<T>(File.ReadAllText(dlg.FileName));
            var replace = store.Items.Count > 0 && MessageBox.Show(this, $"Replace the current {store.Items.Count} rows with {rows.Count} rows from the file?\n\nNo = add them to the end.",
                title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            if (replace)
                store.Items.Clear();
            store.Items.AddRange(rows);
            store.Save();
            binding.ResetBindings();
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "The file could not be imported: " + ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExportCsv<T>(JsonListStore<T> store, string title)
    {
        using var dlg = new SaveFileDialog { Title = $"Export {title}", Filter = "CSV files (*.csv)|*.csv", FileName = title.ToLowerInvariant().Replace(' ', '-') + ".csv" };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            File.WriteAllText(dlg.FileName, Csv.Write(store.Items), new System.Text.UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "The file could not be saved: " + ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ------------------------------------------------------------------ radio log / notepad

    private TextBox? _logView;

    private void BuildLog()
    {
        var me = AppServices.Settings.UserName;
        var signs = AppServices.CallSigns.Items.Select(c => c.Sign).Where(s => s.Length > 0).Distinct().ToArray();
        ComboBox Combo(string text)
        {
            var c = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, Text = text };
            c.Items.AddRange(signs.Prepend(me).Where(s => s.Length > 0).Distinct().Cast<object>().ToArray());
            c.Styled(FontRole.Large);
            return c;
        }
        var from = Combo(me);
        var to = Combo("");
        var text = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Message or note – Enter to log" };
        text.Styled(FontRole.Large);

        void Log()
        {
            if (string.IsNullOrWhiteSpace(text.Text))
                return;
            AppServices.RadioLog.Items.Add(new LogEntry { TimeUtc = DateTime.UtcNow, From = from.Text.Trim(), To = to.Text.Trim(), Text = text.Text.Trim() });
            AppServices.RadioLog.Save();
            text.Clear();
            text.Focus();
        }
        text.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                Log();
                e.SuppressKeyPress = true;
            }
        };

        var entry = new TableLayoutPanel { Dock = DockStyle.Top, Height = 140, ColumnCount = 4, RowCount = 3, Padding = new Padding(8, 4, 8, 2) };
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        entry.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        entry.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        entry.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        entry.Controls.Add(new Label { Text = "From", Dock = DockStyle.Fill, ForeColor = Brand.Muted }.Styled(FontRole.Bold), 0, 0);
        entry.Controls.Add(new Label { Text = "To", Dock = DockStyle.Fill, ForeColor = Brand.Muted }.Styled(FontRole.Bold), 1, 0);
        entry.Controls.Add(from, 0, 1);
        entry.Controls.Add(to, 1, 1);
        var swap = Ui.Button("Swap ⇄", ButtonStyle.Secondary, () => (from.Text, to.Text) = (to.Text, from.Text));
        var location = Ui.Button("+ My location", ButtonStyle.Secondary, () =>
        {
            if (AppServices.Location.Fix is { } fix)
                text.AppendText((text.Text.Length > 0 ? " " : "") + "at " + CoordinateFormat.Mgrs(fix.Latitude, fix.Longitude));
            else
                MessageBox.Show(this, "No position available. Open the Location tool to start the GPS.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }, FontRole.Normal);
        var logButton = Ui.Button("LOG", ButtonStyle.Primary, Log, FontRole.Heading);
        foreach (var b in new[] { swap, location, logButton })
        {
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(6, 2, 0, 2);
        }
        entry.Controls.Add(swap, 2, 1);
        entry.Controls.Add(location, 3, 1);
        entry.Controls.Add(text, 0, 2);
        entry.SetColumnSpan(text, 2);
        entry.Controls.Add(logButton, 2, 2);
        entry.SetColumnSpan(logButton, 2);

        _logView = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.None };
        _logView.Styled(FontRole.Large);

        var bottom = Ui.ButtonRow(
            Ui.Button("Copy log", ButtonStyle.Secondary, () => CopyText(LogText(), "Radio log copied.")),
            Ui.Button("Save as text…", ButtonStyle.Secondary, SaveLog),
            Ui.Button("Start new log…", ButtonStyle.Danger, NewLog));
        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(8, 4, 8, 6) };
        bottom.Dock = DockStyle.Fill;
        bottomPanel.Controls.Add(bottom);

        _content.Controls.Add(_logView);
        _content.Controls.Add(bottomPanel);
        _content.Controls.Add(entry);
        AppServices.RadioLog.Changed += OnLogChanged;
        FillLog();
        FocusLater(text);
    }

    private void OnLogChanged(object? s, EventArgs e) => FillLog();

    private void FillLog()
    {
        if (_logView == null || _logView.IsDisposed)
            return;
        _logView.Text = AppServices.RadioLog.Items.Count == 0
            ? "No entries. Type a message and press Enter or LOG – each entry is time-stamped."
            : string.Join(Environment.NewLine, AppServices.RadioLog.Items.Select(e => e.Format()));
        _logView.SelectionStart = _logView.TextLength;
        _logView.ScrollToCaret();
    }

    private static string LogText() => LogEntry.FormatLog(AppServices.RadioLog.Items,
        "RADIO LOG" + (AppServices.Settings.UserName.Length > 0 ? " – " + AppServices.Settings.UserName : ""));

    private void SaveLog()
    {
        using var dlg = new SaveFileDialog { Title = "Save radio log", Filter = "Text files (*.txt)|*.txt", FileName = $"radio-log-{DateTime.Now:yyyyMMdd-HHmm}.txt" };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            File.WriteAllText(dlg.FileName, LogText());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "The file could not be saved: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Archives the current log as a text file in the data folder and starts an empty one.</summary>
    private void NewLog()
    {
        if (AppServices.RadioLog.Items.Count == 0)
            return;
        if (MessageBox.Show(this, "Start a new, empty log? The current log is archived as a text file in the toolkit's data folder.", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        var archive = Path.Combine(ToolkitSettings.DataFolder, "radio-logs", $"radio-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            File.WriteAllText(archive, LogText());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "The log could not be archived, so it was kept: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        AppServices.RadioLog.Items.Clear();
        AppServices.RadioLog.Save();
        MessageBox.Show(this, "Previous log archived to:\n" + archive, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Focuses a control once the window is on screen (tabs are also built from the constructor).</summary>
    private void FocusLater(Control c)
    {
        if (IsHandleCreated)
            BeginInvoke(() => { if (!c.IsDisposed) c.Focus(); });
        else
            Shown += (_, _) => { if (!c.IsDisposed) c.Focus(); };
    }

    private void CopyText(string text, string confirmation)
    {
        try
        {
            Clipboard.SetText(text.Length == 0 ? " " : text);
            MessageBox.Show(this, confirmation, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "The clipboard is busy. Try again.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
