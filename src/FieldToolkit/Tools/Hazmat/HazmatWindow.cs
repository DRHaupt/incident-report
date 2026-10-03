using FieldToolkit.Core.Hazmat;
using FieldToolkit.Framework;
using FieldToolkit.UI;
using Shared;
using Shared.UI;

namespace FieldToolkit.Tools.Hazmat;

/// <summary>
/// Hazardous materials lookup from the ERG dataset: search by UN/NA number (on-screen keypad) or name, pick a placard,
/// and view the material with its full guide page and, for highlighted materials, Table 1 isolation distances.
/// </summary>
public sealed class HazmatWindow : ToolWindow
{
    public const string ToolId = "hazmat";

    private static readonly Color TihGreen = Color.FromArgb(0x1E, 0x7B, 0x3C);
    private static readonly Color TihTint = Color.FromArgb(0xE3, 0xF4, 0xE8);

    private readonly ErgDataset _data = AppServices.Erg;
    private readonly ErgSearch _search;
    private readonly Panel _home = new() { Dock = DockStyle.Fill };
    private readonly Panel _detail = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly TextBox _query = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    private readonly TableLayoutPanel _keypad = new() { Dock = DockStyle.Top, Height = 250, ColumnCount = 4, RowCount = 3, Padding = new Padding(8, 0, 8, 4), Visible = false };
    private readonly FlowLayoutPanel _placards = new() { Dock = DockStyle.Top, Height = 250, AutoScroll = true, Padding = new Padding(8, 0, 8, 4), Visible = false };
    private readonly FlowLayoutPanel _results;
    private readonly Button _keypadToggle;
    private readonly Button _placardToggle;
    private readonly System.Windows.Forms.Timer _searchDelay = new() { Interval = 150 };
    private ErgPlacard? _placard;
    private string _state = "";

