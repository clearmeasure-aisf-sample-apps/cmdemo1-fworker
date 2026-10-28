using System.Text.Json.Nodes;

namespace Fworker.Workflows.Delivery;

// W5.2: the shadow comparison of the delivery train with v1's promotion pass (docs/design/fworker-wolverine-implementation.md W5, "a shadow run in tdd against the board, 4 hours or 3 app releases").
//
// Both sides are reduced to the same few actions per (deployable, environment, release): promote (a deployment is created), wait (the release does not qualify yet), follow (a deployment of that
// release to the environment is running or was not made by the gateway), signoff-proceed, signoff-abort and signoff-wait (the answer to the Sign-off step). v1's side is what its AutoPromoter
// prints (the actions of the verb auto-promote, the journal of its pass); v2's is the train's TrainDecided. This file is pure: no clock of its own, no I/O.

/// <summary>Which engine made a decision.</summary>
public enum ShadowSide { V1, V2 }

/// <summary>One decision of one side, normalized. <paramref name="Native"/> is the side's own name for it (promoted, promotion-waiting, ... or the train's action).</summary>
public sealed record ShadowDecision(ShadowSide Side, string Deployable, string Environment, string Release, string Action, string Native, IReadOnlyList<string> Reasons, IReadOnlyList<string> Items, DateTimeOffset At, bool Replay = false)
{
    public string Key => $"{Deployable}|{Environment}|{Release}";

    /// <summary>For a wait: what it waits for (<see cref="ShadowReasons.Classify"/>); empty for the other actions, which say nothing more.</summary>
    public string ReasonClass => Action == ShadowActions.Wait ? ShadowReasons.Classify(Reasons) : "";

    /// <summary>Whether the decision changes something outside (a deployment, a Sign-off answer). A one-sided waiting is no finding; a one-sided action is.</summary>
    public bool Actionable => Action is ShadowActions.Promote or ShadowActions.SignOffProceed or ShadowActions.SignOffAbort;

    /// <summary>The decision for the stored comparison and the report.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["action"] = Action, ["native"] = Native, ["at"] = At.ToString("o"),
            ["reasons"] = new JsonArray(Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            ["items"] = new JsonArray(Items.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
        };
        if (ReasonClass.Length > 0) o["reasonClass"] = ReasonClass;
        if (Replay) o["replay"] = true;
        return o;
    }
}

/// <summary>
/// The class of a wait, so that two waits are compared by what they wait for and not by the words of two implementations: failed (the deployment ended in a failure), none (no deployment to the
/// previous environment), started (a deployment still running), health (the previous environment's app: version, reads, answer, URL) and gates (everything else: the items' and the content's gates).
/// When a wait has several reasons the first of this order is its class. v1's wait lists only the release's common reasons (an item's gate is not among them), v2's lists both, so a wait for
/// gates alone is "gates" on both sides.
/// </summary>
public static partial class ShadowReasons
{
    public const string Failed = "failed", None = "none", Started = "started", Health = "health", Gates = "gates";

    public static string Classify(IEnumerable<string> reasons)
    {
        var best = Gates; var rank = 4;
        foreach (var r in reasons)
        {
            var (c, k) = One(r);
            if (k < rank) { best = c; rank = k; }
        }
        return best;
    }

