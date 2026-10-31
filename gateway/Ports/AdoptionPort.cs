using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Integrations.GitHub;
using Fworker.Integrations.Octopus;
using Fworker.Workflows.Adoption;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Merge;
using Fworker.Workflows.Qc;
using Fworker.Workflows.WorkItem;

// W9.9b (docs/design/fworker-wolverine-implementation.md "W9.9b as built"): the adoption in the gateway. Only where the engine acts (Engine.V2Acts) with a database and a home repository; tdd stays in the shadow run and
// its v1 is untouched (nothing here is made there). The run itself is Fworker.Workflows.Adoption.AdoptionRunner; this file is what it reads (GitHub: the board, the issues, the state comments, the pull requests; Octopus:
// the Sign-offs now), the job that runs it (a recurring job of the leader: once per term of the lease, after the plant is restored) and the marker a process that the kill switch turned back to v1 leaves.

/// <summary>The reads of the adoption over the gateway's GitHub and Octopus readers. A read without its source throws or answers empty as noted: a board that cannot be read is a failed run (tried again), no Octopus is no Sign-offs.</summary>
sealed class AdoptionGitHubReads(IAdoptionGitHub github, IItemIssueReader issues, IBoardReader boards, Func<BoardRef?> board, RepoId home, string defaultBranch, PendingSignOffReader? octopus = null,
    Func<string, string?>? environmentKey = null, Func<DateTimeOffset>? clock = null) : IAdoptionReads
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<IReadOnlyList<BoardCard>> Cards(CancellationToken ct) =>
        board() is { } b ? await boards.Read(b, ct) : throw new InvalidOperationException("the catalog names no board");

    public Task<V1ItemRecord?> ReadItem(ItemId item, CancellationToken ct) => github.ReadV1Record(item, ct);

    public async Task<AdoptionPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct)
    {
        if (await github.ReadPullRequestFacts(repository, number, ct) is not { } pull) return null;
        // (the files of an open pull request give the footprint and the docs-only test; a merged one is read for neither)
        var files = pull.State == "open" ? await issues.ReadPullRequestFiles(repository, number, ct) : null;
        return new AdoptionPullRequest(pull.Number, pull.State, pull.Head, pull.MergeCommit, files);
    }

    public async Task<IReadOnlyList<OctopusSignOff>> PendingSignOffs(CancellationToken ct)
    {
        if (octopus is null) return [];
        var list = new List<OctopusSignOff>();
        foreach (var p in await octopus.Read(ct))
            if ((environmentKey?.Invoke(p.Environment) ?? p.Environment) is { Length: > 0 } key)
                list.Add(new OctopusSignOff(key.ToLowerInvariant(), new PendingSignOff(p.InterruptionId, p.DeploymentId, p.TaskId, p.Version, p.Created ?? now())));
        return list;
    }

    public async Task<IReadOnlyList<MergedPullRequest>> RecentMergedPulls(int take, string? afterCommit, CancellationToken ct)
    {
        var pulls = await github.ListMergedPulls(home, defaultBranch, take, ct);
        if (string.IsNullOrEmpty(afterCommit)) return pulls;
        // only the merges the environment does not run yet; when GitHub cannot compare (a force-pushed branch, a commit it does not know) the newest are taken as they are
        var past = await github.CommitsPast(home, afterCommit, defaultBranch, ct);
        return past is null ? pulls : pulls.Where(p => p.MergeCommit is { } c && past.Contains(c)).ToList();
    }
}

/// <summary>The versions the projections of the saga have applied (the table ProjectionVersions).</summary>
sealed class ProjectedSequences(IProjectionVersions versions) : IProjectedSequences
{
    public async Task<(long Comment, long Card)> Of(string itemKey, CancellationToken ct) =>
        (await versions.Applied(itemKey, ProjectionTargets.Comment, ct), await versions.Applied(itemKey, ProjectionTargets.Card, ct));

    public async Task Advance(string itemKey, long sequence, DateTimeOffset at, CancellationToken ct)
    {
        await versions.Advance(itemKey, ProjectionTargets.Comment, sequence, at, ct);
        await versions.Advance(itemKey, ProjectionTargets.Card, sequence, at, ct);
    }
}

/// <summary>What the gateway builds for the adoption: the runner (the run of one try), the job (when it runs) and the markers.</summary>
sealed record AdoptionParts(AdoptionRunner Runner, AdoptionJob Job, IEngineMarkers Markers);

