using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Adoption;

// V1.1 (docs/design/fworker-wolverine-implementation.md, "Revision 2026-11-01 (v2 only)", section 2 and row V1.1): the admission. The rule after the revision is that every open factory item in a column of a step has exactly one open
// WorkItemSaga, whoever put it there. The adoption (AdoptionRunner) maps v1's state comment once per term of the lease; the admission runs after EVERY pass of the board reconciler, over the cards that pass has just read, and brings
// each open card of the home repository that stands in the column of a step and has no open saga under one:
//   - the card has v1's state comment: the adoption's own mapping, with its fencing and its live claim (AdoptionRunner.AdoptOne), so an item v1 is running is taken over with the session it has, and no second one is asked for;
//   - the card has none (a replenished item whose start was lost, a card a person moved into a column, an item v1 never started): StartWorkItem at the step of the column;
//   - the card has a saga that completed and the issue is open again, the card back in a step column some time after the saga ended: ReopenWorkItem.
// What is not admitted: a closed issue, an item that does not carry the factory label, the rival's or the demo label, an item of another environment (env:<other>), a plan this engine does not carry, an item whose dependencies are not met.
// A hold or a pin label does not keep a card out of its column's saga: the saga parks it (HoldObserved) or offers it to the worker only. At most MaxStarts fresh starts per pass; each is logged. It waits for the adoption of the term
// (a break of the engine is fenced there first) and acts for the leader only.

