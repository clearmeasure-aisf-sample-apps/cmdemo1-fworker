using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.GitHub;
using Fworker.Persistence;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;

// W9.3 (#355): the merge train verbs on v2 (approve-pr, merge, clear-hold, base-watch, base-update-branch), registered by gateway/Verbs/Routes/Merge.cs where the engine acts. A verb is v2's for the items the
// WorkItemSaga owns; for an item it does not know the answer is the refusal "not-answered" (exit 10), and when the saga's parts cannot be made yet (the Wolverine host is not up, no GitHub credential) it is "v2-unavailable"
// (exit 20, to be tried again): since V3.2 there is no rule script behind these verbs. The rules and answers are Fworker.Workflows.Merge; this class finds the saga's parts when the first call comes (they need the
// Wolverine host, which is built after the routes).
//   merge             the checks on the saga's state, then the saga is asked to make its merge call now (the MergeTrainSaga's turn and the C# direct merge) and its answer is reported
//   approve-pr        under merge policy auto the refusal "not-required"; the inspector's review under peer-approved
//   clear-hold        under auto the refusal "not-held"; a hold exists only under peer-approved
//   base-watch        the head of the default branch and a row per item the saga owns
//   base-update-branch  the back-merge of one pull request of an item the saga owns
/// <summary>The saga's parts the merge verbs run on: the merge verbs, the back-merge verbs, the saga's store and the home repository.</summary>
sealed record MergeVerbParts(MergeVerbService Verbs, BaseUpdateService? Base, IWorkItemStore Store, RepoId Home);

sealed class MergeVerbHandlers(Func<MergeVerbParts?> find, Func<string, VerbAnswer?>? guard = null)
{
    /// <summary>The handlers of a gateway: the saga's parts are found at the first call that can have them (they need the Wolverine host, which is built after the routes).</summary>
    public static MergeVerbHandlers For(IServiceProvider services)
    {
        var gate = new object();
        MergeVerbParts? parts = null;
        // a failure (the host is not up, no GitHub credential) is not remembered: the next call tries again, and this one is refused (retryable)
        MergeVerbParts? Find()
        {
            lock (gate)
            {
                if (parts is not null) return parts;
                try { return parts = Build(services); }
                catch (Exception e) { Console.Error.WriteLine($"merge verbs on v2: {e.GetType().Name}; the verbs are refused until the parts can be made"); return null; }
            }
        }
        // what the Runner does before it starts the script of these calls, which v2 answers without it: the calls of the gateway itself that can wait are not made while the GitHub budget is low (GitHubBudget.DeferredCalls),
        // and the one that writes to GitHub is not made by a gateway that no longer holds the leader lease (the fence of Runner.FencedVerbs; the script checks it again before its write)
        VerbAnswer? Guard(string verb)
        {
            if (services.GetService<GitHubBudget>() is { } budget && GitHubBudget.DeferredCalls.Contains(verb) && budget.Low() is { Low: true } low)
            {
                budget.CountCallRefusal();
                return new VerbAnswer(20, System.Text.Json.JsonSerializer.Serialize(new { ok = false, verb, code = "github-budget", reason = $"the gateway's GitHub budget ({low.Resource}) is below its reserve; this call waits for the reset",
                    retryAfterSeconds = Math.Max(1, (int)(low.ResetAt - DateTime.UtcNow).TotalSeconds), resetAt = low.ResetAt.ToString("o"), exit = 20 }));
            }
            if (verb == "base-update-branch" && services.GetService<GatewayLeader>() is { MayAct: false })
                return MergeVerbReplies.Reject(verb, 20, "lease-lost", "this gateway no longer holds the leader lease; the step was not performed: base-update-branch");
            return null;
        }
        return new MergeVerbHandlers(Find, Guard);
    }

    static MergeVerbParts? Build(IServiceProvider services)
    {
        var instance = services.GetRequiredService<InstanceConfig>();
        if (instance.Home is not { } home || services.GetService<WorkItemParts>() is not { } saga) return null;
        var settings = MergeVerbSettingsReader.From(instance.Root, saga.Docs.Settings.ColumnOf) with { DefaultBranch = instance.DefaultBranch };
        var host = services.GetRequiredService<WolverineHostService>();
        var clients = GitHubClients.Make(Environment.GetEnvironmentVariable);
        if (clients is null) return null;
        var db = services.GetService<DatabaseOptions>()?.ConnectionString;
        Func<FactoryDbContext>? open = db is null ? null : () => WorkItemWiring.Open(db);
        var name = instance.Root?["name"]?.ToString() is { Length: > 0 } n ? n : "factory-worker";
        // W9.10q: the migration-number collision of the script's merge, and the model ladder's refusal count (the file the rule scripts and the item verbs count in)
        var dir = RuleStateDirectory.Resolve(Environment.GetEnvironmentVariable);
        Directory.CreateDirectory(dir);
        var verbs = new MergeVerbService(home, saga.Store, new GitHubItemLabels(clients), m => host.PublishAsync(m), settings, migrations: new GitHubMergeMigrations(clients, home, instance.DefaultBranch), refusals: new ItemVerbsRefusals(dir));
        var update = new BaseUpdateService(new GitHubBaseUpdate(clients, home, instance.DefaultBranch), new SagaBaseUpdateItems(saga.Store, home, open), new BackmergeSettings(name));
        return new MergeVerbParts(verbs, update, saga.Store, home);
    }

