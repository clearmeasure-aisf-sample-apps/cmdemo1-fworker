using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.WorkItem;
using Fworker.Workflows.WorkItem.Verbs;

// W9.2 (#354): the item verbs in the gateway. Fworker.Workflows.WorkItem.Verbs holds the verbs as the rule scripts' ports against ports; this file is the other side of those ports, over what the gateway already has:
//  - ItemVerbsWire: the REST and GraphQL wire to GitHub with the gateway's identity (GH_TOKEN, else the author App), as Invoke-GhRaw makes it (headers, the three tries, the rate numbers for the shared budget). In the test mode of the golden
//    writes it sends the cause id of the request, so the writes of a v2 verb are told from the background's as a script's are.
//  - ItemVerbsState: Get-State and Save-State of the item's state comment, and Add-Journal into the holder in hand, over the journal store and ledger of Fworker.Integrations (the comment is in v1's format, so v1 can take every item back).
//  - ItemVerbsLock, ItemVerbsRefusals: the lock file of an item and the refusals file of the model ladder, the files the rule scripts use (so a script of v1 that runs beside a v2 verb excludes it, and the ladder sees both).
//  - ItemVerbsV1: the calls that stay the script's for now, through the runner.
// Only where the engine acts: ItemRoutes.Register builds it, and V2VerbRoutes.Build calls that only for Engine.V2Acts.

/// <summary>The wire to GitHub of the item verbs.</summary>
sealed class ItemVerbsWire(HttpClient http, Action<string, string?, string?, string?>? observe = null, Func<int, TimeSpan>? backoff = null) : IItemWire
{
    readonly Func<int, TimeSpan> backoff = backoff ?? (t => TimeSpan.FromSeconds(2 * t));

    public async Task<ItemGhAnswer> Send(ItemGhCall call, CancellationToken ct)
    {
        const int tries = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(new HttpMethod(call.Method), call.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? call.Path : call.Path.TrimStart('/'));
                req.Headers.Accept.ParseAdd(call.Accept);
                req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
                req.Headers.UserAgent.ParseAdd("factory-worker-gateway");
                // test-only (Tier 1 golden writes): the cause id of the harness's call this request serves
                if (TestCause.On && TestCause.Current is { Length: > 0 } cause) req.Headers.TryAddWithoutValidation("X-Fake-Cause", cause);
                if (call.Body is not null) req.Content = new StringContent(call.Body.ToJsonString(), Encoding.UTF8, "application/json");
                using var resp = await http.SendAsync(req, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in resp.Headers) headers[h.Key] = h.Value.FirstOrDefault() ?? "";
                foreach (var h in resp.Content.Headers) headers[h.Key] = h.Value.FirstOrDefault() ?? "";
                if (headers.TryGetValue("x-ratelimit-resource", out var res)) observe?.Invoke(res, headers.GetValueOrDefault("x-ratelimit-remaining"), headers.GetValueOrDefault("x-ratelimit-reset"), headers.GetValueOrDefault("x-ratelimit-limit"));
                if ((int)resp.StatusCode is 502 or 503 or 504 && attempt < tries) { await Task.Delay(backoff(attempt), ct); continue; }
                return new ItemGhAnswer((int)resp.StatusCode, Parse(text), headers);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= tries) throw new ItemRefusal(20, "github-unreachable", e.Message, new JsonObject { ["retryAfterSeconds"] = 30 });
                await Task.Delay(backoff(attempt), ct);
            }
        }
    }

    /// <summary>What Invoke-RestMethod hands the script: the JSON of the answer, or its text when it is none.</summary>
    static JsonNode? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { return JsonValue.Create(text); }
    }
}

/// <summary>Get-State, Save-State, the login of the gateway, and Add-Journal into the holder in hand.</summary>
sealed class ItemVerbsState(IJournalStore store, Func<CancellationToken, Task<string>> login, JournalLedger ledger) : IItemStateStore, IItemJournal
{
    public async Task<ItemStateHolder> Read(int item, CancellationToken ct)
    {
        var st = await Translate(() => store.Read(item, ct));
        // the state is read the way a PowerShell hashtable is: a key without regard to its case
        st.State = (JsonObject)JsonNode.Parse(st.State.ToJsonString(), Ps.CaseBlind)!;
        return new ItemStateHolder { CommentId = st.CommentId, State = st.State, Rev = st.Rev, Backing = st };
    }

    public async Task Save(int item, ItemStateHolder holder, CancellationToken ct)
    {
        var st = (ItemState)(holder.Backing ?? new ItemState { CommentId = holder.CommentId, State = holder.State, Rev = holder.Rev });
        st.CommentId = holder.CommentId; st.Rev = holder.Rev; st.State = holder.State;
        await Translate(async () => { await store.Save(item, st, ct); return 0; });
        holder.CommentId = st.CommentId; holder.Rev = st.Rev; holder.Backing = st;
    }