/// <summary>What the admission needs of the configuration.</summary>
/// <param name="FactoryLabel">An item without it is not a factory item (empty: no label is asked for).</param>
/// <param name="EnvLabel">With scoping on, "env:&lt;environment&gt;": an item of another environment is not admitted (empty: scoping off).</param>
/// <param name="DocsMarker">The marker in the body of an item that starts on the docs plan.</param>
/// <param name="MaxStarts">The most fresh sagas one pass starts (intake.wipLimit): a fault here starts many at once.</param>
/// <param name="Hotfix">The label of a hotfix item: the hotfix plan, which begins at Develop at the earliest.</param>
public sealed record AdmissionConfig(string FactoryLabel, string RivalLabel, string DemoLabel, string EnvLabel, string DocsMarker, WorkItemPolicy Policy, int MaxStarts = 6, string Hotfix = "hotfix")
{
    /// <summary>How long a completed saga must have ended before its item counts as reopened: the completion closes the issue and moves the card, and for that time the card may still stand in the old column.</summary>
    public TimeSpan ReopenGrace { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a card skipped for a reason that does not change by the minute is not looked at again.</summary>
    public TimeSpan SkipFor { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a card whose dependencies are not met is not looked at again.</summary>
    public TimeSpan DependenciesFor { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>An issue younger than this is not started from the board: a state comment written just after the card (v1's, or a test's) must be adopted, not raced. Replenish starts its own item without waiting.</summary>
    public TimeSpan MinAge { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Labels that keep an item out of the engine as intake.holdLabels do (and every hold:*): a held card gets no saga until the hold is lifted (looked at again after <see cref="SkipFor"/>).</summary>
    public IReadOnlyList<string> HoldLabels { get; init; } = [];

    /// <summary>The most reopenings one pass makes.</summary>
    public int MaxReopens { get; init; } = 3;
}

/// <summary>The counts of the admission, for the status block <c>v2.admission</c>: what the passes started, adopted, reopened and skipped (by reason).</summary>
public sealed class AdmissionReport
{
    readonly object gate = new();
    readonly Dictionary<string, int> skippedBy = new(StringComparer.Ordinal);
    readonly List<string> recent = [];
    public int Passes { get; private set; }
    public int Started { get; private set; }
    public int Adopted { get; private set; }
    public int Reopened { get; private set; }
    public int Errors { get; private set; }
    public string Outcome { get; private set; } = "idle";
    public DateTimeOffset? At { get; private set; }

    internal void Begin(DateTimeOffset at) { lock (gate) { Passes++; At = at; Outcome = "running"; } }
    internal void End(string outcome) { lock (gate) Outcome = outcome; }
    internal void Start(string item, string how) { lock (gate) { Started++; Recent($"started {item} ({how})"); } }
    internal void Adopt(string item) { lock (gate) { Adopted++; Recent($"adopted {item}"); } }
    internal void Reopen(string item) { lock (gate) { Reopened++; Recent($"reopened {item}"); } }
    internal void Error() { lock (gate) Errors++; }
    internal void Skip(string reason) { lock (gate) { var code = AdoptionReport.Code(reason); skippedBy[code] = skippedBy.GetValueOrDefault(code) + 1; } }
    void Recent(string line) { recent.Add(line); if (recent.Count > 10) recent.RemoveAt(0); }

    public JsonObject ToJson()
    {
        lock (gate)
            return new JsonObject
            {
                ["outcome"] = Outcome, ["at"] = At?.ToString("o"), ["passes"] = Passes, ["started"] = Started, ["adopted"] = Adopted, ["reopened"] = Reopened, ["errors"] = Errors,
                ["skippedBy"] = new JsonObject(skippedBy.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))), ["recent"] = new JsonArray(recent.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            };
    }
}

public sealed class Admission(AdoptionOptions options, AdmissionConfig settings, AdoptionRunner adoption, AdoptionPorts ports, Func<bool> mayAct, Func<bool> adoptionDone,
    Func<ItemId, CancellationToken, Task<bool>>? issueClosed = null, Func<DateTimeOffset>? clock = null, Action<string>? log = null,
    Func<IReadOnlySet<int>>? excluded = null, Func<string, DateTimeOffset?>? reopenedAt = null, Action<string>? reopenUsed = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Action<string> write = log ?? Console.WriteLine;
    readonly Dictionary<int, (string? Column, DateTimeOffset Until)> looked = [];
    readonly SemaphoreSlim one = new(1, 1);

    /// <summary>The counts of the passes so far.</summary>
    public AdmissionReport Report { get; } = new();

    /// <summary>One pass over the cards the board reconciler has just read. Returns how it ended ("not-leader", "waiting: adoption", "done", "partial").</summary>
    public async Task<string> Run(IReadOnlyList<BoardCard> cards, CancellationToken ct)
    {
        await one.WaitAsync(ct);
        try { return await Pass(cards, ct); }
        finally { one.Release(); }
    }

    async Task<string> Pass(IReadOnlyList<BoardCard> cards, CancellationToken ct)
    {
        if (!mayAct()) return Ended("not-leader");
        // an engine that carries no plan (intake.v2Plans names none: config/instance.json) has nothing to admit, and reads nothing for it
        if (settings.Policy.CarriedPlans is { Count: 0 }) return Ended("off: no plan is carried");
        if (!adoptionDone()) return Ended("waiting: adoption");
        Report.Begin(now());
        var columns = options.Mapping.Columns;
        var candidates = cards.Where(c => c.Repository == options.Home && c.Number > 0 && c.Open && columns.StepOf(c.Column) is not null).OrderBy(c => c.Number).ToList();
        var owned = (await ports.Items.OwnedStates(ct)).Select(s => s.Item).ToHashSet(StringComparer.Ordinal);
        var starts = 0; var reopens = 0; var failed = false;
        var left = excluded?.Invoke() ?? new HashSet<int>();   // (after a break: the items the adoption could not fence; nothing is done to them until a run does)
        var judged = new Dictionary<int, bool>();   // (a dependency is judged once per pass)
        foreach (var card in candidates)
        {
            if (!mayAct()) { Report.End("not-leader"); return "not-leader"; }
            ct.ThrowIfCancellationRequested();
            var id = new ItemId(options.Home, card.Number);
            if (owned.Contains(id.ToString())) continue;   // (an open saga, or one in its settle window: the item has its saga)
            if (left.Contains(card.Number)) { Report.Skip("excluded"); continue; }
            if (looked.TryGetValue(card.Number, out var memo) && memo.Column == card.Column && now() < memo.Until) continue;
            try { starts += await Admit(id, card, cards, judged, starts, reopens < settings.MaxReopens, () => reopens++, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { failed = true; Report.Error(); Console.Error.WriteLine($"admission of {id}: {e.GetType().Name}"); }
        }
        // a card that left its column or the board is forgotten
        foreach (var number in looked.Keys.Where(n => !candidates.Any(c => c.Number == n)).ToList()) looked.Remove(number);
        return Ended(failed ? "partial" : "done");
    }

    string Ended(string outcome) { Report.End(outcome); return outcome; }

    // 1 when this card started a saga of its own (the pass's count against MaxStarts), else 0
    async Task<int> Admit(ItemId id, BoardCard card, IReadOnlyList<BoardCard> cards, Dictionary<int, bool> judged, int startsSoFar, bool mayReopen, Action reopened, CancellationToken ct)
    {
        var key = id.ToString();
        if (Why(card.Labels) is { } early) { Skipped(card, early, settings.SkipFor); return 0; }
        var rec = await ports.Items.Load(key, ct);
        var read = await ports.Reads.ReadItem(id, ct);
        if (read is null || !read.IssueOpen) { Skipped(card, "issue-closed", settings.SkipFor); return 0; }
        if (Why(read.Labels) is { } why) { Skipped(card, why, settings.SkipFor); return 0; }

        // An open saga is the item's own (the pass skips those; this is the explicit rule for one that opened since)
        if (rec is { State.IsOpen: true }) return 0;

        // A saga that completed and an issue that is open: a reopening only on proof, never inferred from the card's column (the completion closes the issue and moves the card in steps that can fail, and a merged item whose
        // close and move failed looks the same as a reopened one). Proof is either v1's state comment, newer than the one the saga was brought to and written after the saga ended (a reopened v1 item: the reconciler's own reopen
        // path), or the board reconciler having seen the card closed and now seeing it open.
        if (rec is { State.IsOpen: false } completed)
        {
            if (completed.State.CompletedAt is not { } ended || now() - ended < settings.ReopenGrace) { Skipped(card, "completed-recently", TimeSpan.Zero); return 0; }
            // a saga that handed the item to v1 (the plan changed to one this engine does not carry) completed too; no reopening puts the item on a plan this engine does not carry
            var reopenPlan = completed.State.Plan == WorkPlans.Redeliver ? WorkPlans.Feature : completed.State.Plan;
            if (!settings.Policy.Carries(reopenPlan)) { Skipped(card, $"plan-not-carried: {reopenPlan}", settings.SkipFor); return 0; }
            // (the saga's own projection writes a state comment on the issue, so a comment alone is no proof: it must be newer than the saga's and written after the saga ended)
            if (read.State is not null && V1StateUpdated(read.State) is { } wrote && wrote >= ended + settings.ReopenGrace)
            {
                if (!mayReopen) { Report.Skip("reopen-limit"); return 0; }
                var again = await adoption.AdoptOne(new AdoptionReport { StartedAt = now() }, id, card, read, ct, afterBreak: true);
                if (again.Kind == AdoptionKind.Skipped) { Skipped(card, again.Reason, settings.SkipFor); return 0; }
                reopened(); Report.Reopen(key); write($"admission: reopened {key} from v1's state comment ({again.Reason})");
                return 0;
            }
            // the proof must come after the saga ended (a card closed and reopened while the saga was open, or before it existed, proves nothing about this ending), and a merged item is not reopened by it
            if (reopenedAt?.Invoke(key) is not { } shown || shown <= ended || completed.State.MergeCommit.Length > 0) { Skipped(card, "completed-no-proof", TimeSpan.Zero); return 0; }
            if (!mayReopen) { Report.Skip("reopen-limit"); return 0; }
            // (the reopened generation begins at the plan's first step, as the saga's own reopen does, whatever column the card stands in)
            if (!await Publish(new ReopenWorkItem(id, "the issue was reopened (the board showed its card closed, and shows it open)", $"admission-reopen|{completed.State.Generation}"))) return 0;
            reopenUsed?.Invoke(key);
            reopened(); Report.Reopen(key); write($"admission: reopened {key} (generation {completed.State.Generation} had ended {ended:o})");
            return 0;
        }

        // v1's state comment: the adoption's mapping, with the live claim and the fencing
        if (read.State is not null)
        {
            var report = new AdoptionReport { StartedAt = now() };
            var result = await adoption.AdoptOne(report, id, card, read, ct);
            if (result.Kind == AdoptionKind.Skipped) Skipped(card, result.Reason, settings.SkipFor);
            else { Report.Adopt(key); write($"admission: adopted {key} at {card.Column} ({result.Reason})"); }
            return 0;
        }

        // no state comment: a fresh start at the column's step, once the issue is old enough for its state comment to be there if v1 is writing one
        if (read.CreatedAt is { } created && now() - created < settings.MinAge) { Skipped(card, "too-new", TimeSpan.Zero); return 0; }
        // the plan: a hotfix label is the hotfix plan (it has no Design: it begins at Develop at the earliest), the docs marker of the body the docs plan, else the feature plan
        var hotfix = read.Labels.Contains(settings.Hotfix, StringComparer.OrdinalIgnoreCase);
        var plan = hotfix ? WorkPlans.Hotfix : (read.Body ?? "").Contains(settings.DocsMarker, StringComparison.Ordinal) && settings.DocsMarker.Length > 0 ? WorkPlans.Docs : WorkPlans.Feature;
        if (!settings.Policy.Carries(plan)) { Skipped(card, $"plan-not-carried: {plan}", settings.SkipFor); return 0; }
        if (!await DependenciesMet(id, read.Body ?? "", cards, judged, ct)) { Skipped(card, "depends-on", settings.DependenciesFor); return 0; }
        if (startsSoFar >= settings.MaxStarts) { Report.Skip("start-limit"); return 0; }
        var step = options.Mapping.Columns.StepOf(card.Column)!.Value;
        if (hotfix && step == StepKind.Design) step = StepKind.Develop;   // (the saga begins there when the plan has the step, else at the plan's first, and its decision says so)
        if (!await Publish(new StartWorkItem(id, "board", plan, Step: step))) return 0;
        Report.Start(key, $"{plan} plan, {step}"); write($"admission: started {key} on the {plan} plan at {step} (column {card.Column})");
        return 1;
    }

    async Task<bool> Publish(object message)
    {
        if (await ports.Publish(message)) return true;
        Report.Error(); Console.Error.WriteLine("admission: the host did not take the message");
        return false;
    }

    void Skipped(BoardCard card, string reason, TimeSpan forTime)
    {
        Report.Skip(reason);
        if (forTime > TimeSpan.Zero) looked[card.Number] = (card.Column, now() + forTime);
    }

    // the time v1's state comment says it was last written, or null
    static DateTimeOffset? V1StateUpdated(JsonObject state) => state["updated"]?.ToString() is { Length: > 0 } t && DateTimeOffset.TryParse(t, out var at) ? at : null;

    // the labels that keep an item out of the engine: not a factory item, the rival's, a demo's, another environment's
    string? Why(IReadOnlyList<string>? labels)
    {
        if (labels is null) return null;
        bool Has(string name) => name.Length > 0 && labels.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (settings.FactoryLabel.Length > 0 && !Has(settings.FactoryLabel)) return "no-factory-label";
        // as IntakeRule: hold:* and intake.holdLabels keep an item out; it is looked at again after SkipFor
        if (labels.Any(l => l.StartsWith("hold:", StringComparison.OrdinalIgnoreCase) || settings.HoldLabels.Contains(l, StringComparer.OrdinalIgnoreCase))) return "held";
        if (Has(settings.RivalLabel)) return "rival-label";
        if (Has(settings.DemoLabel)) return "demo-label";
        if (settings.EnvLabel.Length > 0)
        {
            var envs = labels.Where(l => l.StartsWith("env:", StringComparison.OrdinalIgnoreCase)).ToList();
            if (envs.Count != 1 || !string.Equals(envs[0], settings.EnvLabel, StringComparison.OrdinalIgnoreCase)) return "env-scope";
        }
        return null;
    }

    // Test-DependencyMet for each "Depends on" of the body that names an item of the home repository (another repository blocks nothing, as in v1): met once the dependency is closed, or still open in a hold or the done column.
    async Task<bool> DependenciesMet(ItemId item, string body, IReadOnlyList<BoardCard> cards, Dictionary<int, bool> judged, CancellationToken ct)
    {
        foreach (var dep in ItemMergedDependencies.Parse(item, body))
        {
            if (dep.Repository != options.Home) continue;
            if (!judged.TryGetValue(dep.Number, out var ok)) judged[dep.Number] = ok = await Met(dep.Number, cards, ct);
            if (!ok) return false;
        }
        return true;
    }

    async Task<bool> Met(int number, IReadOnlyList<BoardCard> cards, CancellationToken ct)
    {
        var columns = options.Mapping.Columns;
        if (cards.FirstOrDefault(c => c.Repository == options.Home && c.Number == number) is { } card)
            return !card.Open || columns.IsDone(card.Column) || columns.IsHold(card.Column);
        if (issueClosed is null) return false;
        try { return await issueClosed(new ItemId(options.Home, number), ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return false; }
    }
}
