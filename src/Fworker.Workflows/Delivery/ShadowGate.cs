using System.Text.Json.Nodes;

namespace Fworker.Workflows.Delivery;

// W5.2: the exit condition of the shadow run, judged by what the comparisons say and not by the clock. Per live (not replayed) release in one environment the stored comparisons are folded into the
// sides seen and whether the two sides were ever compared; ShadowGate.Evaluate turns those into the gate of the report. The gate is met when at least 3 releases were compared (both sides decided,
// match or differs) and nothing live is left unexplained: no difference, no release only one side judged, except where a declared difference of v1 or v2 explains the row.

/// <summary>One stored comparison of a live decision, reduced to what the gate needs. <paramref name="ReasonClass"/> is the class of the wait (<see cref="ShadowReasons"/>) when the decision was one.</summary>
public sealed record ShadowLiveRow(string Deployable, string Environment, string Release, string Outcome, string V1Action, string V2Action, string ReasonClass, DateTimeOffset At)
{
    public static ShadowLiveRow Of(ShadowComparisonResult r) => new(r.Deployable, r.Environment, r.Release, r.Outcome, r.V1?.Action ?? "", r.V2?.Action ?? "", (r.V2 ?? r.V1)?.ReasonClass ?? "", r.At);

    /// <summary>From the stored columns: the class is in the stored JSON of the decision (v2's, else v1's).</summary>
    public static ShadowLiveRow Of(string deployable, string environment, string release, string outcome, string v1Action, string v2Action, JsonNode? v1, JsonNode? v2, DateTimeOffset at)
        => new(deployable, environment, release, outcome, v1Action, v2Action, ClassOf(v2) is { Length: > 0 } c ? c : ClassOf(v1), at);

    static string ClassOf(JsonNode? d) => d is JsonObject o && o["reasonClass"] is JsonValue v && v.TryGetValue<string>(out var s) ? s ?? "" : "";
}

/// <summary>What the live comparisons say of one release in one environment (a deployable's): the sides seen, whether they were compared, and the class of the decision they last agreed or differed on.</summary>
public sealed class ShadowLiveRelease(string deployable, string environment, string release)
{
    public string Deployable { get; } = deployable;
    public string Environment { get; } = environment;
    public string Release { get; } = release;
    public bool V1Seen { get; private set; }
    public bool V2Seen { get; private set; }
    /// <summary>A comparison with both sides (outcome match or differs) exists.</summary>
    public bool Compared { get; private set; }
    /// <summary>Some comparison with both sides is a difference.</summary>
    public bool Differs { get; private set; }
    /// <summary>The release has rows of both sides but they were never paired (v1-only and v2-only rows): the sides did not agree on anything.</summary>
    public bool Unpaired => V1Seen && V2Seen && !Compared;
    public IReadOnlyDictionary<string, int> Rows => rows;
    /// <summary>The action (v2's) and the wait class of the newest comparison with both sides.</summary>
    public string Action { get; private set; } = "";
    public string ReasonClass { get; private set; } = "";
    public DateTimeOffset FirstAt { get; private set; } = DateTimeOffset.MaxValue;
    public DateTimeOffset LastAt { get; private set; } = DateTimeOffset.MinValue;

    public string Key => $"{Deployable}|{Environment}|{Release}";

    readonly Dictionary<string, int> rows = [];
    DateTimeOffset lastBoth = DateTimeOffset.MinValue;

    public void Add(ShadowLiveRow r)
    {
        if (r.V1Action.Length > 0) V1Seen = true;
        if (r.V2Action.Length > 0) V2Seen = true;
        rows[r.Outcome] = rows.GetValueOrDefault(r.Outcome) + 1;
        if (r.At < FirstAt) FirstAt = r.At;
        if (r.At > LastAt) LastAt = r.At;
        if (r.Outcome is ShadowOutcomes.Match or ShadowOutcomes.Differs)
        {
            Compared = true;
            if (r.Outcome == ShadowOutcomes.Differs) Differs = true;
            if (r.At >= lastBoth) { lastBoth = r.At; Action = r.V2Action; ReasonClass = r.ReasonClass; }
        }
    }

    /// <summary>The class of the comparison for the report: the action, and for a wait what it waits for ("wait:failed" is a release whose deployment failed).</summary>
    public string Class => Action.Length == 0 ? "" : ReasonClass.Length > 0 ? $"{Action}:{ReasonClass}" : Action;

    public static List<ShadowLiveRelease> Fold(IEnumerable<ShadowLiveRow> rows)
    {
        var all = new Dictionary<string, ShadowLiveRelease>();
        foreach (var r in rows)
        {
            var key = $"{r.Deployable}|{r.Environment}|{r.Release}";
            if (!all.TryGetValue(key, out var x)) all[key] = x = new ShadowLiveRelease(r.Deployable, r.Environment, r.Release);
            x.Add(r);
        }
        return all.Values.OrderBy(x => x.FirstAt).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
    }
}

public static class ShadowGate
{
    public const int NeedsReleases = 3;

    /// <summary>v1 judges only the releases recorded on a Release Queue item (Get-PromoQueue groups by state.merge.release, set when the item is queued): a release that no queued item names is not judged by v1 at all.</summary>
    public const string QueueOnlyV1 = "queue-only-v1";
    /// <summary>v2 judges the newest qualifying release of a deployable and environment; v1 judges per item, so an older release a newer one superseded is judged by v1 alone.</summary>
    public const string NewestOnly = "newest-only";

    /// <summary>v1 judges a release in the first environment of the chain it has not reached (Verbs.Promote.ps1: <c>$tk</c> is the first target whose deployment is not Success) and says nothing of the later ones; v2 has a train per environment and each describes the release, so while uat waits, prod's train says "it has no deployment to uat".</summary>
    public const string ChainPosition = "chain-position";

