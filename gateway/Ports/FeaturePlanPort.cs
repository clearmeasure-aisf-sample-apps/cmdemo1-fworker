using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.WorkItem;

// W7.3 (#340): the feature plan in the gateway, in the hybrid composition the owner decided on (2026-10-28): v1's plant offers the sessions and runs the verbs; the WorkItemSaga decides the items it has started. Wired only where
// the engine acts (Engine.V2Acts), and only for an engine that carries the feature plan (`intake.v2Plans` names it; it is empty in config/instance.json, so no saga of this plan starts until the owner says so). What the gateway
// adds to the docs plan's ports (Ports/DocsPlanPort.cs), each with the fault it must not turn into a lasting answer:
//  - SlotRequestPort: the saga asks for a slot (SlotWanted); the gateway adds what the saga does not know (the item's labels: priority, pin) and the runtime, and sends the scheduler's RequestSlot. A label read that fails is read
//    again and then left out (the item ranks without its priority; the request is replaced by the next one, nothing is remembered).
//  - PullRequestChecks and FeatureChecksJob: the watcher of "CI alongside". It reads the checks of the head of every feature item that is in Functional Testing or waits to merge, through the rule script's own verbs (the CI rule
//    of issue 102, with its one re-run, stays the script's), and tells the saga (ChecksObserved; a head the saga does not know yet, PullRequestChanged). A read that fails is tried again at the next run, never reported as a state.
//  - FeatureNotice: a fix-forward round sends the owner session the trigger v1's sweep sends it (checks.failed), so a connected session goes back to Development at once.
//  - MergeQueueCount: the merged feature items that still wait for their release candidate, for mergeQueueDepth (a documentation change never counts, and never waits).

/// <summary>What the slot request needs of the configuration: the runtime the v2 sessions ask for, and the labels that rank an item.</summary>
public sealed record SlotRequestSettings(string Runtime, string TopLabel, string HighLabel, string PinPrefix = "worker:", IReadOnlyList<(string Phase, string Runtime)>? Routing = null, Func<string, string?>? ColumnOfPhase = null)
{
    public const string RuntimePrefix = "runtime:";

    /// <summary>
    /// The runtime of the requests: <c>scheduler.runtime</c> when it names one the scheduler knows, else octopus (the factory's own hosts) when configured, else the first configured runtime in the scheduler's order. The plant's
    /// routing is read as V1.3 says (<see cref="RuntimeFor"/>); the scheduler holds the capacity, the plant places the session.
    /// </summary>
    public static SlotRequestSettings From(JsonNode? root, CapacitySettings capacity, string configured, string environment = "", Func<string, string?>? columnOfPhase = null)
    {
        string? Text(JsonNode? n, string name) { try { return n?[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null; } catch { return null; } }
        var kinds = capacity.Runtimes.Keys;
        var runtime = RuntimeKinds.All.Contains(configured) ? configured
            : kinds.Contains(RuntimeKinds.Octopus) ? RuntimeKinds.Octopus
            : RuntimeKinds.All.FirstOrDefault(kinds.Contains) ?? RuntimeKinds.Octopus;
        JsonNode? labels = null; try { labels = root?["priorityLabels"]; } catch { }
        return new SlotRequestSettings(runtime, Text(labels, "top") ?? "priority:top", Text(labels, "high") ?? "priority:high", Routing: RoutingOf(root, environment), ColumnOfPhase: columnOfPhase);
    }

    /// <summary>
    /// The routing rules of the runtimes block of this environment (<c>runtimes.environments.&lt;name&gt;.routing</c> when the file has environments, an environment without an entry has none; else <c>runtimes.routing</c>), in order,
    /// as RuntimeSettings.From reads them: a rule with no phase matches every phase.
    /// </summary>
    static IReadOnlyList<(string Phase, string Runtime)> RoutingOf(JsonNode? root, string environment)
    {
        var rules = new List<(string, string)>();
        try
        {
            JsonNode? block = root?["runtimes"];
            if (block is JsonObject o && o["environments"] is JsonObject { Count: > 0 } envs) block = envs[(environment ?? "").Trim().ToLowerInvariant() is { Length: > 0 } e ? e : "tdd"];
            if (block?["routing"] is JsonArray array)
                foreach (var x in array)
                    if (x?["runtime"]?.ToString() is { Length: > 0 } rt) rules.Add((x["phase"]?.ToString() is { Length: > 0 } ph ? ph : "*", rt));
        }
        catch { }
        return rules;
    }

    /// <summary>
    /// V1.3, the runtime of an item's slot request, in v1's order (cloud-worker-runtimes.md 4.4): the item's <c>runtime:&lt;kind&gt;</c> label, else the first routing rule of its environment whose phase matches the column of the step
    /// ("*" matches any; a rule that says local places the session on a connected worker, which is no runtime of the scheduler's, and is passed over), else <c>scheduler.runtime</c>. A label or rule that names a kind the scheduler does
    /// not know is passed over too. A kind that is off in this environment is not changed here: the scheduler holds the slot with the code runtime-off.
    /// </summary>
    public string RuntimeFor(IReadOnlyList<string> labels, string phase)
    {
        foreach (var l in labels)
            if (l.StartsWith(RuntimePrefix, StringComparison.OrdinalIgnoreCase) && RuntimeKinds.All.Contains(l[RuntimePrefix.Length..].ToLowerInvariant())) return l[RuntimePrefix.Length..].ToLowerInvariant();
        var column = ColumnOfPhase?.Invoke(phase);
        foreach (var (rulePhase, runtime) in Routing ?? [])
            if ((rulePhase == "*" || (column is not null && string.Equals(rulePhase, column, StringComparison.OrdinalIgnoreCase))) && RuntimeKinds.All.Contains(runtime)) return runtime;
        return Runtime;
    }

    public string? PriorityOf(IReadOnlyList<string> labels) =>
        labels.Contains(TopLabel, StringComparer.OrdinalIgnoreCase) ? "top" : labels.Contains(HighLabel, StringComparer.OrdinalIgnoreCase) ? "high" : null;

    public bool IsPinned(IReadOnlyList<string> labels) => labels.Any(l => l.StartsWith(PinPrefix, StringComparison.OrdinalIgnoreCase) && l.Length > PinPrefix.Length);
}

/// <summary>The slot request of an item's step: the saga's facts plus the item's labels, as the scheduler's <see cref="RequestSlot"/>. Never throws for a fault of the label read (the request goes with what is known).</summary>
public sealed class SlotRequestPort(IMergeFactsReader? reader, SlotRequestSettings settings, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    static readonly TimeSpan[] Pauses = [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)];
    readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? ((d, ct) => Task.Delay(d, ct));

