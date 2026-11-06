using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.GitHub;
using Fworker.Persistence;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;

// W9.3 (#355): the merge train verbs on v2 (approve-pr, merge, clear-hold, base-watch, base-update-branch), registered by gateway/Verbs/Routes/Merge.cs only where the engine acts. V3.3 (slice 6): a verb is v2's alone,
// for every item and every call; nothing is handed to the rule script any more (the hybrid composition's script for an item the saga does not own, and for a merge policy other than auto, is gone: the adoption gives the
// saga every open item, and the peer-approved policy was dropped, decision 4 of 2026-10-30). The rules and answers are Fworker.Workflows.Merge; this class finds the saga's parts when the first call comes (they need the
// Wolverine host, which is built after the routes), and refuses a call, to be tried again, while it cannot find them.
//   merge             the merge check on the saga's state, then the saga is asked to make its merge call now (the MergeTrainSaga's turn and the C# direct merge) and its answer is reported
//   approve-pr        under merge policy auto the refusal "not-required"
//   clear-hold        under auto the refusal "not-held" (a hold exists only under peer-approved)
//   base-watch        the head of the default branch and a row per item: the saga's open items are read, every other item is a row skipped as "not-owned"
//   base-update-branch  the back-merge of one pull request of an item the saga owns
// What the saga's verbs leave unanswered is refused: "merge-policy" (exit 30) under a policy other than auto, "not-owned" (exit 10) for an item the saga does not own. Before any of it, merge and clear-hold refuse another
// environment's item "out-of-scope" (exit 10) where the environment scope is on, as the script's Get-ItemInfo did first (the saga never owns such an item: its admission takes the environment's items only).
/// <summary>The saga's parts the merge verbs run on: the merge verbs, the back-merge verbs, the saga's store, the home repository, and the item labels with this environment's env: label ("" while the scope is off).</summary>
sealed record MergeVerbParts(MergeVerbService Verbs, BaseUpdateService? Base, IWorkItemStore Store, RepoId Home, IItemLabels? Labels = null, string EnvLabel = "");

sealed class MergeVerbHandlers(Func<MergeVerbParts?> find, Func<string, VerbAnswer?>? guard = null)
{
    /// <summary>The handlers of a gateway: the saga's parts are found at the first call that can have them (they need the Wolverine host, which is built after the routes).</summary>
    public static MergeVerbHandlers For(IServiceProvider services)
    {
        var gate = new object();
        MergeVerbParts? parts = null;
        // a failure (the host is not up, no GitHub credential) is not remembered: the next call tries again, and this one is refused to be tried again
        MergeVerbParts? Find()
        {
            lock (gate)
            {
                if (parts is not null) return parts;
                try { return parts = Build(services); }
                catch (Exception e) { Console.Error.WriteLine($"merge verbs on v2: {e.GetType().Name}; the call is refused until the parts are there"); return null; }
            }
        }
        // what the Runner does before it starts a script, done for these calls, which v2 answers without one: the calls of the gateway itself that can wait are not made while the GitHub budget is low (GitHubBudget.DeferredCalls),
        // and the one that writes to GitHub is not made by a gateway that no longer holds the leader lease (the fence of Runner.FencedVerbs)
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
        var labels = new GitHubItemLabels(clients);
        var verbs = new MergeVerbService(home, saga.Store, labels, m => host.PublishAsync(m), settings, migrations: new GitHubMergeMigrations(clients, home, instance.DefaultBranch), refusals: new ItemVerbsRefusals(dir));
        var update = new BaseUpdateService(new GitHubBaseUpdate(clients, home, instance.DefaultBranch), new SagaBaseUpdateItems(saga.Store, home, open), new BackmergeSettings(name));
        return new MergeVerbParts(verbs, update, saga.Store, home, labels, BoardWiring.EnvLabel(instance.Root, Environment.GetEnvironmentVariable));
    }

    static JsonObject? Body(string stdin) { try { return string.IsNullOrWhiteSpace(stdin) ? new JsonObject() : JsonNode.Parse(stdin) as JsonObject; } catch { return null; } }

    static VerbAnswer NotAnObject(string verb) => MergeVerbReplies.InternalError(verb, "the body is not a JSON object");

    // the saga's parts are not there yet (the Wolverine host is not built, or there is no home repository or GitHub credential): the caller asks again
    static VerbAnswer NotReady(string verb) => MergeVerbReplies.Reject(verb, 20, "not-ready",
        "the merge train is not ready in this gateway (the Wolverine host, the home repository or a GitHub credential is missing); try again", new JsonObject { ["retryAfterSeconds"] = 30 });

    // what the saga's verbs answer null for: a merge policy other than auto (the back-merge reads no policy), or an item the saga does not own
    static VerbAnswer Unanswered(string verb, MergeVerbParts p, JsonObject body) => !p.Verbs.Settings.Auto && verb != "base-update-branch"
        ? MergeVerbReplies.Reject(verb, 30, "merge-policy", $"merge policy '{p.Verbs.Settings.MergePolicy}' is not supported: the gateway merges under merge policy 'auto' only")
        : MergeVerbReplies.Reject(verb, 10, "not-owned", $"item #{body["item"]} is not an item the work item saga owns");

