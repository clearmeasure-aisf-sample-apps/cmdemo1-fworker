using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Messaging;

namespace Fworker.Workflows.Delivery;

// W5.2: the shadow run of the delivery train in tdd. The train runs on the board's and Octopus's facts (ItemMerged, the W4.2 watcher's events) and decides as it would; v1 keeps promoting. This
// service is the shadow's side of both: v2's decisions arrive from the train (TrainDecided) and its commands (PromoteRelease, AnswerSignOff) are RECORDED here and nowhere else (no handler of
// the shadow host executes one, and this class has no way to: it holds no command port and no client); v1's arrive from the pass of its AutoPromoter. A tick compares them (ShadowComparer),
// stores the settled comparisons (the Wolverine database) and the report reads them back: /api/v1/status "deliveryShadow" and GET /api/v1/delivery-shadow.
//
// The shadow run is done when the comparisons say so (ShadowGate, the revision of 2026-10-26 and its honest form): at least 3 releases were compared by both sides and no live difference or one-sided
// release is left unexplained (a declared difference of v1 and v2 explains one). The hours the run has lasted are shown and decide nothing. Everything here is in memory but the stored comparisons:
// a restart starts the pending pairs again, the stored ones stay.

public sealed class DeliveryShadow(IDeliveryPolicies policies, IShadowComparisonStore store, Func<DateTimeOffset>? clock = null, TimeSpan? grace = null)
{
    const int MaxUnsaved = 500, MaxCommands = 50, MaxNamed = 5000;

    readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    readonly ShadowComparer comparer = new(grace ?? ShadowComparer.DefaultGrace);
    readonly object gate = new();
    readonly List<ShadowComparisonResult> unsaved = [];
    readonly LinkedList<JsonObject> commands = new();
    readonly HashSet<string> storedReleases = [];   // the releases with a live decision of the train among the comparisons stored by this process: shown by the gate, as the stored summary does
    readonly Dictionary<string, ShadowLiveRelease> stored = [];   // what the live comparisons stored by this process say of each release in each environment: what the gate judges, as the stored summary does
    readonly HashSet<string> v1Named = new(StringComparer.Ordinal);   // the releases any action of v1's passes named (also those that decide nothing): a queued item named them
    readonly Dictionary<string, int> counts = ShadowOutcomes.All.ToDictionary(o => o, _ => 0);
    DateTimeOffset? firstStored, lastComparison, lastPassAt;
    long v1Decisions, v2Decisions, commandsRecorded, persisted, persistFailures, v1Passes, retracted, replayComparisons;
    readonly Dictionary<string, long> v1Undecided = [];   // v1's actions that decide nothing (promotion-deferred, promotion-error) by name: a release v1 did not judge shows here, not as a gap
    string lastError = "";
    DateTimeOffset? replayingSince;   // the train replays Octopus's history after a restart: v1's decisions are held, not compared, until the train has decided something (or ReplayHold has passed)

    /// <summary>Longest a restart's replay holds the comparison back (the train's own ReplayLimit): a train that decides nothing must show as v1-only rows in the end.</summary>
    public static readonly TimeSpan ReplayHold = DeliveryTrainService.ReplayLimit;

    /// <summary>The watcher begins to replay Octopus's history (a restart). Idempotent: the first call counts.</summary>
    public void ReplayBegan() { lock (gate) replayingSince ??= clock(); }

    // ---- v1: what the promotion pass did -------------------------------------------------------------------------------------------------------------------

    /// <summary>The actions of one pass of the verb auto-promote. Never throws: the pass is v1's and goes on whatever happens here.</summary>
    public void ObserveV1(JsonArray actions)
    {
        try
        {
            var at = clock();
            var found = new List<ShadowDecision>();
            var undecided = new List<string>();
            var named = new List<string>();
            foreach (var node in actions)
                if (node is JsonObject a)
                {
                    if (Text(a["release"]) is { Length: > 0 } namedRelease) named.Add(namedRelease);
                    if (V1Decision(a, at) is { } d) found.Add(d);
                    else if (Text(a["action"]) is { Length: > 0 } name && ShadowActions.OfV1(name, Text(a["signoff"])) is null) undecided.Add(name);   // promotion-deferred, promotion-error: v1 judged nothing for that release
                }
            lock (gate)
            {
                v1Passes++; lastPassAt = at;
                if (v1Named.Count > MaxNamed) v1Named.Clear();
                foreach (var release in named) v1Named.Add(release);
                foreach (var name in undecided) v1Undecided[name] = v1Undecided.GetValueOrDefault(name) + 1;
                foreach (var d in found) { v1Decisions++; comparer.Observe(d); }
            }
        }
        catch (Exception e) { lock (gate) lastError = $"v1 pass: {e.GetType().Name}"; }
    }

