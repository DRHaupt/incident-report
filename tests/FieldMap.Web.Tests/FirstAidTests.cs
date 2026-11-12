using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FieldMap.Web.Tests;

/// <summary>The first aid bundle phones download once: the Field Toolkit's topics and CPR settings, numbers filled in.</summary>
public sealed class FirstAidTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _fx;

    public FirstAidTests(ServerFixture fx) => _fx = fx;

    [Fact]
    public async Task Serves_every_topic_with_the_numbers_filled_in()
    {
        var client = _fx.Default.CreateClient();
        var res = await client.GetAsync("/api/firstaid");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("{EMERGENCY}", text);
        Assert.DoesNotContain("{POISON}", text);
        Assert.DoesNotContain("{CRISIS}", text);

        var fa = JsonDocument.Parse(text).RootElement;
        var topics = fa.GetProperty("topics").EnumerateArray().ToList();
        Assert.True(topics.Count >= 40);
        Assert.True(topics[0].GetProperty("pinnedFirst").GetBoolean()); // scene safety first
        var bleeding = topics.Single(t => t.GetProperty("id").GetString() == "severe-bleeding");
        Assert.Contains(bleeding.GetProperty("steps").EnumerateArray(), s => s.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.Object && a.GetProperty("type").GetString() == "timer");
        Assert.False(string.IsNullOrWhiteSpace(fa.GetProperty("guide").GetProperty("disclaimer").GetString()));
        Assert.Equal(3, fa.GetProperty("cpr").GetProperty("presets").GetArrayLength());
        Assert.Empty(fa.GetProperty("problems").EnumerateArray()); // the toolkit's own content checks pass

        var again = new HttpRequestMessage(HttpMethod.Get, "/api/firstaid");
        again.Headers.IfNoneMatch.Add(res.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(again)).StatusCode);
    }
}
