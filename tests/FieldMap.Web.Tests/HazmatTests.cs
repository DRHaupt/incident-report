using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FieldMap.Web.Tests;

/// <summary>The ERG 2024 data phones download once for offline hazmat lookups.</summary>
public sealed class HazmatTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _fx;

    public HazmatTests(ServerFixture fx) => _fx = fx;

    [Fact]
    public async Task Serves_the_guidebook_and_says_when_a_phone_already_has_it()
    {
        var client = _fx.Default.CreateClient();
        var res = await client.GetAsync("/api/erg");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var etag = res.Headers.ETag;
        Assert.NotNull(etag);

        var erg = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ERG2024", erg.GetProperty("edition").GetString());
        Assert.False(erg.GetProperty("isSample").GetBoolean());
        Assert.True(erg.GetProperty("materials").GetArrayLength() > 2000);
        Assert.Contains(erg.GetProperty("isolationTable").EnumerateArray(), t => t.GetProperty("id").GetString() == "1005");
        Assert.Contains(erg.GetProperty("guides").EnumerateArray(), g => g.GetProperty("number").GetString() == "128");

        var again = new HttpRequestMessage(HttpMethod.Get, "/api/erg");
        again.Headers.IfNoneMatch.Add(etag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task Needs_the_access_code_when_one_is_set()
    {
        using var factory = _fx.Factory("osprey-3");
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/erg")).StatusCode);
    }
}
