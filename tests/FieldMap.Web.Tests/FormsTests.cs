using System.Net;
using System.Text.Json;

namespace FieldMap.Web.Tests;

/// <summary>The incident report forms phones download: the Windows Incident Report Manager's templates as JSON.</summary>
public sealed class FormsTests : IClassFixture<ServerFixture>
{
    private static readonly string[] Types =
    {
        "Text", "MultiLine", "Number", "Date", "Time", "DateTime", "Choice", "MultiChoice", "YesNo", "CheckBox",
        "Phone", "Email", "Signature", "ReportLink"
    };

    private readonly ServerFixture _fx;

    public FormsTests(ServerFixture fx) => _fx = fx;

    [Fact]
    public async Task Serves_every_form_with_its_sections_and_fields()
    {
        var client = _fx.Default.CreateClient();
        var res = await client.GetAsync("/api/forms");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var forms = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("forms").EnumerateArray().ToList();
        Assert.True(forms.Count >= 90, $"only {forms.Count} forms");
        Assert.Equal(forms.Count, forms.Select(f => f.GetProperty("code").GetString()).Distinct().Count());

        foreach (var form in forms)
        {
            Assert.False(string.IsNullOrWhiteSpace(form.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(form.GetProperty("category").GetString()));
            var fields = form.GetProperty("sections").EnumerateArray().SelectMany(s => s.GetProperty("fields").EnumerateArray()).ToList();
            Assert.NotEmpty(fields);
            // Every form starts with the standard header, which the phone uses for its list of reports.
            Assert.Contains(fields, f => f.GetProperty("key").GetString() == "title");
            foreach (var f in fields)
            {
                Assert.Contains(f.GetProperty("type").GetString(), Types);
                var type = f.GetProperty("type").GetString();
                if (type is "Choice" or "MultiChoice")
                    Assert.NotEqual(0, f.GetProperty("options").GetArrayLength());
            }
        }

        var medical = forms.Single(f => f.GetProperty("code").GetString() == "MED-300");
        Assert.Equal("Medical", medical.GetProperty("category").GetString());
        Assert.Contains(medical.GetProperty("sections").EnumerateArray(), s => s.GetProperty("title").GetString() == "Vital Signs");

        // Unchanged data isn't downloaded again.
        var again = new HttpRequestMessage(HttpMethod.Get, "/api/forms");
        again.Headers.TryAddWithoutValidation("If-None-Match", res.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task Forms_need_the_access_code_when_one_is_set()
    {
        using var factory = _fx.Factory("blue-heron-42");
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/forms")).StatusCode);
    }
}
