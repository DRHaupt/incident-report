using FieldToolkit.Core.FirstAid;

namespace FieldToolkit.Tests;

public class FirstAidContentTests
{
    private static readonly Lazy<FirstAidLibrary> Library = new(() => FirstAidLibrary.Load(TestPaths.Content("firstaid")));

    public static readonly string[] RequiredTopics =
    {
        "scene-safety", "cpr-adult", "cpr-child", "cpr-infant", "choking-adult", "choking-infant", "severe-bleeding",
        "shock", "burns", "fractures-sprains", "head-injury", "spinal-injury", "seizures", "stroke", "heart-attack",
        "anaphylaxis", "asthma", "diabetic-emergency", "heat-illness", "hypothermia", "drowning", "electric-shock",
        "poisoning-overdose", "bites-stings", "unconscious-recovery", "childbirth",
        "chest-wound", "abdominal-wound", "impaled-object", "amputation", "eye-injury", "nosebleed", "dental-injury",
        "minor-wounds", "frostbite", "fainting", "opioid-overdose", "smoke-co", "mental-health-crisis", "triage-start",
        "secondary-survey"
    };

    [Fact]
    public void Shipped_content_loads_without_problems()
    {
        var lib = Library.Value;
        Assert.True(lib.Problems.Count == 0, string.Join(Environment.NewLine, lib.Problems));
    }

    [Theory]
    [MemberData(nameof(RequiredTopicIds))]
    public void Every_required_topic_is_present_and_complete(string id)
    {
        var t = Library.Value.Find(id);
        Assert.NotNull(t);
        Assert.NotEmpty(t!.Steps);
        Assert.NotEmpty(t.CallEmergency.When);
        Assert.False(string.IsNullOrWhiteSpace(t.CallEmergency.Headline));
        Assert.NotEmpty(t.DoNot);
        Assert.NotEmpty(t.WarningSigns);
        Assert.NotEmpty(t.Related);
        Assert.NotEmpty(t.Keywords);
        Assert.NotEmpty(t.Sources);
        Assert.All(t.Sources, s => Assert.Contains(s.Body, RecognisedBodies));
        Assert.False(string.IsNullOrWhiteSpace(t.Review.Status));
    }

    public static IEnumerable<object[]> RequiredTopicIds() => RequiredTopics.Select(id => new object[] { id });

    private static readonly string[] RecognisedBodies =
    {
        "American Heart Association", "American Red Cross", "American Heart Association and American Red Cross",
        "European Resuscitation Council", "International Liaison Committee on Resuscitation (ILCOR)",
        "BC Centre for Disease Control", "U.S. Department of Health and Human Services"
    };

    [Fact]
    public void Scene_safety_is_always_first()
    {
        Assert.Equal("scene-safety", Library.Value.Topics[0].Id);
        Assert.True(Library.Value.Topics[0].PinnedFirst);
    }

    [Fact]
    public void Guide_has_disclaimer_and_review_fields()
    {
        var g = Library.Value.Guide;
        Assert.Contains("does not replace", g.Disclaimer);
        Assert.False(string.IsNullOrWhiteSpace(g.Review.Status));
        Assert.NotNull(g.Review.ReviewedBy);
        Assert.NotNull(g.Review.ReviewedOn);
    }

    [Fact]
    public void Placeholders_are_expanded()
    {
        var lib = Library.Value;
        var text = lib.Expand(lib.Find("cpr-adult")!.CallEmergency.Headline);
        Assert.DoesNotContain("{EMERGENCY}", text);
        Assert.Contains(lib.Guide.EmergencyNumber, text);
    }

    [Fact]
    public void Every_placeholder_in_every_topic_expands()
    {
        var lib = Library.Value;
        foreach (var t in lib.Topics)
        {
            var text = string.Join("\n", new[] { t.Title, t.Summary, t.CallEmergency.Headline }
                .Concat(t.CallEmergency.When).Concat(t.WarningSigns).Concat(t.DoNot)
                .Concat(t.Steps.SelectMany(s => new[] { s.Title, s.Text, s.Caution, s.Action?.Label ?? "" }.Concat(s.Points))));
            Assert.DoesNotContain("{", lib.Expand(text));
        }
        Assert.Contains("988", lib.Expand("{CRISIS}"));
        Assert.Equal("911", lib.Guide.EmergencyNumber);
    }

    [Fact]
    public void Every_category_and_order_is_set()
    {
        Assert.All(Library.Value.Topics, t => Assert.False(string.IsNullOrWhiteSpace(t.Category)));
        Assert.Equal(new[] { "scene-safety", "triage-start", "secondary-survey" }, Library.Value.Topics.Take(3).Select(t => t.Id));
    }

    [Fact]
    public void Topic_timers_have_expected_settings()
    {
        var bleeding = Library.Value.Find("severe-bleeding")!;
        var tq = bleeding.Steps.Select(s => s.Action).OfType<StepAction>().Single(a => a.Type == StepActionType.Timer);
        Assert.Equal("stopwatch", tq.Mode);

        var stroke = Library.Value.Find("stroke")!;
        var onset = stroke.Steps.Select(s => s.Action).OfType<StepAction>().Single(a => a.Type == StepActionType.Timer);
        Assert.True(onset.AskStartTime);

        var cpr = Library.Value.Find("cpr-adult")!;
        Assert.Contains(cpr.Steps, s => s.Action?.Type == StepActionType.Cpr);
    }

    [Fact]
    public void Validation_reports_broken_files()
    {
        var dir = TestPaths.TempFolder();
        File.WriteAllText(Path.Combine(dir, "_guide.json"), "{ \"disclaimer\": \"x\" }");
        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ \"id\": ");
        File.WriteAllText(Path.Combine(dir, "a.json"), """
            { "id": "a", "title": "A", "steps": [ { "title": "s", "action": { "type": "timer", "label": "Go", "mode": "countdown" } } ],
              "related": ["missing"] }
            """);
        var lib = FirstAidLibrary.Load(dir);
        Assert.Single(lib.Topics);
        var messages = string.Join("\n", lib.Problems);
        Assert.Contains("broken.json", messages);
        Assert.Contains("No sources recorded", messages);
        Assert.Contains("callEmergency", messages);
        Assert.Contains("countdown needs 'minutes'", messages);
        Assert.Contains("'missing' does not exist", messages);
    }

    [Fact]
    public void Missing_folder_is_reported_not_thrown()
    {
        var lib = FirstAidLibrary.Load(Path.Combine(TestPaths.TempFolder(), "nope"));
        Assert.Empty(lib.Topics);
        Assert.NotEmpty(lib.Problems);
    }
}