    // Test-ItemInScope behind Get-ItemInfo: with the scope on, the item carries exactly one env: label and it is this environment's; an issue that is not there is the script's item-not-found
    static async Task<VerbAnswer?> OutOfScope(string verb, MergeVerbParts p, JsonObject body)
    {
        if (p.EnvLabel.Length == 0 || p.Labels is null || !MergeVerbReplies.ItemNumber(verb, body, out var item, out _)) return null;
        if (await p.Labels.LabelsOf(new ItemId(p.Home, item), CancellationToken.None) is not { } labels) return MergeVerbReplies.Reject(verb, 12, "item-not-found", $"issue #{item} not found");
        var envs = labels.Where(l => l.StartsWith("env:", StringComparison.OrdinalIgnoreCase)).ToList();
        return envs.Count == 1 && string.Equals(envs[0], p.EnvLabel, StringComparison.OrdinalIgnoreCase) ? null
            : MergeVerbReplies.Reject(verb, 10, "out-of-scope", $"issue #{item} is outside this gateway's scope: it does not carry exactly one env: label, '{p.EnvLabel}'");
    }

    static JsonObject NotOwnedRow(int item) => new() { ["item"] = item, ["pr"] = 0, ["branch"] = "", ["head"] = "", ["behind"] = 0, ["overlap"] = new JsonArray(), ["skip"] = "not-owned" };

    async Task<VerbAnswer> Run(string verb, string stdin, Func<MergeVerbParts, JsonObject, Task<VerbAnswer?>> v2, bool scoped = false)
    {
        var body = Body(stdin);
        if (guard?.Invoke(verb) is { } refused) return refused;
        if (body is null) return NotAnObject(verb);
        if (find() is not { } p) return NotReady(verb);
        VerbAnswer? answer;
        try { answer = (scoped ? await OutOfScope(verb, p, body) : null) ?? await v2(p, body); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"merge verb {verb} on v2: {e.GetType().Name}: {e.Message}");
            return MergeVerbReplies.InternalError(verb, $"{e.GetType().Name}: {e.Message}");
        }
        if (answer is not { } v2Answer) return Unanswered(verb, p, body);
        // a refusal 10 that v2 answered is one data point of the model ladder (factory.ps1: complete, record-review, validation-verdict, approve-pr, merge, open-pr, append-body)
        if (verb is "merge" or "approve-pr") await p.Verbs.CountRefusal(body, v2Answer);
        return v2Answer;
    }

    public Task<VerbAnswer> Merge(string stdin, TimeSpan timeout) => Run("merge", stdin, async (p, body) => await p.Verbs.Merge(body, CancellationToken.None), scoped: true);

    public Task<VerbAnswer> ApprovePr(string stdin, TimeSpan timeout) => Run("approve-pr", stdin, (p, body) => Task.FromResult(p.Verbs.ApprovePr(body)));

    public Task<VerbAnswer> ClearHold(string stdin, TimeSpan timeout) => Run("clear-hold", stdin, async (p, body) => await p.Verbs.ClearHold(body, CancellationToken.None), scoped: true);

    public Task<VerbAnswer> BaseUpdateBranch(string stdin, TimeSpan timeout) =>
        Run("base-update-branch", stdin, async (p, body) => p.Base is null ? null : await p.Base.UpdateBranch(body, CancellationToken.None));

    public async Task<VerbAnswer> BaseWatch(string stdin, TimeSpan timeout)
    {
        var body = Body(stdin);
        if (guard?.Invoke("base-watch") is { } refused) return refused;
        if (body is null) return NotAnObject("base-watch");
        if (find() is not { Base: { } update } p) return NotReady("base-watch");
        try
        {
            var items = BaseUpdateService.ItemsOf(body);
            var owned = (await p.Store.OwnedItems(CancellationToken.None)).Where(k => ItemId.TryParse(k, out var id) && id.Repository == p.Home).Select(k => ItemId.Parse(k).Number).ToHashSet();
            var mine = items.Where(owned.Contains).ToHashSet();
            var answer = await update.Watch(body, mine, CancellationToken.None);
            if (items.All(mine.Contains)) return answer;
            // an item the saga does not own has no branch the gateway keeps current: its row says so, in the order the call named the items
            var ours = (JsonObject)JsonNode.Parse(answer.Output)!;
            var byItem = new Dictionary<int, JsonNode>();
            foreach (var row in ours["rows"] as JsonArray ?? []) if (row?["item"]?.GetValue<int>() is { } n) byItem[n] = row.DeepClone();
            var rows = new JsonArray();
            foreach (var i in items) rows.Add(byItem.TryGetValue(i, out var row) ? row.DeepClone() : NotOwnedRow(i));
            ours["rows"] = rows;
            return new VerbAnswer(0, ours.ToJsonString());
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