    public Task<string> Login(CancellationToken ct) => login(ct);

    public async Task<bool> Add(int item, ItemStateHolder holder, string ev, JsonObject fields, string key, CancellationToken ct)
    {
        var st = (ItemState)(holder.Backing ?? new ItemState { CommentId = holder.CommentId, State = holder.State, Rev = holder.Rev });
        st.State = holder.State;
        return await ledger.AddInHand(item, st, ev, JournalLedger.FieldsOf(fields), key, ct);
    }

    /// <summary>The failures of the store as the refusals of the script: a state conflict is 11, a state over its budget 10, a fault of GitHub 20.</summary>
    static async Task<T> Translate<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (StateConflictException e) { throw new ItemRefusal(11, "state-conflict", e.Message, new JsonObject { ["retryAfterSeconds"] = 1 }); }
        catch (JournalRefusedException e) when (e.Message.StartsWith("state-too-large", StringComparison.Ordinal)) { throw new ItemRefusal(10, "state-too-large", e.Message["state-too-large: ".Length..]); }
        catch (JournalRefusedException e) { throw new ItemRefusal(20, "github-error", e.Message, new JsonObject { ["retryAfterSeconds"] = 30 }); }
        catch (HttpRequestException e) { throw new ItemRefusal(20, "github-error", e.Message, new JsonObject { ["retryAfterSeconds"] = 30 }); }
    }
}

