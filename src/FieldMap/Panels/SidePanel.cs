using Shared;
using Shared.UI;

namespace FieldMap.Panels;

/// <summary>
/// A panel shown at the side of a map window (layers, search results, "What's here?", nearest, directions, drawing
/// tools, details). Large text and buttons; content stacks vertically and scrolls.
/// </summary>
public abstract class SidePanel : Panel
{
    private readonly Label _title;

    protected SidePanel(Views.MapWindow map, string title)
    {
        Map = map;
        Dock = DockStyle.Fill;
        BackColor = Brand.Panel;
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 52, ColumnCount = 2, RowCount = 1, BackColor = Brand.Navy, Padding = new Padding(8, 4, 4, 4) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _title = new Label { Text = title, Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false }.Styled(FontRole.Heading);
        header.Controls.Add(_title, 0, 0);
        var close = new Button
        {
            Text = "✕ Close", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0x41, 0x52, 0x6A), ForeColor = Color.White, UseVisualStyleBackColor = false, Padding = new Padding(8, 0, 8, 0)
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Map.ClosePanel();
        header.Controls.Add(close.Styled(FontRole.Bold), 1, 0);
        Stack = Ui.Stack(new Padding(10, 8, 10, 10));
        Controls.Add(Stack);
        Controls.Add(header);
    }

    protected Views.MapWindow Map { get; }

    protected FlowLayoutPanel Stack { get; }

    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    /// <summary>Called when the panel is closed or replaced (stop timers, clear map previews).</summary>
    public virtual void OnClosing()
    {
    }

    /// <summary>The map window's position changed (GPS fix), for panels that show "from my position" data.</summary>
    public virtual void OnFix(GeoFix fix)
    {
    }

    protected void Clear()
    {
        Stack.SuspendLayout();
        foreach (Control c in Stack.Controls.Cast<Control>().ToList())
            c.Dispose();
        Stack.Controls.Clear();
        Stack.ResumeLayout();
    }

    protected WrapLabel AddText(string text, FontRole font = FontRole.Normal, Color? color = null)
    {
        var l = Ui.Text(text, font, color ?? Brand.Navy, new Padding(0, 3, 0, 3));
        Stack.Controls.Add(l);
        return l;
    }

    protected Button AddButton(string text, ButtonStyle style, Action onClick, FontRole font = FontRole.Bold)
    {
        var b = Ui.Button(text, style, onClick, font);
        b.Margin = new Padding(0, 3, 0, 3);
        Stack.Controls.Add(b);
        return b;
    }

    protected TableLayoutPanel AddButtons(params Button[] buttons)
    {
        var row = Ui.ButtonRow(buttons);
        row.Height = Ui.TargetHeight + 6;
        row.Margin = new Padding(0, 3, 0, 3);
        row.FixedSize();
        Stack.Controls.Add(row);
        return row;
    }

    protected Button Btn(string text, ButtonStyle style, Action onClick) => Ui.Button(text, style, onClick);

    /// <summary>A big, tappable row with a title and detail line (results lists).</summary>
    protected Control AddRow(string title, string detail, Action onClick, Color? accent = null)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, Height = Ui.Scaled(64), Margin = new Padding(0, 2, 0, 2), Padding = new Padding(8, 4, 8, 4), BackColor = Color.White };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = accent ?? Brand.Navy, Margin = new Padding(0) };
        row.Controls.Add(bar, 0, 0);
        row.SetRowSpan(bar, 2);
        row.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = Brand.Navy, AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft }.Styled(FontRole.Bold), 1, 0);
        row.Controls.Add(new Label { Text = detail, Dock = DockStyle.Fill, ForeColor = Brand.Muted, AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft }, 1, 1);
        row.FixedSize();
        Ui.OnTap(row, onClick);
        Stack.Controls.Add(row);
        return row;
    }

    /// <summary>A big on/off toggle with a detail line under it.</summary>
    protected CheckBox AddToggle(string title, bool on, Action<bool> changed, string detail)
    {
        var box = new CheckBox
        {
            Text = title, Checked = on, AutoSize = false, Height = Ui.TargetHeight, Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleLeft,
            FlatStyle = FlatStyle.Flat, UseMnemonic = false, Margin = new Padding(0, 4, 0, 0), Padding = new Padding(10, 0, 0, 0)
        }.Styled(FontRole.Bold);
        box.FixedSize();
        void Sync()
        {
            box.Text = (box.Checked ? "✓  " : "○  ") + title;
            UiMode.SetColors(box, box.Checked ? Brand.Navy : Color.White, box.Checked ? Color.White : Brand.Navy);
        }
        box.CheckedChanged += (_, _) =>
        {
            Sync();
            changed(box.Checked);
        };
        Sync();
        Stack.Controls.Add(box);
        if (detail.Length > 0)
            AddText(detail, FontRole.Normal, Brand.Muted);
        return box;
    }

    protected void Refit() => UiMode.Apply(this);

    protected static void Copy(IWin32Window owner, string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(owner, "The clipboard is busy. Try again.", Branding.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
