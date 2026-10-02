using FieldToolkit.Core.Kit;
using FieldToolkit.Framework;
using FieldToolkit.UI;
using Shared;
using Shared.UI;

namespace FieldToolkit.Tools.Kit;

/// <summary>Expiry and service dates for medical kit, AED pads, oxygen, extinguishers and other equipment.</summary>
public sealed class KitWindow : ToolWindow
{
    public const string ToolId = "kit";

    private readonly VStack _summary = new() { Dock = DockStyle.Top, Padding = new Padding(10, 6, 10, 6) };
    private readonly TextBox _filter = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    private readonly Button _attentionOnly;
    private readonly FlowLayoutPanel _list;
    private bool _onlyAttention;

    public KitWindow(string? state) : base(ToolRegistry.Find(ToolId)!)
    {
        _onlyAttention = state == "attention";
        _list = Ui.Stack(new Padding(8, 4, 8, 8));

        var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 62, ColumnCount = 4, RowCount = 1, Padding = new Padding(8, 6, 8, 2) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        _filter.Styled(FontRole.Large);
        _filter.PlaceholderText = "Filter by name, vehicle, category…";
        _filter.TextChanged += (_, _) => Rebuild();
        _attentionOnly = Ui.Button("", ButtonStyle.Secondary, () => { _onlyAttention = !_onlyAttention; Rebuild(); });
        var add = Ui.Button("Add item", ButtonStyle.Accent, Add);
        var copy = Ui.Button("Copy list", ButtonStyle.Secondary, CopyList);
        foreach (var b in new[] { _attentionOnly, add, copy })
        {
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(6, 0, 0, 0);
        }
        bar.Controls.Add(_filter, 0, 0);
        bar.Controls.Add(_attentionOnly, 1, 0);
        bar.Controls.Add(add, 2, 0);
        bar.Controls.Add(copy, 3, 0);

        Body.Controls.Add(_list);
        Body.Controls.Add(bar);
        Body.Controls.Add(_summary);
        AppServices.Kit.Changed += OnKitChanged;
        FormClosed += (_, _) => AppServices.Kit.Changed -= OnKitChanged;
        Rebuild();
    }

    public override string SaveState() => _onlyAttention ? "attention" : "";

    private void OnKitChanged(object? s, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        var today = AppServices.Today;
        var items = AppServices.Kit.Items;
        var (expired, due) = KitRules.Summary(items, today);

        foreach (Control c in _summary.Controls.Cast<Control>().ToList())
            c.Dispose();
        _summary.Controls.Clear();
        var (back, fore, text) = expired > 0
            ? (Brand.Danger, Color.White, $"{expired} EXPIRED" + (due > 0 ? $" · {due} due soon" : "") + $" · {items.Count} items tracked")
            : due > 0
                ? (Brand.Amber, Brand.Navy, $"{due} due soon · {items.Count} items tracked")
                : (Brand.Success, Color.White, items.Count == 0 ? "No kit tracked yet. Tap Add item." : $"All {items.Count} items in date");
        _summary.BackColor = back;
        _summary.Controls.Add(Ui.Text(text, FontRole.Heading, fore));
        UiMode.Apply(_summary);
        Ui.SetSelected(_attentionOnly, _onlyAttention, ButtonStyle.Primary);
        _attentionOnly.Text = _onlyAttention ? "Showing expired & due" : "Expired & due only";

        _list.SuspendLayout();
        foreach (Control c in _list.Controls.Cast<Control>().ToList())
            c.Dispose();
        _list.Controls.Clear();
        var filter = _filter.Text.Trim();
        var shown = KitRules.Sorted(items, today)
            .Where(i => !_onlyAttention || KitRules.Status(i, today) is KitStatus.Expired or KitStatus.DueSoon)
            .Where(i => filter.Length == 0 || new[] { i.Name, i.Category, i.Location, i.Identifier, i.Notes }.Any(f => f.Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();
        foreach (var item in shown)
            _list.Controls.Add(Row(item, today));
        if (shown.Count == 0 && items.Count > 0)
            _list.Controls.Add(Ui.Text("Nothing matches.", FontRole.Large, Brand.Muted));
        UiMode.Apply(_list);
        _list.ResumeLayout(true);
    }

    private Control Row(KitItem item, DateOnly today)
    {
        var status = KitRules.Status(item, today);
        var (back, fore) = status switch
        {
            KitStatus.Expired => (Brand.Danger, Color.White),
            KitStatus.DueSoon => (Brand.Amber, Brand.Navy),
            _ => (Brand.Panel, Brand.Navy)
        };
        var box = new VStack { BackColor = back, Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 2, 0, 4) };
        box.Controls.Add(Ui.Text(item.Name, FontRole.Heading, fore));
        box.Controls.Add(Ui.Text(KitRules.Describe(item, today), FontRole.Large, fore));
        var details = string.Join("  ·  ", new[] { item.Category, item.Location, item.Identifier }.Where(x => x.Length > 0));
        if (item.LastChecked is { } checkedOn)
            details += (details.Length > 0 ? "  ·  " : "") + $"checked {checkedOn:dd MMM yyyy}";
        if (details.Length > 0)
            box.Controls.Add(Ui.Text(details, fore: fore));
        if (item.Notes.Length > 0)
            box.Controls.Add(Ui.Text(item.Notes, fore: fore));
        var buttons = Ui.ButtonRow(
            Ui.Button("Checked today", ButtonStyle.Secondary, () => { item.LastChecked = AppServices.Today; AppServices.Kit.Save(); }),
            Ui.Button("Replaced…", ButtonStyle.Primary, () => Replace(item)),
            Ui.Button("Edit", ButtonStyle.Secondary, () => Edit(item)),
            Ui.Button("Delete", ButtonStyle.Danger, () => Delete(item)));
        buttons.Height = Ui.Scaled(50);
        buttons.FixedSize();
        buttons.Margin = new Padding(0, 4, 0, 0);
        box.Controls.Add(buttons);
        return box;
    }

    private void Add()
    {
        var item = new KitItem();
        using var d = new KitItemDialog(item, "Add kit item");
        if (d.ShowDialog(this) != DialogResult.OK)
            return;
        AppServices.Kit.Items.Add(item);
        AppServices.Kit.Save();
    }

    private void Edit(KitItem item)
    {
        using var d = new KitItemDialog(item, "Edit kit item");
        if (d.ShowDialog(this) == DialogResult.OK)
            AppServices.Kit.Save();
    }

    private void Replace(KitItem item)
    {
        // A replacement usually has a new expiry and lot number: open the editor with the date cleared to force a choice.
        var oldExpiry = item.Expires;
        item.Expires = null;
        using var d = new KitItemDialog(item, $"Replaced: {item.Name} – enter the new expiry date");
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            item.LastChecked = AppServices.Today;
            AppServices.Kit.Save();
        }
        else
        {
            item.Expires = oldExpiry;
        }
    }

    private void Delete(KitItem item)
    {
        if (MessageBox.Show(this, $"Stop tracking \"{item.Name}\"?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        AppServices.Kit.Items.Remove(item);
        AppServices.Kit.Save();
    }

    private void CopyList()
    {
        try
        {
            Clipboard.SetText(KitRules.Report(AppServices.Kit.Items, AppServices.Today));
            MessageBox.Show(this, "Kit list copied.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "The clipboard is busy. Try again.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
