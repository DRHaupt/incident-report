using System.Runtime.CompilerServices;
using IncidentReporting.Services;

namespace IncidentReporting.UI;

/// <summary>Text styles that follow the current interface mode.</summary>
public enum FontRole
{
    Normal,
    Bold,
    Heading,
    Title,
    Signature
}

/// <summary>
/// Standard and touch-friendly interface modes. Windows register with <see cref="Track"/>; switching modes
/// rescales fonts, row heights, buttons and input widths in place without rebuilding any window.
/// </summary>
public static class UiMode
{
    private const float TouchScale = 1.3f;
    private const int TouchRowHeight = 40;
    private const int TouchButtonHeight = 44;
    private const int TouchListItemHeight = 44;

    private sealed class BaseInfo
    {
        public Size Size;
        public Size MaximumSize;
        public Size MinimumSize;
        public int ItemHeight;
        public float[] ColumnWidths = Array.Empty<float>();
        public float[] RowHeights = Array.Empty<float>();
        public int[] HeaderWidths = Array.Empty<int>();
    }

    private static readonly ConditionalWeakTable<Control, BaseInfo> _base = new();
    private static readonly ConditionalWeakTable<ToolStripItem, StrongBox<(Padding Padding, int Width)>> _itemBase = new();
    private static readonly ConditionalWeakTable<Control, StrongBox<FontRole>> _fontRoles = new();
    private static readonly ConditionalWeakTable<CheckBox, object> _optionBoxes = new();
    private static readonly ConditionalWeakTable<ListBox, object> _ownerDrawn = new();

    private static readonly Font NormalFont = new("Segoe UI", 9f);
    private static readonly Font TouchFont = new("Segoe UI", 12f);
    private static readonly Font NormalBold = new("Segoe UI", 9f, FontStyle.Bold);
    private static readonly Font TouchBold = new("Segoe UI", 12f, FontStyle.Bold);
    private static readonly Font NormalHeading = new("Segoe UI", 11f, FontStyle.Bold);
    private static readonly Font TouchHeading = new("Segoe UI", 14f, FontStyle.Bold);
    private static readonly Font NormalTitle = new("Segoe UI", 12f, FontStyle.Bold);
    private static readonly Font TouchTitle = new("Segoe UI", 15f, FontStyle.Bold);
    private static readonly Font NormalSignature = new("Segoe UI", 10f, FontStyle.Italic);
    private static readonly Font TouchSignature = new("Segoe UI", 13f, FontStyle.Italic);
    private static readonly Font TouchToolFont = new("Segoe UI", 11f);

    public static bool Touch { get; private set; }

    public static float Scale => Touch ? TouchScale : 1f;

    public static event EventHandler? Changed;

    public static void Initialize(bool touch) => Touch = touch;