static class AdoptionWiring
{
    /// <summary>
    /// The adoption of the gateway, or null when it cannot be made (no GitHub credential: nothing could be read). <paramref name="restored"/> says that the plant's restore is over; <paramref name="deriver"/> is the one of the
    /// merged items (null: the merged items are not backfilled and the trains' candidates are left).
    /// </summary>
    public static AdoptionParts? Make(GatewayComposition c, WorkItemParts parts, string connectionString, WolverineHostService host, Func<ISystemCatalog?> catalog, ItemMergedDeriver? deriver, Func<bool> restored,
        IMergeTrainStore mergeTrains, ITrainStore trains, ICapacityStore capacity, IQcEnvironmentStore qc, Func<string, string?> env, HttpClient? http = null, ReadoptionGate? readoption = null)
    {
        if (c.Instance.Home is not { } home || parts.Docs.Facts is not IAdoptionGitHub github || parts.Docs.Issues is not { } issues || parts.Docs.Boards is not { } boards || parts.Docs.Versions is not { } versions) return null;
        var root = c.Instance.Root;
        string Str(string name, string fallback) => root?[name]?.ToString() is { Length: > 0 } s ? s : fallback;
        var settings = parts.Docs.Settings;
        var columns = new AdoptionColumns(settings.ColumnOf(StepKind.Design) ?? "", settings.ColumnOf(StepKind.Develop) ?? "", settings.ColumnOf(StepKind.Verify) ?? "", settings.DoneColumn, settings.HoldColumns);
        var gates = new ItemGateConfig(settings.AutoMergeLabel, Str("videoLabel", "video"), settings.HoldLabels);
        // the saga carries every plan where none is named (the saga on its own, as the unit tests run it); the gateway's engine carries only the plans of intake.v2Plans, and with none named (config/instance.json) nothing is v2's to adopt:
        // v1's items stay v1's, and every Tier 1 scenario of a plain v2 run has items in v1's hands that must stay there
        var mapping = new AdoptionSettings(settings.Policy with { CarriedPlans = settings.Policy.CarriedPlans ?? [] }, columns, DocsOnlySettingsReader.From(root), gates, settings.SlotRuntime);
        var branch = Str("defaultBranch", "master");
        // the trains of the home repository's deployables: every environment of the chain (the train of an environment that has no state yet is just not there)
        Func<IReadOnlyList<string>> trainKeys = () => catalog()?.Current.Deployables.Where(d => d.Repository == home).SelectMany(d => DeliveryPolicy.Chain.Select(e => TrainKey.Of(d.Name, e))).ToList() ?? [];
        var options = new AdoptionOptions(home, mapping, trainKeys, MergeQueueDepth: settings.MergeQueueDepth);
        var octopus = PendingReader(root, env, http);
        var names = (root?["octopus"]?["environments"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase) ?? [];
        // the Octopus name of an environment to its key (tdd, uat, prod); a name that is none of the chain's is not a train's Sign-off
        string? KeyOf(string name) => DeliveryPolicy.Chain.FirstOrDefault(k => string.Equals(names.GetValueOrDefault(k, k), name, StringComparison.OrdinalIgnoreCase) || string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        var reads = new AdoptionGitHubReads(github, issues, boards, parts.Docs.Board, home, branch, octopus, KeyOf);
        var delivered = new DeliveredToTable(() => ItemMergedWiring.Open(connectionString));
        var markers = new SqlEngineMarkers(() => ItemMergedWiring.Open(connectionString));
        var ports = new AdoptionPorts(reads, parts.Store, new ProjectedSequences(versions), capacity, mergeTrains, trains, qc, new SqlMergedItemStore(() => ItemMergedWiring.Open(connectionString)), new DeliveredBackfillTable(delivered), deriver,
            m => host.PublishAsync(m), (m, due) => host.ScheduleAsync(m, due));
        var runner = new AdoptionRunner(options, ports, gate: readoption);
        var node = Fworker.Integrations.Commands.ClaimOwner.ThisProcess().Node;
        var job = new AdoptionJob(runner, markers, () => c.Leader.MayAct, () => c.Leader.ClaimEpoch, () => restored() && !c.GitHubBudget.Low().Low, node, readoption: readoption);
        return new AdoptionParts(runner, job, markers);
    }

    // octopus.url, .space, .project and OCTOPUS_API_KEY, as the watcher reads them; null when any is missing (the Sign-offs are then not adopted)
    static PendingSignOffReader? PendingReader(JsonNode? root, Func<string, string?> env, HttpClient? http)
    {
        var o = root?["octopus"];
        string S(string n) => o?[n]?.ToString() ?? "";
        var key = env("OCTOPUS_API_KEY") ?? "";
        if (key == "" || S("url") == "" || S("space") == "" || S("project") == "") return null;
        return new PendingSignOffReader(new HttpOctopusReader(http ?? new HttpClient(), S("url"), key), S("space"), S("project"));
    }
}

/// <summary>
/// W9.9b: the marker of a process that the kill switch (FACTORY_V2_OFF) turned back to v1 in an environment the configuration names for v2. It writes "v1-ran" once, so that the engine's next start knows v1 may have acted on the items
/// in between (the adoption fences them, W9.10n). Registered only for that combination, so no other environment, tdd's shadow run included, writes anything. A database that is paused or has no table yet is tried again with
/// growing pauses for ten minutes and then left: a missing marker is a missing piece of evidence, and the gap rule of the adoption (AdoptionBreak.Gap) still catches a long absence.
/// </summary>
sealed class KillSwitchMarkerService(IEngineMarkers markers, string holder, Func<DateTimeOffset>? clock = null) : BackgroundService
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var at = now();
        var give = DateTimeOffset.UtcNow.AddMinutes(10);
        for (var pause = TimeSpan.FromSeconds(5); DateTimeOffset.UtcNow < give && !ct.IsCancellationRequested; pause = TimeSpan.FromSeconds(Math.Min(60, pause.TotalSeconds * 2)))
        {
            try { await markers.Mark(EngineMarkerNames.V1Ran, at, holder, "FACTORY_V2_OFF", ct); return; }
            catch (OperationCanceledException) { return; }
            catch (Exception) { }
            try { await Task.Delay(pause, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