    static readonly (string Id, string Reason)[] Declared =
    [
        (QueueOnlyV1, "v1 judges only releases that a Release Queue item names (Get-PromoQueue groups by state.merge.release, set at queue time): a v2-only row for a release no queued item names is not a difference, and it is not a comparison either"),
        (ChainPosition, "v1 judges a release in the first environment it has not reached and says nothing of the later ones; v2 has a train per environment, and the later ones say they wait for the earlier one: a v2-only row for a release v1 judges in an earlier environment of the same deployable is not a difference"),
        (NewestOnly, "v2 judges the newest qualifying release (#496); v1 judges per item: a v1-only row for an older release that a newer release v2 decided supersedes is not a difference"),
    ];

    /// <summary>
    /// The gate. <paramref name="v1Named"/> are the releases the passes of v1 named in this process (any action, also those that decide nothing): a v2-only release v1 named was queued and is no declared difference.
    /// <paramref name="firstLive"/> and <paramref name="liveV2Releases"/> are the hours and the count of releases the train decided live: information, they decide nothing.
    /// </summary>
    public static JsonObject Evaluate(IReadOnlyList<ShadowLiveRelease> seen, IReadOnlySet<string> v1Named, DateTimeOffset? firstLive, DateTimeOffset now, int liveV2Releases)
    {
        var hours = firstLive is { } s ? Math.Max(0, (now - s).TotalHours) : 0;
        var compared = seen.Where(x => x.Compared).ToList();
        var differs = seen.Where(x => x.Differs || x.Unpaired).ToList();
        var v1Only = seen.Where(x => x.V1Seen && !x.V2Seen).ToList();
        var v2Only = seen.Where(x => x.V2Seen && !x.V1Seen).ToList();
        var v1Judged = seen.Where(x => x.V1Seen).Select(x => x.Release).ToHashSet(StringComparer.Ordinal);

        var queueOnly = v2Only.Where(x => !v1Named.Contains(x.Release) && !v1Judged.Contains(x.Release)).ToList();
        var newestOnly = v1Only.Where(x => seen.Any(n => n.V2Seen && n.Deployable == x.Deployable && n.Environment == x.Environment && CompareReleases(n.Release, x.Release) > 0)).ToList();
        // (V2.1) v1 saw this release of this deployable in another environment: it is waiting there, and the row of the later environment is v2's description of the same wait. The 6 unexplained rows of tdd on 2026-11-01 were these:
        // prod rows of the releases 2.4.1228 to 2.4.1288, which v1 judged for uat. (The rule is by rule, not by the list of releases: a later release v1 judges is covered the same way.)
        var chainPosition = v2Only.Where(x => !queueOnly.Contains(x) && seen.Any(n => n.V1Seen && n.Deployable == x.Deployable && n.Release == x.Release && n.Environment != x.Environment
            && DeliveryPolicy.Chain.ToList().IndexOf(n.Environment.ToLowerInvariant()) is var ne and >= 0 && ne < DeliveryPolicy.Chain.ToList().IndexOf(x.Environment.ToLowerInvariant()))).ToList();
        var declaredKeys = queueOnly.Concat(newestOnly).Concat(chainPosition).Select(x => x.Key).ToHashSet();
        var unexplained = differs.Count + v1Only.Count + v2Only.Count - declaredKeys.Count;
        var comparedReleases = compared.Select(x => x.Release).Distinct(StringComparer.Ordinal).Count();

        JsonArray Releases(IEnumerable<ShadowLiveRelease> l) => new(l.Select(x => x.Release).Distinct(StringComparer.Ordinal).Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        return new JsonObject
        {
            // the hours and the count of the train's live releases are information; the gate is the comparisons (comparedReleases, unexplainedLive)
            ["hours"] = Math.Round(hours, 2), ["releases"] = liveV2Releases, ["needsReleases"] = NeedsReleases,
            ["comparedReleases"] = comparedReleases, ["liveDiffers"] = differs.Count, ["liveV1Only"] = v1Only.Count, ["liveV2Only"] = v2Only.Count,
            ["declared"] = new JsonArray(Declared.Select(d => (JsonNode?)new JsonObject
            {
                ["id"] = d.Id, ["reason"] = d.Reason, ["releases"] = Releases(d.Id == QueueOnlyV1 ? queueOnly : d.Id == ChainPosition ? chainPosition : newestOnly),
            }).ToArray()),
            ["unexplainedLive"] = unexplained,
            // per compared release: the outcome (match or differs) and what the sides decided (promote, follow, wait:failed ...): all in a failed class would be no evidence of a good deployment
            ["outcomeClasses"] = new JsonArray(compared.Select(x => (JsonNode?)new JsonObject
            {
                ["deployable"] = x.Deployable, ["environment"] = x.Environment, ["release"] = x.Release, ["outcome"] = x.Differs ? ShadowOutcomes.Differs : ShadowOutcomes.Match, ["class"] = x.Class,
            }).ToArray()),
            ["met"] = comparedReleases >= NeedsReleases && unexplained == 0,
        };
    }

    /// <summary>Orders release names by their numbers (1.0.10 after 1.0.9); names without numbers by their text.</summary>
    public static int CompareReleases(string a, string b)
    {
        var pa = a.Split('.', '-', '+'); var pb = b.Split('.', '-', '+');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            if (i >= pa.Length) return -1;
            if (i >= pb.Length) return 1;
            var c = long.TryParse(pa[i], out var x) && long.TryParse(pb[i], out var y) ? x.CompareTo(y) : string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return 0;
    }
}
