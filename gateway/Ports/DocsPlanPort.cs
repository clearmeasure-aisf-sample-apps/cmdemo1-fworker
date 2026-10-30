using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.GitHub;
using Fworker.Persistence;
using Fworker.Workflows.Intake;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Microsoft.EntityFrameworkCore;

// W6.6 (#335): the docs plan in the gateway, in the hybrid composition the owner decided on (2026-10-28): v1's plant offers the sessions and v1's verbs do their work; the WorkItemSaga decides the docs items it has started
// (the steps of Fworker.Workflows.WorkItem.Plans); v1's sweep leaves those items alone. Wired only where the engine acts (Engine.V2Acts); `intake.v2Plans` is empty, so even there nothing starts a saga until the owner names the docs plan.
// What the gateway adds to the saga, each with the fault it must not turn into a lasting answer:
//  - DocsMergePort: the merge the saga asks for (MergeRequested) goes through the merge train of its repository (W7.2, Fworker.Workflows.Merge: every request at once under the direct strategy, one at a time under train) and
//    is the C# direct merge of W6.2, after the checks the script does first (the Auto Merge label, a hold). A fault of GitHub or of the call is a transient answer the saga asks
//    again after; only GitHub's own refusals are permanent, and the ledger of the command keeps those by their key (a blocked or transient answer is not kept).
//  - DocsProjection: the card follows the saga (a started item to its design column, a merged one to Done) as a versioned projection; a refusal for good is logged, a fault is tried again, and an item that stays behind is read by v1 again
//    like an item a person merged.
//  - DocsIntakeJob: pulls the next Todo item whose body carries the docs-only marker, by the intake rule, and starts its saga.
//  - the offers of the plant (V2Ownership.OfferNodes) and the feed of the verbs (V2VerbFeed): see their files.

