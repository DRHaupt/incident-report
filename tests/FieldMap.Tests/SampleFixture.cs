using FieldMap.Core.Data;
using FieldMap.Core.Routing;
using FieldMap.Core.Search;

namespace FieldMap.Tests;

/// <summary>Builds the fictitious sample region once through the full pipeline; shared by the tests in the collection.</summary>
public sealed class SampleFixture : IDisposable
{
    public SampleFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "fieldmap-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Store = new RegionStore(Path.Combine(Root, "data"));
        var staging = Store.NewStagingDir("sample");
        Manifest = RegionBuilder.BuildSample(staging, Path.Combine(Root, "work"));
        Version = Store.Publish("sample", staging);
        Dir = Store.CurrentDir("sample")!;
        Graph = RoadGraph.Load(Path.Combine(Dir, RegionBuilder.GraphFile));
        Search = new SearchEngine(Path.Combine(Dir, RegionBuilder.SearchFile), Graph);
    }

    public string Root { get; }
    public RegionStore Store { get; }
    public RegionManifest Manifest { get; }
    public string Version { get; }
    public string Dir { get; }
    public RoadGraph Graph { get; }
    public SearchEngine Search { get; }

    public void Dispose()
    {
        Search.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}

[CollectionDefinition("sample")]
public sealed class SampleCollection : ICollectionFixture<SampleFixture>
{
}
