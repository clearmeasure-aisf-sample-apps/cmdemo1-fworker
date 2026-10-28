using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Integrations.Correlation;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.Reconcilers;

// W4.3: the board reconciler as a recurring job of the gateway (the slot "board-reconciler", RecurringJobSlots.cs). It reads the board with GH_TOKEN or the author App's installation token (GitHubTokenProvider; the token of the
// rule scripts) and publishes each changed card into the ingress inbox as source "poll".
// A job that cannot read (no token) is not made, and the slot stays empty. The GitHub budget is v1's (GitHubBudget.cs): below its reserve the pass is skipped, as sync-board
// skips its non-essential reads, and the reads of this job report to it.
static class BoardReconcilerJob
{
    public static Func<CancellationToken, Task>? Make(GatewayComposition c, ISystemCatalog catalog, IngressInboxPort inbox, Func<string, string?> env, HttpClient? http = null)
    {
        var tokens = GitHubTokenProvider.FromEnvironment(env);   // GH_TOKEN, else the author App's installation token
        if (tokens is null) return null;
        var api = env("GITHUB_API_URL");
        http ??= new HttpClient(new GitHubAuthHandler(tokens)) { BaseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/"), Timeout = TimeSpan.FromSeconds(60) };
        var budget = c.GitHubBudget;
        var reader = new HttpBoardReader(http, new ProviderCredentialSource(tokens),
            (resource, remaining, reset, limit) => budget.Observe(new JsonObject { [resource] = new JsonObject { ["remaining"] = remaining, ["reset"] = reset, ["limit"] = limit } }));
        var reconciler = new BoardReconciler(reader, () => catalog.Current, json => inbox.Publish(new IngressInboxMessage("poll", json)), () => c.Leader.MayAct, () => budget.Low().Low);
        return async ct =>
        {
            var r = await reconciler.Run(ct);
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["boardReconciler"] = r.Outcome, ["cards"] = r.Cards, ["published"] = r.Published }.ToJsonString());
        };
    }
}