    public static void SetTouch(bool touch)
    {
        if (Touch == touch)
            return;
        Touch = touch;
        try
        {
            AppSession.Settings.TouchMode = touch;
            AppSession.Settings.Save();
        }
        catch (IOException)
        {
            // Preference not persisted; the mode still applies for this session.
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static Font GetFont(FontRole role) => role switch
    {
        FontRole.Bold => Touch ? TouchBold : NormalBold,
        FontRole.Heading => Touch ? TouchHeading : NormalHeading,
        FontRole.Title => Touch ? TouchTitle : NormalTitle,
        FontRole.Signature => Touch ? TouchSignature : NormalSignature,
        _ => Touch ? TouchFont : NormalFont
    };

    /// <summary>Gives a control a fixed text style (heading, bold label...) that still scales with the mode.</summary>
    public static T Styled<T>(this T control, FontRole role) where T : Control
    {
        _fontRoles.AddOrUpdate(control, new StrongBox<FontRole>(role));
        control.Font = GetFont(role);
        return control;
    }

    /// <summary>Marks a check box as one option of a multi-select group; in touch mode it renders as a toggle button.</summary>
    public static CheckBox AsOption(this CheckBox box)
    {
        _optionBoxes.AddOrUpdate(box, new object());
        return box;
    }

    /// <summary>Applies the current mode now and whenever it changes, for the lifetime of the form.</summary>
    public static void Track(Form form)
    {
        void OnChanged(object? s, EventArgs e) => Apply(form);
        form.Load += (_, _) => Apply(form);
        Changed += OnChanged;
        form.FormClosed += (_, _) => Changed -= OnChanged;
    }

    /// <summary>For short-lived dialogs: applies the mode and enlarges the window itself in touch mode.</summary>
    public static void TrackDialog(Form dialog)
    {
        dialog.Load += (_, _) =>
        {
            if (Touch)
            {
                dialog.MinimumSize = ScaleSize(dialog.MinimumSize);
                dialog.Size = ScaleSize(dialog.Size);
            }
            Apply(dialog);
        };
    }

    public static void Apply(Control root)
    {
        root.SuspendLayout();
        try
        {
            ApplyControl(root);
        }
        finally
        {
            root.ResumeLayout(true);
        }
    }

    private static Size ScaleSize(Size s) => new((int)(s.Width * Scale), (int)(s.Height * Scale));

    private static int Scaled(int value) => (int)(value * Scale);

    private static BaseInfo Base(Control c) => _base.GetValue(c, x => new BaseInfo
    {
        Size = x.Size,
        MaximumSize = x.MaximumSize,
        MinimumSize = x.MinimumSize,
        ItemHeight = x switch { ListBox lb => lb.ItemHeight, TreeView tv => tv.ItemHeight, _ => 0 },
        ColumnWidths = x is TableLayoutPanel t ? t.ColumnStyles.Cast<ColumnStyle>().Select(cs => cs.Width).ToArray() : Array.Empty<float>(),
        RowHeights = x is TableLayoutPanel t2 ? t2.RowStyles.Cast<RowStyle>().Select(rs => rs.Height).ToArray() : Array.Empty<float>(),
        HeaderWidths = x is ListView lv ? lv.Columns.Cast<ColumnHeader>().Select(ch => ch.Width).ToArray() : Array.Empty<int>()
    });

    private static void ApplyControl(Control c)
    {
        var b = Base(c);

        if (_fontRoles.TryGetValue(c, out var role))
            c.Font = GetFont(role.Value);
        else if (c is Form)
            c.Font = GetFont(FontRole.Normal);

        // Fixed-size docked chrome (header bands, button bars, side panels, captions) grows with the mode.
        if (!c.AutoSize && c is not ToolStrip and not Splitter and not Form)
        {
            switch (c.Dock)
            {
                case DockStyle.Top or DockStyle.Bottom:
                    c.Height = Scaled(b.Size.Height);
                    break;
                case DockStyle.Left or DockStyle.Right:
                    c.Width = Scaled(b.Size.Width);
                    break;
            }
        }

        if (b.MaximumSize.Width > 0)
            c.MaximumSize = new Size(Scaled(b.MaximumSize.Width), b.MaximumSize.Height);

        switch (c)
        {
            case ToolStrip strip:
                ApplyToolStrip(strip);
                break;

            case Button button when button.Dock == DockStyle.None:
                button.Width = Scaled(b.Size.Width);
                button.Height = Touch ? Math.Max(TouchButtonHeight, b.Size.Height) : b.Size.Height;
                break;

            case CheckBox box when _optionBoxes.TryGetValue(box, out _):
                box.Appearance = Touch ? Appearance.Button : Appearance.Normal;
                box.TextAlign = Touch ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft;
                box.Size = Touch ? new Size(Scaled(b.Size.Width), TouchButtonHeight) : b.Size;
                break;

            case TextBox text when text.Dock == DockStyle.None:
                text.Width = Scaled(b.Size.Width);
                if (text.Multiline)
                    text.Height = Scaled(b.Size.Height);
                break;

            case ComboBox combo when combo.Dock == DockStyle.None:
                combo.Width = Scaled(b.Size.Width);
                break;

            case DateTimePicker picker when picker.Dock == DockStyle.None:
                picker.Width = Scaled(b.Size.Width);
                picker.CalendarFont = GetFont(FontRole.Normal);
                break;

            case TableLayoutPanel table:
                for (var i = 0; i < table.ColumnStyles.Count && i < b.ColumnWidths.Length; i++)
                    if (table.ColumnStyles[i].SizeType == SizeType.Absolute)
                        table.ColumnStyles[i].Width = b.ColumnWidths[i] * Scale;
                for (var i = 0; i < table.RowStyles.Count && i < b.RowHeights.Length; i++)
                    if (table.RowStyles[i].SizeType == SizeType.Absolute)
                        table.RowStyles[i].Height = b.RowHeights[i] * Scale;
                break;

            case TreeView tree:
                tree.ItemHeight = Touch ? TouchRowHeight : b.ItemHeight;
                break;

            case ListView list:
                // A one-pixel-wide image list is the standard way to set ListView row height.
                list.SmallImageList = Touch ? RowSpacer : null;
                for (var i = 0; i < list.Columns.Count && i < b.HeaderWidths.Length; i++)
                    list.Columns[i].Width = Scaled(b.HeaderWidths[i]);
                break;

            case ListBox listBox:
                if (listBox.DrawMode == DrawMode.Normal)
                {
                    listBox.DrawMode = DrawMode.OwnerDrawFixed;
                    if (_ownerDrawn.TryGetValue(listBox, out _) == false)
                    {
                        _ownerDrawn.Add(listBox, new object());
                        listBox.DrawItem += DrawListItem;
                    }
                }
                listBox.ItemHeight = Touch ? TouchListItemHeight : Math.Max(b.ItemHeight, 18);
                break;
        }

        if (c.ContextMenuStrip != null)
            ApplyToolStrip(c.ContextMenuStrip);

        foreach (Control child in c.Controls)
            ApplyControl(child);
    }

    private static readonly ImageList RowSpacer = new() { ImageSize = new Size(1, TouchRowHeight) };

    private static void ApplyToolStrip(ToolStrip strip)
    {
        strip.Font = Touch ? TouchToolFont : GetFont(FontRole.Normal);
        foreach (ToolStripItem item in strip.Items)
            ApplyToolStripItem(item, strip is MenuStrip or StatusStrip ? new Padding(8, 6, 8, 6) : new Padding(10, 8, 10, 8));
    }

    private static void ApplyToolStripItem(ToolStripItem item, Padding touchPadding)
    {
        var b = _itemBase.GetValue(item, i => new StrongBox<(Padding, int)>((i.Padding, i.Width))).Value;
        item.Padding = Touch ? touchPadding : b.Padding;
        if (item is ToolStripTextBox text)
        {
            text.Font = Touch ? TouchToolFont : GetFont(FontRole.Normal);
            text.Width = Scaled(b.Width);
        }
        if (item is ToolStripDropDownItem dropDown)
        {
            dropDown.DropDown.Font = Touch ? TouchToolFont : GetFont(FontRole.Normal);
            foreach (ToolStripItem child in dropDown.DropDownItems)
                ApplyToolStripItem(child, new Padding(4, 8, 4, 8));
        }
    }

    private static void DrawListItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox list || e.Index < 0)
            return;
        e.DrawBackground();
        var selected = (e.State & DrawItemState.Selected) != 0;
        var rect = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, list.GetItemText(list.Items[e.Index]), list.Font, rect,
            selected ? SystemColors.HighlightText : SystemColors.ControlText, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }
}
