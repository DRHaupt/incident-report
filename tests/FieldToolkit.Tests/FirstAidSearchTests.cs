using FieldToolkit.Core.FirstAid;

namespace FieldToolkit.Tests;

public class FirstAidSearchTests
{
    private static readonly Lazy<FirstAidSearch> Search = new(() => new FirstAidSearch(FirstAidLibrary.Load(TestPaths.Content("firstaid"))));

    [Theory]
    [InlineData("not breathing", "cpr-adult")]
    [InlineData("Not Breathing!", "cpr-adult")]
    [InlineData("bleeding a lot", "severe-bleeding")]
    [InlineData("blood everywhere", "severe-bleeding")]
    [InlineData("tourniquet", "severe-bleeding")]
    [InlineData("fitting", "seizures")]
    [InlineData("he's having a fit", "seizures")]
    [InlineData("convulsions", "seizures")]
    [InlineData("chest pain", "heart-attack")]
    [InlineData("face drooping", "stroke")]
    [InlineData("slurred speech", "stroke")]
    [InlineData("swollen lips", "anaphylaxis")]
    [InlineData("epipen", "anaphylaxis")]
    [InlineData("choking", "choking-adult")]
    [InlineData("baby choking", "choking-infant")]
    [InlineData("baby not breathing", "cpr-infant")]
    [InlineData("burnt hand", "burns")]
    [InlineData("scalded", "burns")]
    [InlineData("broken leg", "fractures-sprains")]
    [InlineData("twisted ankle", "fractures-sprains")]
    [InlineData("hit head", "head-injury")]
    [InlineData("can't feel legs", "spinal-injury")]
    [InlineData("low sugar", "diabetic-emergency")]
    [InlineData("diabetic", "diabetic-emergency")]
    [InlineData("overheated", "heat-illness")]
    [InlineData("shivering", "hypothermia")]
    [InlineData("pulled from water", "drowning")]
    [InlineData("electrocuted", "electric-shock")]
    [InlineData("overdose", "poisoning-overdose")]
    [InlineData("snake bite", "bites-stings")]
    [InlineData("bee sting", "bites-stings")]
    [InlineData("passed out", "unconscious-recovery")]
    [InlineData("recovery position", "unconscious-recovery")]
    [InlineData("baby coming", "childbirth")]
    [InlineData("pale and clammy", "shock")]
    [InlineData("wheezing", "asthma")]
    [InlineData("what do i do", "scene-safety")]
    [InlineData("drabc", "scene-safety")]
    public void Plain_word_queries_find_the_right_topic_first(string query, string expectedTopic)
    {
        var hits = Search.Value.Search(query);
        Assert.NotEmpty(hits);
        Assert.Equal(expectedTopic, hits[0].Topic.Id);
    }

    [Theory]
    [InlineData("can't breathe", new[] { "choking-adult", "asthma", "anaphylaxis" })]
    [InlineData("not breathing", new[] { "cpr-adult", "scene-safety" })]
    public void Broad_symptoms_list_all_likely_topics_near_the_top(string query, string[] expected)
    {
        var top = Search.Value.Search(query).Take(5).Select(h => h.Topic.Id).ToList();
        Assert.All(expected, id => Assert.Contains(id, top));
    }

    [Fact]
    public void Empty_query_lists_all_topics_in_display_order()
    {
        var hits = Search.Value.Search("  ");
        Assert.Equal(26, hits.Count);
        Assert.Equal("scene-safety", hits[0].Topic.Id);
    }

    [Fact]
    public void Nonsense_returns_nothing()
    {
        Assert.Empty(Search.Value.Search("xylophone quantum"));
    }

    [Theory]
    [InlineData("bleeding", "bleed")]
    [InlineData("fitting", "fit")]
    [InlineData("burns", "burn")]
    [InlineData("stings", "sting")]
    [InlineData("injuries", "injury")]
    [InlineData("stopped", "stop")]
    [InlineData("falling", "fall")]
    [InlineData("glass", "glass")]
    public void Stemmer_handles_common_forms(string word, string stem) => Assert.Equal(stem, FirstAidSearch.Stem(word));
}
