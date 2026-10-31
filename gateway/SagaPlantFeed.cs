using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Workflows.WorkItem;

// W9.10k: the plant fed from saga state. Where the engine acts, the sweep leaves the items that have a saga alone (skip_items, SweepV2Filter), so everything the sweep's answer says about them is gone: the plant would see no
// owner's lease, no park, no pin, no priority, no runtime label and no Release Queue entry for them, and after two sweeps it would drop their owners (Plant.ReplaceOwned). This is the adapter that builds the same fields from the
// saga's state (V2Ownership.States: the step and its column, the lease, the park, the Deliver step) and from the labels of the reconciler's cards (BoardReconciler.LastCards), and adds them to the sweep's answer after the
// filter, so the plant, the keeper, the status, the patrol digest and the manager read saga items as they read v1's. It adds nothing for an item without a saga, and nothing where the engine does not act (no feed is made).
//   - owned / owners: the plant's own owner of the item (its record of the accept; the claim itself is in the item state comment and the claim ref, which the plant corrects from at start), with the column of the saga's step, the
//     step's lease as lease_until (the digest's stalled and lease-warning rules) and whether the item is parked;
//   - parks: every parked saga item with its park code (the text before the colon of the reason) and when the park ends, for the status's list of unowned items;
//   - release_queue: the open items on the Deliver step with a merge, waiting for the delivery train; the release is the one the item reached, if any;
//   - pins, priorities, runtimes, profiles and holds: from the card's labels, by the rules of the rule script (worker:<name>, the priority labels, runtime:<kind>, the test label, the owner's hold label).
// The patrol digest reads the parked items of V2ItemViews (SagaAttention) and the release check counts what the saga has in flight (SagaFlight).

/// <summary>The label names the sweep reads (config/instance.json): the priority labels, the test label and the owner's hold label.</summary>
sealed record SagaLabelRules(string TopLabel, string HighLabel, string TestLabel, string OwnerHoldLabel)
{
    static readonly Regex RuntimeRx = new("^runtime:[a-z0-9-]{1,20}$", RegexOptions.CultureInvariant);

    public static SagaLabelRules From(JsonNode? root)
    {
        static string Text(JsonNode? n, string fallback) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : fallback;
        return new SagaLabelRules(Text(root?["priorityLabels"]?["top"], "priority:top"), Text(root?["priorityLabels"]?["high"], "priority:high"), Text(root?["testLabel"], ""), Text(root?["ownerHoldLabel"], "hold:owner"));
    }

    static bool Has(IEnumerable<string> labels, string name) => name.Length > 0 && labels.Any(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The worker a label pins the item to (the first <c>worker:</c> label), or null.</summary>
    public string? Pin(IEnumerable<string> labels) => labels.FirstOrDefault(l => l.StartsWith("worker:", StringComparison.OrdinalIgnoreCase)) is { Length: > 7 } l ? l[7..] : null;

    /// <summary>"top", "high" or null, as Get-ItemPriority: top wins.</summary>
    public string? Priority(IEnumerable<string> labels) => Has(labels, TopLabel) ? "top" : Has(labels, HighLabel) ? "high" : null;

    /// <summary>The runtime a <c>runtime:&lt;kind&gt;</c> label forces, or null.</summary>
    public string? Runtime(IEnumerable<string> labels) => labels.FirstOrDefault(l => RuntimeRx.IsMatch(l)) is { } l ? l[8..] : null;

    public bool Test(IEnumerable<string> labels) => Has(labels, TestLabel);

    public bool OwnerHold(IEnumerable<string> labels) => Has(labels, OwnerHoldLabel);
}

/// <summary>A saga item the patrol digest reports: parked, with the park's code and since when.</summary>
sealed record SagaAttention(int Item, string Kind, string Code, DateTime Since);

/// <summary>A saga item with work in flight that a restart would interrupt: its code and the step it is on.</summary>
sealed record SagaFlight(int Item, string Code, string Step);

sealed class SagaPlantFeed(V2Ownership ownership, Func<int, string?> ownerOf, Func<int, IReadOnlyList<string>?> labelsOf, SagaLabelRules rules, V2ItemViews? views = null, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    static string Iso(DateTimeOffset? t) => t is { } x ? x.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) : "";

