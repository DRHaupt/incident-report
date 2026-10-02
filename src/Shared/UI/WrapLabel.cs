namespace Shared.UI;

/// <summary>
/// A label that word-wraps to whatever width its container gives it and sizes its own height to fit, so long
/// instructions reflow when a window is resized, snapped side-by-side or switched to touch mode.
/// </summary>
public class WrapLabel : Label
{
    public WrapLabel()
    {
        AutoSize = false;
        UseMnemonic = false;
        this.FixedSize();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Fit();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Fit();
    }

    protected override void OnPaddingChanged(EventArgs e)
    {
        base.OnPaddingChanged(e);
        Fit();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Fit();
    }

    private bool _fitting;

    private void Fit()
    {
        if (_fitting || Width <= 0)
            return;
        _fitting = true;
        try
        {
            var inner = Math.Max(10, Width - Padding.Horizontal);
            var size = TextRenderer.MeasureText(string.IsNullOrEmpty(Text) ? " " : Text, Font, new Size(inner, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
            var height = size.Height + Padding.Vertical + 2;
            if (Height != height)
                Height = height;
        }
        finally
        {
            _fitting = false;
        }
    }
}
