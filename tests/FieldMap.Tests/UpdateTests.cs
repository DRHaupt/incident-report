using System.IO.Compression;
using System.Security.Cryptography;
using FieldMap.Core.Data;
using FieldMap.Core.Layers;

namespace FieldMap.Tests;

public class DownloaderTests
{
    [Fact]
    public async Task Interrupted_download_resumes_and_verifies_checksum()
    {
        using var server = new TestHttpServer();
        var content = RandomNumberGenerator.GetBytes(300_000);
        server.Serve("region.osm.pbf", content);
        var md5 = Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant();
        server.Serve("region.osm.pbf.md5", System.Text.Encoding.ASCII.GetBytes($"{md5}  region.osm.pbf\n"));
        var dir = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N")[..8]);
        var dest = Path.Combine(dir, "region.osm.pbf");
        var d = new Downloader(Downloader.CreateClient());
        try
        {
            var head = await d.HeadAsync(server.BaseUrl + "region.osm.pbf");
            Assert.Equal(content.Length, head.Length);

            server.CutAfter = 100_000;
            await Assert.ThrowsAnyAsync<Exception>(() => d.DownloadAsync(server.BaseUrl + "region.osm.pbf", dest, md5, "md5"));
            Assert.False(File.Exists(dest));
            Assert.True(File.Exists(dest + ".part"));
            var partial = new FileInfo(dest + ".part").Length;
            Assert.InRange(partial, 1, content.Length - 1);

            var hash = Downloader.ParseChecksumFile(await d.GetStringAsync(server.BaseUrl + "region.osm.pbf.md5"));
            var info = await d.DownloadAsync(server.BaseUrl + "region.osm.pbf", dest, hash, "md5");
            Assert.Equal(1, server.RangeRequests);
            Assert.Equal(content, File.ReadAllBytes(dest));
            Assert.False(File.Exists(dest + ".part"));
            Assert.Equal(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc), info.LastModifiedUtc);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Changed_file_on_server_restarts_and_bad_checksum_is_rejected()
    {
        using var server = new TestHttpServer();
        var v1 = RandomNumberGenerator.GetBytes(50_000);
        server.Serve("f.bin", v1);
        var dir = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N")[..8]);
        var dest = Path.Combine(dir, "f.bin");
        var d = new Downloader(Downloader.CreateClient());
        try
        {
            server.CutAfter = 20_000;
            await Assert.ThrowsAnyAsync<Exception>(() => d.DownloadAsync(server.BaseUrl + "f.bin", dest));
            // The file changes on the server: If-Range no longer matches, so the download starts again.
            var v2 = RandomNumberGenerator.GetBytes(60_000);
            server.Serve("f.bin", v2);
            server.ETag = "\"v2\"";
            await d.DownloadAsync(server.BaseUrl + "f.bin", dest);
            Assert.Equal(v2, File.ReadAllBytes(dest));

            var wrong = new string('0', 64);
            await Assert.ThrowsAsync<InvalidDataException>(() => d.DownloadAsync(server.BaseUrl + "f.bin", dest + "2", wrong, "sha256"));
            Assert.False(File.Exists(dest + "2"));
            Assert.False(File.Exists(dest + "2.part"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

[Collection("sample")]
public class UpdateAndRollbackTests
{
    private readonly SampleFixture _f;

    public UpdateAndRollbackTests(SampleFixture f) => _f = f;

    private static RegionStore NewStore() => new(Path.Combine(Path.GetTempPath(), "store-" + Guid.NewGuid().ToString("N")[..8]));

    [Fact]
    public void Publish_keeps_previous_and_rollback_swaps()
    {
        var store = NewStore();
        var service = new UpdateService(store);
        var first = service.InstallSample();
        Assert.True(first.Success, first.Message);
        var second = service.InstallSample();
        Assert.True(second.Success, second.Message);
        Assert.NotEqual(first.Version, second.Version);
        Assert.Equal(second.Version, store.CurrentVersion("sample"));
        Assert.Equal(first.Version, store.PreviousVersion("sample"));

        Assert.Equal(first.Version, store.Rollback("sample"));
        Assert.Equal(first.Version, store.ResolveCurrent("sample").Version);
        Assert.Equal(second.Version, store.Rollback("sample")); // and back again

        // A third version prunes the oldest; only current + previous remain.
        var third = service.InstallSample();
        Assert.True(third.Success);
        Assert.Equal(2, Directory.GetDirectories(store.VersionsDir("sample")).Length);
        Assert.Equal(new[] { "sample" }, store.InstalledRegions());
        Directory.Delete(store.Root, true);
    }

    [Fact]
    public void Failed_build_or_validation_never_changes_the_current_version()
    {
        var store = NewStore();
        var service = new UpdateService(store);
        var ok = service.InstallSample();
        var before = store.CurrentVersion("sample");

        // A build whose output fails validation (a data file corrupted after hashing).
        var staging = store.NewStagingDir("sample");
        RegionBuilder.BuildSample(staging, Path.Combine(store.Root, "work"));
        File.WriteAllText(Path.Combine(staging, RegionBuilder.GraphFile), "garbage");
        var result = service.ValidateAndPublish("sample", staging, null);
        Assert.False(result.Success);
        Assert.False(Directory.Exists(staging));
        Assert.Equal(before, store.CurrentVersion("sample"));

        // A build that crashes (missing OSM file).
        var def = new RegionDefinition { Id = "sample", Name = "x" };
        var bad = service.BuildAndPublish(def, new List<SourceFile>
        {
            new(new SourceDefinition { Kind = SourceKinds.OsmPbf, Url = "file:///none.osm" }, Path.Combine(store.Root, "missing.osm"), new DataSourceInfo())
        }, null, CancellationToken.None);
        Assert.False(bad.Success);
        Assert.Contains("not changed", bad.Message);
        Assert.Equal(before, store.CurrentVersion("sample"));
        Assert.Empty(Directory.GetDirectories(store.StagingRoot));

        // Interrupted between moving the folder and swapping the pointer (e.g. power cut).
        var staging2 = store.NewStagingDir("sample");
        RegionBuilder.BuildSample(staging2, Path.Combine(store.Root, "work"));
        Assert.ThrowsAny<Exception>(() => store.Publish("sample", staging2, beforePointerSwap: () => throw new IOException("power cut")));
        Assert.Equal(before, store.ResolveCurrent("sample").Version);
        Assert.True(ok.Success);
        Directory.Delete(store.Root, true);
    }

    [Fact]
    public void Damaged_current_version_falls_back_to_previous()
    {
        var store = NewStore();
        var service = new UpdateService(store);
        var v1 = service.InstallSample().Version;
        var v2 = service.InstallSample().Version;
        File.Delete(Path.Combine(store.VersionDir("sample", v2!), RegionBuilder.TilesFile));
        var (version, message) = store.ResolveCurrent("sample");
        Assert.Equal(v1, version);
        Assert.Contains("previous", message);
        // A half-written pointer file is ignored, not trusted.
        File.WriteAllText(Path.Combine(store.RegionDir("sample"), RegionStore.CurrentFile), "{\"version\": \"2026-");
        Assert.Equal(v1, store.ResolveCurrent("sample").Version);
        Directory.Delete(store.Root, true);
    }

    [Fact]
    public void Update_package_exports_and_installs_on_another_vehicle()
    {
        var outDir = Path.Combine(_f.Root, "usb");
        var package = UpdatePackage.Export(_f.Store, "sample", outDir);
        Assert.EndsWith(UpdatePackage.Extension, package);
        var desc = UpdatePackage.ReadDescription(package);
        Assert.Equal("sample", desc.RegionId);
        Assert.True(desc.IsSample);

        var vehicle = NewStore();
        var result = UpdatePackage.Install(vehicle, package);
        Assert.True(result.Success, result.Message);
        Assert.True(RegionValidator.Validate(vehicle.CurrentDir("sample")!).Ok);

        // A tampered package (one file changed) is refused and nothing changes.
        var tampered = Path.Combine(outDir, "tampered.fmupdate");
        File.Copy(package, tampered, true);
        using (var zip = ZipFile.Open(tampered, ZipArchiveMode.Update))
        {
            zip.GetEntry("data/" + RegionBuilder.GraphFile)!.Delete();
            using var w = new StreamWriter(zip.CreateEntry("data/" + RegionBuilder.GraphFile).Open());
            w.Write("tampered");
        }
        var before = vehicle.CurrentVersion("sample");
        var refused = UpdatePackage.Install(vehicle, tampered);
        Assert.False(refused.Success);
        Assert.Contains("checksum", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, vehicle.CurrentVersion("sample"));

        // Not a package at all.
        var junk = Path.Combine(outDir, "junk.fmupdate");
        File.WriteAllText(junk, "not a zip");
        Assert.False(UpdatePackage.Install(vehicle, junk).Success);
        Directory.Delete(vehicle.Root, true);
    }
}

public class ImportTests
{
    private static string Temp(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")[..8] + "-" + name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Aed_csv_and_geojson_import_with_sources_and_dates()
    {
        var csv = Temp("aeds.csv", "name,latitude,longitude,address,access,location,last_verified,source,id\n" +
                                   "Arena AED,57.4001,-125.9001,1 Arena Way,24/7,Lobby,2026-05-01,City Registry,A1\n" +
                                   "\"Library, main floor\",57.401,-125.899,,Mo-Fr,\"By the desk, left\",01/06/2025,,A2\n" +
                                   "bad row,,,,,,,,\n");
        var aeds = PoiImport.ReadAeds(csv, "Org AED list");
        Assert.Equal(2, aeds.Count);
        Assert.Equal("City Registry", aeds[0].Source);
        Assert.Equal("Org AED list", aeds[1].Source);
        Assert.Equal("2026-05-01", aeds[0].LastVerified);
        Assert.Equal("Library, main floor", aeds[1].Name);
        Assert.Equal("By the desk, left", aeds[1].Props["defibrillator:location"]);
        Assert.All(aeds, a => Assert.Equal(LayerCatalog.Aed, a.Layer));

        var geo = Temp("aeds.geojson", """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[-125.9,57.4]},"properties":{"name":"GeoJSON AED","last_verified":"2026-01-02"}}
            ]}
            """);
        var g = PoiImport.ReadAeds(geo, "Registry");
        Assert.Single(g);
        Assert.Equal(57.4, g[0].Lat);
        File.Delete(csv);
        File.Delete(geo);
    }

    [Fact]
    public void Imported_aeds_merge_by_location_and_keep_both_sources()
    {
        var existing = new List<FieldMap.Core.Build.Poi>
        {
            new() { Id = 1, Layer = "aed", Kind = "aed", Name = "", Lat = 57.4, Lon = -125.9, Source = "OpenStreetMap" },
            new() { Id = 2, Layer = "hospital", Kind = "ed", Name = "H", Lat = 57.4, Lon = -125.9, Source = "OpenStreetMap" }
        };
        var imported = new[]
        {
            new FieldMap.Core.Build.Poi { Layer = "aed", Kind = "aed", Name = "Arena AED", Lat = 57.40010, Lon = -125.90010, Source = "City Registry", LastVerified = "2026-05-01" },
            new FieldMap.Core.Build.Poi { Layer = "aed", Kind = "aed", Name = "Far AED", Lat = 57.41, Lon = -125.9, Source = "City Registry" }
        };
        var (merged, added) = PoiImport.Merge(existing, imported, 25);
        Assert.Equal(1, merged);
        Assert.Equal(1, added);
        var arena = existing.Single(p => p.Name == "Arena AED");
        Assert.Equal(1, arena.Id);
        Assert.Equal("City Registry + OpenStreetMap", arena.Source);
        Assert.Equal("2026-05-01", arena.LastVerified);
        Assert.Equal("H", existing.Single(p => p.Layer == "hospital").Name); // other layers untouched
        Assert.Equal(3, existing.Max(p => p.Id));
    }

    [Fact]
    public void Oda_address_csv_import_including_zip()
    {
        var csv = "latitude,longitude,source_id,id,group_id,street_no,street,str_name,str_type,str_dir,unit,city,postal_code,full_addr,city_pcs,str_name_pcs,str_type_pcs,str_dir_pcs,csduid,csdname,pruid,provider\n" +
                  "57.4,-125.9,1,1,,123,MAIN ST,,,,,SAMPLETON,,,,,,,,,59,Sample\n" +
                  "57.401,-125.901,2,2,,45,,ALDER,ST,NW,7,,,,,,,,,Sampleton,59,Sample\n";
        var path = Temp("oda.csv", csv);
        var data = new FieldMap.Core.Build.MapData();
        AddressImport.Import(path, data, "ODA");
        Assert.Equal(2, data.Addresses.Count);
        Assert.Equal("Main St", data.Addresses[0].Street);
        Assert.Equal("Sampleton", data.Addresses[0].City);
        Assert.Equal("Alder St NW", data.Addresses[1].Street);
        Assert.Equal("7", data.Addresses[1].Unit);
        Assert.Equal("ODA", data.Addresses[1].Source);

        var zip = Path.ChangeExtension(path, ".zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            z.CreateEntryFromFile(path, "ODA_BC_v1.csv");
        var data2 = new FieldMap.Core.Build.MapData();
        AddressImport.Import(zip, data2, "ODA", "ODA_*.csv");
        Assert.Equal(2, data2.Addresses.Count);
        File.Delete(path);
        File.Delete(zip);
    }

    [Fact]
    public void Official_facility_table_with_mapping_and_filter()
    {
        var csv = Temp("facilities.csv", "FACILITY_NAME,LAT,LON,TYPE,ER\nSample Hospital,57.4,-125.9,Hospital,Yes\nSample Clinic,57.41,-125.91,Clinic,No\n");
        var m = new PoiMapping
        {
            Layer = "hospital", Kind = "hospital", NameColumn = "facility_name", LatColumn = "lat", LonColumn = "lon",
            FilterColumn = "type", FilterValues = { "hospital" }, KindColumn = "er", KindMap = { ["Yes"] = "ed", ["No"] = "no-ed" }
        };
        var pois = PoiImport.Read(csv, m, "Official list");
        Assert.Single(pois);
        Assert.Equal("ed", pois[0].Kind);
        Assert.Equal("Official list", pois[0].Source);
        File.Delete(csv);
    }
}
