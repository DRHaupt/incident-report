using Shared.Core.Update;
using Shared.UI;

namespace Shared.Update;

/// <summary>
/// Puts update news at the top of an app's main window: the result of an update that just installed, and – after a
/// quiet background check shortly after start-up – a banner when a newer Field Suite is ready. Tapping the banner opens
/// <see cref="UpdateWindow"/>. Nothing is downloaded or installed without the user saying so.
/// </summary>
public static class UpdateNotifier
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    public static void Attach(Form main)
    {
        Button? banner = null;

        void ShowBanner(string text, ButtonStyle style, Action onClick)
        {
            if (main.IsDisposed)
                return;
            if (banner == null)
            {
                // The click runs whatever action the banner currently shows (kept in Tag), so one banner is reused.
                banner = Ui.Button("", style, () => (banner?.Tag as Action)?.Invoke());
                banner.Dock = DockStyle.Top;
                banner.Height = Ui.Scaled(52);
                main.Controls.Add(banner);
            }
            var (back, fore) = Ui.Colors(style);
            UiMode.SetColors(banner, back, fore);
            banner.Text = text;
            banner.Tag = onClick;
            banner.Visible = true;
        }

        void HideBanner()
        {
            if (banner != null)
                banner.Visible = false;
        }

        void Open(UpdateOffer? offer)
        {
            using var w = new UpdateWindow(offer);
            w.OfferHandled += (_, _) => HideBanner();
            w.ShowDialog(main);
        }

        main.Shown += (_, _) =>
        {
            if (SuiteUpdater.TakeLastResult() is { } last)
            {
                var ok = last.Code is 0 or 3010;
                ShowBanner($"Field Suite {last.Version}: update {SuiteUpdater.DescribeResult(last.Code)}. Tap to close.",
                    ok ? ButtonStyle.Success : ButtonStyle.Danger, HideBanner);
            }

            var timer = new System.Windows.Forms.Timer { Interval = (int)StartupDelay.TotalMilliseconds };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                var settings = SuiteUpdater.LoadSettings();
                if (!settings.CheckDue(DateTime.UtcNow))
                    return;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                    var result = await SuiteUpdater.CheckAsync(cts.Token);
                    if (result.Best is { } offer && offer.Manifest.Version != SuiteUpdater.LoadSettings().SkippedVersion && !main.IsDisposed)
                        ShowBanner($"Field Suite {offer.Manifest.Version} is ready to install – tap for details", ButtonStyle.Accent, () => Open(offer));
                }
                catch (OperationCanceledException)
                {
                    // Slow or no network: try again next start.
                }
            };
            timer.Start();
        };
    }

    /// <summary>Opens the update window (for a "Check for updates" menu item or button).</summary>
    public static void ShowWindow(IWin32Window owner)
    {
        using var w = new UpdateWindow();
        w.ShowDialog(owner);
    }
}