    public async Task<RequestSlot> Run(SlotWanted m, CancellationToken ct)
    {
        var labels = await Labels(m.Item, ct);
        // (the first AI column is the intake column of the plan's order: past intake before intake, delivery-speed.md 3.1)
        // (the hotfix plan of an incident ranks at the top whatever its labels say, W8.1)
        var rank = new Fworker.Domain.PlanRank(settings.IsPinned(labels), m.TopRank ? "top" : settings.PriorityOf(labels), InIntake: string.Equals(m.Phase, CapacityPhases.Design, StringComparison.OrdinalIgnoreCase));
        var footprint = m.Footprint.Count == 0 ? Footprint.Empty : new Footprint(m.Footprint, m.FootprintFromPullRequest ? FootprintSource.PullRequest : FootprintSource.Design);
        return new RequestSlot(m.Item, m.Key, m.Generation, settings.RuntimeFor(labels, m.Phase), m.Phase, rank, false, m.Resume, m.ColumnDepth, m.RequestedAt, [], footprint);
    }

    async Task<IReadOnlyList<string>> Labels(ItemId item, CancellationToken ct)
    {
        if (reader is null) return [];
        for (var attempt = 0; ; attempt++)
        {
            try { return (await reader.ReadItem(item, ct))?.Labels ?? []; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (attempt < Pauses.Length) { Console.Error.WriteLine($"slot request for {item}: the labels could not be read ({e.GetType().Name}); trying again"); await wait(Pauses[attempt], ct); }
            catch (Exception e) { Console.Error.WriteLine($"slot request for {item}: the labels could not be read ({e.GetType().Name}); the request goes without them"); return []; }
        }
    }
}

/// <summary>The handler of the saga's slot requests; the request cascades to the scheduler's queue. Public because Wolverine's generated code refers to it.</summary>
public static class SlotRequestHandler
{
    public static Task<RequestSlot> Handle(SlotWanted m, SlotRequestPort port, CancellationToken ct) => port.Run(m, ct);
}

/// <summary>What the checks of a head say, as the watcher reads them.</summary>
public sealed record CheckRead(string Head, ChecksVerdict Verdict, IReadOnlyList<string> Failed);

/// <summary>The checks of an item's pull request. A read that cannot be made throws (the watcher tries again at its next run); it never answers a state it did not see.</summary>
public interface IPullRequestChecks
{
    Task<CheckRead> Read(ItemId item, CancellationToken ct);
}

/// <summary>The checks read through the rule script's verbs, so that the CI rule of issue 102 (one re-run of a failed job that fails on the base branch too) stays the script's.</summary>
public sealed class PullRequestChecks(Func<string, string, TimeSpan, Task<(int Code, string Output)>> run) : IPullRequestChecks
{
    public async Task<CheckRead> Read(ItemId item, CancellationToken ct)
    {
        var brief = await run("pr-checks", new JsonObject { ["item"] = item.Number, ["brief"] = true }.ToJsonString(), TimeSpan.FromSeconds(120));
        var status = Parse(brief.Code, brief.Output, "pr-checks");
        if (status.State != "failure") return new CheckRead(status.Sha, status.State == "success" ? ChecksVerdict.Green : ChecksVerdict.Pending, []);
        // red: the script's wait applies its rule (the one re-run, the master defect), and answers red only when the head stays red
        var ruled = await run("wait-for-checks", new JsonObject { ["item"] = item.Number, ["timeoutSeconds"] = 1 }.ToJsonString(), TimeSpan.FromSeconds(150));
        return Ruled(status, ruled.Code, ruled.Output);
    }

