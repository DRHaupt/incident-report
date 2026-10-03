using System.Net;
using System.Security.Cryptography;
using System.Text;
using Shared.Core.Update;

namespace FieldToolkit.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fs-update-tests-" + Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Writes a fake installer and its manifest into <paramref name="folder"/>.</summary>
    private static (UpdateManifest Manifest, byte[] Bytes) Release(string folder, string version, string? privateKey = null, int size = 200_000)
    {
        Directory.CreateDirectory(folder);
        var bytes = new byte[size];
        new Random(version.GetHashCode()).NextBytes(bytes);
        var msi = Path.Combine(folder, $"FieldSuite-{version}-x64.msi");
        File.WriteAllBytes(msi, bytes);
        var m = UpdateManifest.ForFile(msi, version, "Notes for " + version, new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc));
        if (privateKey != null)
            UpdateSigning.Sign(m, privateKey);
        File.WriteAllText(Path.Combine(folder, UpdateManifest.FileName), m.ToJson());
        return (m, bytes);
    }

    private static readonly UpdateTrust NoKeys = new(Array.Empty<string>());

    // ------------------------------------------------------------------ manifest and signatures

    [Fact]
    public void Manifest_round_trips_and_rejects_unsafe_or_broken_fields()
    {
        var (m, _) = Release(_dir, "0.1.57");
        var back = UpdateManifest.Parse(m.ToJson());
        Assert.Equal("0.1.57", back.Version);
        Assert.Equal(new Version(0, 1, 57), back.ParsedVersion);
        Assert.Equal(m.Sha256, back.Sha256);
        Assert.Equal(m.PublishedUtc, back.PublishedUtc);
        Assert.Contains("\"sha256\"", m.ToJson());

        string With(Action<UpdateManifest> change)
        {
            var c = UpdateManifest.Parse(m.ToJson());
            change(c);
            return System.Text.Json.JsonSerializer.Serialize(c, Shared.Core.Json.Options);
        }
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(With(c => c.File = @"..\..\Windows\evil.msi")));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(With(c => c.File = "C:/x.msi")));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(With(c => c.File = "setup.exe")));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(With(c => c.Sha256 = "abc")));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(With(c => c.Version = "latest")));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(With(c => c.Product = "Other App")));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(With(c => c.Size = 0)));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse("{ not json"));
    }

    [Fact]
    public void Signature_covers_every_field_and_only_the_right_key_verifies_it()
    {
        var (priv, pub) = UpdateSigning.CreateKey();
        var (_, otherPub) = UpdateSigning.CreateKey();
        Assert.Equal(pub, UpdateSigning.PublicKeyOf(priv));

        var (m, _) = Release(_dir, "0.1.60", priv);
        var parsed = UpdateManifest.Parse(File.ReadAllText(Path.Combine(_dir, UpdateManifest.FileName)));
        Assert.True(UpdateSigning.Verify(parsed, pub));
        Assert.False(UpdateSigning.Verify(parsed, otherPub));

        foreach (var tamper in new Action<UpdateManifest>[]
                 {
                     x => x.Version = "0.1.61", x => x.File = "Other.msi", x => x.Sha256 = new string('0', 64), x => x.Size++,
                     x => x.Notes += "!", x => x.PublishedUtc = x.PublishedUtc.AddSeconds(1)
                 })
        {
            var copy = UpdateManifest.Parse(m.ToJson());
            tamper(copy);
            Assert.False(UpdateSigning.Verify(copy, pub));
        }
        Assert.Throws<InvalidDataException>(() => UpdateSigning.Sign(m, "not a key"));
    }

    [Fact]
    public void Trust_requires_a_signature_only_when_the_installation_has_a_key()
    {
        var (priv, pub) = UpdateSigning.CreateKey();
        var (unsigned, _) = Release(Path.Combine(_dir, "u"), "0.1.2");
        var (signed, _) = Release(Path.Combine(_dir, "s"), "0.1.2", priv);

        Assert.Equal(UpdateTrustResult.Unsigned, NoKeys.Check(unsigned));
        var withKey = new UpdateTrust(new[] { pub });
        Assert.Equal(UpdateTrustResult.Rejected, withKey.Check(unsigned));
        Assert.Equal(UpdateTrustResult.Signed, withKey.Check(signed));

        var keyFile = Path.Combine(_dir, UpdateTrust.FileName);
        File.WriteAllLines(keyFile, new[] { "# Field Suite release key", "", pub });
        Assert.Equal(UpdateTrustResult.Signed, UpdateTrust.Load(keyFile).Check(signed));
        Assert.False(UpdateTrust.Load(Path.Combine(_dir, "missing.txt")).RequiresSignature);
    }

    // ------------------------------------------------------------------ sources and choosing

    [Fact]
    public async Task Folder_source_finds_the_release_directly_or_in_a_FieldSuite_Updates_subfolder()
    {
        var stick = Path.Combine(_dir, "stick");
        Release(Path.Combine(stick, SuiteInfo.UpdateFolderName), "0.1.9");
        var m = await new FolderUpdateSource(stick).GetManifestAsync(default);
        Assert.Equal("0.1.9", m!.Version);

        Assert.Null(await new FolderUpdateSource(_dir).GetManifestAsync(default));
        var missing = await Assert.ThrowsAsync<UpdateSourceException>(() => new FolderUpdateSource(Path.Combine(_dir, "nope")).GetManifestAsync(default));
        Assert.Contains("not found", missing.Message);

        File.Delete(Path.Combine(stick, SuiteInfo.UpdateFolderName, m.File));
        var gone = await Assert.ThrowsAsync<UpdateSourceException>(() => new FolderUpdateSource(stick).GetManifestAsync(default));
        Assert.Contains("missing", gone.Message);
    }

    [Fact]
    public async Task Checker_picks_the_newest_acceptable_update_and_explains_every_source()
    {
        var (priv, pub) = UpdateSigning.CreateKey();
        var a = Path.Combine(_dir, "a");
        var b = Path.Combine(_dir, "b");
        var c = Path.Combine(_dir, "c");
        Release(a, "0.1.10", priv);
        Release(b, "0.1.12", priv);
        Release(c, "0.1.20"); // newest but unsigned
        var sources = new IUpdateSource[] { new FolderUpdateSource(a, "A"), new FolderUpdateSource(b, "B"), new FolderUpdateSource(c, "C"), new FolderUpdateSource(Path.Combine(_dir, "x"), "X") };

        var signedOnly = await UpdateChecker.CheckAsync(sources, new Version(0, 1, 9), new UpdateTrust(new[] { pub }), default);
        Assert.Equal("0.1.12", signedOnly.Best!.Manifest.Version);
        Assert.Equal("B", signedOnly.Best.Source.Name);
        Assert.Equal(UpdateTrustResult.Signed, signedOnly.Best.Trust);
        Assert.Equal(4, signedOnly.Report.Count);
        Assert.Contains(signedOnly.Report, r => r.Source == "C" && r.Message.Contains("not signed"));
        Assert.Contains(signedOnly.Report, r => r.Source == "X" && r.Message.Contains("not found"));

        var any = await UpdateChecker.CheckAsync(sources, new Version(0, 1, 9), NoKeys, default);
        Assert.Equal("0.1.20", any.Best!.Manifest.Version);
        Assert.Equal(UpdateTrustResult.Unsigned, any.Best.Trust);

        var upToDate = await UpdateChecker.CheckAsync(sources, new Version(0, 1, 20), NoKeys, default);
        Assert.Null(upToDate.Best);
        Assert.Contains(upToDate.Report, r => r.Message.Contains("up to date"));

        // Same version in two places: the first (nearest) source wins.
        Release(Path.Combine(_dir, "d"), "0.1.12", priv);
        var tie = await UpdateChecker.CheckAsync(new IUpdateSource[] { new FolderUpdateSource(Path.Combine(_dir, "d"), "USB"), new FolderUpdateSource(b, "Share") }, null, NoKeys, default);
        Assert.Equal("USB", tie.Best!.Source.Name);
    }

    [Fact]
    public async Task Download_verifies_size_and_checksum_and_reuses_a_verified_file()
    {
        var src = Path.Combine(_dir, "src");
        var (m, bytes) = Release(src, "0.2.0");
        var offer = new UpdateOffer(m, new FolderUpdateSource(src), UpdateTrustResult.Unsigned);
        var downloads = Path.Combine(_dir, "downloads");
        var reports = new List<double>();

        var path = await UpdateChecker.DownloadAsync(offer, downloads, new SyncProgress(reports.Add), default);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(1.0, reports[^1], 3);
        Assert.False(File.Exists(path + ".partial"));

        // Already there and correct: reused without copying again.
        var stamp = File.GetLastWriteTimeUtc(path);
        Assert.Equal(path, await UpdateChecker.DownloadAsync(offer, downloads, null, default));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));

        // A damaged installer is refused and nothing is left behind.
        File.Delete(path);
        var damaged = (byte[])bytes.Clone();
        damaged[1234] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(src, m.File), damaged);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadAsync(offer, downloads, null, default));
        Assert.Contains("checksum", ex.Message);
        Assert.Empty(Directory.GetFiles(downloads));

        // Longer than described: stopped.
        File.WriteAllBytes(Path.Combine(src, m.File), bytes.Concat(new byte[10]).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadAsync(offer, downloads, null, default));
        // Shorter: incomplete.
        File.WriteAllBytes(Path.Combine(src, m.File), bytes.Take(1000).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadAsync(offer, downloads, null, default));
        Assert.Empty(Directory.GetFiles(downloads));
    }

    [Fact]
    public void Clean_downloads_keeps_only_the_named_installer()
    {
        File.WriteAllText(Path.Combine(_dir, "FieldSuite-0.1.1-x64.msi"), "x");
        File.WriteAllText(Path.Combine(_dir, "FieldSuite-0.1.2-x64.msi"), "x");
        File.WriteAllText(Path.Combine(_dir, "FieldSuite-0.1.3-x64.msi.partial"), "x");
        File.WriteAllText(Path.Combine(_dir, "last-result.txt"), "0");
        UpdateChecker.CleanDownloads(_dir, "FieldSuite-0.1.2-x64.msi");
        Assert.Equal(new[] { "FieldSuite-0.1.2-x64.msi", "last-result.txt" }, Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public async Task GitHub_source_reads_the_latest_release_assets_with_the_token()
    {
        var (priv, _) = UpdateSigning.CreateKey();
        var (m, bytes) = Release(_dir, "0.1.77", priv);
        var manifestJson = File.ReadAllText(Path.Combine(_dir, UpdateManifest.FileName));
        var handler = new FakeHttp(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/repos/DRHaupt/incident-report/releases/latest")
            {
                Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
                Assert.Equal("secret-token", req.Headers.Authorization?.Parameter);
                Assert.Contains("FieldSuite-Updater", req.Headers.UserAgent.ToString());
                return Json($$"""
                    { "tag_name": "v0.1.77", "assets": [
                      { "name": "{{m.File}}", "url": "https://api.example/assets/2" },
                      { "name": "fieldsuite-update.json", "url": "https://api.example/assets/1" } ] }
                    """);
            }
            Assert.Equal("application/octet-stream", req.Headers.Accept.Single().MediaType);
            return path switch
            {
                "/assets/1" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifestJson) },
                "/assets/2" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var http = new HttpClient(handler);
        var source = new GitHubUpdateSource("DRHaupt/incident-report", "secret-token", http);
        Assert.Equal("GitHub DRHaupt/incident-report", source.Name);

        var found = await source.GetManifestAsync(default);
        Assert.Equal("0.1.77", found!.Version);
        var path = await UpdateChecker.DownloadAsync(new UpdateOffer(found, source, UpdateTrustResult.Signed), Path.Combine(_dir, "dl"), null, default);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task GitHub_source_explains_missing_or_rejected_tokens_and_offline()
    {
        using var notFound = new HttpClient(new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var ex = await Assert.ThrowsAsync<UpdateSourceException>(() => new GitHubUpdateSource("o/r", null, notFound).GetManifestAsync(default));
        Assert.Contains("access token", ex.Message);
        Assert.Null(await new GitHubUpdateSource("o/r", "t", notFound).GetManifestAsync(default));

        using var unauthorized = new HttpClient(new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        ex = await Assert.ThrowsAsync<UpdateSourceException>(() => new GitHubUpdateSource("o/r", "t", unauthorized).GetManifestAsync(default));
        Assert.Contains("rejected", ex.Message);

        using var offline = new HttpClient(new FakeHttp(_ => throw new HttpRequestException("No such host", new System.Net.Sockets.SocketException())));
        ex = await Assert.ThrowsAsync<UpdateSourceException>(() => new GitHubUpdateSource("o/r", "t", offline).GetManifestAsync(default));
        Assert.Contains("internet", ex.Message);

        using var noManifest = new HttpClient(new FakeHttp(_ => Json("""{ "assets": [ { "name": "other.zip", "url": "https://x/1" } ] }""")));
        Assert.Null(await new GitHubUpdateSource("o/r", "t", noManifest).GetManifestAsync(default));

        Assert.Throws<ArgumentException>(() => new GitHubUpdateSource("just-a-name", null, noManifest));
        Assert.Throws<ArgumentException>(() => new GitHubUpdateSource("o/r/../x", null, noManifest));
    }

    [Fact]
    public async Task Url_source_fetches_the_installer_next_to_the_manifest_and_requires_https()
    {
        var (m, bytes) = Release(_dir, "0.3.0");
        var json = File.ReadAllText(Path.Combine(_dir, UpdateManifest.FileName));
        using var http = new HttpClient(new FakeHttp(req => req.RequestUri!.AbsoluteUri switch
        {
            "https://updates.example.org/fieldsuite/fieldsuite-update.json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) },
            var u when u == "https://updates.example.org/fieldsuite/" + m.File => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var source = new UrlUpdateSource(new Uri("https://updates.example.org/fieldsuite/fieldsuite-update.json"), http);
        var found = await source.GetManifestAsync(default);
        var path = await UpdateChecker.DownloadAsync(new UpdateOffer(found!, source, UpdateTrustResult.Unsigned), Path.Combine(_dir, "dl"), null, default);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Throws<ArgumentException>(() => new UrlUpdateSource(new Uri("http://updates.example.org/x.json"), http));
    }

    [Fact]
    public void Source_list_puts_USB_drives_first_and_skips_disabled_or_invalid_entries()
    {
        var usb = Path.Combine(_dir, "E");
        Directory.CreateDirectory(Path.Combine(usb, SuiteInfo.UpdateFolderName));
        var emptyUsb = Path.Combine(_dir, "F");
        Directory.CreateDirectory(emptyUsb);
        var settings = new UpdateSettings
        {
            Sources =
            {
                new UpdateSourceSetting { Kind = UpdateSourceKind.Url, Location = "https://updates.example.org/fieldsuite-update.json" },
                new UpdateSourceSetting { Kind = UpdateSourceKind.Url, Location = "http://insecure.example.org/x.json" },
                new UpdateSourceSetting { Kind = UpdateSourceKind.Folder, Location = @"\\server\share\FieldSuite" },
                new UpdateSourceSetting { Kind = UpdateSourceKind.Folder, Location = @"D:\Off", Enabled = false },
                new UpdateSourceSetting { Kind = UpdateSourceKind.GitHub, Location = "not valid" }
            }
        };
        using var http = new HttpClient();
        var list = UpdateSourceList.Create(settings, t => t == "enc" ? "token" : null, http, new[] { usb, emptyUsb });
        Assert.Equal(new[] { "USB drive " + usb, @"\\server\share\FieldSuite", "updates.example.org", "GitHub " + UpdateSettings.DefaultRepository },
            list.Select(s => s.Name).ToArray());

        settings.ScanRemovableDrives = false;
        Assert.DoesNotContain(UpdateSourceList.Create(settings, _ => null, http, new[] { usb }), s => s.Name.StartsWith("USB"));
    }

    [Fact]
    public void Automatic_checks_run_once_per_interval()
    {
        var s = new UpdateSettings { CheckEveryHours = 24 };
        var now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(s.CheckDue(now));
        s.LastCheckUtc = now.AddHours(-3);
        Assert.False(s.CheckDue(now));
        s.LastCheckUtc = now.AddHours(-25);
        Assert.True(s.CheckDue(now));
        s.LastCheckUtc = now.AddDays(5); // clock was wrong: check rather than wait days
        Assert.True(s.CheckDue(now));
        s.AutoCheck = false;
        Assert.False(s.CheckDue(now));
    }

    [Fact]
    public void Versions_parse_with_or_without_v_prefix()
    {
        Assert.Equal(new Version(0, 1, 57), SuiteInfo.ParseVersion("v0.1.57"));
        Assert.Equal(new Version(1, 2, 0), SuiteInfo.ParseVersion("1.2"));
        Assert.Null(SuiteInfo.ParseVersion("next"));
        Assert.Null(SuiteInfo.ParseVersion(null));
        Assert.True(SuiteInfo.ParseVersion("0.1.100") > SuiteInfo.ParseVersion("0.1.99"));
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHttp : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }

    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _report;
        public SyncProgress(Action<double> report) => _report = report;
        public void Report(double value) => _report(value);
    }
}