/// <summary>What the docs plan reads of config/instance.json.</summary>
public sealed record DocsPlanSettings(
    string AutoMergeLabel, IReadOnlyList<string> HoldLabels, int MergeQueueDepth, bool UpdateBranchWhenBehind, int MaxUpdates,
    IReadOnlyDictionary<StepKind, string> Columns, string DoneColumn, string DocsMarker, string DesignSection, DocsPlanRules Rules, IReadOnlyList<string> CarriedPlans,
    string ReleaseColumn = "", string FinalEnvironment = "prod", bool FeatureSlots = true, string SlotRuntime = "")
{
    /// <summary>The card of an item on that step; null when the step has none of its own (the merge happens in the validation column).</summary>
    public string? ColumnOf(StepKind step) => Columns.TryGetValue(step, out var c) ? c : null;

    public bool IsHold(string label) => label.StartsWith("hold:", StringComparison.OrdinalIgnoreCase) || HoldLabels.Contains(label, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The policy of the saga with these rules; a plan list that is empty carries every plan (the saga is not wired by it then). Where the engine carries the feature plan (W7.3) the policy has its rules: the plan's steps,
    /// and the slots of the scheduler unless <c>scheduler.featureSlots</c> is off. The last environment an item is delivered to is <c>delivery.finalEnvironment</c> (prod; a tdd-only factory names tdd).
    /// </summary>
    public WorkItemPolicy Policy => WorkItemPolicy.Default with
    {
        Docs = Rules, CarriedPlans = CarriedPlans.Count == 0 ? null : CarriedPlans, FinalEnvironment = FinalEnvironment,
        FeatureRules = CarriedPlans.Contains(WorkPlans.Feature, StringComparer.OrdinalIgnoreCase) ? new FeaturePlanRules(FeatureSlots) : null,
    };

    /// <summary>Whether the engine carries the feature plan: its steps run, its items are pulled by the intake and its checks are watched.</summary>
    public bool CarriesFeature => CarriedPlans.Contains(WorkPlans.Feature, StringComparer.OrdinalIgnoreCase);
}

public static class DocsPlanSettingsReader
{
    public static DocsPlanSettings From(JsonNode? root)
    {
        var columns = new Dictionary<StepKind, string>();
        string? design = null, section = null;
        try { if (root?["requiredBodySection"] is JsonObject sections && sections.FirstOrDefault() is { } first) { design = first.Key; section = first.Value?.ToString(); } } catch { }
        string[] ai = []; try { ai = Strings(root?["columns"], "ai").ToArray(); } catch { }
        design ??= ai.FirstOrDefault();
        var develop = Text(root?["columns"], "development"); var validation = Text(root?["columns"], "validation");
        if (design is { Length: > 0 }) columns[StepKind.Design] = design;
        if (develop is { Length: > 0 }) columns[StepKind.Develop] = develop;
        if (validation is { Length: > 0 }) columns[StepKind.Verify] = validation;
        var timing = Node(root, "timing");
        // the steps whose column needs a reviewer's verdict (reviewVerdictColumns); a configuration that names none of them keeps the defaults of the rules
        var reviewed = Strings(root, "reviewVerdictColumns");
        var reviewedSteps = reviewed.Count == 0 ? null : columns.Where(kv => reviewed.Contains(kv.Value, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
        var rules = new DocsPlanRules(Int(timing, "maxReviewRounds") ?? 3, Int(timing, "docsOnlyMaxReviewRounds") ?? 2, ReviewedSteps: reviewedSteps);
        var policyAuto = (Text(root, "mergePolicy") ?? "auto") == "auto";
        var autoUpdate = false; try { autoUpdate = root?["baseUpdate"]?["autoUpdateBranch"] is JsonValue v && v.TryGetValue<bool>(out var b) && b; } catch { }
        var intake = Node(root, "intake"); var scheduler = Node(root, "scheduler");
        var featureSlots = true; try { if (scheduler?["featureSlots"] is JsonValue fv && fv.TryGetValue<bool>(out var fb)) featureSlots = fb; } catch { }
        return new DocsPlanSettings(Text(root, "autoMergeLabel") ?? "Auto Merge", Strings(root, "holdLabels"), Int(root, "mergeQueueDepth") ?? 0, policyAuto && autoUpdate,
            Int(timing, "outOfDateMergeAttempts") ?? 4, columns, Text(root?["columns"], "done") ?? "Done", Text(root, "docsOnlyMarker") ?? "<!-- aisf:docs-only -->", section ?? "", rules,
            Strings(intake, "v2Plans").Select(p => p.ToLowerInvariant()).ToList(),
            Strings(Node(root, "columns"), "hold").FirstOrDefault() ?? "", Text(Node(root, "delivery"), "finalEnvironment") ?? "prod", featureSlots, Text(scheduler, "runtime") ?? "");
    }

    static JsonNode? Node(JsonNode? n, string name) { try { return n?[name]; } catch { return null; } }
    static string? Text(JsonNode? n, string name) { try { return n?[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null; } catch { return null; } }
    static int? Int(JsonNode? n, string name) { try { return n?[name] is JsonValue v && v.TryGetValue<int>(out var i) && i >= 0 ? i : null; } catch { return null; } }
    static List<string> Strings(JsonNode? n, string name)
    {
        var l = new List<string>();
        try { if (n?[name] is JsonArray a) foreach (var x in a) if (x is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0) l.Add(s.Trim()); } catch { }
        return l;
    }
}

/// <summary>
/// The merge the saga asks for. The command of W6.2 does the GitHub side; this port first does the checks of the script that are not GitHub's (Verb_merge: the Auto Merge label, a hold) and answers every outcome as a
/// <see cref="MergeResult"/>. It never throws for a fault: GitHub or the network failing is a transient answer (the saga asks again after a pause), because an exception would end in Wolverine's retries or dead letters and
/// the answer the saga waits for would not come.
/// </summary>
public sealed class DocsMergePort(MergePullRequestHandler? merge, IMergeFactsReader? reader, DocsPlanSettings settings, Func<bool>? budgetLow = null, Func<MergeRequested, CancellationToken, Task<int>>? queueBusy = null)
{
    /// <summary>The merge of a request as the saga asked, and its answer for the saga. <paramref name="firstTurn"/> says whether a branch that is behind is brought up to date first; unset, it is the saga's first attempt (the direct strategy).</summary>
    public async Task<MergeAttempted> Run(MergeRequested m, CancellationToken ct, bool? firstTurn = null) => MergeTrain.Attempted(m, await Answer(m, firstTurn ?? m.Attempt == 1, ct));

    /// <summary>The merge of a turn the merge train granted (W7.2): the answer goes back to the train, which settles its line by it and hands it on to the saga.</summary>
    public async Task<MergeTrainAnswered> Run(MergeGranted g, CancellationToken ct) => new(g.Key, g.Request, await Answer(g.Request, g.FirstTurn, ct));

    async Task<MergeResult> Answer(MergeRequested m, bool firstTurn, CancellationToken ct)
    {
        try { return await Decide(m, firstTurn, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (NoInstallationException) { return new MergeResult(MergeResultKind.Failed, "no-installation", "the factory has no GitHub installation for the owner of the repository"); }
        catch (Exception e) { return new MergeResult(MergeResultKind.Blocked, "merge-error", e.GetType().Name, Transient: true); }
    }

    async Task<MergeResult> Decide(MergeRequested m, bool firstTurn, CancellationToken ct)
    {
        if (merge is null || reader is null) return new MergeResult(MergeResultKind.Blocked, "merge-unavailable", "the gateway has no GitHub credential to merge with", Transient: true);
        // below the reserve of the GitHub budget only the essential calls run (GitHubBudget.cs): the merge waits for the reset like every other read
        if (budgetLow?.Invoke() == true) return new MergeResult(MergeResultKind.Blocked, "github-budget", "the gateway's GitHub budget is below its reserve; the merge waits for the reset", Transient: true);
        var facts = await reader.ReadItem(m.Item, ct);
        if (facts is null) return new MergeResult(MergeResultKind.Failed, "item-not-found", $"{m.Item} does not exist");
        // the script merges nothing without the label (a person merges it) and nothing that a hold stands in front of; both can clear, so they are waits
        if (!facts.Labels.Contains(settings.AutoMergeLabel, StringComparer.OrdinalIgnoreCase))
            return new MergeResult(MergeResultKind.Blocked, "no-auto-merge-label", $"{m.Item} does not carry '{settings.AutoMergeLabel}': a person merges it", Transient: true);
        if (facts.Labels.FirstOrDefault(settings.IsHold) is { } hold)
            return new MergeResult(MergeResultKind.Blocked, "hold", $"{m.Item} carries the hold '{hold}'", Transient: true);
        // a documentation change builds no release, so the merge queue never holds it (DocsOnly); a code change (the feature plan, W7.3) waits while mergeQueueDepth merged items still wait for their release candidate: the
        // count is the saga's own items in that state (a read that fails is a fault, which the caller turns into a transient answer). GitHub's update of a branch that is behind is asked once per merge arm, as the script does once per verdict
        var docsOnly = string.Equals(m.Plan, WorkPlans.Docs, StringComparison.OrdinalIgnoreCase);
        // a hotfix of an incident (W8.1) is not held back by the queue either: it ranks first and passes every quality gate, and what waits for a release candidate is what it is meant to overtake
        var busy = !docsOnly && !string.Equals(m.Plan, WorkPlans.Hotfix, StringComparison.OrdinalIgnoreCase) && settings.MergeQueueDepth > 0 && queueBusy is not null ? await queueBusy(m, ct) : 0;
        var cmd = new MergePullRequest(MergePullRequest.KeyFor(m.Item.Repository, m.PullRequest, m.Head, m.UpdatesSoFar), m.Item.Repository, m.PullRequest, m.Head,
            MergeQueueDepth: settings.MergeQueueDepth, QueueBusy: busy, DocsOnly: docsOnly,
            UpdateWhenBehind: settings.UpdateBranchWhenBehind && firstTurn && m.UpdatedFrom.Length == 0, UpdatedFrom: m.UpdatedFrom, UpdatesSoFar: m.UpdatesSoFar, MaxUpdates: settings.MaxUpdates,
            Item: m.Item.Number, RecordedPullRequest: m.PullRequest);
        return Map(await merge.Process(cmd, ct));
    }

    /// <summary>The command's outcome as the saga's answer. A transport fault the command base classified as transient or rate limited stays transient; a refusal GitHub gave is permanent.</summary>
    public static MergeResult Map(MergeOutcome o)
    {
        var transient = o.Kind is Fworker.Contracts.Commands.FailureKind.Transient or Fworker.Contracts.Commands.FailureKind.RateLimited;
        return o.Status switch
        {
            MergeStatus.Merged => new MergeResult(MergeResultKind.Merged, o.Reason, o.Detail, o.Sha, UpdatesAdded: o.UpdatesAdded, Updated: o.Updated),
            MergeStatus.HeadMoved => new MergeResult(MergeResultKind.HeadMoved, o.Reason, o.Detail),
            MergeStatus.Blocked => new MergeResult(MergeResultKind.Blocked, o.Reason, o.Detail, NewHead: o.NewHead, Transient: true, RetryAfter: o.RetryAfter, UpdatesAdded: o.UpdatesAdded, Updated: o.Updated),
            _ => new MergeResult(MergeResultKind.Failed, o.Reason, o.Detail, Transient: transient, RetryAfter: o.RetryAfter, UpdatesAdded: o.UpdatesAdded, Updated: o.Updated),
        };
    }
}

/// <summary>The handler of the turn the merge train grants (W7.2); the answer cascades through the outbox to the train, which hands it on to the saga. Public because Wolverine's generated code refers to it.</summary>
public static class MergeTrainPortHandler
{
    public static Task<MergeTrainAnswered> Handle(MergeGranted g, DocsMergePort port, CancellationToken ct) => port.Run(g, ct);
}

/// <summary>The last version a projection applied, in the table of W2.1 (factory.ProjectionVersions). An advance is one conditional update, so two writers cannot move it backwards.</summary>
public sealed class SqlProjectionVersions(Func<FactoryDbContext> open) : IProjectionVersions
{
    public async Task<long> Applied(string itemKey, string target, CancellationToken ct = default)
    {
        await using var db = open();
        return await db.ProjectionVersions.AsNoTracking().Where(x => x.ItemKey == itemKey && x.Target == target).Select(x => x.AppliedVersion).FirstOrDefaultAsync(ct);
    }

    public async Task<bool> Advance(string itemKey, string target, long version, DateTimeOffset at, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db = open();
            var moved = await db.ProjectionVersions.Where(x => x.ItemKey == itemKey && x.Target == target && x.AppliedVersion < version)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AppliedVersion, version).SetProperty(x => x.AppliedAt, at), ct);
            if (moved > 0) return true;
            if (await db.ProjectionVersions.AnyAsync(x => x.ItemKey == itemKey && x.Target == target, ct)) return false;   // there, and not older than this version
            db.ProjectionVersions.Add(new ProjectionVersion { ItemKey = itemKey, Target = target, AppliedVersion = version, AppliedAt = at });
            try { await db.SaveChangesAsync(ct); return true; }
            catch (DbUpdateException) { /* another writer inserted the row between the check and the insert: look again */ }
        }
        return false;
    }
}

/// <summary>
/// The card follows the saga. A decision that enters a step puts the card in that step's column (the design column for a started item, so it leaves Todo), the completion of a merged item puts it in Done. The decision's
/// sequence number is the version, so a late decision never undoes a newer one. A fault is tried again a few times and then left: nothing is remembered as failed (the version is not advanced), the next decision projects the card
/// again, and an item that never gets its Done is read by v1 like one a person merged.
/// </summary>
public sealed class DocsProjection(Func<BoardColumnProjection?> boardOf, DocsPlanSettings settings, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    static readonly TimeSpan[] Pauses = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)];
    readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? ((d, ct) => Task.Delay(d, ct));

    /// <summary>The column a decision puts the card in, or null for a decision that moves nothing.</summary>
    public string? ColumnFor(WorkItemDecided d)
    {
        var docs = string.Equals(d.Plan, WorkPlans.Docs, StringComparison.OrdinalIgnoreCase);
        var feature = WorkPlans.IsCode(d.Plan) && settings.CarriesFeature;   // (the hotfix plan of an incident, W8.1, moves its card as the feature plan does)
        if (!docs && !feature) return null;
        return d.Action switch
        {
            // the feature plan's merged item waits in the release queue (a hold column) for its delivery, as v1's settle puts it there; Done follows the settle window
            WorkItemActions.StepEntered when feature && d.Step == nameof(StepKind.Deliver) => settings.ReleaseColumn.Length > 0 ? settings.ReleaseColumn : null,
            WorkItemActions.StepEntered when d.Step is { } step && Enum.TryParse<StepKind>(step, out var kind) => settings.ColumnOf(kind),
            WorkItemActions.Completed => settings.DoneColumn,
            _ => null,
        };
    }

    /// <summary>Projects the decision. False when it gave up after the tries (the next decision projects again); true when the card is where the decision wants it or the decision wants nothing.</summary>
    public async Task<bool> Project(WorkItemDecided d, CancellationToken ct)
    {
        if (ColumnFor(d) is not { Length: > 0 } column || boardOf() is not { } board) return true;
        for (var attempt = 0; ; attempt++)
        {
            try { await board.Move(d.Item, d.Sequence, column, ct); return true; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (ProjectionRefusedException e) { Console.Error.WriteLine($"projection of {d.Item} to '{column}' refused: {e.Message}"); return true; }   // GitHub or the board said no for good: a retry says the same
            catch (Exception e) when (attempt < Pauses.Length)
            {
                Console.Error.WriteLine($"projection of {d.Item} to '{column}': {e.GetType().Name}; trying again");
                await wait(Pauses[attempt], ct);
            }
            catch (Exception e) { Console.Error.WriteLine($"projection of {d.Item} to '{column}' gave up: {e.GetType().Name}"); return false; }
        }
    }
}

/// <summary>The head of a pull request now, for the verbs' feed: the head the validator tested is the one v1 recorded with the verdict, which is the head of the pull request when the verdict was given.</summary>
public sealed class PullRequestHeads(GitHubClientFactory clients)
{
    /// <summary>The head sha (lower case), or "" when it cannot be read: the feed goes on without it (the saga then knows the head only from the verdict's own).</summary>
    public async Task<string> HeadOf(RepoId repository, int pullRequest, CancellationToken ct)
    {
        try
        {
            var client = await clients.For(repository, ct);
            using var resp = await client.Send(HttpMethod.Get, $"pulls/{pullRequest}", null, ct);
            if (!resp.IsSuccessStatusCode) return "";
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["head"]?["sha"]?.ToString().ToLowerInvariant() ?? "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return ""; }
    }

    /// <summary>The paths the pull request changes (W7.3: the footprint of the file-overlap rule after the first push), three pages of a hundred at most, or null when they cannot be read: the footprint then stays what it was.</summary>
    public async Task<IReadOnlyList<string>?> FilesOf(RepoId repository, int pullRequest, CancellationToken ct)
    {
        try
        {
            var client = await clients.For(repository, ct);
            var files = new List<string>();
            for (var page = 1; page <= 3; page++)
            {
                using var resp = await client.Send(HttpMethod.Get, $"pulls/{pullRequest}/files?per_page=100&page={page}", null, ct);
                if (!resp.IsSuccessStatusCode) return null;
                if (JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) is not JsonArray array) return null;
                foreach (var f in array) if (f?["filename"]?.ToString() is { Length: > 0 } name) files.Add(name);
                if (array.Count < 100) break;
            }
            return files;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }
}

/// <summary>The handler of the saga's decisions for the board. Public because Wolverine's generated code refers to it.</summary>
public static class DocsProjectionHandler
{
    public static Task Handle(WorkItemDecided m, DocsProjection projection, CancellationToken ct) => projection.Project(m, ct);
}

/// <summary>
/// The intake of the docs plan: the next Todo item of the home repository whose body carries the docs-only marker, by the intake rule (the WIP limits, the labels that keep an item in the column), and the start of its saga. The marker in
/// the body only chooses the plan the item starts on; the design still decides (a design without the marker switches the item to the feature plan, which hands it to v1). Off unless intake is enabled and names the docs plan.
/// </summary>
public sealed class DocsIntakeJob(IBoardReader boards, IMergeFactsReader items, WorkItemIntake intake, Func<BoardRef?> board, RepoId home, DocsPlanSettings settings, IReadOnlyList<string> aiColumns,
    Func<int> freeSlots, Func<bool> designWaiting, Func<bool> mayAct, Func<bool> budgetLow)
{
    const int Looked = 10;
    static readonly Regex Dependency = new(@"\bdepends\s+on\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public async Task<string> Run(CancellationToken ct)
    {
        var s = intake.Settings;
        if (!s.Enabled || s.V2Plans.Count == 0) return "off";
        if (!mayAct()) return "not-leader";
        if (budgetLow()) return "budget-low";
        if (board() is not { } b) return "no-board";
        var cards = await boards.Read(b, ct);
        if (!mayAct()) return "not-leader";
        var inFlight = cards.Where(c => c.Open && c.Column is { } col && aiColumns.Contains(col, StringComparer.OrdinalIgnoreCase)).GroupBy(c => c.Repository.ToString()).ToDictionary(g => g.Key, g => g.Count());
        var candidates = new List<IntakeCandidate>();
        foreach (var card in cards.Where(c => c.Open && c.Repository == home && string.Equals(c.Column, s.FromColumn, StringComparison.OrdinalIgnoreCase)).Take(Looked))
        {
            var id = new ItemId(home, card.Number);
            var facts = await items.ReadItem(id, ct);
            if (facts is null) continue;
            // the item's own body decides the plan it starts on; a body that names what it depends on is left to v1's intake, which checks the dependencies
            var body = facts.Body ?? "";
            // (an item that names no marker is known to be a code change only when the engine carries the feature plan; otherwise v1's own intake takes it)
            var plan = body.Contains(settings.DocsMarker, StringComparison.Ordinal) ? WorkPlans.Docs : settings.CarriesFeature ? WorkPlans.Feature : null;
            candidates.Add(new IntakeCandidate(id, true, true, facts.Labels, !Dependency.IsMatch(body), plan));
        }
        var d = await intake.Run(candidates, freeSlots(), designWaiting(), inFlight, ct);
        return d.Pull is { Engine: IntakeEngine.V2 } pull ? $"started {pull.Item}" : d.Reason;
    }
}