    internal sealed record ChecksStatus(string State, string Sha, IReadOnlyList<string> Failed);

    static ChecksStatus Parse(int code, string output, string verb)
    {
        if (code != 0) throw new InvalidOperationException($"{verb} answered {code}");
        var o = ParseJson(output) ?? throw new InvalidOperationException($"{verb} answered no JSON");
        var failed = o["failed_checks"] is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : [];
        return new ChecksStatus(o["status"]?.ToString() ?? "pending", (o["sha"]?.ToString() ?? "").ToLowerInvariant(), failed);
    }

    /// <summary>The answer of the wait on a head the first look found failed: green when it turned green meanwhile, pending while the script re-runs the failed job or waits for a run, red when it stays red. Any other answer is a fault.</summary>
    internal static CheckRead Ruled(ChecksStatus first, int code, string output)
    {
        var o = ParseJson(output);
        var answered = o?["code"]?.ToString() ?? "";
        var sha = (o?["sha"]?.ToString() ?? o?["checks"]?["sha"]?.ToString() ?? first.Sha).ToLowerInvariant();
        if (code == 0 && (o?["status"]?.ToString() ?? "") == "success") return new CheckRead(sha, ChecksVerdict.Green, []);
        if (code == 21 && answered is "rerun-started" or "still-pending") return new CheckRead(sha, ChecksVerdict.Pending, []);
        if (code == 10 && answered == "checks-red")
        {
            var failed = o?["checks"]?["failed_checks"] is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : [];
            return new CheckRead(sha, ChecksVerdict.Red, failed.Count > 0 ? failed : first.Failed);
        }
        throw new InvalidOperationException($"wait-for-checks answered {code} {answered}");
    }

    static JsonObject? ParseJson(string text) { try { return JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) { return null; } }
}

/// <summary>
/// The watcher of "CI alongside" (a recurring job, every minute, on the leader only): for every feature item in Functional Testing or waiting to merge it reads the checks of the pull request's head and tells the saga. A head the
/// saga does not know yet (a push since the last report) is told first, so the checks are those of the head the saga is on. A green or red state of a head is told once; a pending state is a sign of life of the step every run
/// (its key changes every minute). A read or a publish that fails leaves the item for the next run.
/// </summary>
public sealed class FeatureChecksJob(IWorkItemStore store, RepoId home, IPullRequestChecks checks, Func<WorkItemInput, Task<bool>> publish, Func<bool> mayAct, Func<bool> budgetLow, Func<DateTimeOffset>? clock = null)
{
    public const int PerRun = 8;
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Dictionary<(string Item, string Head), ChecksVerdict> told = [];
    readonly Dictionary<string, DateTimeOffset> looked = [];