    /// <summary>The fields of the sweep's answer for the items of the saga, from the states the ownership read last.</summary>
    public JsonObject Build()
    {
        var owned = new JsonObject(); var owners = new JsonObject(); var pins = new JsonObject(); var priorities = new JsonObject(); var runtimes = new JsonObject(); var profiles = new JsonObject();
        var parks = new JsonArray(); var queue = new JsonArray(); var holds = new JsonArray();
        foreach (var s in ownership.States.OrderBy(s => s.ItemId.Number))
        {
            if (!s.IsOpen) continue;
            var n = s.ItemId.Number; var key = n.ToString(CultureInfo.InvariantCulture);
            var column = ownership.Column(s.Step) ?? "";
            if (labelsOf(n) is { } labels)
            {
                if (rules.Pin(labels) is { } pin) pins[key] = pin;
                if (rules.Priority(labels) is { } priority) priorities[key] = priority;
                if (rules.Runtime(labels) is { } runtime) runtimes[key] = runtime;
                if (rules.Test(labels)) profiles[key] = "testing";
                if (rules.OwnerHold(labels)) holds.Add(n);
            }
            if (ownerOf(n) is { Length: > 0 } holder)
            {
                owners[key] = holder;
                owned[key] = new JsonObject { ["holder"] = holder, ["column"] = column, ["lease_until"] = s.LeaseDue is null ? null : Iso(s.LeaseDue), ["heartbeat"] = null, ["parked"] = s.Parked };
            }
            if (s.Parked)
                parks.Add(new JsonObject { ["item"] = n, ["column"] = column, ["code"] = SweepParks.CodeOf(s.ParkReason), ["since"] = null, ["until"] = s.ParkUntil is null ? null : Iso(s.ParkUntil) });
            // the Deliver step with a merge: waiting for the delivery train. The release is the newest the item reached (tdd, uat, prod), or none yet.
            if (s.Status == WorkItemStatus.Open && s.Step == StepKind.Deliver && s.MergeCommit.Length > 0 && !s.Parked)
                queue.Add(new JsonObject { ["item"] = n, ["release"] = ReleaseOf(s), ["since"] = null });
        }
        return new JsonObject { ["owned"] = owned, ["owners"] = owners, ["pins"] = pins, ["priorities"] = priorities, ["runtimes"] = runtimes, ["profiles"] = profiles, ["parks"] = parks, ["release_queue"] = queue, ["holds"] = holds };
    }

    static string ReleaseOf(WorkItemState s) => new[] { "prod", "uat", "tdd" }.Select(e => s.Delivered.GetValueOrDefault(e, "")).FirstOrDefault(r => r.Length > 0) ?? "";

    /// <summary>
    /// Adds <see cref="Build"/>'s fields to the sweep's answer, after SweepV2Filter took the saga items out of it: the keyed objects get the saga items' keys, the lists their entries. The rule script's answer may hold a one-entry
    /// list as an object (PowerShell), which becomes a list. Returns how many entries were added.
    /// </summary>
    public int Apply(JsonNode sweep)
    {
        if (sweep is not JsonObject answer) return 0;
        var extra = Build(); var added = 0;
        foreach (var name in new[] { "owned", "owners", "pins", "priorities", "runtimes", "profiles" })
        {
            if (extra[name] is not JsonObject add || add.Count == 0) continue;
            if (answer[name] is not JsonObject target) answer[name] = target = [];
            foreach (var kv in add.ToList()) { add.Remove(kv.Key); target[kv.Key] = kv.Value; added++; }
        }
        foreach (var name in new[] { "parks", "release_queue", "holds" })
        {
            if (extra[name] is not JsonArray add || add.Count == 0) continue;
            var target = answer[name] switch { JsonArray a => a, JsonObject o => new JsonArray(o.DeepClone()), _ => [] };
            answer[name] = target;
            foreach (var x in add.ToList()) { add.Remove(x); target.Add(x); added++; }
        }
        return added;
    }

