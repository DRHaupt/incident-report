using System.Runtime.CompilerServices;

namespace Shared.UI;

/// <summary>Text styles that follow the current interface mode.</summary>
public enum FontRole
{
    Normal,
    Bold,
    Heading,
    Title,
    Signature,
    /// <summary>Very large text for glanceable read-outs (timers, step text in field tools).</summary>
    Large,
    /// <summary>Huge digits (CPR counter, timer read-outs).</summary>
    Display
}

/// <summary>Colour themes. Exactly one is active; switching applies live to every tracked window.</summary>
public enum UiTheme
{
    Standard,
    /// <summary>High contrast for direct sunlight: pure black and white, heavy borders.</summary>
    Daylight,
    /// <summary>Dark surfaces and dim warm text that keep night vision (no white).</summary>
    Night
}

/// <summary>
/// Interface scale (Standard / Touch) and colour theme (Standard / Daylight high-contrast / Night). Windows register
/// with <see cref="Track"/>; switching either setting restyles every open window in place without rebuilding it.
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
        public Color BackColor;
        public Color ForeColor;
        public FlatStyle? FlatStyle;
        public int BorderSize;
        public Color BorderColor;
    }

    private static readonly ConditionalWeakTable<Control, BaseInfo> _base = new();
    private static readonly ConditionalWeakTable<ToolStripItem, StrongBox<(Padding Padding, int Width)>> _itemBase = new();
    private static readonly ConditionalWeakTable<Control, StrongBox<FontRole>> _fontRoles = new();
    private static readonly ConditionalWeakTable<CheckBox, object> _optionBoxes = new();
    private static readonly ConditionalWeakTable<ListBox, object> _ownerDrawn = new();
    private static readonly ConditionalWeakTable<Control, object> _fixedSize = new();

    private static readonly Dictionary<(FontRole, bool Touch, bool Daylight), Font> _fonts = new();

    public static bool Touch { get; private set; }

    public static UiTheme Theme { get; private set; }

    /// <summary>High-contrast theme for use in direct sunlight.</summary>
    public static bool Daylight => Theme == UiTheme.Daylight;

    /// <summary>Dark theme that keeps night vision.</summary>
    public static bool Night => Theme == UiTheme.Night;

    /// <summary>"Standard", "Daylight" or "Night" (for status bars).</summary>
    public static string ThemeName => Theme.ToString();

    public static float Scale => Touch ? TouchScale : 1f;

    /// <summary>Body text size in points in the standard scale. Every other text style is sized relative to it.</summary>
    public static float BaseFontSize { get; private set; } = 9f;

    /// <summary>Raised after the scale or theme changes (and the change has been applied to tracked windows).</summary>
    public static event EventHandler? Changed;

    public static void Initialize(bool touch, bool daylight = false, float baseFontSize = 9f, bool night = false)
    {
        BaseFontSize = baseFontSize;
        Touch = touch;
        Theme = daylight ? UiTheme.Daylight : night ? UiTheme.Night : UiTheme.Standard;
    }

    public static void SetTouch(bool touch)
    {
        if (Touch == touch)
            return;
        Touch = touch;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Turns Daylight on, or back to Standard.</summary>
    public static void SetDaylight(bool daylight) => SetTheme(daylight ? UiTheme.Daylight : Daylight ? UiTheme.Standard : Theme);

    /// <summary>Turns Night on, or back to Standard.</summary>
    public static void SetNight(bool night) => SetTheme(night ? UiTheme.Night : Night ? UiTheme.Standard : Theme);

    public static void SetTheme(UiTheme theme)
    {
        if (Theme == theme)
            return;
        Theme = theme;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static Font GetFont(FontRole role)
    {
        var key = (role, Touch, Daylight);
        lock (_fonts)
        {
            if (_fonts.TryGetValue(key, out var f))
                return f;
            var (size, style) = role switch
            {
                FontRole.Bold => (9f, FontStyle.Bold),
                FontRole.Heading => (11f, FontStyle.Bold),
                FontRole.Title => (12f, FontStyle.Bold),
                FontRole.Signature => (10f, FontStyle.Italic),
                FontRole.Large => (14f, FontStyle.Regular),
                FontRole.Display => (32f, FontStyle.Bold),
                _ => (9f, FontStyle.Regular)
            };
            // Sizes above are for a 9 pt base font; touch mode adds 3 pt (8 pt for the display digits).
            size += BaseFontSize - 9f + (Touch ? (role == FontRole.Display ? 8f : 3f) : 0f);
            // Daylight uses the semibold face for regular text: thin strokes wash out in sunlight.
            var family = Daylight && style == FontStyle.Regular ? "Segoe UI Semibold" : "Segoe UI";
            return _fonts[key] = new Font(family, size, style);
        }
    }

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

    /// <summary>Excludes a button from automatic resizing (the layout sizes it, e.g. large tool buttons in a table).</summary>
    public static T FixedSize<T>(this T control) where T : Control
    {
        _fixedSize.AddOrUpdate(control, new object());
        return control;
    }

    /// <summary>
    /// Sets the colours a control should have in the standard theme. Use this instead of assigning BackColor/ForeColor
    /// for colours that change at run time, so the daylight theme can translate them.
    /// </summary>
    public static void SetColors(Control c, Color back, Color fore)
    {
        var b = Base(c);
        b.BackColor = back;
        b.ForeColor = fore;
        ApplyColors(c, b);
    }

    /// <summary>The colour a standard-theme colour becomes in the current theme (for custom painting).</summary>
    public static Color Map(Color standard, bool background) => Theme switch
    {
        UiTheme.Daylight => DaylightColor(standard, background),
        UiTheme.Night => NightColor(standard, background),
        _ => standard
    };

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
        root.Invalidate(true);
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
        HeaderWidths = x is ListView lv ? lv.Columns.Cast<ColumnHeader>().Select(ch => ch.Width).ToArray() : Array.Empty<int>(),
        BackColor = x.BackColor,
        ForeColor = x.ForeColor,
        FlatStyle = x is ButtonBase bb ? bb.FlatStyle : null,
        BorderSize = x is ButtonBase bb2 ? bb2.FlatAppearance.BorderSize : 0,
        BorderColor = x is ButtonBase bb3 ? bb3.FlatAppearance.BorderColor : Color.Empty
    });

    private static void ApplyControl(Control c)
    {
        var b = Base(c);

        if (_fontRoles.TryGetValue(c, out var role))
            c.Font = GetFont(role.Value);
        else if (c is Form)
            c.Font = GetFont(FontRole.Normal);

        // Fixed-size docked chrome (header bands, button bars, side panels, captions) grows with the mode.
        if (!c.AutoSize && c is not ToolStrip and not Splitter and not Form && !_fixedSize.TryGetValue(c, out _))
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

        ApplyColors(c, b);

        switch (c)
        {
            case ToolStrip strip:
                ApplyToolStrip(strip);
                break;

            case Button button when button.Dock == DockStyle.None && !_fixedSize.TryGetValue(button, out _):
                button.Width = Scaled(b.Size.Width);
                button.Height = Touch ? Math.Max(TouchButtonHeight, Scaled(b.Size.Height)) : b.Size.Height;
                break;

            case CheckBox box when _optionBoxes.TryGetValue(box, out _):
                box.Appearance = Touch ? Appearance.Button : Appearance.Normal;
                box.TextAlign = Touch ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft;
                box.Size = Touch ? new Size(Scaled(b.Size.Width), TouchButtonHeight) : b.Size;
                break;

            case TextBox text when text.Dock == DockStyle.None && !_fixedSize.TryGetValue(text, out _):
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

            case DataGridView grid:
                ApplyGrid(grid);
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
                if (_ownerDrawn.TryGetValue(listBox, out _))
                    listBox.ItemHeight = Touch ? TouchListItemHeight : Math.Max(b.ItemHeight, 18);
                break;
        }

        if (c.ContextMenuStrip != null)
            ApplyToolStrip(c.ContextMenuStrip);

        foreach (Control child in c.Controls)
            ApplyControl(child);
    }

    // ------------------------------------------------------------------ colours

    private static void ApplyColors(Control c, BaseInfo b)
    {
        if (c is ToolStrip or WebBrowser)
            return;

        if (Night)
        {
            c.BackColor = NightColor(b.BackColor, background: true);
            c.ForeColor = NightColor(b.ForeColor, background: false);
            if (c is ButtonBase nb and (Button or CheckBox { Appearance: Appearance.Button } or RadioButton { Appearance: Appearance.Button }))
            {
                nb.FlatStyle = FlatStyle.Flat;
                nb.FlatAppearance.BorderSize = 1;
                nb.FlatAppearance.BorderColor = NightPalette.Border;
            }
            return;
        }

        if (!Daylight)
        {
            c.BackColor = b.BackColor;
            c.ForeColor = b.ForeColor;
            if (c is ButtonBase btn && b.FlatStyle is { } fs)
            {
                btn.FlatStyle = fs;
                btn.FlatAppearance.BorderSize = b.BorderSize;
                btn.FlatAppearance.BorderColor = b.BorderColor;
            }
            return;
        }

        var back = DaylightColor(b.BackColor, background: true);
        c.BackColor = back;
        c.ForeColor = IsDark(back) ? (IsAmber(b.ForeColor) ? DaylightPalette.Amber : Color.White) : DaylightColor(b.ForeColor, background: false);
        if (c is ButtonBase button and (Button or CheckBox { Appearance: Appearance.Button } or RadioButton { Appearance: Appearance.Button }))
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 2;
            button.FlatAppearance.BorderColor = Color.Black;
        }
    }

    /// <summary>Colours used by the daylight theme: no greys, no pastel tints; pure black and white plus strong signal colours.</summary>
    public static class DaylightPalette
    {
        public static readonly Color Amber = Color.FromArgb(0xFF, 0xC4, 0x00);
        public static readonly Color Red = Color.FromArgb(0xC0, 0x00, 0x00);
        public static readonly Color Green = Color.FromArgb(0x00, 0x6B, 0x2D);
        public static readonly Color Blue = Color.FromArgb(0x00, 0x3C, 0xB4);
    }

    /// <summary>
    /// Colours used by the night theme: near-black surfaces, dim warm text and dimmed signal colours. Nothing is brighter
    /// than about 75 % so the screen doesn't ruin dark adaptation; the cab's display brightness should also be turned down.
    /// </summary>
    public static class NightPalette
    {
        public static readonly Color Background = Color.FromArgb(0x0A, 0x0C, 0x0F);
        public static readonly Color Surface = Color.FromArgb(0x14, 0x17, 0x1C);
        public static readonly Color Raised = Color.FromArgb(0x1E, 0x23, 0x2B);
        public static readonly Color Header = Color.FromArgb(0x0E, 0x14, 0x1C);
        public static readonly Color Border = Color.FromArgb(0x3A, 0x33, 0x28);
        public static readonly Color Text = Color.FromArgb(0xC4, 0xB4, 0x96);
        public static readonly Color MutedText = Color.FromArgb(0x86, 0x7A, 0x66);
        public static readonly Color Amber = Color.FromArgb(0xC8, 0x8A, 0x10);
        public static readonly Color AmberBack = Color.FromArgb(0x4A, 0x33, 0x00);
        public static readonly Color Red = Color.FromArgb(0xD8, 0x50, 0x40);
        public static readonly Color RedBack = Color.FromArgb(0x55, 0x10, 0x0E);
        public static readonly Color Green = Color.FromArgb(0x4E, 0x9A, 0x62);
        public static readonly Color GreenBack = Color.FromArgb(0x10, 0x36, 0x1E);
        public static readonly Color Blue = Color.FromArgb(0x6A, 0x88, 0xC0);
        public static readonly Color BlueBack = Color.FromArgb(0x12, 0x24, 0x44);
    }

    private static Color NightColor(Color c, bool background)
    {
        if (c.IsEmpty || c == Color.Transparent)
            return c;
        var sat = Saturation(c);
        var lum = Luminance(c);
        if (background)
        {
            if (sat > 0.55 && lum > 0.15)
            {
                if (IsAmber(c) || (c.R > 200 && c.G > 150)) return NightPalette.AmberBack;
                if (c.R > c.G && c.R > c.B) return NightPalette.RedBack;
                if (c.G > c.R && c.G >= c.B) return NightPalette.GreenBack;
                if (c.B > c.R && c.B > c.G) return NightPalette.BlueBack;
            }
            if (lum > 0.6) return NightPalette.Surface;
            if (lum > 0.3) return NightPalette.Raised;
            // Dark navy headers keep a hint of blue so they still read as headers.
            return c.B > c.R + 15 ? NightPalette.Header : NightPalette.Background;
        }
        if (sat > 0.55 && lum > 0.15)
        {
            if (IsAmber(c) || (c.R > 200 && c.G > 150)) return NightPalette.Amber;
            if (c.R > c.G + 60 && c.R > c.B + 60) return NightPalette.Red;
            if (c.G > c.R + 40 && c.G > c.B) return NightPalette.Green;
            if (c.B > c.R + 40 && c.B > c.G) return NightPalette.Blue;
        }
        // Mid-grey text (captions, hints) stays dimmer than body text.
        return lum is > 0.3 and < 0.6 ? NightPalette.MutedText : NightPalette.Text;
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static bool IsDark(Color c) => Luminance(c) < 0.45;

    private static bool IsAmber(Color c) => c.R > 200 && c.G is > 120 and < 220 && c.B < 80;

    private static double Saturation(Color c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        var min = Math.Min(c.R, Math.Min(c.G, c.B));
        return max == 0 ? 0 : (max - min) / (double)max;
    }

    private static Color DaylightColor(Color c, bool background)
    {
        if (c.IsEmpty || c == Color.Transparent)
            return c;
        var sat = Saturation(c);
        if (background)
        {
            if (sat > 0.55)
            {
                // Strong signal colours keep their meaning, pushed to their most saturated form.
                if (IsAmber(c) || (c.R > 200 && c.G > 150)) return DaylightPalette.Amber;
                if (c.R > c.G && c.R > c.B) return DaylightPalette.Red;
                if (c.G > c.R && c.G >= c.B) return DaylightPalette.Green;
                if (c.B > c.R && c.B > c.G && Luminance(c) > 0.25) return DaylightPalette.Blue;
            }
            return IsDark(c) ? Color.Black : Color.White;
        }
        // Foreground on a light background: black, except strong reds/greens which keep their warning meaning.
        if (sat > 0.55 && c.R > c.G + 60 && c.R > c.B + 60) return DaylightPalette.Red;
        if (sat > 0.55 && c.G > c.R + 40 && c.G > c.B) return DaylightPalette.Green;
        return Luminance(c) > 0.7 ? Color.White : Color.Black;
    }

    // ------------------------------------------------------------------ grids

    private static void ApplyGrid(DataGridView grid)
    {
        var rowHeight = Touch ? TouchRowHeight + 4 : (int)(GetFont(FontRole.Normal).GetHeight() + 10);
        grid.DefaultCellStyle.Font = GetFont(FontRole.Normal);
        grid.ColumnHeadersDefaultCellStyle.Font = GetFont(FontRole.Bold);
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = rowHeight + 4;
        grid.RowTemplate.Height = rowHeight;
        foreach (DataGridViewRow row in grid.Rows)
            row.Height = rowHeight;
        if (Night)
        {
            grid.EnableHeadersVisualStyles = false;
            grid.BackgroundColor = NightPalette.Background;
            grid.GridColor = NightPalette.Border;
            grid.DefaultCellStyle.BackColor = NightPalette.Surface;
            grid.DefaultCellStyle.ForeColor = NightPalette.Text;
            grid.DefaultCellStyle.SelectionBackColor = NightPalette.AmberBack;
            grid.DefaultCellStyle.SelectionForeColor = NightPalette.Amber;
            grid.ColumnHeadersDefaultCellStyle.BackColor = NightPalette.Header;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = NightPalette.Text;
            return;
        }
        grid.EnableHeadersVisualStyles = !Daylight;
        grid.BackgroundColor = Daylight ? Color.White : SystemColors.AppWorkspace;
        grid.GridColor = Daylight ? Color.Black : SystemColors.ControlDark;
        grid.DefaultCellStyle.BackColor = Daylight ? Color.White : SystemColors.Window;
        grid.DefaultCellStyle.ForeColor = Daylight ? Color.Black : SystemColors.ControlText;
        grid.DefaultCellStyle.SelectionBackColor = Daylight ? Color.Black : SystemColors.Highlight;
        grid.DefaultCellStyle.SelectionForeColor = Daylight ? Color.White : SystemColors.HighlightText;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Daylight ? Color.Black : SystemColors.Control;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Daylight ? Color.White : SystemColors.ControlText;
    }

    // ------------------------------------------------------------------ tool strips and lists

    private static readonly ImageList RowSpacer = new() { ImageSize = new Size(1, TouchRowHeight) };

    private static void ApplyToolStrip(ToolStrip strip)
    {
        var font = Touch ? (Daylight ? TouchToolFontDaylight : TouchToolFont) : GetFont(FontRole.Normal);
        strip.Font = font;
        if (Daylight)
        {
            strip.RenderMode = ToolStripRenderMode.Professional;
            strip.Renderer = DaylightRenderer.Instance;
        }
        else if (Night)
        {
            strip.RenderMode = ToolStripRenderMode.Professional;
            strip.Renderer = NightRenderer.Instance;
        }
        else if (strip.Renderer == DaylightRenderer.Instance || strip.Renderer == NightRenderer.Instance)
        {
            strip.RenderMode = strip is MenuStrip or ContextMenuStrip or StatusStrip ? ToolStripRenderMode.ManagerRenderMode : ToolStripRenderMode.System;
        }
        foreach (ToolStripItem item in strip.Items)
            ApplyToolStripItem(item, strip is MenuStrip or StatusStrip ? new Padding(8, 6, 8, 6) : new Padding(10, 8, 10, 8), font);
    }

    private static readonly Font TouchToolFont = new("Segoe UI", 11f);
    private static readonly Font TouchToolFontDaylight = new("Segoe UI Semibold", 11f);

    private static void ApplyToolStripItem(ToolStripItem item, Padding touchPadding, Font font)
    {
        var b = _itemBase.GetValue(item, i => new StrongBox<(Padding, int)>((i.Padding, i.Width))).Value;
        item.Padding = Touch ? touchPadding : b.Padding;
        item.ForeColor = Daylight ? Color.Black : Night ? NightPalette.Text : SystemColors.ControlText;
        if (item is ToolStripTextBox text)
        {
            text.Font = font;
            text.Width = Scaled(b.Width);
        }
        if (item is ToolStripDropDownItem dropDown)
        {
            dropDown.DropDown.Font = font;
            if (Daylight)
                dropDown.DropDown.Renderer = DaylightRenderer.Instance;
            else if (Night)
                dropDown.DropDown.Renderer = NightRenderer.Instance;
            else if (dropDown.DropDown.Renderer == DaylightRenderer.Instance || dropDown.DropDown.Renderer == NightRenderer.Instance)
                dropDown.DropDown.RenderMode = ToolStripRenderMode.ManagerRenderMode;
            foreach (ToolStripItem child in dropDown.DropDownItems)
                ApplyToolStripItem(child, new Padding(4, 8, 4, 8), font);
        }
    }

    private sealed class DaylightRenderer : ToolStripProfessionalRenderer
    {
        public static readonly DaylightRenderer Instance = new();

        private DaylightRenderer() : base(new DaylightColorTable()) => RoundedEdges = false;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected || e.Item.Pressed || (e.Item as ToolStripButton)?.Checked == true ? Color.White : Color.Black;
            base.OnRenderItemText(e);
        }

        private sealed class DaylightColorTable : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin => Color.White;
            public override Color ToolStripGradientMiddle => Color.White;
            public override Color ToolStripGradientEnd => Color.White;
            public override Color MenuStripGradientBegin => Color.White;
            public override Color MenuStripGradientEnd => Color.White;
            public override Color StatusStripGradientBegin => Color.White;
            public override Color StatusStripGradientEnd => Color.White;
            public override Color ToolStripDropDownBackground => Color.White;
            public override Color ImageMarginGradientBegin => Color.White;
            public override Color ImageMarginGradientMiddle => Color.White;
            public override Color ImageMarginGradientEnd => Color.White;
            public override Color MenuItemSelected => Color.Black;
            public override Color MenuItemSelectedGradientBegin => Color.Black;
            public override Color MenuItemSelectedGradientEnd => Color.Black;
            public override Color MenuItemPressedGradientBegin => Color.Black;
            public override Color MenuItemPressedGradientEnd => Color.Black;
            public override Color MenuItemBorder => Color.Black;
            public override Color MenuBorder => Color.Black;
            public override Color ButtonSelectedGradientBegin => Color.Black;
            public override Color ButtonSelectedGradientMiddle => Color.Black;
            public override Color ButtonSelectedGradientEnd => Color.Black;
            public override Color ButtonSelectedBorder => Color.Black;
            public override Color ButtonPressedGradientBegin => Color.Black;
            public override Color ButtonPressedGradientMiddle => Color.Black;
            public override Color ButtonPressedGradientEnd => Color.Black;
            public override Color ButtonCheckedGradientBegin => Color.Black;
            public override Color ButtonCheckedGradientMiddle => Color.Black;
            public override Color ButtonCheckedGradientEnd => Color.Black;
            public override Color SeparatorDark => Color.Black;
            public override Color ToolStripBorder => Color.Black;
        }
    }

    private sealed class NightRenderer : ToolStripProfessionalRenderer
    {
        public static readonly NightRenderer Instance = new();

        private NightRenderer() : base(new NightColorTable()) => RoundedEdges = false;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected || e.Item.Pressed || (e.Item as ToolStripButton)?.Checked == true ? NightPalette.Amber : NightPalette.Text;
            base.OnRenderItemText(e);
        }

        private sealed class NightColorTable : ProfessionalColorTable
        {
            private static Color Bg => NightPalette.Surface;
            private static Color Sel => NightPalette.Raised;
            public override Color ToolStripGradientBegin => Bg;
            public override Color ToolStripGradientMiddle => Bg;
            public override Color ToolStripGradientEnd => Bg;
            public override Color MenuStripGradientBegin => Bg;
            public override Color MenuStripGradientEnd => Bg;
            public override Color StatusStripGradientBegin => Bg;
            public override Color StatusStripGradientEnd => Bg;
            public override Color ToolStripDropDownBackground => Bg;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color MenuItemSelected => Sel;
            public override Color MenuItemSelectedGradientBegin => Sel;
            public override Color MenuItemSelectedGradientEnd => Sel;
            public override Color MenuItemPressedGradientBegin => Sel;
            public override Color MenuItemPressedGradientEnd => Sel;
            public override Color MenuItemBorder => NightPalette.Border;
            public override Color MenuBorder => NightPalette.Border;
            public override Color ButtonSelectedGradientBegin => Sel;
            public override Color ButtonSelectedGradientMiddle => Sel;
            public override Color ButtonSelectedGradientEnd => Sel;
            public override Color ButtonSelectedBorder => NightPalette.Border;
            public override Color ButtonPressedGradientBegin => Sel;
            public override Color ButtonPressedGradientMiddle => Sel;
            public override Color ButtonPressedGradientEnd => Sel;
            public override Color ButtonCheckedGradientBegin => Sel;
            public override Color ButtonCheckedGradientMiddle => Sel;
            public override Color ButtonCheckedGradientEnd => Sel;
            public override Color SeparatorDark => NightPalette.Border;
            public override Color ToolStripBorder => NightPalette.Border;
        }
    }

    private static void DrawListItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox list || e.Index < 0)
            return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var back = selected ? (Daylight ? Color.Black : Night ? NightPalette.AmberBack : SystemColors.Highlight) : list.BackColor;
        using (var brush = new SolidBrush(back))
            e.Graphics.FillRectangle(brush, e.Bounds);
        var rect = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, list.GetItemText(list.Items[e.Index]), list.Font, rect,
            selected ? (Daylight ? Color.White : Night ? NightPalette.Amber : SystemColors.HighlightText) : list.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }
}