    ShadowDecision? V1Decision(JsonObject a, DateTimeOffset at)
    {
        var name = Text(a["action"]);
        var action = ShadowActions.OfV1(name, Text(a["signoff"]));
        var release = Text(a["release"]); var envName = Text(a["environment"]);
        if (action is null || release.Length == 0 || envName.Length == 0) return null;
        // the Octopus name of the environment to its key, and the deployable that promotes there (v1 promotes one project)
        var policy = policies.ForOctopusEvents().Select(p => (Policy: p, Key: p.KeyOf(envName))).Where(x => x.Key == x.Policy.Environment).Select(x => x.Policy).FirstOrDefault();
        if (policy is null) return null;
        var items = a["items"] is JsonArray ia ? ia.Select(Text).Where(t => t.Length > 0).ToList() : Text(a["item"]) is { Length: > 0 } one ? [one] : [];
        var reasons = a["reasons"] is JsonArray ra ? ra.Select(Text).Where(t => t.Length > 0).ToList() : [];
        var native = name == "promotion-following" && Text(a["signoff"]) is { Length: > 0 } so ? $"{name}, signoff {so}" : name;
        return new ShadowDecision(ShadowSide.V1, policy.Deployable, policy.Environment, release, action, native, reasons, items, at);
    }

    // ---- v2: what the train decided, and the commands it would have sent ------------------------------------------------------------------------------------

    public void ObserveV2(TrainDecided d)
    {
        try
        {
            var (deployable, environment) = TrainKey.Split(d.Key);
            lock (gate) replayingSince = null;   // the train has judged its state: v1's decisions can be compared with it
            // the train says a release is done with (delivered, with those it superseded) or not judged any more (idle): what the shadow held for it is retracted, not compared
            if (d.Action is "delivered" or "idle")
            {
                IEnumerable<string> gone = d.Release.Length > 0 ? [d.Release, .. d.Superseded ?? []] : [];
                lock (gate) { retracted++; comparer.Retract(deployable, environment, gone); }
                return;
            }
            var action = ShadowActions.OfTrain(d.Action);
            if (action is null || d.Release.Length == 0) return;
            var decision = new ShadowDecision(ShadowSide.V2, deployable, environment, d.Release, action, d.Action, d.Reasons, d.Items, d.At, d.Replay);
            lock (gate) { v2Decisions++; comparer.Observe(decision); }
        }
        catch (Exception e) { lock (gate) lastError = $"v2 decision: {e.GetType().Name}"; }
    }

    /// <summary>A command of the train in shadow: counted and kept (the last few) for the report. Nothing is sent.</summary>
    public void RecordCommand(IntegrationCommand command)
    {
        var line = new JsonObject { ["at"] = clock().ToString("o"), ["command"] = command.Name, ["idempotencyKey"] = command.IdempotencyKey, ["executed"] = false };
        lock (gate)
        {
            commandsRecorded++;
            commands.AddFirst(line);
            while (commands.Count > MaxCommands) commands.RemoveLast();
        }
    }