    /// <summary>Reads the views of the saga's items once (a read of the last five seconds is reused), so that the patrol has them without waiting on the database. A read that fails keeps the last answer.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await ownership.RefreshAsync(ct);
        await RefreshViewsAsync(ct);
    }

    /// <summary>Only the views (the sweep has read the ownership already).</summary>
    public async Task RefreshViewsAsync(CancellationToken ct = default)
    {
        if (views is not null) { try { await views.Read(ct); } catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"saga views: {e.GetType().Name}"); } }
    }

    /// <summary>The saga items the patrol digest reports, from the last read of V2ItemViews: the parked ones, with the park's code and since when the park began (the newest "parked" decision of the history, else the last update).</summary>
    public IReadOnlyList<SagaAttention> Attention()
    {
        var answer = views?.Latest;
        if (answer?.Set is not { } set) return [];
        var list = new List<SagaAttention>();
        foreach (var v in set.Items)
        {
            if (!v.IsOpen || !v.Parked) continue;
            var since = v.History.FirstOrDefault(h => h.Action == WorkItemActions.Parked)?.At ?? v.UpdatedAt;
            list.Add(new SagaAttention(v.Number, "parked", SweepParks.CodeOf(v.ParkReason), since.UtcDateTime));
        }
        return list;
    }

    /// <summary>
    /// What the saga has in flight, from the states the ownership read last: a merge call asked and not yet answered ("merge-requested"), and a slot granted whose session the plant may not hold yet ("slot-granted": the plant
    /// decides whether it does). A restart now loses the first answer or the offer in between; the release check says so.
    /// </summary>
    public IReadOnlyList<SagaFlight> InFlight()
    {
        var list = new List<SagaFlight>();
        foreach (var s in ownership.States.OrderBy(s => s.ItemId.Number))
        {
            if (!s.IsOpen) continue;
            if (s.MergeRequest is not null) list.Add(new SagaFlight(s.ItemId.Number, "merge-requested", s.Step.ToString()));
            else if (!s.Parked && s.SlotId.Length > 0 && s.Step is StepKind.Design or StepKind.Develop or StepKind.Verify) list.Add(new SagaFlight(s.ItemId.Number, "slot-granted", s.Step.ToString()));
        }
        return list;
    }
}

static class SagaPlantWiring
{
    /// <summary>
    /// The feed of a gateway where the engine acts, with the plant's sources set (the patrol's parked items, the release check's in-flight work). The labels are those of the reconciler's last read of the board (null before its first
    /// pass: the pins, priorities and runtime labels of the saga's items are then unknown, not absent).
    /// </summary>
    public static SagaPlantFeed Make(GatewayComposition c, IServiceProvider sp)
    {
        var plant = sp.GetRequiredService<Plant>();
        var home = c.Instance.Home;
        (IReadOnlyList<Fworker.Contracts.Seams.BoardCard> Source, Dictionary<int, IReadOnlyList<string>> ByNumber)? cache = null;
        IReadOnlyList<string>? LabelsOf(int item)
        {
            var cards = c.BoardReconciler?.LastCards;
            if (cards is null || cards.Count == 0 || home is null) return null;
            if (cache is not { } hit || !ReferenceEquals(hit.Source, cards))
            {
                var by = new Dictionary<int, IReadOnlyList<string>>();
                foreach (var card in cards) if (card.Repository == home && card.Labels is { } labels) by[card.Number] = labels;
                cache = hit = (cards, by);
            }
            return hit.ByNumber.TryGetValue(item, out var l) ? l : null;
        }
        var feed = new SagaPlantFeed(sp.GetRequiredService<V2Ownership>(), plant.OwnerNameOf, LabelsOf, SagaLabelRules.From(c.Instance.Root), sp.GetService<V2ItemViews>());
        plant.SagaAttentionSource = feed.Attention; plant.SagaFlightSource = feed.InFlight;
        return feed;
    }
}
