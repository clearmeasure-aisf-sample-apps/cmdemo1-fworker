using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Messaging;

namespace Fworker.Workflows.Delivery;

// W5.2: the shadow run of the delivery train in tdd. The train runs on the board's and Octopus's facts (ItemMerged, the W4.2 watcher's events) and decides as it would; v1 keeps promoting. This
// service is the shadow's side of both: v2's decisions arrive from the train (TrainDecided) and its commands (PromoteRelease, AnswerSignOff) are RECORDED here and nowhere else (no handler of
// the shadow host executes one, and this class has no way to: it holds no command port and no client); v1's arrive from the pass of its AutoPromoter. A tick compares them (ShadowComparer),
// stores the settled comparisons (the Wolverine database) and the report reads them back: /api/v1/status "deliveryShadow" and GET /api/v1/delivery-shadow.
//
// The shadow run is done when it has run 4 hours or seen 3 app releases (the revision of 2026-10-26); Gate says so. Everything here is in memory but the stored comparisons: a restart
// starts the pending pairs again, the stored ones stay.

public sealed class DeliveryShadow(IDeliveryPolicies policies, IShadowComparisonStore store, Func<DateTimeOffset>? clock = null, TimeSpan? grace = null)
{
    public static readonly TimeSpan GateHours = TimeSpan.FromHours(4);
    public const int GateReleases = 3;
    const int MaxUnsaved = 500, MaxCommands = 50;

    readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    readonly ShadowComparer comparer = new(grace ?? ShadowComparer.DefaultGrace);
    readonly object gate = new();
    readonly List<ShadowComparisonResult> unsaved = [];
    readonly LinkedList<JsonObject> commands = new();
    readonly HashSet<string> storedReleases = [];   // the releases of the comparisons stored by this process: what the gate counts, as the stored summary does
    readonly Dictionary<string, int> counts = ShadowOutcomes.All.ToDictionary(o => o, _ => 0);
    DateTimeOffset? firstStored, lastComparison, lastPassAt;
    long v1Decisions, v2Decisions, commandsRecorded, persisted, persistFailures, v1Passes;
    string lastError = "";

    // ---- v1: what the promotion pass did -------------------------------------------------------------------------------------------------------------------

    /// <summary>The actions of one pass of the verb auto-promote. Never throws: the pass is v1's and goes on whatever happens here.</summary>
    public void ObserveV1(JsonArray actions)
    {
        try
        {
            var at = clock();
            var found = new List<ShadowDecision>();
            foreach (var node in actions)
                if (node is JsonObject a && V1Decision(a, at) is { } d) found.Add(d);
            lock (gate)
            {
                v1Passes++; lastPassAt = at;
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
            var action = ShadowActions.OfTrain(d.Action);
            if (action is null || d.Release.Length == 0) return;
            var (deployable, environment) = TrainKey.Split(d.Key);
            var decision = new ShadowDecision(ShadowSide.V2, deployable, environment, d.Release, action, d.Action, d.Reasons, d.Items, d.At);
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
            foreach (var r in comparer.Compare(now)) { unsaved.Add(r); counts[r.Outcome]++; lastComparison = now; }
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
                foreach (var r in batch) { storedReleases.Add(r.Release); if (firstStored is null || r.At < firstStored) firstStored = r.At; }
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
                ["v1"] = new JsonObject { ["passes"] = v1Passes, ["decisions"] = v1Decisions, ["lastPass"] = lastPassAt?.ToString("o") },
                ["v2"] = new JsonObject { ["decisions"] = v2Decisions, ["commandsRecorded"] = commandsRecorded, ["commandsExecuted"] = 0L },
                ["stored"] = persisted, ["storeFailures"] = persistFailures, ["lastComparison"] = lastComparison?.ToString("o"),
                ["gate"] = Gate(firstStored, storedReleases.Count),
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
        JsonObject live; JsonArray recentCommands;
        lock (gate) { live = Status(); live["gate"] = Gate(summary.First, summary.Releases); recentCommands = new JsonArray(commands.Select(c => (JsonNode?)c.DeepClone()).ToArray()); }
        var counts = ShadowOutcomes.All.ToDictionary(o => o, o => summary.Counts.GetValueOrDefault(o));
        return new JsonObject
        {
            ["ok"] = true,
            ["summary"] = new JsonObject
            {
                ["total"] = summary.Total, ["counts"] = Counts(counts), ["releases"] = summary.Releases,
                ["first"] = summary.First?.ToString("o"), ["last"] = summary.Last?.ToString("o"),
                ["gate"] = Gate(summary.First, summary.Releases),
            },
            ["live"] = live,
            ["comparisons"] = new JsonArray(rows.Select(r => (JsonNode?)r.ToJson()).ToArray()),
            ["recentCommands"] = recentCommands,
        };
    }

    /// <summary>The exit condition of the shadow run: 4 hours observed or 3 app releases compared.</summary>
    JsonObject Gate(DateTimeOffset? since, int releaseCount)
    {
        var hours = since is { } s ? Math.Max(0, (clock() - s).TotalHours) : 0;
        return new JsonObject
        {
            ["hours"] = Math.Round(hours, 2), ["releases"] = releaseCount, ["needsHours"] = GateHours.TotalHours, ["needsReleases"] = GateReleases,
            ["met"] = hours >= GateHours.TotalHours || releaseCount >= GateReleases,
        };
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
