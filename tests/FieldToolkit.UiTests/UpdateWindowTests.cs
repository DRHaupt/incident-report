using Shared.Core.Update;
using Shared.Update;
using Shared.UI;

namespace FieldToolkit.UiTests;

/// <summary>The update window opens, shows an offer from a USB-style folder and survives every theme.</summary>
public class UpdateWindowTests
{
    [Fact]
    public void Update_window_shows_an_offer_and_switches_themes()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "fs-update-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Directory.CreateDirectory(dir);
                var msi = Path.Combine(dir, "FieldSuite-9.9.9-x64.msi");
                File.WriteAllBytes(msi, new byte[4096]);
                var m = UpdateManifest.ForFile(msi, "9.9.9", "Test release", DateTime.UtcNow);
                var offer = new UpdateOffer(m, new FolderUpdateSource(dir, "USB drive E:"), UpdateTrustResult.Unsigned);

                using var w = new UpdateWindow(offer);
                w.Show();
                Pump(300);
                var texts = AllText(w);
                Assert.Contains(texts, t => t.Contains("Field Suite 9.9.9 is available"));
                Assert.Contains(texts, t => t == "Install 9.9.9");
                Assert.Contains(texts, t => t.Contains("not signed"));
                foreach (var touch in new[] { false, true })
                {
                    UiMode.SetTouch(touch);
                    UiMode.SetDaylight(true);
                    Pump(50);
                    UiMode.SetNight(true);
                    Pump(50);
                    UiMode.SetNight(false);
                    UiMode.SetDaylight(false);
                    Pump(50);
                }
                UiMode.SetTouch(false);
                w.Close();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "Update window test timed out.");
        if (error != null)
            throw new Exception("Update window test failed: " + error, error);
    }

    private static List<string> AllText(Control root)
    {
        var list = new List<string> { root.Text };
        foreach (Control c in root.Controls)
            list.AddRange(AllText(c));
        return list;
    }

    private static void Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            Application.DoEvents();
            Thread.Sleep(5);
        } while (DateTime.UtcNow < until);
    }
}