    // ---- the comparison --------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Compares what can be compared and stores it. A store that does not answer keeps the comparisons for the next tick (up to a bound).</summary>
    public async Task Tick(CancellationToken ct)
    {
        List<ShadowComparisonResult> batch;
        lock (gate)
        {
            var now = clock();
            if (replayingSince is { } since && now - since >= ReplayHold) replayingSince = null;
            // while the train still replays, a missing v2 side is not "the train made no decision": v1's decisions wait (no v1-only rows)
            foreach (var r in replayingSince is null ? comparer.Compare(now) : []) { unsaved.Add(r); counts[r.Outcome]++; lastComparison = now; }
            if (unsaved.Count > MaxUnsaved) unsaved.RemoveRange(0, unsaved.Count - MaxUnsaved);
            batch = unsaved.ToList();
        }
        if (batch.Count == 0) return;
        try
        {
            await store.Add(batch, ct);
            lock (gate)
            {
                unsaved.RemoveAll(batch.Contains); persisted += batch.Count;
                foreach (var r in batch)
                {
                    if (r.Replay) { replayComparisons++; continue; }   // a judgment of replayed history is stored and shown, and counts for nothing
                    var row = ShadowLiveRow.Of(r);   // (a v1-only row counts for the gate as what it is: a release only v1 judged)
                    var key = $"{row.Deployable}|{row.Environment}|{row.Release}";
                    if (!stored.TryGetValue(key, out var seen)) stored[key] = seen = new ShadowLiveRelease(row.Deployable, row.Environment, row.Release);
                    seen.Add(row);
                    if (!r.Live) continue;   // v1's decision alone is no evidence that the train ran
                    storedReleases.Add(r.Release);
                    if (firstStored is null || r.At < firstStored) firstStored = r.At;
                }
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            lock (gate) { persistFailures++; lastError = $"store: {e.GetType().Name}"; }
        }
    }

    // ---- the report ------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The block of /api/v1/status: counts since this process started, no database read.</summary>
    public JsonObject Status()
    {
        lock (gate)
        {
            var o = new JsonObject
            {
                ["mode"] = "shadow", ["comparisons"] = Counts(counts), ["pending"] = comparer.Tracked, ["unsaved"] = unsaved.Count,
                ["v1"] = new JsonObject { ["passes"] = v1Passes, ["decisions"] = v1Decisions, ["undecided"] = new JsonObject(v1Undecided.Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value)))), ["lastPass"] = lastPassAt?.ToString("o") },
                ["v2"] = new JsonObject { ["decisions"] = v2Decisions, ["retracted"] = retracted, ["replayComparisons"] = replayComparisons, ["commandsRecorded"] = commandsRecorded, ["commandsExecuted"] = 0L },
                ["stored"] = persisted, ["storeFailures"] = persistFailures, ["lastComparison"] = lastComparison?.ToString("o"),
                ["gate"] = Gate(firstStored, storedReleases.Count, stored.Values.ToList()),
            };
            if (lastError.Length > 0) o["lastError"] = lastError;
            return o;
        }
    }

    /// <summary>The report of GET /api/v1/delivery-shadow: the stored summary and the newest comparisons (read from the database), with this process's live counts.</summary>
    public async Task<JsonObject> Report(int limit, string? outcome, CancellationToken ct)
    {
        var summary = await store.Summary(ct);
        var rows = await store.Recent(limit, outcome, ct);
        JsonObject live, storedGate; JsonArray recentCommands;
        lock (gate) { live = Status(); storedGate = Gate(summary.FirstLive, summary.LiveReleases, summary.LiveSeen ?? []); live["gate"] = storedGate.DeepClone(); recentCommands = new JsonArray(commands.Select(c => (JsonNode?)c.DeepClone()).ToArray()); }
        var counts = ShadowOutcomes.All.ToDictionary(o => o, o => summary.Counts.GetValueOrDefault(o));
        return new JsonObject
        {
            ["ok"] = true,
            ["summary"] = new JsonObject
            {
                // both are shown: everything stored (the replay of history included) and what the gate counts, the live decisions of the train
                ["total"] = summary.Total, ["counts"] = Counts(counts), ["releases"] = summary.Releases,
                ["replay"] = new JsonObject { ["comparisons"] = summary.Replays },
                ["live"] = new JsonObject
                {
                    ["comparisons"] = summary.Total - summary.Replays, ["releases"] = summary.LiveReleases, ["first"] = summary.FirstLive?.ToString("o"),
                    ["counts"] = Counts(ShadowOutcomes.All.ToDictionary(o => o, o => summary.LiveCounts?.GetValueOrDefault(o) ?? 0)),
                    // the sides seen per live release in each environment, and whether the two were compared: what the gate is made of
                    ["seen"] = new JsonArray((summary.LiveSeen ?? []).Select(x => (JsonNode?)Seen(x)).ToArray()),
                },
                ["first"] = summary.First?.ToString("o"), ["last"] = summary.Last?.ToString("o"),
                ["gate"] = storedGate,
            },
            ["live"] = live,
            ["comparisons"] = new JsonArray(rows.Select(r => (JsonNode?)r.ToJson()).ToArray()),
            ["recentCommands"] = recentCommands,
        };
    }

    /// <summary>The exit condition of the shadow run (<see cref="ShadowGate"/>): 3 compared releases and nothing live unexplained. The hours since the first live comparison are information.</summary>
    JsonObject Gate(DateTimeOffset? since, int liveReleases, IReadOnlyList<ShadowLiveRelease> seen) => ShadowGate.Evaluate(seen, v1Named, since, clock(), liveReleases);

    static JsonObject Seen(ShadowLiveRelease x)
    {
        var o = new JsonObject
        {
            ["deployable"] = x.Deployable, ["environment"] = x.Environment, ["release"] = x.Release, ["v1"] = x.V1Seen, ["v2"] = x.V2Seen, ["compared"] = x.Compared,
            ["rows"] = new JsonObject(x.Rows.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value)))),
        };
        if (x.Class.Length > 0) o["class"] = x.Class;
        return o;
    }

    static JsonObject Counts(IReadOnlyDictionary<string, int> c)
    {
        var o = new JsonObject();
        foreach (var k in ShadowOutcomes.All) o[k] = c.GetValueOrDefault(k);
        return o;
    }

    static string Text(JsonNode? n) => n switch { null => "", JsonValue v when v.TryGetValue<string>(out var s) => s ?? "", JsonValue v => v.ToJsonString().Trim('"'), _ => "" };
}

/// <summary>
/// The shadow host's handlers of the train's outputs. They only record: a PromoteRelease or an AnswerSignOff that the train would send is counted and kept here, and no other handler of these
/// commands exists in a shadow host (the executor joins with the cutover). Public because Wolverine's generated code refers to them.
/// </summary>
public static class DeliveryShadowHandlers
{
    public static void Handle(TrainDecided message, DeliveryShadow shadow) => shadow.ObserveV2(message);
    public static void Handle(PromoteRelease message, DeliveryShadow shadow) => shadow.RecordCommand(message);
    public static void Handle(AnswerSignOff message, DeliveryShadow shadow) => shadow.RecordCommand(message);
}
