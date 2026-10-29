using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fworker.Workflows.Merge;

// W7.2: the state of one merge train, a repository's: the line of pull requests that asked to be merged and the last merges. It is a JSON document in the MergeTrains table (Fworker.Persistence), saved with a
// revision so two writers cannot both win. Plain mutable classes: System.Text.Json round-trips them.

/// <summary>One item in the line: the request it last made and its place.</summary>
public sealed class MergeTrainEntry
{
    /// <summary>The qualified item, "owner/repo#N".</summary>
    public string Item { get; set; } = "";
    public int Generation { get; set; }
    /// <summary>The arm of the saga's merge step the request belongs to; a new arm is a new place in the line.</summary>
    public string ArmId { get; set; } = "";
    public int Attempt { get; set; }
    public int PullRequest { get; set; }
    public string Head { get; set; } = "";
    public int UpdatesSoFar { get; set; }
    public string UpdatedFrom { get; set; } = "";
    /// <summary>The place in the line: the lowest is first. A new entry and a rotated one take the next number.</summary>
    public long Order { get; set; }
    public DateTimeOffset EnqueuedAt { get; set; }
    /// <summary>The last time the item asked; an item that stops asking loses its place (<see cref="MergeTrainSettings.Expiry"/>).</summary>
    public DateTimeOffset LastSeen { get; set; }
    /// <summary>The turn was granted and the merge port's answer is not in yet.</summary>
    public bool Granted { get; set; }
    /// <summary>When the item first got the first place of the line (train strategy); null while it has not, and again after a rotation.</summary>
    public DateTimeOffset? HeadSince { get; set; }
    /// <summary>How many times the turn was granted.</summary>
    public int Turns { get; set; }
}

/// <summary>A merge the train saw through: kept for a short while so that an answer that arrives twice records it once.</summary>
public sealed class MergeTrainMerged
{
    public string Item { get; set; } = "";
    public string Commit { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

public sealed class MergeTrainState
{
    /// <summary>"owner/repo", lower case.</summary>
    public string Key { get; set; } = "";
    public List<MergeTrainEntry> Line { get; set; } = [];
    /// <summary>The next <see cref="MergeTrainEntry.Order"/>.</summary>
    public long NextOrder { get; set; } = 1;
    /// <summary>Counts the train's decisions.</summary>
    public long Sequence { get; set; }
    public List<MergeTrainMerged> Merged { get; set; } = [];

    public const int KeepMerged = 50;

    public static MergeTrainState New(string key) => new() { Key = key };

    /// <summary>The item first in line (the lowest place), or null for an empty line.</summary>
    [JsonIgnore]
    public MergeTrainEntry? First => Line.Count == 0 ? null : Line.MinBy(e => e.Order);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static MergeTrainState FromJson(string json) => JsonSerializer.Deserialize<MergeTrainState>(json, Json) ?? new MergeTrainState();
}
