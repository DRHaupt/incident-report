using Shared;

namespace Shared.UI;

/// <summary>Button styles used across the suite. All are large, flat and high-contrast so they work with gloves.</summary>
public enum ButtonStyle
{
    Primary,
    Secondary,
    Danger,
    Accent,
    Success
}

/// <summary>Factory helpers for the toolkit's big, glove-friendly controls.</summary>
public static class Ui
{
    /// <summary>Minimum touch target height in the standard scale (touch mode multiplies it).</summary>
    public const int TargetHeight = 52;

    public static int Scaled(int value) => (int)(value * UiMode.Scale);

    public static (Color Back, Color Fore) Colors(ButtonStyle style) => style switch
    {
        ButtonStyle.Primary => (Brand.Navy, Color.White),
        ButtonStyle.Danger => (Brand.Danger, Color.White),
        ButtonStyle.Accent => (Brand.Amber, Brand.Navy),
        ButtonStyle.Success => (Brand.Success, Color.White),
        _ => (Brand.Panel, Brand.Navy)
    };

    public static Button Button(string text, ButtonStyle style, Action onClick, FontRole font = FontRole.Bold)
    {
        var (back, fore) = Colors(style);
        var b = new Button
        {
            Text = text,
            BackColor = back,
            ForeColor = fore,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Margin = new Padding(4),
            Height = TargetHeight,
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter,
            UseMnemonic = false
        };
        b.FlatAppearance.BorderSize = style == ButtonStyle.Secondary ? 1 : 0;
        b.FlatAppearance.BorderColor = Brand.Muted;
        // Deferred so an action may rebuild (and dispose) the page the button sits on.
        b.Click += (_, _) => b.BeginInvoke(onClick);
        b.Styled(font).FixedSize();
        return b;
    }

    /// <summary>Restyles a button for a selected/unselected toggle state (theme-aware).</summary>
    public static void SetSelected(Button b, bool selected, ButtonStyle on = ButtonStyle.Primary, ButtonStyle off = ButtonStyle.Secondary)
    {
        var (back, fore) = Colors(selected ? on : off);
        UiMode.SetColors(b, back, fore);
    }

    public static WrapLabel Text(string text, FontRole font = FontRole.Normal, Color? fore = null, Padding? padding = null)
    {
        var l = new WrapLabel { Text = text, Margin = new Padding(0), Padding = padding ?? new Padding(0, 2, 0, 2) };
        if (fore is { } f)
            l.ForeColor = f;
        l.Styled(font);
        return l;
    }

    /// <summary>A vertically scrolling stack whose children are stretched to its width (for wrapping text).</summary>
    public static FlowLayoutPanel Stack(Padding padding)
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = padding
        };
        void Stretch()
        {
            var width = flow.ClientSize.Width - flow.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
            flow.SuspendLayout();
            foreach (Control c in flow.Controls)
                c.Width = Math.Max(50, width - c.Margin.Horizontal);
            flow.ResumeLayout(true);
        }
        flow.Resize += (_, _) => Stretch();
        flow.ControlAdded += (_, e) => e.Control!.Width = Math.Max(50, flow.ClientSize.Width - flow.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - e.Control.Margin.Horizontal);
        return flow;
    }

    /// <summary>A row of equally sized buttons that fills its width (for nav bars and choice rows).</summary>
    public static TableLayoutPanel ButtonRow(params Control[] buttons)
    {
        var row = new TableLayoutPanel { ColumnCount = buttons.Length, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0) };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < buttons.Length; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / buttons.Length));
            buttons[i].Dock = DockStyle.Fill;
            row.Controls.Add(buttons[i], i, 0);
        }
        return row;
    }

    /// <summary>Makes a control (and its children) act as a single tap target.</summary>
    public static void OnTap(Control root, Action action)
    {
        root.Cursor = Cursors.Hand;
        root.Click += (_, _) => root.BeginInvoke(action);
        foreach (Control c in root.Controls)
            OnTap(c, action);
    }
}
