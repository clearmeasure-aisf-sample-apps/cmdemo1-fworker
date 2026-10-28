using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;

namespace Fworker.Integrations.Correlation;

/// <summary>
/// The writers of the correlation map, as pure functions from what a source knows to the mappings it implies (W4.1). Three sources: a webhook or poll event
/// (the rule verb ingest-event's object: item, pr, sha), a verb's answer (a pull request or branch the verb opened) and a runtime (the task or agent id a
/// start returned). Keys are qualified: "owner/repo!N", "owner/repo@sha", "owner/repo:branch"; the generation is not known to these sources (0).
/// </summary>
public static class CorrelationWriters
{
    public const int GenerationUnknown = 0;

    public static IReadOnlyList<CorrelationRef> FromEvent(JsonObject ev, RepoId home, string source)
    {
        var item = Int(ev["item"]);
        if (item <= 0) return [];
        var key = new ItemId(home, item).ToString();
        var list = new List<CorrelationRef>();
        if (Int(ev["pr"]) is > 0 and var pr) list.Add(new("pr", $"{home}!{pr}", key, GenerationUnknown, source));
        if (Str(ev["sha"]) is { Length: > 0 } sha && Str(ev["event"]) is "pr.opened" or "pr.head_changed") list.Add(new("commit", $"{home}@{sha.ToLowerInvariant()}", key, GenerationUnknown, source));
        return list;
    }

    /// <summary>A verb's answer for an item: the pull request number and the branch it names, when it names them.</summary>
    public static IReadOnlyList<CorrelationRef> FromVerb(string verb, int item, JsonObject output, RepoId home)
    {
        if (item <= 0) return [];
        var key = new ItemId(home, item).ToString();
        var list = new List<CorrelationRef>();
        var pr = Int(output["pr"]); if (pr <= 0) pr = Int(output["pr_number"]);
        if (pr > 0) list.Add(new("pr", $"{home}!{pr}", key, GenerationUnknown, "verb:" + verb));
        if (Str(output["branch"]) is { Length: > 0 } b) list.Add(new("branch", $"{home}:{b}", key, GenerationUnknown, "verb:" + verb));
        return list;
    }

    /// <summary>A runtime session's execution (the Copilot task id, the Cursor agent id, an Octopus execution) for the item it was started for.</summary>
    public static IReadOnlyList<CorrelationRef> FromRuntime(string runtime, string execution, int item, RepoId home)
    {
        if (item <= 0 || string.IsNullOrWhiteSpace(execution)) return [];
        var kind = runtime.Equals("cursor", StringComparison.OrdinalIgnoreCase) ? "agent" : "task";
        return [new(kind, execution, new ItemId(home, item).ToString(), GenerationUnknown, "runtime:" + runtime)];
    }

    static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