    static (string Class, int Rank) One(string r)
    {
        if (FailedText().IsMatch(r)) return (Failed, 0);
        if (r.StartsWith("it has no deployment to ")) return (None, 1);
        if (StartedText().IsMatch(r)) return (Started, 2);
        if (r.Contains(" app ") || r.StartsWith("no health URL")) return (Health, 3);
        return (Gates, 4);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\bended (Failed|TimedOut|Canceled)\b|deployment to .+ is (Failed|TimedOut|Canceled) \(")]
    private static partial System.Text.RegularExpressions.Regex FailedText();

    [System.Text.RegularExpressions.GeneratedRegex(@"deployment to .+ is still ")]
    private static partial System.Text.RegularExpressions.Regex StartedText();
}

public static class ShadowActions
{
    public const string Promote = "promote", Wait = "wait", Follow = "follow", SignOffProceed = "signoff-proceed", SignOffAbort = "signoff-abort", SignOffWait = "signoff-wait";

    /// <summary>The train's action as a normalized one; null for those that have no counterpart in v1's pass (idle, delivered, held, observing).</summary>
    public static string? OfTrain(string action) => action switch
    {
        "promote" => Promote, "waiting" => Wait, "following" => Follow,
        "signoff-waiting" => SignOffWait, "signoff-proceed" => SignOffProceed, "signoff-abort" => SignOffAbort,
        _ => null,
    };

    /// <summary>
    /// v1's action of the verb auto-promote as a normalized one; null for those that decide nothing (promotion-error, promotion-deferred, ...). A promotion-following with a signoff says what
    /// became of the Sign-off: Proceed or Abort when the gateway answered it, waiting when it left it pending.
    /// </summary>
    public static string? OfV1(string action, string? signoff) => action switch
    {
        "promoted" => Promote,
        "promotion-waiting" => Wait,
        "promotion-following" => signoff switch
        {
            "Proceed" => SignOffProceed, "Abort" => SignOffAbort, "waiting" => SignOffWait,
            _ => Follow,
        },
        _ => null,
    };
}

/// <summary>match, v2-only, v1-only or differs.</summary>
public static class ShadowOutcomes
{
    public const string Match = "match", V2Only = "v2-only", V1Only = "v1-only", Differs = "differs";
    public static readonly string[] All = [Match, V2Only, V1Only, Differs];
}

/// <summary>A settled comparison of the two sides for one release.</summary>
public sealed record ShadowComparisonResult(DateTimeOffset At, string Deployable, string Environment, string Release, string Outcome, string Reason, ShadowDecision? V1, ShadowDecision? V2)
{
    /// <summary>The train's side is a judgment of the replayed history, not of a live event: stored and shown, not counted by the gate.</summary>
    public bool Replay => V2?.Replay == true;

    /// <summary>The train's side is a decision on a live event: what the gate counts.</summary>
    public bool Live => V2 is { Replay: false };
}

public static class ShadowClassifier
{
    /// <summary>
    /// The outcome of two decisions of the same release. The same normalized action is a match (a wait is a match whatever its words: the reasons are texts of two implementations); one side
    /// alone is v1-only or v2-only; two actions are a difference, and the reason says what each did and why.
    /// </summary>
    public static (string Outcome, string Reason) Classify(ShadowDecision? v1, ShadowDecision? v2)
    {
        if (v1 is null && v2 is null) throw new ArgumentException("a comparison needs a decision of at least one side");
        if (v1 is null) return (ShadowOutcomes.V2Only, $"v2 decided {Describe(v2!)}; v1's pass made no decision for this release");
        if (v2 is null) return (ShadowOutcomes.V1Only, $"v1 decided {Describe(v1)}; the train made no decision for this release");
        if (Agree(v1, v2)) return (ShadowOutcomes.Match, "");
        var v1Why = v1.Reasons.Count > 0 ? $" [v1: {string.Join("; ", v1.Reasons)}]" : "";
        var v2Why = v2.Reasons.Count > 0 ? $" [v2: {string.Join("; ", v2.Reasons)}]" : "";
        return (ShadowOutcomes.Differs, $"v1 decided {Describe(v1)}, v2 decided {Describe(v2)}{v1Why}{v2Why}");
    }

    /// <summary>The same action, and for a wait the same class of reason (failed, none, started, health, gates): a wait for the wrong thing is a difference.</summary>
    public static bool Agree(ShadowDecision v1, ShadowDecision v2) => v1.Action == v2.Action && v1.ReasonClass == v2.ReasonClass;

    static string Describe(ShadowDecision d) => d.ReasonClass.Length > 0 ? $"{d.Action} for {d.ReasonClass} ({d.Native})" : $"{d.Action} ({d.Native})";
}

/// <summary>
/// Pairs the decisions of the two sides. v1 repeats itself at every pass and the train publishes a decision once per change, so each side keeps its latest decision per key. A comparison is made
/// when a side has something new: an agreement at once, anything else once <c>grace</c> has passed since the newest decision (the lagging side may still catch up: v1 decides at its next pass, the
/// train when Octopus's events arrive). After the grace a missing side is v1-only or v2-only whatever the decision (a waiting too: a train that decides nothing must show as v1-only rows, not as silence). The same
/// pair is not reported twice. Not thread safe: <see cref="DeliveryShadow"/> locks.
/// </summary>
public sealed class ShadowComparer(TimeSpan grace)
{
    sealed class Slot
    {
        public ShadowDecision? V1, V2;
        public bool V1New, V2New;
        public string LastPair = "";
    }

    readonly Dictionary<string, Slot> slots = [];

    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(5);
    /// <summary>Most keys kept: a key is a release of a deployable in one environment, so this is far beyond a day of releases.</summary>
    const int MaxSlots = 2000;

    public ShadowComparer() : this(DefaultGrace) { }

    /// <summary>The keys with a decision that is not compared yet (waiting for the other side or for the grace).</summary>
    public int Tracked => slots.Values.Count(s => s.V1New || s.V2New);

    /// <summary>Takes one decision. Returns true when it is news (a different decision from the side's last for this release).</summary>
    public bool Observe(ShadowDecision d)
    {
        if (!slots.TryGetValue(d.Key, out var s)) { Trim(); slots[d.Key] = s = new Slot(); }
        if (d.Side == ShadowSide.V1)
        {
            if (s.V1 is { } last && Same(last, d)) return false;
            s.V1 = d; s.V1New = true; return true;
        }
        if (s.V2 is { } last2 && Same(last2, d)) return false;
        s.V2 = d; s.V2New = true; return true;
    }

    /// <summary>
    /// The train no longer holds a decision for these releases (delivered, superseded, not judged any more): the pending decision of v2 is dropped, uncompared, and the pair may be reported again
    /// if the train decides for the release anew. A comparison already made stays stored; v1's decision stays and is compared on its own after the grace (v1-only) unless v1 moves on.
    /// </summary>
    public void Retract(string deployable, string environment, IEnumerable<string> releases)
    {
        foreach (var release in releases)
        {
            var key = $"{deployable}|{environment}|{release}";
            if (!slots.TryGetValue(key, out var s) || s.V2 is null) continue;
            s.V2 = null; s.V2New = false; s.LastPair = "";
            if (s.V1 is null) slots.Remove(key);   // (v1's last decision stays: v1 repeats itself without being new again, and the train may decide for the release anew)
        }
    }

    static bool Same(ShadowDecision a, ShadowDecision b) => a.Action == b.Action && a.Native == b.Native && a.Reasons.SequenceEqual(b.Reasons) && a.Items.SequenceEqual(b.Items);

    /// <summary>The comparisons that can be made at <paramref name="now"/>.</summary>
    public IReadOnlyList<ShadowComparisonResult> Compare(DateTimeOffset now)
    {
        var done = new List<ShadowComparisonResult>();
        foreach (var s in slots.Values)
        {
            if (!s.V1New && !s.V2New) continue;
            var aged = now - Newest(s) >= grace;
            ShadowComparisonResult? r = null;
            if (s.V1 is not null && s.V2 is not null)
            {
                if (ShadowClassifier.Agree(s.V1, s.V2) || aged) r = Make(s, now);
            }
            else if (aged) r = Make(s, now);   // one side only after the grace: v1-only or v2-only, a waiting too (the report counts what each side did, not only what changed something)
            if (r is null) continue;
            var pair = $"{s.V1?.Action}/{s.V1?.Native}|{s.V2?.Action}/{s.V2?.Native}|{r.Outcome}";
            s.V1New = s.V2New = false;
            if (pair == s.LastPair) continue;
            s.LastPair = pair;
            done.Add(r);
        }
        return done;
    }

    static DateTimeOffset Newest(Slot s)
    {
        var t = DateTimeOffset.MinValue;
        if (s.V1New && s.V1 is not null && s.V1.At > t) t = s.V1.At;
        if (s.V2New && s.V2 is not null && s.V2.At > t) t = s.V2.At;
        return t;
    }

    static ShadowComparisonResult Make(Slot s, DateTimeOffset now)
    {
        var (outcome, reason) = ShadowClassifier.Classify(s.V1, s.V2);
        var d = (s.V1 ?? s.V2)!;
        return new ShadowComparisonResult(now, d.Deployable, d.Environment, d.Release, outcome, reason, s.V1, s.V2);
    }

    void Trim()
    {
        if (slots.Count < MaxSlots) return;
        foreach (var k in slots.Where(kv => !kv.Value.V1New && !kv.Value.V2New).Select(kv => kv.Key).Take(MaxSlots / 4).ToList()) slots.Remove(k);
    }
}
