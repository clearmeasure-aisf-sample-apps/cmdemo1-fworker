using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Persistence;
using Fworker.Workflows.Qc;

// W9.1: the QC service's ports (src/Fworker.Workflows/Qc/QcPorts.cs) made over the gateway's GitHub clients. Only where the engine acts (V2VerbRoutes -> QcRoutes); v1 keeps Qc.ps1.
// The reads and writes are the rule script's own: the same REST paths and the same GraphQL queries as plugin/scripts/lib/{Core,Board,Prs,Verbs.Inspect}.ps1, with Invoke-Gh's reading of the answers
// (404 is 'github-not-found' 12, a rate limit 'github-rate-limited' 20, a refusal of permission 'github-forbidden' 30, anything else 'github-error' 20).

/// <summary>GitHub for the QC verbs: the pull requests, their files, reviews and comments, and the issue and board reads (Get-ItemInfo, Get-BoardList).</summary>
sealed class QcGitHubClient(HttpClient http, GitHubTokenProvider tokens, QcConfig cfg, Func<string, string?> env, Action<string, string?, string?, string?>? observe = null) : IQcGitHub
{
    string? login;

    // ---- the call: Invoke-GhRaw and Invoke-Gh --------------------------------------------------------------------------------------------------------

    async Task<(int Status, string Body, HttpResponseHeaders Headers)> Raw(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(method, path.TrimStart('/'));
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
                if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var resp = await http.SendAsync(req, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                static string? H(HttpResponseMessage r, string n) => r.Headers.TryGetValues(n, out var v) ? v.FirstOrDefault() : null;
                if (H(resp, "x-ratelimit-resource") is { } resource) observe?.Invoke(resource, H(resp, "x-ratelimit-remaining"), H(resp, "x-ratelimit-reset"), H(resp, "x-ratelimit-limit"));
                var status = (int)resp.StatusCode;
                if (status is 502 or 503 or 504 && attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct); continue; }
                return (status, text, resp.Headers);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= 3) throw new QcReject(20, "github-unreachable", e.Message, new JsonObject { ["retryAfterSeconds"] = 30 });
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }

    /// <summary>Invoke-Gh: the parsed body of a 2xx answer; the codes of the script for anything else (<paramref name="allow404"/>: null for a 404).</summary>
    async Task<JsonNode?> Call(HttpMethod method, string path, object? body, CancellationToken ct, bool allow404 = false)
    {
        var (status, text, headers) = await Raw(method, path, body, ct);
        if (status is >= 200 and < 300) return string.IsNullOrWhiteSpace(text) ? null : ParseJson(text);
        if (status == 404 && allow404) return null;
        var detail = text.Length > 400 ? text[..400] : text;
        var extra = new JsonObject { ["detail"] = detail };
        switch (status)
        {
            case 404: throw new QcReject(12, "github-not-found", $"GitHub 404 on {method.Method} {path}", extra);
            case 403 or 429:
                var message = ParseJson(text)?["message"]?.ToString() ?? detail;
                var wait = RateLimitWait(status, headers, message);
                if (wait >= 0) { extra["retryAfterSeconds"] = wait; throw new QcReject(20, "github-rate-limited", $"GitHub {status} on {method.Method} {path}", extra); }
                extra["status"] = 403;
                throw new QcReject(30, "github-forbidden", $"GitHub refused {method.Method} {path} (403): {message}", extra);
            default:
                extra["status"] = status; extra["retryAfterSeconds"] = 30;
                throw new QcReject(20, "github-error", $"GitHub {status} on {method.Method} {path}", extra);
        }
    }

    static JsonNode? ParseJson(string text) { try { return JsonNode.Parse(text); } catch (JsonException) { return null; } }

    // Get-GhRateLimitWait: the seconds to wait when GitHub says it is a rate limit, else -1
    static int RateLimitWait(int status, HttpResponseHeaders headers, string message)
    {
        string? H(string n) => headers.TryGetValues(n, out var v) ? v.FirstOrDefault() : null;
        if (int.TryParse(H("retry-after"), out var ra)) return Math.Max(1, ra);
        if (H("x-ratelimit-remaining") == "0") return long.TryParse(H("x-ratelimit-reset"), out var reset) ? (int)Math.Max(1, reset - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) : 60;
        if (status == 429 || System.Text.RegularExpressions.Regex.IsMatch(message, "rate limit|abuse detection|secondary rate", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return 60;
        return -1;
    }

    /// <summary>Invoke-GhGraphQL: the data of the answer.</summary>
    public async Task<JsonNode?> GraphQl(string query, JsonObject variables, CancellationToken ct)
    {
        var (status, text, headers) = await Raw(HttpMethod.Post, "graphql", new { query, variables }, ct);
        if (status != 200) throw new QcReject(20, "github-graphql-error", $"GraphQL HTTP {status}", new JsonObject { ["retryAfterSeconds"] = 30 });
        var root = ParseJson(text);
        if (root?["errors"] is JsonArray { Count: > 0 } errors)
        {
            var message = string.Join("; ", errors.Select(e => e?["message"]?.ToString()));
            if (errors.Any(e => e?["type"]?.ToString() == "RATE_LIMITED"))
            {
                var reset = headers.TryGetValues("x-ratelimit-reset", out var rv) && long.TryParse(rv.FirstOrDefault(), out var r) ? (int)Math.Max(1, r - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) : 60;
                throw new QcReject(20, "github-rate-limited", message, new JsonObject { ["retryAfterSeconds"] = reset });
            }
            throw new QcReject(20, "github-graphql-error", message, new JsonObject { ["retryAfterSeconds"] = 30 });
        }
        return root?["data"];
    }

    // ---- the pull requests and comments --------------------------------------------------------------------------------------------------------------

    public async Task<JsonObject?> GetPull(int number, CancellationToken ct) => await Call(HttpMethod.Get, $"{cfg.RepoPath}/pulls/{number}", null, ct, allow404: true) as JsonObject;

    public async Task<IReadOnlyList<JsonObject>> OpenPulls(CancellationToken ct) =>
        (await Call(HttpMethod.Get, $"{cfg.RepoPath}/pulls?state=open&per_page=100", null, ct) as JsonArray ?? []).OfType<JsonObject>().ToList();

    public async Task<IReadOnlyList<string>> PullFiles(int number, CancellationToken ct)
    {
        var all = new List<string>();
        for (var page = 1; ; page++)
        {
            var chunk = await Call(HttpMethod.Get, $"{cfg.RepoPath}/pulls/{number}/files?per_page=100&page={page}", null, ct) as JsonArray ?? [];
            all.AddRange(chunk.OfType<JsonObject>().Select(f => QcJson.Str(f, "filename")));
            if (chunk.Count != 100) return all;
        }
    }

    public async Task<IReadOnlyList<JsonObject>> Reviews(int number, CancellationToken ct) =>
        (await Call(HttpMethod.Get, $"{cfg.RepoPath}/pulls/{number}/reviews?per_page=100", null, ct) as JsonArray ?? []).OfType<JsonObject>().ToList();

    public async Task<IReadOnlyList<QcComment>> Comments(int number, CancellationToken ct)
    {
        var all = new List<QcComment>();
        for (var page = 1; ; page++)
        {
            var chunk = await Call(HttpMethod.Get, $"{cfg.RepoPath}/issues/{number}/comments?per_page=100&page={page}", null, ct) as JsonArray ?? [];
            foreach (var c in chunk.OfType<JsonObject>()) all.Add(new QcComment(c["id"] is JsonValue id && id.TryGetValue<long>(out var l) ? l : 0, QcJson.Str(QcJson.Obj(c, "user"), "login"), QcJson.Str(c, "body")));
            if (chunk.Count != 100) return all;
        }
    }

    public async Task PostComment(int number, string body, CancellationToken ct) => await Call(HttpMethod.Post, $"{cfg.RepoPath}/issues/{number}/comments", new { body }, ct);

    public async Task PatchComment(long commentId, string body, CancellationToken ct) => await Call(HttpMethod.Patch, $"{cfg.RepoPath}/issues/comments/{commentId}", new { body }, ct);

    public async Task<JsonObject> MarkReady(JsonObject pull, CancellationToken ct)
    {
        if (!QcJson.Bool(pull, "draft")) return pull;
        try { await GraphQl("mutation($id:ID!){ markPullRequestReadyForReview(input:{pullRequestId:$id}){ pullRequest{ number } } }", new JsonObject { ["id"] = QcJson.Str(pull, "node_id") }, ct); }
        catch (QcReject) { return pull; }
        return await GetPull(QcJson.Int(pull, "number"), ct) ?? pull;
    }

    public async Task<string> ServerLogin(CancellationToken ct)
    {
        if (login is not null) return login;
        if (env("FACTORY_SERVER_LOGIN") is { Length: > 0 } fixedLogin) return login = fixedLogin;
        // an App installation token cannot ask GitHub who it is (GET /user is 403 for it): the login of an App is <slug>[bot]
        if (tokens.AppLogin is { } bot) return login = bot;
        return login = QcJson.Str(await Call(HttpMethod.Get, "user", null, ct) as JsonObject, "login");
    }

    // ---- the issue and the board ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Test-ItemInScope: with scoping on, the labels carry exactly one env: label and it is this environment's.</summary>
    bool InScope(IEnumerable<string> labels)
    {
        if (cfg.EnvLabel.Length == 0) return true;
        var envs = labels.Where(l => l.StartsWith("env:", StringComparison.OrdinalIgnoreCase)).ToList();
        return envs.Count == 1 && string.Equals(envs[0], cfg.EnvLabel, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<QcItemInfo> ItemInfo(int item, CancellationToken ct)
    {
        const string query = """
            query($o:String!,$r:String!,$n:Int!){ repository(owner:$o,name:$r){ issue(number:$n){ id number state title body url createdAt
              labels(first:50){ nodes{ name } }
              projectItems(first:20){ nodes{ id project{ number } fieldValueByName(name:"Status"){ ... on ProjectV2ItemFieldSingleSelectValue{ name } } } } } } }
            """;
        var data = await GraphQl(query, new JsonObject { ["o"] = cfg.Owner, ["r"] = cfg.Repo, ["n"] = item }, ct);
        var issue = data?["repository"]?["issue"];
        if (issue is null) throw new QcReject(12, "item-not-found", $"issue #{item} not found");
        var labels = (issue["labels"]?["nodes"] as JsonArray ?? []).Select(l => l?["name"]?.ToString() ?? "").ToList();
        if (!InScope(labels)) throw new QcReject(10, "out-of-scope", $"issue #{item} is outside this gateway's scope: it does not carry exactly one env: label, '{cfg.EnvLabel}'");
        var card = (issue["projectItems"]?["nodes"] as JsonArray ?? []).FirstOrDefault(n => n?["project"]?["number"] is JsonValue pn && pn.TryGetValue<int>(out var num) && num == cfg.Project);
        return new QcItemInfo(item, issue["state"]?.ToString() == "OPEN", issue["title"]?.ToString() ?? "", labels, card?["fieldValueByName"]?["name"]?.ToString());
    }

    public async Task<IReadOnlyList<QcBoardItem>> Board(CancellationToken ct)
    {
        const string query = """
            query($o:String!,$n:Int!,$after:String){ organization(login:$o){ projectV2(number:$n){ items(first:100, after:$after){ pageInfo{ hasNextPage endCursor }
              nodes{ id fieldValueByName(name:"Status"){ ... on ProjectV2ItemFieldSingleSelectValue{ name } }
                content{ ... on Issue{ number state title body repository{ nameWithOwner } labels(first:30){ nodes{ name } } } } } } } } }
            """;
        var list = new List<QcBoardItem>(); string? after = null;
        while (true)
        {
            var data = await GraphQl(query, new JsonObject { ["o"] = cfg.Owner, ["n"] = cfg.Project, ["after"] = after }, ct);
            var items = data?["organization"]?["projectV2"]?["items"];
            foreach (var node in items?["nodes"] as JsonArray ?? [])
            {
                var content = node?["content"];
                if (content is null || content["number"] is not JsonValue nv || !nv.TryGetValue<int>(out var number)) continue;
                // a card of another repository linked to the board: its number is not an issue of this repository, so it is never seen here
                var repo = content["repository"]?["nameWithOwner"]?.ToString() ?? "";
                if (!string.Equals(repo, cfg.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                var labels = (content["labels"]?["nodes"] as JsonArray ?? []).Select(l => l?["name"]?.ToString() ?? "").ToList();
                if (!InScope(labels)) continue;   // another environment's card
                list.Add(new QcBoardItem(number, content["state"]?.ToString() == "OPEN", node?["fieldValueByName"]?["name"]?.ToString(), labels));
            }
            if (items?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() != true) return list;
            after = items["pageInfo"]?["endCursor"]?.ToString();
        }
    }
}

/// <summary>
/// The work item: the issue and board (QcGitHubClient), the state comment and the journal through the stores of the journal command (the same Get-State, Save-State and Add-Journal in C#). The save of the state comment is taken under the
/// item's lock (the one IItemLock, W9.10l), as the journal ledger's is: the store's save is a read of the revision followed by a write, and two savers that both pass the read overwrite each other (the plant's "offered" journal line,
/// posted while the QC record was being assigned, wrote back the record as it was read, and the worker's accept found no assignment: exit 11, then 40 for every verb of the environment).
/// </summary>
sealed class QcItemsAdapter(QcGitHubClient github, IJournalStore store, JournalLedger ledger, IItemLock itemLock) : IQcItems
{
    public Task<QcItemInfo> GetInfo(int item, CancellationToken ct) => github.ItemInfo(item, ct);

    public async Task<QcItemState> ReadState(int item, CancellationToken ct)
    {
        try
        {
            var st = await store.Read(item, ct);
            return new QcItemState { State = st.State, Comments = st.Comments.Select(c => new QcComment(c.Id, c.Login, c.Body)).ToList(), Handle = st };
        }
        catch (JournalRefusedException e) when (e.Message.Contains("GitHub 404", StringComparison.Ordinal)) { throw new QcReject(12, "github-not-found", e.Message); }
        catch (Exception e) when (e is HttpRequestException or JournalRefusedException) { throw new QcReject(20, "github-error", e.Message, new JsonObject { ["retryAfterSeconds"] = 30 }); }
    }

    public async Task SaveState(int item, QcItemState state, CancellationToken ct)
    {
        var holder = (ItemState)state.Handle!;
        try { await itemLock.Use(item, async () => { await store.Save(item, holder, ct); return 0; }, ct); }
        catch (ItemBusyException e) { throw new QcReject(20, "item-busy", e.Message, new JsonObject { ["retryAfterSeconds"] = 5 }); }
        catch (StateConflictException e) { throw new QcStateConflictException(e.Message); }
        catch (JournalRefusedException e) when (e.Message.StartsWith("state-too-large", StringComparison.Ordinal)) { throw new QcReject(10, "state-too-large", e.Message); }
        catch (Exception e) when (e is HttpRequestException or JournalRefusedException) { throw new QcReject(20, "github-error", e.Message, new JsonObject { ["retryAfterSeconds"] = 30 }); }
    }

    public async Task Journal(int item, string ev, JsonObject fields, string key, CancellationToken ct) =>
        await ledger.Append(item, ev, JournalLedger.FieldsOf(fields), key, ct);

    public Task<IReadOnlyList<QcBoardItem>> Board(CancellationToken ct) => github.Board(ct);
}

/// <summary>The inspector's facts through the verb router: the rule script's inspect-facts until W9.5 answers it on v2. Resolved at the first call (the router is built after the verbs it routes).</summary>
sealed class QcFactsAdapter(Func<IVerbRouter> router) : IQcFacts
{
    public async Task<IReadOnlyList<string>> MandatoryCodes(int item, int pr, CancellationToken ct)
    {
        var answer = await router().RunAsync("inspect-facts", new JsonObject { ["item"] = item, ["pr"] = pr }.ToJsonString(), TimeSpan.FromSeconds(120));
        JsonObject? o = null; try { o = JsonNode.Parse(answer.Output) as JsonObject; } catch (JsonException) { }
        if (answer.Code != 0)
            throw new QcReject(answer.Code, QcJson.Str(o, "code") is { Length: > 0 } c ? c : "error", QcJson.Str(o, "reason"), o is null ? null : (JsonObject)o.DeepClone());
        return (o?["mandatory"] as JsonArray ?? []).Select(m => QcJson.Str(m as JsonObject, "code")).Where(c => c.Length > 0).ToList();
    }
}

/// <summary>
/// qc-items.json of the state directory, which v1's sweep reads (Get-QcIndex) to reach an item that left Functional Testing with a live environment: a JSON array of item numbers. The directory is the script's
/// (FACTORY_STATE_DIR, else state under FACTORY_DATA_DIR, else under the user's home or the temp directory).
/// </summary>
sealed class FileQcIndex(Func<string, string?> env) : IQcIndex
{
    readonly object gate = new();

    string File_()
    {
        var dir = env("FACTORY_STATE_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            var data = env("FACTORY_DATA_DIR");
            if (string.IsNullOrEmpty(data))
            {
                var home = env("HOME") ?? env("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                data = !string.IsNullOrEmpty(home) && Directory.Exists(home) ? Path.Combine(home, ".local/share/factory-worker") : Path.Combine(Path.GetTempPath(), "factory-worker");
            }
            dir = Path.Combine(data, "state");
        }
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "qc-items.json");
    }

    public IReadOnlyList<int> Items()
    {
        lock (gate)
        {
            try
            {
                var f = File_();
                if (!File.Exists(f)) return [];
                // (the script reads a single number as a list of one; it writes a list)
                var parsed = JsonNode.Parse(File.ReadAllText(f));
                var numbers = parsed as JsonArray ?? (parsed is JsonValue ? new JsonArray(parsed.DeepClone()) : []);
                return numbers.Select(x => QcJson.TryInt(x, out var i) ? i : 0).Where(i => i != 0).ToList();
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
        }
    }

    public void Update(int item, bool live)
    {
        lock (gate)
        {
            try
            {
                var cur = Items().Where(i => i != item).ToList();
                if (live) cur.Add(item);
                File.WriteAllText(File_(), new JsonArray(cur.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()).ToJsonString());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"qc: the index of live items was not written: {e.Message}"); }
        }
    }
}

/// <summary>
/// What the rule scripts' requests carry and the gateway's own clients do not: the User-Agent of the rule scripts (the fake's golden writes tell a script's write from the gateway's own by it) and, in the Tier 1 test
/// mode only (FACTORY_TEST_CAUSE_HEADER=1, TestCause.cs), the cause id of the call being served as X-Fake-Cause, which Invoke-GhRaw sends with every request. The QC verbs write as the script did.
/// </summary>
sealed class QcRequestHandler(HttpMessageHandler? inner = null) : DelegatingHandler(inner ?? new HttpClientHandler())
{
    static readonly System.Text.RegularExpressions.Regex ValidCause = new("^[A-Za-z0-9._-]{1,80}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Headers.UserAgent.Count == 0) request.Headers.UserAgent.ParseAdd("factory-worker-gateway");
        if (TestCause.On && TestCause.Current is { } cause && ValidCause.IsMatch(cause)) request.Headers.TryAddWithoutValidation("X-Fake-Cause", cause);
        return base.SendAsync(request, ct);
    }
}

/// <summary>The table is the scripts' (DbUp's): not read or written before the schema is good (a fresh database has no tables yet).</summary>
sealed class SchemaGatedQcStore(FactorySchemaStatus schema, IQcEnvironmentStore inner) : IQcEnvironmentStore
{
    public Task<QcEnvironmentEntry?> Load(string key, CancellationToken ct) => schema.IsGood ? inner.Load(key, ct) : Task.FromResult<QcEnvironmentEntry?>(null);
    public Task Save(string key, string repository, string? itemKey, JsonObject record, CancellationToken ct) => schema.IsGood ? inner.Save(key, repository, itemKey, record, ct) : Task.CompletedTask;
    public Task<IReadOnlyList<QcEnvironmentEntry>> Live(string repository, CancellationToken ct) => schema.IsGood ? inner.Live(repository, ct) : Task.FromResult<IReadOnlyList<QcEnvironmentEntry>>([]);
}

static class QcWiring
{
    /// <summary>
    /// The QC service for a gateway whose engine acts; null without a GitHub identity (GH_TOKEN or the author App) or a home repository, where the verbs stay on v1. The table is used when the gateway has a database.
    /// </summary>
    public static QcService? Make(GatewayComposition c, IServiceProvider sp, Func<string, string?>? env = null, HttpClient? http = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (c.Instance.Home is not { } home) return null;
        var tokens = GitHubTokenProvider.FromEnvironment(env);
        if (tokens is null) return null;
        var cfg = QcConfig.From(c.Instance.Root, env);
        var api = env("GITHUB_API_URL");
        http ??= new HttpClient(new GitHubAuthHandler(tokens, new QcRequestHandler(), "factory-worker-gateway")) { BaseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/"), Timeout = TimeSpan.FromSeconds(60) };
        var budget = c.GitHubBudget;
        Action<string, string?, string?, string?> observe = (resource, remaining, reset, limit) =>
            budget?.Observe(new JsonObject { [resource] = new JsonObject { ["remaining"] = remaining, ["reset"] = reset, ["limit"] = limit } });
        var root = c.Instance.Root;
        var authors = root?["stateAuthors"] is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
        IQcEnvironmentStore? table = null;
        if (c.Database.ConnectionString is { Length: > 0 } cs && sp.GetService<FactorySchemaStatus>() is { } schema)
            table = new SchemaGatedQcStore(schema, new SqlQcEnvironmentStore(() => WorkItemWiring.Open(WolverineHostService.WithConnectTimeout(cs, sp.GetRequiredService<WolverineHostOptions>().ConnectTimeoutSeconds))));
        return Build(cfg, home, tokens, http, env, observe, authors, () => sp.GetRequiredService<IVerbRouter>(), table, new FileQcIndex(env), locks: ItemLocks.From(sp, env));
    }

    /// <summary>The service over the given clients: the GitHub client of the QC reads and writes, the journal store and ledger of the journal command (Get-State, Save-State, Add-Journal), the inspector through the router.</summary>
    public static QcService Build(QcConfig cfg, Fworker.Contracts.Ids.RepoId home, GitHubTokenProvider tokens, HttpClient http, Func<string, string?> env, Action<string, string?, string?, string?>? observe,
        IReadOnlyList<string> stateAuthors, Func<IVerbRouter> router, IQcEnvironmentStore? table, IQcIndex index, Func<DateTime>? utcNow = null, IItemLock? locks = null)
    {
        var github = new QcGitHubClient(http, tokens, cfg, env, observe);
        var clients = new GitHubClientFactory(http, new ProviderCredentialSource(tokens), new Fworker.Integrations.GitHub.GitHubBudget());
        var repoClient = clients.For(home).GetAwaiter().GetResult();
        var store = new GitHubJournalStore(repoClient, new JournalIdentity(cfg.Name, ct => github.ServerLogin(ct), stateAuthors), utcNow);
        var minutes = cfg.Timing.TryGetValue("journalWaitingMinutes", out var m) ? m : 30;
        var itemLock = locks ?? ItemLocks.Default(env);
        var ledger = new JournalLedger(store, new JournalSettings(minutes, cfg.SecretPatterns), utcNow, itemLock: itemLock);
        return new QcService(cfg, new QcItemsAdapter(github, store, ledger, itemLock), github, new QcFactsAdapter(router), index, table, utcNow);
    }
}