/// <summary>The directory the rule scripts keep their state in (Get-FwSubDir 'state' $env:FACTORY_STATE_DIR).</summary>
static class ItemVerbsFiles
{
    public static string StateDir(Func<string, string?> env)
    {
        string dir;
        if (env("FACTORY_STATE_DIR") is { Length: > 0 } s) dir = s;
        else
        {
            string data;
            if (env("FACTORY_DATA_DIR") is { Length: > 0 } d) data = d;
            else
            {
                var home = env("HOME") ?? env("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                data = !string.IsNullOrEmpty(home) && Directory.Exists(home) ? Path.Combine(home, ".local/share/factory-worker") : Path.Combine(Path.GetTempPath(), "factory-worker");
            }
            dir = Path.Combine(data, "state");
        }
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>Use-ItemLock: the lock file of the item in the state directory, opened for exclusive use. The scripts of v1 take the same file, so a verb of v2 and a script exclude each other.</summary>
sealed class ItemVerbsLock(string dir) : IItemLock
{
    public async Task<T> Use<T>(int item, Func<Task<T>> body, int waitSeconds, CancellationToken ct)
    {
        var path = Path.Combine(dir, $"item-{item}.lock");
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        FileStream? fs;
        while (true)
        {
            try { fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException) when (DateTime.UtcNow <= deadline) { await Task.Delay(300, ct); }
            catch (IOException) { throw new ItemRefusal(20, "item-busy", $"another operation on item {item} is running", new JsonObject { ["retryAfterSeconds"] = 5 }); }
        }
        using (fs) return await body();
    }
}

/// <summary>The refusals file of the model ladder (Models.ps1): {"&lt;item&gt;": count}, in the state directory.</summary>
sealed class ItemVerbsRefusals(string dir) : IItemRefusals
{
    readonly SemaphoreSlim gate = new(1, 1);
    string File => Path.Combine(dir, "refusals.json");

    JsonObject Read()
    {
        try { if (System.IO.File.Exists(File) && JsonNode.Parse(System.IO.File.ReadAllText(File)) is JsonObject o) return o; } catch { }
        return new JsonObject();
    }

    public async Task Add(int item, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { var r = Read(); var k = item.ToString(CultureInfo.InvariantCulture); r[k] = (r.TryGetPropertyValue(k, out var v) && v is not null ? Ps.Int(v) : 0) + 1; System.IO.File.WriteAllText(File, r.ToJsonString()); }
        finally { gate.Release(); }
    }

    public async Task Reset(int item, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { var r = Read(); if (r.Remove(item.ToString(CultureInfo.InvariantCulture))) System.IO.File.WriteAllText(File, r.ToJsonString()); }
        finally { gate.Release(); }
    }

    public async Task<int> Count(int item, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { var r = Read(); return r.TryGetPropertyValue(item.ToString(CultureInfo.InvariantCulture), out var v) && v is not null ? Ps.Int(v) : 0; }
        finally { gate.Release(); }
    }
}

/// <summary>The calls that stay the rule script's: through the runner, as the router of v1 runs them.</summary>
sealed class ItemVerbsV1(Runner runner) : IItemV1
{
    public async Task<ItemRawAnswer?> Run(string verb, string stdin, TimeSpan timeout, CancellationToken ct)
    {
        var (code, output) = await runner.RunAsync(verb, stdin, timeout);
        return new ItemRawAnswer(code, output);
    }
}

/// <summary>The item verbs as a router: the answer of the port in the shape the verb pipeline takes.</summary>
sealed class ItemVerbsRouter(ItemVerbs verbs) : IVerbRouter
{
    public async Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout)
    {
        var a = await verbs.Run(verb, stdin, timeout);
        return new VerbAnswer(a.Exit, a.Text);
    }
}

/// <summary>
/// What a person does to the park of an item is the saga's as well, for an item it owns: the park-item and unpark-item verbs tell it (ParkWorkItem, ResumeWorkItem), so the saga stops its lease at the park and arms the step again
/// at the unpark. The answer of the verb is returned as it is; a fault is logged and dropped (the saga's lease runs out and its budget parks the item, the way a lost session ends).
/// </summary>
sealed class ItemAdminFeed(IVerbRouter inner, V2Ownership ownership, RepoId home, Func<WorkItemInput, Task<bool>> publish) : IVerbRouter
{
    public async Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout)
    {
        var answer = await inner.RunAsync(verb, stdin, timeout);
        if (answer.Code == 0 && verb is "park-item" or "unpark-item")
        {
            try
            {
                if (JsonNode.Parse(stdin) is JsonObject body && JsonNode.Parse(answer.Output) is JsonObject a && int.TryParse(body["item"]?.ToString(), out var n) && n > 0 && ownership.Owns(n))
                {
                    var id = new ItemId(home, n);
                    WorkItemInput? input = verb == "park-item" && a["already"] is null
                        ? new ParkWorkItem(id, 0, $"park|{a["park"]?["at"]}", a["park"]?["reason"]?.ToString() is { Length: > 0 } r ? r : "parked")
                        : verb == "unpark-item" ? new ResumeWorkItem(id, 0, $"resume|{a["park"]?["at"]}") : null;
                    if (input is not null && !await publish(input)) Console.Error.WriteLine($"v2 feed {verb} for {id} dropped: the Wolverine host did not take it");
                }
            }
            catch (Exception e) { Console.Error.WriteLine($"v2 feed {verb}: {e.GetType().Name}"); }
        }
        return answer;
    }
}

/// <summary>Makes the item verbs from the gateway's services; null where they cannot be made (no configuration, no GitHub credential): the verbs then stay v1's.</summary>
static class ItemVerbsWiring
{
    public static IVerbRouter? Make(IServiceProvider services, Func<string, string?>? env = null, HttpClient? http = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (services.GetService(typeof(InstanceConfig)) is not InstanceConfig instance || instance.Root is null || instance.Home is not { } home) return null;
        try
        {
            var tokens = GitHubTokenProvider.FromEnvironment(env);
            if (tokens is null) { Console.Error.WriteLine("item verbs (W9.2) not registered, the verbs stay on v1: no GH_TOKEN and no author App"); return null; }
            var budget = services.GetService(typeof(GitHubBudget)) as GitHubBudget;
            var api = env("GITHUB_API_URL");
            http ??= new HttpClient(new GitHubAuthHandler(tokens)) { BaseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/"), Timeout = TimeSpan.FromSeconds(120) };
            // every request of the verbs carries the user agent of the rule scripts, the state comment's and the journal's included: the golden writes tell a write of a verb ("rule") from the gateway's own loops by it
            if (!http.DefaultRequestHeaders.UserAgent.Any()) http.DefaultRequestHeaders.UserAgent.ParseAdd("factory-worker-gateway");
            var wire = new ItemVerbsWire(http, budget is null ? null : (resource, remaining, reset, limit) => budget.Observe(new JsonObject { [resource] = new JsonObject { ["remaining"] = remaining, ["reset"] = reset, ["limit"] = limit } }));
            var cfg = ItemVerbConfig.From(instance.Root, env, EnvLabel(instance.Root, env), MessagingOn(instance.Root, env), QcGateActive(instance.Root, env));
            var gh = new ItemGitHub(wire, cfg);
            var login = Login(env, http, tokens);
            // the store is made at its first use, not here: an App token is minted by the client factory, and nothing may go to the network while the routes are built (the request sets the bearer of its own through the handler)
            var factory = new GitHubClientFactory(http, new ProviderCredentialSource(tokens), new Fworker.Integrations.GitHub.GitHubBudget());
            IJournalStore journalStore = new LazyJournalStore(async ct => new GitHubJournalStore(await factory.For(home, ct), new JournalIdentity(cfg.Name, login, cfg.StateAuthors)));
            var minutes = cfg.TimingOrNull("journalWaitingMinutes") ?? 30;
            var ledger = new JournalLedger(journalStore, new JournalSettings(minutes, SecretPatterns(instance.Root)));
            var state = new ItemVerbsState(journalStore, login, ledger);
            var dir = ItemVerbsFiles.StateDir(env);
            var v1 = services.GetService(typeof(Runner)) is Runner runner ? new ItemVerbsV1(runner) : null;
            var board = new ItemBoard(gh, new SystemItemClock());
            var verbs = new ItemVerbs(gh, board, new ItemPulls(gh, state), state, state, new ItemVerbsLock(dir), new ItemVerbsRefusals(dir), v1);
            IVerbRouter router = new ItemVerbsRouter(verbs);
            // the saga hears what the script did (the feed of the hybrid composition) and what a person does to a park
            if (services.GetService(typeof(WorkItemParts)) is WorkItemParts { Ownership: { } ownership } parts && services.GetService(typeof(WolverineHostService)) is WolverineHostService host)
            {
                Func<WorkItemInput, Task<bool>> publish = m => host.PublishAsync(m);
                router = new ItemAdminFeed(router, ownership, home, publish);
                router = new V2VerbFeed(router, ownership, parts.Store, home, parts.Docs.Settings, publish, parts.Docs.Heads);
            }
            return router;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"item verbs (W9.2) not registered, the verbs stay on v1: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>Get-EnvLabel: "env:&lt;environment&gt;" while the environment scope is on, else "".</summary>
    public static string EnvLabel(JsonNode root, Func<string, string?> env)
    {
        var e = (env("FACTORY_ENVIRONMENT") ?? "").Trim().ToLowerInvariant(); if (e.Length == 0) e = "tdd";
        bool on;
        if (env("FACTORY_ENV_SCOPE") is "0" or "1") on = env("FACTORY_ENV_SCOPE") == "1";
        else on = root["envScope"] is JsonArray a && a.Any(x => string.Equals(x?.ToString().ToLowerInvariant(), e, StringComparison.Ordinal));
        return on ? $"env:{e}" : "";
    }

    /// <summary>Test-MessagingOn: messaging or the supervisor is on, the config overridden by FACTORY_MESSAGING and FACTORY_MANAGER.</summary>
    public static bool MessagingOn(JsonNode root, Func<string, string?> env)
    {
        static bool Enabled(JsonNode? section) { try { return section?["enabled"] is JsonValue v && v.TryGetValue<bool>(out var b) && b; } catch { return false; } }
        var msg = Enabled(root["messaging"]); var mgr = Enabled(root["manager"]);
        if (env("FACTORY_MESSAGING") is "0" or "1") msg = env("FACTORY_MESSAGING") == "1";
        if (env("FACTORY_MANAGER") is "0" or "1") mgr = env("FACTORY_MANAGER") == "1";
        return msg || mgr;
    }

    /// <summary>Test-QcGateActive: owner mode with a token secret, mergePolicy peer-approved, and a requirement of the gate on (the defaults are on under peer-approved).</summary>
    public static bool QcGateActive(JsonNode root, Func<string, string?> env)
    {
        if (env("FACTORY_OWNER_MODE") != "1" || (env("FACTORY_TOKEN_SECRET") ?? "").Length < 16) return false;
        if (root["mergePolicy"]?.ToString() != "peer-approved") return false;
        bool Req(string name) { try { return root["qc"]?[name] is JsonValue v ? v.TryGetValue<bool>(out var b) && b : true; } catch { return true; } }
        return Req("requireRun") || Req("requireApproval");
    }

    static IReadOnlyList<string> SecretPatterns(JsonNode root)
    {
        var patterns = new List<string>();
        try { foreach (var group in new[] { "patterns", "redactOnly" }) if (root["secrets"]?[group] is JsonArray a) patterns.AddRange(a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0)); } catch { }
        return patterns;
    }

    /// <summary>The login the gateway writes as: FACTORY_SERVER_LOGIN, the App's bot, or what GET /user answers (the author of the state comment and the factory's comments).</summary>
    static Func<CancellationToken, Task<string>> Login(Func<string, string?> env, HttpClient http, GitHubTokenProvider tokens)
    {
        string? login = env("FACTORY_SERVER_LOGIN");
        return async ct =>
        {
            if (!string.IsNullOrEmpty(login)) return login;
            if (tokens.AppLogin is { } bot) return login = bot;
            using var req = new HttpRequestMessage(HttpMethod.Get, "user");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.UserAgent.ParseAdd("factory-worker-gateway");
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return login = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["login"]?.ToString() ?? throw new JournalRefusedException("GitHub did not name the gateway's login");
        };
    }
}
