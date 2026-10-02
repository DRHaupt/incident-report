using System.Text.Json.Serialization;

namespace FieldToolkit.Core.FirstAid;

/// <summary>Guide-wide settings from <c>_guide.json</c>: disclaimer, review record, local numbers and search synonyms.</summary>
public sealed class FirstAidGuide
{
    public string Title { get; set; } = "First Aid & Emergency Guide";
    public string ContentVersion { get; set; } = "";
    public string Disclaimer { get; set; } = "";
    public ReviewRecord Review { get; set; } = new();

    /// <summary>Replaces <c>{EMERGENCY}</c> in topic text, e.g. "911 / 112".</summary>
    public string EmergencyNumber { get; set; } = "911 / 112";

    /// <summary>Replaces <c>{POISON}</c> in topic text.</summary>
    public string PoisonCentre { get; set; } = "";

    public List<SynonymGroup> Synonyms { get; set; } = new();
}

/// <summary>Plain-language words and phrases that point straight at particular topics ("fitting" → seizures).</summary>
public sealed class SynonymGroup
{
    public List<string> Phrases { get; set; } = new();
    public List<string> Topics { get; set; } = new();
}

public sealed class ReviewRecord
{
    /// <summary>e.g. "Draft – awaiting clinical review" or "Reviewed".</summary>
    public string Status { get; set; } = "";
    public string ReviewedBy { get; set; } = "";
    public string ReviewedOn { get; set; } = "";
    public string Notes { get; set; } = "";

    [JsonIgnore]
    public bool IsReviewed => !string.IsNullOrWhiteSpace(ReviewedBy) && !string.IsNullOrWhiteSpace(ReviewedOn);
}

public sealed class FirstAidTopic
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public int Order { get; set; } = 100;

    /// <summary>Always listed first (scene safety / primary survey).</summary>
    public bool PinnedFirst { get; set; }

    public string Summary { get; set; } = "";

    /// <summary>Plain words and symptoms a bystander might type ("not breathing", "blue lips").</summary>
    public List<string> Keywords { get; set; } = new();

    public CallEmergency CallEmergency { get; set; } = new();
    public List<FirstAidStep> Steps { get; set; } = new();
    public List<string> WarningSigns { get; set; } = new();
    public List<string> DoNot { get; set; } = new();
    public List<string> Related { get; set; } = new();
    public List<ContentSource> Sources { get; set; } = new();
    public ReviewRecord Review { get; set; } = new();

    /// <summary>The file the topic was loaded from.</summary>
    [JsonIgnore]
    public string SourceFile { get; set; } = "";
}

public sealed class CallEmergency
{
    public string Headline { get; set; } = "";
    public List<string> When { get; set; } = new();
}

public sealed class FirstAidStep
{
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public List<string> Points { get; set; } = new();

    /// <summary>Text shown in a warning box on the step (e.g. "Pain is expected – keep tightening").</summary>
    public string Caution { get; set; } = "";

    public StepAction? Action { get; set; }
}

public enum StepActionType
{
    /// <summary>Start a timer in the shared timer system.</summary>
    Timer,
    /// <summary>Open the CPR helper (metronome and compression counter).</summary>
    Cpr,
    /// <summary>Open another topic.</summary>
    Topic
}

public sealed class StepAction
{
    public StepActionType Type { get; set; }

    /// <summary>Button caption, e.g. "Start tourniquet timer".</summary>
    public string Label { get; set; } = "";

    /// <summary>Timer: the timer's name, e.g. "Tourniquet applied".</summary>
    public string TimerLabel { get; set; } = "";

    /// <summary>Timer: "stopwatch" (counts up) or "countdown".</summary>
    public string Mode { get; set; } = "stopwatch";

    /// <summary>Countdown length in minutes.</summary>
    public double Minutes { get; set; }

    public int RemindEveryMinutes { get; set; }
    public int AlertAfterMinutes { get; set; }

    /// <summary>Timer: let the user enter an earlier start time (e.g. when symptoms began).</summary>
    public bool AskStartTime { get; set; }

    /// <summary>Topic: the topic to open. CPR: the CPR helper preset ("adult", "child", "infant").</summary>
    public string Target { get; set; } = "";
}

public sealed class ContentSource
{
    public string Body { get; set; } = "";
    public string Title { get; set; } = "";
    public string Section { get; set; } = "";
    public int? Year { get; set; }
    public string Url { get; set; } = "";
}