    // the parts could not be made (the Wolverine host is not up, no GitHub credential): exit 20, to be tried again
    static VerbAnswer Unavailable(string verb) => MergeVerbReplies.Reject(verb, 20, "v2-unavailable", "the merge verbs cannot run yet: the Wolverine host or the GitHub credential is not available; try again");

    // the saga does not know the item (or the merge policy is not auto, which v2 does not answer): there is no other engine to ask
    static VerbAnswer NoSaga(string verb) => MergeVerbReplies.Reject(verb, 10, "not-answered", "v2 does not answer this call: the item has no work item saga, or the merge policy is not auto");

    static JsonObject? Body(string stdin) { try { return string.IsNullOrWhiteSpace(stdin) ? new JsonObject() : JsonNode.Parse(stdin) as JsonObject; } catch { return null; } }

    async Task<VerbAnswer> Run(string verb, string stdin, TimeSpan timeout, Func<MergeVerbParts, JsonObject, Task<VerbAnswer?>> v2)
    {
        var body = Body(stdin);
        if (guard?.Invoke(verb) is { } refused) return refused;
        if (body is null) return MergeVerbReplies.InternalError(verb, "the body is not a JSON object");
        if (find() is not { } p) return Unavailable(verb);
        VerbAnswer? answer;
        try { answer = await v2(p, body); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"merge verb {verb} on v2: {e.GetType().Name}: {e.Message}");
            return MergeVerbReplies.InternalError(verb, $"{e.GetType().Name}: {e.Message}");
        }
        // a refusal 10 that v2 answered is one data point of the model ladder, as the script counts its own (factory.ps1: complete, record-review, validation-verdict, approve-pr, merge, open-pr, append-body)
        if (answer is { } v2Answer && verb is "merge" or "approve-pr") await p.Verbs.CountRefusal(body, v2Answer);
        return answer ?? NoSaga(verb);
    }

    public Task<VerbAnswer> Merge(string stdin, TimeSpan timeout) => Run("merge", stdin, timeout, async (p, body) => await p.Verbs.Merge(body, CancellationToken.None));

    public Task<VerbAnswer> ApprovePr(string stdin, TimeSpan timeout) => Run("approve-pr", stdin, timeout, (p, body) => Task.FromResult(p.Verbs.ApprovePr(body)));

    public Task<VerbAnswer> ClearHold(string stdin, TimeSpan timeout) => Run("clear-hold", stdin, timeout, async (p, body) => await p.Verbs.ClearHold(body, CancellationToken.None));

    public Task<VerbAnswer> BaseUpdateBranch(string stdin, TimeSpan timeout) =>
        Run("base-update-branch", stdin, timeout, async (p, body) => p.Base is null ? null : await p.Base.UpdateBranch(body, CancellationToken.None));

    public async Task<VerbAnswer> BaseWatch(string stdin, TimeSpan timeout)
    {
        var body = Body(stdin);
        if (guard?.Invoke("base-watch") is { } refused) return refused;
        if (body is null) return MergeVerbReplies.InternalError("base-watch", "the body is not a JSON object");
        if (find() is not { Base: { } update } p) return Unavailable("base-watch");
        try
        {
            var items = BaseUpdateService.ItemsOf(body);
            var owned = (await p.Store.OwnedItems(CancellationToken.None)).Where(k => ItemId.TryParse(k, out var id) && id.Repository == p.Home).Select(k => ItemId.Parse(k).Number).ToHashSet();
            var mine = items.Where(owned.Contains).ToHashSet();
            // the items the saga does not own have no row: with no other engine there is nothing to read for them
            return await update.Watch(body, mine, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"merge verb base-watch on v2: {e.GetType().Name}: {e.Message}");
            return MergeVerbReplies.InternalError("base-watch", $"{e.GetType().Name}: {e.Message}");
        }
    }
}

/// <summary>What the merge verbs read of config/instance.json: merge policy, the Auto Merge label, the hold labels (hold:owner when the file names none) and the validation column.</summary>
static class MergeVerbSettingsReader
{
    public static MergeVerbSettings From(JsonNode? root, Func<StepKind, string?> columnOf)
    {
        string Text(JsonNode? n, string name, string fallback) { try { return n?[name]?.ToString() is { Length: > 0 } s ? s : fallback; } catch { return fallback; } }
        var holds = new List<string>();
        try
        {
            if (root?["holdLabels"] is JsonArray labels) holds.AddRange(labels.Select(x => x?.ToString() ?? "").Where(s => s.Length > 0));
            else holds.Add("hold:owner");
        }
        catch { holds.Add("hold:owner"); }
        return new MergeVerbSettings(Text(root, "mergePolicy", "auto"), Text(root, "autoMergeLabel", "Auto Merge"), holds, Text(root?["columns"], "validation", "Functional Testing"), columnOf);
    }
}

/// <summary>The GitHub client the merge verbs read with: the token of the rule scripts (GH_TOKEN, else the author App's installation token), against GITHUB_API_URL or api.github.com.</summary>
static class GitHubClients
{
    public static GitHubClientFactory? Make(Func<string, string?> env)
    {
        var tokens = GitHubTokenProvider.FromEnvironment(env);
        if (tokens is null) return null;
        var api = env("GITHUB_API_URL");
        var http = new HttpClient(new GitHubAuthHandler(tokens)) { BaseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/"), Timeout = TimeSpan.FromSeconds(60) };
        return new GitHubClientFactory(http, new ProviderCredentialSource(tokens), new Fworker.Integrations.GitHub.GitHubBudget());
    }
}