    public HazmatWindow(string? state) : base(ToolRegistry.Find(ToolId)!)
    {
        _search = new ErgSearch(_data);
        _results = Ui.Stack(new Padding(8, 4, 8, 8));

        var searchRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 64, ColumnCount = 3, RowCount = 1, Padding = new Padding(8, 8, 8, 4) };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        _query.Styled(FontRole.Large);
        _query.PlaceholderText = "UN number or material name";
        _query.TextChanged += (_, _) => { _placard = null; _searchDelay.Stop(); _searchDelay.Start(); };
        _query.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { _query.Clear(); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Enter && _results.Controls.OfType<Button>().FirstOrDefault()?.Tag is ErgMaterial m) { ShowMaterial(m); e.SuppressKeyPress = true; }
        };
        _keypadToggle = Ui.Button("Keypad", ButtonStyle.Secondary, () => TogglePanel(_keypad));
        _placardToggle = Ui.Button("Placards", ButtonStyle.Secondary, () => TogglePanel(_placards));
        _keypadToggle.Dock = _placardToggle.Dock = DockStyle.Fill;
        _keypadToggle.Margin = _placardToggle.Margin = new Padding(6, 0, 0, 0);
        searchRow.Controls.Add(_query, 0, 0);
        searchRow.Controls.Add(_keypadToggle, 1, 0);
        searchRow.Controls.Add(_placardToggle, 2, 0);

        BuildKeypad();
        BuildPlacards();

        _home.Controls.Add(_results);
        _home.Controls.Add(_placards);
        _home.Controls.Add(_keypad);
        _home.Controls.Add(searchRow);
        Body.Controls.Add(_detail);
        Body.Controls.Add(_home);
        Body.Controls.Add(BuildDataBanner());

        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); ShowResults(); };
        FormClosed += (_, _) => _searchDelay.Dispose();
        ShowResults();
        RestoreState(state);
    }

    // ------------------------------------------------------------------ state

    public override string SaveState() => _state;

    private void RestoreState(string? state)
    {
        if (string.IsNullOrEmpty(state))
            return;
        if (state.StartsWith("material:", StringComparison.Ordinal))
        {
            var parts = state["material:".Length..].Split('|');
            var material = _data.Materials.FirstOrDefault(m => m.Id == parts[0] && (parts.Length < 2 || m.Name == parts[1]))
                           ?? (parts.Length > 1 ? _data.Materials.FirstOrDefault(m => m.Name == parts[1]) : null)
                           ?? _data.Materials.FirstOrDefault(m => m.Id == parts[0]);
            if (material != null)
                ShowMaterial(material);
        }
        else if (state.StartsWith("guide:", StringComparison.Ordinal) && _search.Guide(state["guide:".Length..]) is { } g)
        {
            ShowGuide(g);
        }
    }

    // ------------------------------------------------------------------ banner and data info

    private Control BuildDataBanner()
    {
        var banner = new VStack { Dock = DockStyle.Top, Padding = new Padding(10, 6, 10, 6) };
        string text;
        if (_data.IsEmpty)
        {
            banner.BackColor = Brand.Danger;
            text = "NO HAZMAT DATA LOADED. " + string.Join(" ", _data.Problems.Take(1)) + " Tap for how to import the ERG.";
        }
        else if (_data.IsSample)
        {
            banner.BackColor = Brand.Danger;
            text = "SAMPLE DATA – NOT REAL ERG CONTENT. Do not use for an incident. Tap for how to import the official ERG.";
        }
        else
        {
            banner.BackColor = Brand.Panel;
            text = $"{_data.Edition} · imported {_data.ImportedOn}" + (_data.Problems.Count > 0 ? $" · {_data.Problems.Count} data problem(s)" : "") + " · Tap for details";
        }
        banner.Controls.Add(Ui.Text(text, FontRole.Bold, _data.IsSample || _data.IsEmpty ? Color.White : Brand.Navy));
        Ui.OnTap(banner, () => { using var d = new ErgInfoDialog(_data); d.ShowDialog(this); });
        return banner;
    }

    // ------------------------------------------------------------------ search page

    private void BuildKeypad()
    {
        for (var i = 0; i < 4; i++)
            _keypad.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        for (var i = 0; i < 3; i++)
            _keypad.RowStyles.Add(new RowStyle(SizeType.Percent, 33.3f));
        var keys = new[] { "1", "2", "3", "⌫", "4", "5", "6", "0", "7", "8", "9", "Clear" };
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var b = Ui.Button(key, key.Length == 1 ? ButtonStyle.Primary : ButtonStyle.Secondary, () => Key(key), FontRole.Title);
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(3);
            _keypad.Controls.Add(b, i % 4, i / 4);
        }
    }

    private void Key(string key)
    {
        _query.Text = key switch
        {
            "⌫" => _query.Text.Length > 0 ? _query.Text[..^1] : "",
            "Clear" => "",
            _ when _query.Text.All(char.IsDigit) && _query.Text.Length < 4 => _query.Text + key,
            _ when _query.Text.All(char.IsDigit) => key,
            _ => key
        };
        _query.SelectionStart = _query.Text.Length;
    }

    private void BuildPlacards()
    {
        foreach (var p in _data.Placards)
        {
            var tile = new PlacardTile(p) { Size = new Size(110, 110) };
            tile.FixedSize();
            tile.Click += (_, _) => BeginInvoke(() => SelectPlacard(p));
            _placards.Controls.Add(tile);
        }
        UiMode.Changed += ResizePlacards;
        FormClosed += (_, _) => UiMode.Changed -= ResizePlacards;
        ResizePlacards(null, EventArgs.Empty);
    }

    private void ResizePlacards(object? s, EventArgs e)
    {
        foreach (var tile in _placards.Controls.OfType<PlacardTile>())
            tile.Size = new Size(Ui.Scaled(110), Ui.Scaled(110));
    }

    private void TogglePanel(Control panel)
    {
        var show = !panel.Visible;
        _keypad.Visible = panel == _keypad && show;
        _placards.Visible = panel == _placards && show;
        Ui.SetSelected(_keypadToggle, _keypad.Visible);
        Ui.SetSelected(_placardToggle, _placards.Visible);
    }

    private void SelectPlacard(ErgPlacard p)
    {
        _placard = _placard == p ? null : p;
        foreach (var tile in _placards.Controls.OfType<PlacardTile>())
            tile.Selected = tile.Placard == _placard;
        ShowResults();
    }

    private void ShowResults()
    {
        _results.SuspendLayout();
        foreach (Control c in _results.Controls.Cast<Control>().ToList())
            c.Dispose();
        _results.Controls.Clear();

        IReadOnlyList<ErgMaterial> hits;
        if (_placard != null)
        {
            var p = _placard;
            _results.Controls.Add(Ui.Text($"Placard {p.Code} – {p.ClassName}", FontRole.Heading, Brand.Navy));
            if (p.Guide.Length > 0 && _search.Guide(p.Guide) is { } g)
            {
                var b = Ui.Button($"Initial response: Guide {g.Number} – {g.Title}", ButtonStyle.Accent, () => ShowGuide(g));
                b.TextAlign = ContentAlignment.MiddleLeft;
                _results.Controls.Add(b);
            }
            else
            {
                _results.Controls.Add(Ui.Text("The initial response guide for this placard has not been imported (see placards.csv in the import tool).", fore: Brand.Muted));
            }
            hits = _search.ByPlacard(p);
        }
        else if (string.IsNullOrWhiteSpace(_query.Text))
        {
            _results.Controls.Add(Ui.Text("Type or key in a 4-digit UN/NA number, type a material name, or tap Placards.", FontRole.Large, Brand.Muted));
            hits = Array.Empty<ErgMaterial>();
        }
        else
        {
            hits = _search.Search(_query.Text, 200);
            _results.Controls.Add(Ui.Text(hits.Count == 0 ? "No matching materials." : $"{hits.Count}{(hits.Count == 200 ? "+" : "")} match{(hits.Count == 1 ? "" : "es")}",
                FontRole.Bold, Brand.Muted));
        }

        foreach (var m in hits.Take(200))
            _results.Controls.Add(MaterialButton(m));
        UiMode.Apply(_results);
        _results.ResumeLayout(true);
    }

    private Button MaterialButton(ErgMaterial m)
    {
        var text = $"{m.IdLabel}   {m.Name}   ·   Guide {m.GuideLabel}" + (m.Highlighted ? "   ·   ISOLATION DISTANCES" : "");
        var b = Ui.Button(text, m.Highlighted ? ButtonStyle.Success : ButtonStyle.Secondary, () => ShowMaterial(m), FontRole.Bold);
        b.TextAlign = ContentAlignment.MiddleLeft;
        b.Padding = new Padding(8, 0, 0, 0);
        b.Height = Ui.Scaled(56);
        b.Margin = new Padding(0, 2, 0, 2);
        b.Tag = m;
        return b;
    }

    // ------------------------------------------------------------------ detail page

    private FlowLayoutPanel NewDetail(string title)
    {
        _detail.SuspendLayout();
        foreach (Control c in _detail.Controls.Cast<Control>().ToList())
            c.Dispose();
        _detail.Controls.Clear();

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 60, ColumnCount = 2, RowCount = 1, Padding = new Padding(8, 6, 8, 2) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var back = Ui.Button("‹ Search", ButtonStyle.Secondary, ShowHome);
        back.Dock = DockStyle.Fill;
        back.Margin = new Padding(0, 0, 8, 0);
        top.Controls.Add(back, 0, 0);
        top.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Brand.Navy, UseMnemonic = false }.Styled(FontRole.Title), 1, 0);

        var stack = Ui.Stack(new Padding(12, 6, 12, 12));
        _detail.Controls.Add(stack);
        _detail.Controls.Add(top);
        _home.Visible = false;
        _detail.Visible = true;
        return stack;
    }

    private void FinishDetail(FlowLayoutPanel stack)
    {
        UiMode.Apply(_detail);
        _detail.ResumeLayout(true);
        stack.AutoScrollPosition = Point.Empty;
    }

    private void ShowHome()
    {
        _state = "";
        _detail.Visible = false;
        _home.Visible = true;
        SetTitle(Tool.Title);
        _query.Focus();
    }

    private void ShowMaterial(ErgMaterial m)
    {
        _state = $"material:{m.Id}|{m.Name}";
        SetTitle($"{m.IdLabel} {m.Name}");
        var stack = NewDetail($"{m.IdLabel} – {m.Name}");

        var facts = new List<string> { m.Id.Length > 0 ? $"ID {m.Id}" : "No ID number", $"Guide {m.GuideLabel}" };
        if (m.HazardClass.Length > 0)
            facts.Add($"Class {m.HazardClass}");
        stack.Controls.Add(Ui.Text(string.Join("   ·   ", facts), FontRole.Heading));
        if (m.Polymerizable)
            stack.Controls.Add(Notice("P – this material may polymerize explosively when heated or involved in a fire (see guide).", Brand.Amber, Brand.Navy));
        if (m.WaterReactive)
            stack.Controls.Add(Notice("Water-reactive: produces toxic gas when spilled in water (ERG Table 2)" +
                                      (m.WaterReactiveGases.Length > 0 ? $": {m.WaterReactiveGases}." : "."), Brand.Amber, Brand.Navy));
        if (_data.IsSample)
            stack.Controls.Add(Notice("SAMPLE DATA – fictitious entry for testing. Not ERG content.", Brand.Danger, Color.White));

        var isolation = _search.Isolation(m);
        if (m.Highlighted || isolation.Count > 0)
        {
            stack.Controls.Add(Ui.Text("Table 1 – Initial isolation and protective action distances", FontRole.Heading, TihGreen, new Padding(0, 10, 0, 2)));
            if (isolation.Count == 0)
                stack.Controls.Add(Ui.Text("This material is highlighted but no Table 1 entry was imported – check the printed guidebook.", FontRole.Bold, Brand.Danger));
            foreach (var e in isolation)
                stack.Controls.Add(DistanceTable(e));
        }

        if (_search.Guide(m.Guide) is { } guide)
            AddGuide(stack, guide);
        else
            stack.Controls.Add(Ui.Text($"Guide {m.Guide} is not in the dataset.", FontRole.Bold, Brand.Danger));
        FinishDetail(stack);
    }

    private void ShowGuide(ErgGuide g)
    {
        _state = $"guide:{g.Number}";
        SetTitle($"Guide {g.Number}");
        var stack = NewDetail($"Guide {g.Number}");
        if (_data.IsSample)
            stack.Controls.Add(Notice("SAMPLE DATA – fictitious guide for testing. Not ERG content.", Brand.Danger, Color.White));
        AddGuide(stack, g);
        stack.Controls.Add(Ui.Text("Materials using this guide", FontRole.Heading, Brand.Navy, new Padding(0, 12, 0, 2)));
        foreach (var m in _search.ByGuide(g.Number).Take(100))
            stack.Controls.Add(MaterialButton(m));
        FinishDetail(stack);
    }

    private static VStack Notice(string text, Color back, Color fore)
    {
        var box = new VStack { BackColor = back, Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 4, 0, 4) };
        box.Controls.Add(Ui.Text(text, FontRole.Bold, fore));
        return box;
    }

    private static Control DistanceTable(ErgIsolationEntry e)
    {
        var box = new VStack { BackColor = TihTint, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 4, 0, 6) };
        box.Controls.Add(Ui.Text(e.Name + (e.Note.Length > 0 ? $"  ({e.Note})" : ""), FontRole.Bold, TihGreen));

        var grid = new TableLayoutPanel { ColumnCount = 3, RowCount = 4, Height = Ui.Scaled(170), CellBorderStyle = TableLayoutPanelCellBorderStyle.Single, BackColor = Color.White };
        grid.FixedSize();
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        for (var i = 0; i < 4; i++)
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        Label Cell(string text, FontRole role) => new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, UseMnemonic = false }.Styled(role);
        grid.Controls.Add(Cell("", FontRole.Bold), 0, 0);
        grid.Controls.Add(Cell("SMALL SPILL", FontRole.Bold), 1, 0);
        grid.Controls.Add(Cell("LARGE SPILL", FontRole.Bold), 2, 0);
        grid.Controls.Add(Cell("First ISOLATE in all directions", FontRole.Bold), 0, 1);
        grid.Controls.Add(Cell("Then PROTECT downwind – DAY", FontRole.Bold), 0, 2);
        grid.Controls.Add(Cell("Then PROTECT downwind – NIGHT", FontRole.Bold), 0, 3);
        grid.Controls.Add(Cell(e.Small.Isolate, FontRole.Heading), 1, 1);
        grid.Controls.Add(Cell(e.Small.ProtectDay, FontRole.Heading), 1, 2);
        grid.Controls.Add(Cell(e.Small.ProtectNight, FontRole.Heading), 1, 3);
        grid.Controls.Add(Cell(e.Large.Isolate, FontRole.Heading), 2, 1);
        grid.Controls.Add(Cell(e.Large.ProtectDay, FontRole.Heading), 2, 2);
        grid.Controls.Add(Cell(e.Large.ProtectNight, FontRole.Heading), 2, 3);
        box.Controls.Add(grid);
        box.Controls.Add(Ui.Text($"ERG page {e.Page}", fore: Brand.Muted));
        return box;
    }

    private void AddGuide(FlowLayoutPanel stack, ErgGuide g)
    {
        var header = new VStack { BackColor = Brand.Amber, Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 12, 0, 4) };
        header.Controls.Add(Ui.Text($"GUIDE {g.Number}", FontRole.Title, Brand.Navy));
        header.Controls.Add(Ui.Text(g.Title, FontRole.Heading, Brand.Navy));
        stack.Controls.Add(header);
        foreach (var section in g.Sections)
        {
            var band = new VStack { BackColor = Brand.Navy, Padding = new Padding(10, 4, 10, 4), Margin = new Padding(0, 10, 0, 2) };
            band.Controls.Add(Ui.Text(section.Heading, FontRole.Heading, Color.White));
            stack.Controls.Add(band);
            foreach (var sub in section.Subsections)
            {
                if (sub.Heading.Length > 0)
                    stack.Controls.Add(Ui.Text(sub.Heading, FontRole.Heading, Brand.Navy, new Padding(0, 8, 0, 2)));
                foreach (var item in sub.Items)
                    stack.Controls.Add(Ui.Text(item.StartsWith("– ", StringComparison.Ordinal) ? "      " + item : "•  " + item, FontRole.Large, padding: new Padding(6, 2, 0, 2)));
                // Every guide's FIRST AID refers to the guidebook's General First Aid section: show it right here.
                if (sub.Heading == "FIRST AID" && _data.GeneralFirstAid is { } general)
                {
                    var box = new VStack { BackColor = Brand.Panel, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 6, 0, 6) };
                    box.Controls.Add(Ui.Text("General First Aid" + (_data.GeneralFirstAidPage.Length > 0 ? $"  (ERG page {_data.GeneralFirstAidPage})" : ""), FontRole.Heading, Brand.Navy));
                    foreach (var item in general.Subsections.SelectMany(s => s.Items))
                        box.Controls.Add(Ui.Text(item.StartsWith("– ", StringComparison.Ordinal) ? "      " + item : "•  " + item, FontRole.Large, Brand.Navy));
                    stack.Controls.Add(box);
                }
            }
        }
        foreach (var note in g.Notes)
            stack.Controls.Add(Notice(note, Brand.Amber, Brand.Navy));
        if (g.Pages.Length > 0)
            stack.Controls.Add(Ui.Text($"ERG page(s) {g.Pages}", fore: Brand.Muted, padding: new Padding(0, 8, 0, 0)));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _detail.Visible)
        {
            ShowHome();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
