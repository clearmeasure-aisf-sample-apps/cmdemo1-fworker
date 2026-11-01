using System.Text.Json.Nodes;
using Fworker.Integrations.GitHub;
using Fworker.Persistence;
using Fworker.Workflows.Reconcilers;
using Fworker.Workflows.Transcripts;

// W9.8: the wiring of the board group (gateway/Verbs/Routes/Board.cs, BoardVerbs.cs). Only where the engine acts (Engine.V2Acts): the transcripts move to the database, the webhook translation and the journal
// verb are answered without the rule scripts, and the parts the verbs read are registered once. Nothing here exists in v1, in the shadow run or under the kill switch.
static class BoardWiring
{
    public static void Register(IServiceCollection services, GatewayComposition c, FactorySchemaStatus schema)
    {
        if (!c.Engine.V2Acts) return;
        var gw = c.Gateway;
        var connection = c.Database.ConnectionString is { } raw ? WolverineHostService.WithConnectTimeout(raw, WolverineHostOptions.From(c.Instance).ConnectTimeoutSeconds) : null;
        ITranscriptRepository? repository = connection is null ? null : new SqlTranscriptRepository(() => WorkItemWiring.Open(connection));
        var home = c.Instance.Home;
        // the sessions' transcripts: rows of factory.Transcripts, with the ref store (v1) as the fallback of a read for the 14 days written before the move. FACTORY_TRANSCRIPTS=0 keeps them in memory only, as in v1
        if (repository is not null && gw.OwnerMode && Environment.GetEnvironmentVariable("FACTORY_TRANSCRIPTS") != "0")
            c.Transcripts.UseBackend(new DatabaseTranscriptBackend(repository, home, () => schema.IsGood, c.Transcripts.Backend));
        services.AddSingleton(sp => new BoardVerbParts
        {
            Transcripts = repository,
            Seal = new TranscriptSeal(gw.TokenSecret),
            Home = home,
            Ready = () => schema.IsGood,
            Translator = Translator(c),
            Ledger = () => c.JournalLedger,
            History = connection is null || home is null ? null : new JournalHistory(() => WorkItemWiring.Open(connection)),
            Reconciler = () => c.BoardReconciler,
            RuleScript = sp.GetRequiredService<Runner>().RunScriptAsync,   // not RunAsync: that asks the routes first and the gateway's own calls of the verbs come back here (W9.9a)
        });
    }

    // the translation of webhook payloads: the settings of the instance file, this environment's env label (Get-EnvLabel), and the two reads by GraphQL with the gateway's own credential
    static WebhookEventTranslator? Translator(GatewayComposition c)
    {
        var root = c.Instance.Root;
        string S(string k) => root?[k]?.ToString() ?? "";
        if (S("owner").Length == 0 || S("repo").Length == 0 || c.Instance.Home is not { } home) return null;
        var tokens = GitHubTokenProvider.FromEnvironment(Environment.GetEnvironmentVariable);
        var api = Environment.GetEnvironmentVariable("GITHUB_API_URL");
        var http = new HttpClient(new GitHubUserAgentHandler()) { BaseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/"), Timeout = TimeSpan.FromSeconds(60) };
        var budget = c.GitHubBudget;
        var project = root?["project"] is JsonValue pv && pv.TryGetValue<int>(out var pn) ? pn : 0;
        var lookups = new HttpWebhookLookups(http, tokens, home, project,
            (resource, remaining, reset, limit) => budget.Observe(new JsonObject { [resource] = new JsonObject { ["remaining"] = remaining, ["reset"] = reset, ["limit"] = limit } }));
        return new WebhookEventTranslator(new WebhookTranslatorConfig(S("owner"), S("repo"), S("name"), S("defaultBranch"), S("label"), EnvLabel(root, Environment.GetEnvironmentVariable)), lookups);
    }

    /// <summary>Get-EnvLabel of Board.ps1: "env:&lt;environment&gt;" when this environment is scoped (FACTORY_ENV_SCOPE 0 or 1, else the instance file's envScope names it), else "".</summary>
    public static string EnvLabel(JsonNode? root, Func<string, string?> env)
    {
        var e = (env("FACTORY_ENVIRONMENT") ?? "").Trim().ToLowerInvariant();
        if (e.Length == 0) e = "tdd";
        bool on;
        if (env("FACTORY_ENV_SCOPE") is "0" or "1") on = env("FACTORY_ENV_SCOPE") == "1";
        else on = (root?["envScope"] as JsonArray ?? []).Any(x => string.Equals(x?.ToString(), e, StringComparison.OrdinalIgnoreCase));
        return on ? "env:" + e : "";
    }
}
