using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FieldMap.Web.Tests;

/// <summary>Team positions and shared pins over HTTP: behind the access code, saved across restarts.</summary>
public sealed class TeamApiTests : IClassFixture<ServerFixture>
{
    private const string Code = "kestrel-7";
    private readonly ServerFixture _fx;

    public TeamApiTests(ServerFixture fx) => _fx = fx;

    private static async Task<HttpClient> SignedIn(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/login", new { code = Code })).StatusCode);
        return client;
    }

    [Fact]
    public async Task Team_features_are_off_without_an_access_code()
    {
        var client = _fx.Default.CreateClient();
        var res = await client.GetAsync("/api/team");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("team-off", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/team/pins", new { name = "A", label = "x", lat = 57.4, lon = -125.9 })).StatusCode);
    }

    [Fact]
    public async Task Team_needs_the_access_code_cookie()
    {
        using var factory = _fx.Factory(Code);
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/team")).StatusCode);
    }

    [Fact]
    public async Task Units_share_positions_with_a_trail_and_can_stop()
    {
        using var factory = _fx.Factory(Code);
        var client = await SignedIn(factory);

        // A name is needed before sharing.
        var noName = await client.PostAsJsonAsync("/api/team/position", new { device = "phone-aaaa-1111", name = " ", lat = 57.40, lon = -125.90 });
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);

        foreach (var lat in new[] { 57.400, 57.401, 57.402 })
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/team/position",
                new { device = "phone-aaaa-1111", name = "Medic 12", lat, lon = -125.90, accuracy = 6.0, heading = 0.0, speed = 10.0 })).StatusCode);

        var team = await client.GetFromJsonAsync<JsonElement>("/api/team");
        var unit = Assert.Single(team.GetProperty("units").EnumerateArray());
        Assert.Equal("Medic 12", unit.GetProperty("name").GetString());
        Assert.Equal(57.402, unit.GetProperty("lat").GetDouble(), 6);
        Assert.False(unit.GetProperty("stale").GetBoolean());
        Assert.Equal(3, unit.GetProperty("trail").GetArrayLength()); // 111 m apart: each one is a trail point

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/team/position/phone-aaaa-1111")).StatusCode);
        team = await client.GetFromJsonAsync<JsonElement>("/api/team");
        Assert.Empty(team.GetProperty("units").EnumerateArray());
    }

    [Fact]
    public async Task Pins_can_be_added_edited_by_anyone_removed_and_survive_a_restart()
    {
        string id;
        using (var factory = _fx.Factory(Code))
        {
            var client = await SignedIn(factory);
            await client.DeleteAsync("/api/team/pins"); // the fixture's data folder is shared with other tests

            var missingLabel = await client.PostAsJsonAsync("/api/team/pins", new { name = "Medic 12", kind = "hazard", label = "", lat = 57.40, lon = -125.90 });
            Assert.Equal(HttpStatusCode.BadRequest, missingLabel.StatusCode);

            var added = await client.PostAsJsonAsync("/api/team/pins", new { name = "Medic 12", kind = "hazard", label = "Downed power line", note = "Stay 10 m back", lat = 57.40, lon = -125.90 });
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            var pin = await added.Content.ReadFromJsonAsync<JsonElement>();
            id = pin.GetProperty("id").GetString()!;
            Assert.Equal("hazard", pin.GetProperty("kind").GetString());
            Assert.Equal("Medic 12", pin.GetProperty("createdBy").GetString());

            // Someone else edits it; unknown kinds become a plain note.
            var edited = await client.PutAsJsonAsync($"/api/team/pins/{id}", new { name = "Engine 3", label = "Power line down – BC Hydro on scene", kind = "nonsense" });
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
            pin = await edited.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Engine 3", pin.GetProperty("updatedBy").GetString());
            Assert.Equal("Medic 12", pin.GetProperty("createdBy").GetString());
            Assert.Equal("note", pin.GetProperty("kind").GetString());
            Assert.Equal("Stay 10 m back", pin.GetProperty("note").GetString()); // left out: unchanged

            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/team/pins/nope", new { name = "X", label = "y" })).StatusCode);
        }

        // A restarted server still has the pin.
        using (var factory = _fx.Factory(Code))
        {
            var client = await SignedIn(factory);
            var team = await client.GetFromJsonAsync<JsonElement>("/api/team");
            var pin = Assert.Single(team.GetProperty("pins").EnumerateArray());
            Assert.Equal(id, pin.GetProperty("id").GetString());

            Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/team/pins/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/team/pins/{id}")).StatusCode);

            await client.PostAsJsonAsync("/api/team/pins", new { name = "A", label = "One", lat = 57.40, lon = -125.90 });
            await client.PostAsJsonAsync("/api/team/pins", new { name = "A", label = "Two", lat = 57.41, lon = -125.90 });
            var cleared = await (await client.DeleteAsync("/api/team/pins")).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, cleared.GetProperty("removed").GetInt32());
        }
    }
}