    public async Task<string> Run(CancellationToken ct)
    {
        if (!mayAct()) return "not-leader";
        if (budgetLow()) return "budget-low";
        var watched = (await store.OwnedStates(ct))
            .Where(s => WorkPlans.IsCode(s.Plan) && s.Status == WorkItemStatus.Open && !s.Parked && s.Step is StepKind.Verify or StepKind.Merge && s.PullRequest > 0 && ItemId.TryParse(s.Item, out var id) && id.Repository == home).ToList();
        var due = watched.OrderBy(s => looked.GetValueOrDefault(s.Item)).ThenBy(s => s.Item, StringComparer.Ordinal).Take(PerRun).ToList();
        var count = 0;
        foreach (var state in due)
        {
            ct.ThrowIfCancellationRequested();
            if (!mayAct()) return "not-leader";
            try { if (await Watch(state, ct)) count++; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { Console.Error.WriteLine($"feature checks for {state.Item}: {e.GetType().Name}; read again at the next run"); }
            looked[state.Item] = now();
        }
        // the entries of items that are no longer watched go
        var live = watched.Select(s => s.Item).ToHashSet();
        foreach (var gone in told.Keys.Where(k => !live.Contains(k.Item)).ToList()) told.Remove(gone);
        foreach (var gone in looked.Keys.Where(k => !live.Contains(k)).ToList()) looked.Remove(gone);
        return due.Count == 0 ? "none" : $"{due.Count} read, {count} told";
    }

    async Task<bool> Watch(WorkItemState state, CancellationToken ct)
    {
        var id = state.ItemId;
        var read = await checks.Read(id, ct);
        var head = read.Head.ToLowerInvariant();
        if (head.Length == 0) return false;
        if (read.Verdict != ChecksVerdict.Pending && told.TryGetValue((state.Item, head), out var before) && before == read.Verdict) return false;
        // a push the saga was not told of: its head first, so the checks below are those of the head it is on
        if (!string.Equals(head, state.Head, StringComparison.OrdinalIgnoreCase))
            if (!await publish(new PullRequestChanged(id, state.Generation, $"pr|watch|{head}", state.PullRequest, head, []))) throw new InvalidOperationException("the Wolverine host did not take the head");
        var key = read.Verdict == ChecksVerdict.Pending ? $"checks|{head}|pending|{now().UtcTicks / TimeSpan.TicksPerMinute}" : $"checks|{head}|{read.Verdict.ToString().ToLowerInvariant()}|{string.Join(',', read.Failed.Take(10))}";
        if (!await publish(new ChecksObserved(id, state.Generation, key, head, read.Verdict, read.Failed))) throw new InvalidOperationException("the Wolverine host did not take the checks");
        if (read.Verdict != ChecksVerdict.Pending) told[(state.Item, head)] = read.Verdict;
        return true;
    }
}

/// <summary>A fix-forward round tells the owner session what v1's sweep tells it for a red head: <c>checks.failed</c> for the item, in the development column (the plant routes it to the owner, if one holds the item).</summary>
public sealed class FeatureNotice(Func<int, JsonObject, bool> route, string developmentColumn)
{
    public bool Notify(WorkItemDecided d)
    {
        if (d.Action != WorkItemActions.FixForward || !WorkPlans.IsCode(d.Plan)) return false;
        var trigger = new JsonObject { ["provider"] = "gateway", ["event"] = "checks.failed", ["item"] = d.Item.Number, ["column"] = developmentColumn, ["reason"] = "fix-forward" };
        try { return route(d.Item.Number, trigger); }
        catch (Exception e) { Console.Error.WriteLine($"fix-forward notice for {d.Item}: {e.GetType().Name}"); return false; }
    }
}

/// <summary>The handler of the saga's decisions for the owner session. Public because Wolverine's generated code refers to it.</summary>
public static class FeatureNoticeHandler
{
    public static void Handle(WorkItemDecided m, FeatureNotice notice) => notice.Notify(m);
}

/// <summary>
/// mergeQueueDepth for the feature plan: the Release workflow keeps one queued run, so a code change merges only while fewer than N other merged items wait for their release candidate. The saga's own items in the Deliver step
/// that no environment has seen yet are those (v1's merged items are counted by the script, not here). A read that fails is the caller's fault to turn into a wait.
/// </summary>
public sealed class MergeQueueCount(IWorkItemStore store)
{
    public async Task<int> Busy(Fworker.Workflows.WorkItem.MergeRequested m, CancellationToken ct)
    {
        var self = m.Item.ToString();
        return (await store.OwnedStates(ct)).Count(s => s.Item != self && s.Plan != WorkPlans.Docs && s.Step == StepKind.Deliver && s.MergeCommit.Length > 0 && s.Delivered.Count == 0
            && ItemId.TryParse(s.Item, out var id) && id.Repository == m.Item.Repository);
    }
}