/// <summary>Team timing rules, with a clock the test controls.</summary>
public sealed class TeamClockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-team-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    private Team NewTeam() => new(new WebOptions { DataDir = _dir }, () => _now);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static JsonElement Only(Team t) => Assert.Single(JsonSerializer.SerializeToElement(t.Units()).EnumerateArray());

    [Fact]
    public void Units_go_stale_after_two_minutes_and_disappear_after_fifteen()
    {
        var team = NewTeam();
        Assert.Null(team.Report("phone-bbbb-2222", "Engine 3", 50, -120, 5, null, null));
        _now += TimeSpan.FromMinutes(3);
        Assert.True(Only(team).GetProperty("stale").GetBoolean());
        _now += TimeSpan.FromMinutes(13);
        Assert.Empty(team.Units());
    }

    [Fact]
    public void Trail_keeps_one_hour_and_skips_points_while_parked()
    {
        var team = NewTeam();
        for (var i = 0; i < 10; i++)
        {
            team.Report("phone-cccc-3333", "Rescue 1", 50, -120, 5, null, null); // not moving
            _now += TimeSpan.FromSeconds(5);
        }
        Assert.Equal(2, Only(team).GetProperty("trail").GetArrayLength()); // first point, then one after 30 s

        for (var i = 0; i < 80; i++)
        {
            team.Report("phone-cccc-3333", "Rescue 1", 50 + i * 0.001, -120, 5, null, null);
            _now += TimeSpan.FromMinutes(1);
        }
        var trail = Only(team).GetProperty("trail");
        Assert.InRange(trail.GetArrayLength(), 59, 61); // about an hour of one-a-minute points
    }

    [Fact]
    public void Lines_and_areas_are_stored_checked_and_redrawn()
    {
        var team = NewTeam();
        var square = new List<double[]> { new[] { -120.0, 50.0 }, new[] { -119.99, 50.0 }, new[] { -119.99, 50.01 }, new[] { -120.0, 50.01 }, new[] { -120.0, 50.0 } };
        var (area, error) = team.AddPin(new Team.PinInput("phone-ffff-6666", "IC", "perimeter", "Hot zone", null, null, null, "area", square));
        Assert.Null(error);
        Assert.Equal("area", area!.Shape);
        Assert.Equal(4, area.Points.Count);           // the repeated closing point is implied
        Assert.Equal(50.005, area.Lat, 6);              // label at the area's centre
        Assert.Equal(-119.995, area.Lon, 6);

        Assert.Equal("An area needs at least 3 points.", team.AddPin(new Team.PinInput("d", "IC", "perimeter", "x", null, null, null, "area", square.Take(2).ToList())).Error);
        Assert.Equal("A line needs at least 2 points.", team.AddPin(new Team.PinInput("d", "IC", "route", "x", null, null, null, "line", square.Take(1).ToList())).Error);
        Assert.Equal("One of the points isn't valid.", team.AddPin(new Team.PinInput("d", "IC", "route", "x", null, null, null, "line", new() { new[] { -120.0, 50.0 }, new[] { 200.0, 50.0 } })).Error);

        // Redrawn by someone else: new points, the label follows; a point's Lat/Lon is ignored for a shape.
        var line = new List<double[]> { new[] { -120.0, 50.0 }, new[] { -120.0, 50.02 }, new[] { -120.0, 50.04 } };
        var (redrawn, _) = team.UpdatePin(area.Id, new Team.PinInput("e", "Engine 3", null, null, null, 10, 10, null, square.Skip(1).ToList()));
        Assert.Equal("Engine 3", redrawn!.UpdatedBy);
        Assert.Equal(4, redrawn.Points.Count); // four different corners: nothing to drop
        Assert.NotEqual(10, redrawn.Lat);

        var (route, _) = team.AddPin(new Team.PinInput("e", "Engine 3", "route", "Access road", null, null, null, "line", line));
        Assert.Equal(50.02, route!.Lat, 6); // a line's label sits on its middle point

        // Saved and loaded again (a restart): shapes come back as they were.
        var again = NewTeam().Pins();
        Assert.Contains(again, p => p.Shape == "line" && p.Points.Count == 3 && p.Label == "Access road");
    }

    [Fact]
    public void Pins_saved_before_shapes_load_as_points()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "shared"));
        File.WriteAllText(Path.Combine(_dir, "shared", "pins.json"), """
            [ { "id": "old1", "kind": "hazard", "label": "Old pin", "note": "", "lat": 50, "lon": -120,
                "createdBy": "A", "createdUtc": "2026-11-01T00:00:00Z", "updatedBy": "A", "updatedUtc": "2026-11-01T00:00:00Z" } ]
            """);
        var pin = Assert.Single(NewTeam().Pins());
        Assert.Equal("point", pin.Shape);
        Assert.Empty(pin.Points);
    }

    [Fact]
    public void Bad_input_is_refused()
    {
        var team = NewTeam();
        Assert.NotNull(team.Report("short", "A", 50, -120, null, null, null));
        Assert.NotNull(team.Report("phone-dddd-4444", "A", 91, -120, null, null, null));
        Assert.NotNull(team.Report("phone-dddd-4444", "A", double.NaN, -120, null, null, null));
        Assert.Equal("Give the pin a label.", team.AddPin(new Team.PinInput("phone-dddd-4444", "A", "hazard", "   ", null, 50, -120)).Error);
        var (pin, _) = team.AddPin(new Team.PinInput("phone-dddd-4444", "A", "hazard", new string('x', 200), "line1\nline2\u0007", 50, -120));
        Assert.Equal(Team.MaxLabelLength, pin!.Label.Length);
        Assert.Equal("line1\nline2", pin.Note);
    }
}
