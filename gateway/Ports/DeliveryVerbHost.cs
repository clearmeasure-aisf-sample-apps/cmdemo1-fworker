using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.Delivery.Verbs;

// W9.4 (#356): the HTTP side of the delivery verbs (settle, publish-demo, ci-release, copilot-settle, copilot-owner, private-build) for the environments where the engine acts. The rules are Fworker.Workflows.Delivery.Verbs,
// ported one for one from the rule scripts; this is what they reach GitHub, the item's state comment and Octopus through. A call is what the script run was: the same GitHub calls with the same identity (the author App or
// GH_TOKEN, user agent factory-worker-gateway), the same exit codes and answers, the lease checked before a write of the verbs v1 fences (settle, copilot-settle, ci-release), the item's lock held, the test cause id sent.

/// <summary>Adds the test cause id of the request being served (tests/scenarios/golden/README.md) to every call the verbs make for it; inert unless FACTORY_TEST_CAUSE_HEADER=1.</summary>
sealed class CauseHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (TestCause.On && TestCause.Current is { Length: > 0 } id && !request.Headers.Contains("X-Fake-Cause")) request.Headers.TryAddWithoutValidation("X-Fake-Cause", id);
        return base.SendAsync(request, ct);
    }
}

/// <summary>
/// The gateway's own calls of the delivery verbs (the CI release, the Copilot owner and settle, the demo upload, ...) go through the runner; where a v2 route answers the verb they go to the route instead. Created with the
/// runtimes, connected when the routes are built (<see cref="DeliveryRoutes.Register"/>), so a gateway where the engine does not act always runs the rule script.
/// </summary>
sealed class DeliveryVerbBridge(Func<string, string, TimeSpan, Task<(int Code, string Output)>> runner)
{
    volatile V2VerbRoutes? routes;

    public void Connect(V2VerbRoutes routes) => this.routes = routes;

    public async Task<(int Code, string Output)> RunAsync(string verb, string stdin, TimeSpan timeout)
    {
        if (routes is { } r && DeliveryRoutes.Verbs.Contains(verb) && r.TryGet(verb, out var handler))
        {
            var answer = await handler(stdin, timeout);
            return (answer.Code, answer.Output);
        }
        return await runner(verb, stdin, timeout);
    }
}

/// <summary>Where the verb feed of the item saga (V2VerbFeed) is told of a verb a v2 route answered, so a session's call is still a sign of life for the step's lease. Connected when the feed is composed.</summary>
sealed class V2ActivityTap
{
    volatile Func<string, string, string, Task>? observe;

    public void Connect(Func<string, string, string, Task> observe) => this.observe = observe;

    public async Task Observe(string verb, string stdin, string output)
    {
        try { if (observe is { } o) await o(verb, stdin, output); }
        catch (Exception e) { Console.Error.WriteLine($"v2 activity {verb}: {e.GetType().Name}"); }
    }
}

sealed class DeliveryVerbHost
{
    /// <summary>The verbs v1 runs under the leader lease's fence (Runner.FencedVerbs) of the six.</summary>
    static readonly HashSet<string> Fenced = new(StringComparer.Ordinal) { "settle", "copilot-settle", "ci-release" };
    /// <summary>NoLockVerbs of factory.ps1 among the six: no item lock.</summary>
    static readonly HashSet<string> NoLock = new(StringComparer.Ordinal) { "publish-demo", "ci-release" };

    readonly JsonObject config;
    readonly GitHubTokenProvider? tokens;
    readonly Func<string, string?> env;
    readonly HttpClient github, uploads, octopus;
    readonly Fworker.Integrations.GitHub.GitHubClientFactory? clients;
    readonly Fworker.Contracts.Ids.RepoId? home;
    readonly JournalIdentity? identity;
    readonly Func<bool> mayAct;
    readonly Action<JsonObject> observeRate;
    readonly BoardMetaCache board = new();
    readonly IItemLock locks;
    readonly VerbCall openPr;
    readonly V2ActivityTap? tap;
    readonly Func<TimeSpan, CancellationToken, Task> delay;

    public DeliveryVerbHost(JsonObject root, Func<string, string?> env, Func<bool> mayAct, Action<JsonObject> observeRate, VerbCall openPr, V2ActivityTap? tap = null,
        HttpMessageHandler? inner = null, GitHubTokenProvider? tokens = null, Func<TimeSpan, CancellationToken, Task>? delay = null, IGitHubCredentialSource? credentials = null, IItemLock? itemLock = null)
    {
        locks = itemLock ?? ItemLocks.Default(env);
        config = RuleKit.WithTimingOverrides(root, env); this.env = env; this.mayAct = mayAct; this.observeRate = observeRate; this.openPr = openPr; this.tap = tap;
        this.tokens = tokens ?? GitHubTokenProvider.FromEnvironment(env);
        this.delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        var api = env("GITHUB_API_URL");
        var baseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/");
        HttpClient Make(TimeSpan timeout)
        {
            HttpMessageHandler chain = inner ?? new HttpClientHandler();
            if (inner is null && this.tokens is not null) chain = new GitHubAuthHandler(this.tokens, chain);
            var client = new HttpClient(new CauseHandler { InnerHandler = chain }) { BaseAddress = baseAddress, Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("factory-worker-gateway");
            return client;
        }
        github = Make(TimeSpan.FromSeconds(100)); uploads = Make(TimeSpan.FromSeconds(480));
        octopus = new HttpClient(inner ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        octopus.DefaultRequestHeaders.UserAgent.ParseAdd("factory-worker-gateway");
        // Nothing here reaches GitHub: the construction must not fail on a transient fault, because a host that failed to build would be cached by the route's Lazy. The client of the state comment's repository (and with it
        // the token mint) is resolved inside Run, per call, where a fault is answered as a retryable exit 20.
        if (this.tokens is not null && Home(root) is { } h)
        {
            home = h;
            clients = new GitHubClientFactory(github, credentials ?? new ProviderCredentialSource(this.tokens), new Fworker.Integrations.GitHub.GitHubBudget());
            identity = Identity(root, env, github, this.tokens);
        }
    }

    /// <summary>The item's lock (the gateway's one IItemLock, W9.10l); a lock that stays busy is the refusal 20 item-busy, as the delivery verbs always answered it.</summary>
    async Task<JsonObject> WithLock(int item, Func<Task<JsonObject>> body, CancellationToken ct)
    {
        try { return await locks.Use(item, body, 45, ct); }
        catch (ItemBusyException) { throw new Fworker.Workflows.Delivery.Verbs.VerbReject(20, "item-busy", $"another operation on item {item} is running", new JsonObject { ["retryAfterSeconds"] = 5 }); }
    }

    /// <summary>The state comment's store, over a client made now (the credential source returns the provider's current token).</summary>
    async Task<GitHubJournalStore> Store(CancellationToken ct) =>
        new(await clients!.For(home!.Value, ct), identity!);

    static Fworker.Contracts.Ids.RepoId? Home(JsonObject root) =>
        root["owner"]?.ToString() is { Length: > 0 } o && root["repo"]?.ToString() is { Length: > 0 } r ? new Fworker.Contracts.Ids.RepoId(o, r) : null;

    /// <summary>The login the state comment is written under: the App's bot, or the owner of GH_TOKEN (GET /user); FACTORY_SERVER_LOGIN names it for a test.</summary>
    static JournalIdentity Identity(JsonObject root, Func<string, string?> env, HttpClient http, GitHubTokenProvider tokens)
    {
        var name = root["name"]?.ToString() ?? "";
        var authors = root["stateAuthors"] is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : [];
        var login = env("FACTORY_SERVER_LOGIN");
        return new JournalIdentity(name, async ct =>
        {
            if (!string.IsNullOrEmpty(login)) return login;
            // an installation token cannot read /user: the App's login is its slug plus [bot]
            if (tokens.AppLogin is { } bot) return login = bot;
            using var resp = await http.GetAsync("user", ct);
            resp.EnsureSuccessStatusCode();
            return login = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["login"]?.ToString() ?? throw new JournalRefusedException("GitHub did not name the gateway's login");
        }, authors);
    }

    public static DeliveryVerbHost Make(IServiceProvider sp)
    {
        var instance = sp.GetRequiredService<InstanceConfig>();
        var budget = sp.GetRequiredService<GitHubBudget>();
        var leadership = sp.GetService<ILeadership>();
        // copilot-settle opens the item's pull request through the verb open-pr, answered by whoever answers it (the item verbs' route, or the rule script)
        VerbCall call = async (verb, args, ct) => { var a = await sp.GetRequiredService<IVerbRouter>().RunAsync(verb, args.ToJsonString(), TimeSpan.FromSeconds(120)); return (a.Code, a.Output); };
        return new DeliveryVerbHost(instance.Root as JsonObject ?? new JsonObject(), Environment.GetEnvironmentVariable, () => leadership?.MayAct ?? true, budget.Observe, call, sp.GetService<V2ActivityTap>(), itemLock: ItemLocks.From(sp, Environment.GetEnvironmentVariable));
    }

    /// <summary>
    /// W9.10f: a kit for the gateway's own reads of GitHub (master's CI for a merge commit, a pull request, its reviews): the same transport as a verb call, the lease checked before the one write it may make (the re-run of
    /// the failed jobs), and no state comment (the kit's state store is never used by those reads).
    /// </summary>
    public async Task<T> WithKit<T>(Func<RuleKit, Task<T>> body, CancellationToken ct)
    {
        if (tokens is null) throw new Fworker.Workflows.Delivery.Verbs.VerbReject(30, "secret-missing", "GH_TOKEN is not set on the gateway");
        var transport = new HttpGhTransport(github, uploads, octopus, config, env, mayAct, observeRate, delay);
        return await body(new RuleKit(transport, new UnusedStateStore(), config, board, env));
    }

    // ---- one call --------------------------------------------------------------------------------------------------------------------------------------

    public async Task<VerbAnswer> Run(string verb, string stdin, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var (exit, output) = await VerbAnswers.Run(verb, async () =>
        {
            JsonObject a;
            try { a = string.IsNullOrWhiteSpace(stdin) ? new JsonObject() : JsonNode.Parse(stdin) as JsonObject ?? new JsonObject(); }
            catch (JsonException) { a = new JsonObject(); }
            var session = a["_session"]?.DeepClone();
            foreach (var k in new[] { "_session", "_cause", "_lease" }) a.Remove(k);
            if (tokens is null) throw new Fworker.Workflows.Delivery.Verbs.VerbReject(30, "secret-missing", "GH_TOKEN is not set on the gateway");
            var fenced = Fenced.Contains(verb);
            var transport = new HttpGhTransport(github, uploads, octopus, config, env, fenced ? mayAct : null, observeRate, delay);
            var kit = new RuleKit(transport, new FencedStateStore(await Store(cts.Token), fenced ? mayAct : null), config, board, env).ForSession(session);
            Task<JsonObject> Body() => Dispatch(verb, kit, a, cts.Token);
            if (!NoLock.Contains(verb) && J.Has(a, "item")) return await WithLock(J.I(a["item"]), Body, cts.Token);
            return await Body();
        });
        var line = new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["v2route"] = verb, ["exit"] = exit };
        if (exit != 0) { try { var o = JsonNode.Parse(output); line["code"] = o?["code"]?.ToString(); line["reason"] = o?["reason"]?.ToString() is { Length: > 200 } r ? r[..200] : o?["reason"]?.ToString(); } catch (JsonException) { } }   // (a refusal says why)
        Console.WriteLine(line.ToJsonString());
        if (exit == 0 && verb is "publish-demo" or "private-build" && tap is not null) await tap.Observe(verb, stdin, output);
        return new VerbAnswer(exit, output);
    }

    Task<JsonObject> Dispatch(string verb, RuleKit kit, JsonObject a, CancellationToken ct) => verb switch
    {
        PrivateBuildVerb.Verb => PrivateBuildVerb.Run(kit, a, ct),
        CopilotOwnerVerb.Verb => CopilotOwnerVerb.Run(kit, a, ct),
        CiReleaseVerb.Verb => CiReleaseVerb.Run(kit, a, env, ct),
        PublishDemoVerb.Verb => PublishDemoVerb.Run(kit, a, Path.GetTempPath(), ct),
        CopilotSettleVerb.Verb => CopilotSettleVerb.Run(kit, a, openPr, ct),
        SettleVerb.Verb => SettleVerb.Run(kit, a, ct),
        _ => throw new Fworker.Workflows.Delivery.Verbs.VerbReject(2, "unknown-verb", $"unknown verb '{verb}'"),
    };
}

/// <summary>The kit of a read that has no item state to read or write (<see cref="DeliveryVerbHost.WithKit"/>): asking it for one is a bug.</summary>
sealed class UnusedStateStore : IItemStateStore
{
    public Task<StateHolder> Read(int item, CancellationToken ct) => throw new InvalidOperationException("this kit has no state comment");
    public Task Save(int item, StateHolder holder, CancellationToken ct) => throw new InvalidOperationException("this kit has no state comment");
}

/// <summary>The state comment of an item over <see cref="GitHubJournalStore"/> (Get-State, Save-State), with the lease checked before a write of a fenced verb.</summary>
sealed class FencedStateStore(GitHubJournalStore inner, Func<bool>? mayAct) : IItemStateStore
{
    public async Task<StateHolder> Read(int item, CancellationToken ct)
    {
        try
        {
            var s = await inner.Read(item, ct);
            return new StateHolder { CommentId = s.CommentId, State = s.State, Rev = s.Rev, Tag = s };
        }
        catch (Exception e) when (e is JournalRefusedException || e.GetType().Name == "GitHubForbiddenException") { throw Refused(e); }
    }

    public async Task Save(int item, StateHolder holder, CancellationToken ct)
    {
        if (mayAct is not null && !mayAct()) throw HttpGhTransport.LeaseLost($"PATCH issues/comments (the state of #{item})");
        var native = holder.Tag as ItemState ?? new ItemState();
        native.CommentId = holder.CommentId; native.State = holder.State; native.Rev = holder.Rev;
        try { await inner.Save(item, native, ct); }
        catch (StateConflictException e) { throw new Fworker.Workflows.Delivery.Verbs.VerbReject(11, "state-conflict", e.Message, new JsonObject { ["item"] = item, ["retryAfterSeconds"] = 1 }); }
        catch (Exception e) when (e is JournalRefusedException || e.GetType().Name == "GitHubForbiddenException") { throw Refused(e); }
        holder.CommentId = native.CommentId; holder.Rev = native.Rev; holder.Tag = native;
    }

    /// <summary>Invoke-Gh's codes for what the state store refused: a 404 is github-not-found (12), a 403 that is no rate limit github-forbidden (30), the state's budget state-too-large (10), anything else github-error (20).</summary>
    static Fworker.Workflows.Delivery.Verbs.VerbReject Refused(Exception e)
    {
        var detail = e.Message.Length > 400 ? e.Message[..400] : e.Message;
        if (e.Message.StartsWith("state-too-large", StringComparison.Ordinal)) return new Fworker.Workflows.Delivery.Verbs.VerbReject(10, "state-too-large", e.Message["state-too-large: ".Length..]);
        if (e.Message.StartsWith("GitHub 404", StringComparison.Ordinal)) return new Fworker.Workflows.Delivery.Verbs.VerbReject(12, "github-not-found", e.Message, new JsonObject { ["detail"] = detail });
        if (e.Message.StartsWith("GitHub 403", StringComparison.Ordinal)) return new Fworker.Workflows.Delivery.Verbs.VerbReject(30, "github-forbidden", e.Message, new JsonObject { ["status"] = 403, ["detail"] = detail });
        return new Fworker.Workflows.Delivery.Verbs.VerbReject(20, "github-error", e.Message, new JsonObject { ["retryAfterSeconds"] = 30 });
    }
}

/// <summary>GitHub, Octopus and the App for one verb call (Invoke-GhRaw, Invoke-Octo, Get-AppActionsPermission).</summary>
sealed class HttpGhTransport(HttpClient github, HttpClient uploads, HttpClient octopus, JsonObject config, Func<string, string?> env, Func<bool>? mayAct, Action<JsonObject> observeRate,
    Func<TimeSpan, CancellationToken, Task> delay) : IGhTransport
{
    public static Fworker.Workflows.Delivery.Verbs.VerbReject LeaseLost(string step) =>
        new(20, "lease-lost", $"this gateway no longer holds the leader lease; the step was not performed: {step}", new JsonObject { ["step"] = step, ["retryAfterSeconds"] = 30 });

    /// <summary>Test-FencedWrite: any request that is not a read; a GraphQL call only when it is a mutation.</summary>
    static bool IsWrite(HttpMethod method, string path, JsonNode? body)
    {
        if (method == HttpMethod.Get || method == HttpMethod.Head) return false;
        if (path.TrimEnd('/').EndsWith("graphql", StringComparison.Ordinal)) return body?["query"]?.ToString().TrimStart().StartsWith("mutation", StringComparison.Ordinal) == true;
        return true;
    }

    static JsonNode? Body(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); } catch (JsonException) { return JsonValue.Create(text); }
    }

    static Dictionary<string, string> Headers(HttpResponseMessage r)
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in r.Headers) h[kv.Key.ToLowerInvariant()] = kv.Value.FirstOrDefault() ?? "";
        return h;
    }

    void Rate(IReadOnlyDictionary<string, string> h)
    {
        if (!h.TryGetValue("x-ratelimit-remaining", out var rem) || !int.TryParse(rem, out _)) return;
        var resource = h.TryGetValue("x-ratelimit-resource", out var r) && System.Text.RegularExpressions.Regex.IsMatch(r, "^[a-z_]{1,30}$") ? r : "core";
        observeRate(new JsonObject { [resource] = new JsonObject { ["remaining"] = rem, ["reset"] = h.GetValueOrDefault("x-ratelimit-reset"), ["limit"] = h.GetValueOrDefault("x-ratelimit-limit") } });
    }

    public async Task<GhResult> Send(HttpMethod method, string path, JsonNode? body, CancellationToken ct, string? accept = null, int tries = 3)
    {
        if (mayAct is not null && IsWrite(method, path, body) && !mayAct()) throw LeaseLost($"{method.Method} {path}");
        for (var attempt = 1; attempt <= tries; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(method, path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(path) : new Uri(github.BaseAddress!, path.TrimStart('/')));
                req.Headers.Accept.ParseAdd(accept ?? "application/vnd.github+json");
                req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
                if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var resp = await github.SendAsync(req, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                var h = Headers(resp);
                Rate(h);
                if ((int)resp.StatusCode is 502 or 503 or 504 && attempt < tries) { await delay(TimeSpan.FromSeconds(2 * attempt), ct); continue; }
                return new GhResult((int)resp.StatusCode, Body(text), h);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= tries) throw new Fworker.Workflows.Delivery.Verbs.VerbReject(20, "github-unreachable", e.Message, new JsonObject { ["retryAfterSeconds"] = 30 });
                await delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
        throw new Fworker.Workflows.Delivery.Verbs.VerbReject(20, "github-unreachable", "no attempt was made", new JsonObject { ["retryAfterSeconds"] = 30 });
    }

    public async Task<GhResult> UploadFile(string uploadUrl, string contentType, string file, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(uploadUrl));
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        await using var stream = File.OpenRead(file);
        req.Content = new StreamContent(stream);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var resp = await uploads.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var h = Headers(resp);
        Rate(h);
        return new GhResult((int)resp.StatusCode, Body(text), h);
    }

    public async Task<JsonNode?> OctopusGet(string path, CancellationToken ct)
    {
        var key = env("OCTOPUS_API_KEY");
        if (string.IsNullOrEmpty(key)) throw new Fworker.Workflows.Delivery.Verbs.VerbReject(30, "secret-missing", "OCTOPUS_API_KEY is not set on the gateway");
        var url = config["octopus"]?["url"]?.ToString() ?? "";
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{url.TrimEnd('/')}/{path.TrimStart('/')}");
        req.Headers.TryAddWithoutValidation("X-Octopus-ApiKey", key);
        int code;
        try
        {
            using var resp = await octopus.SendAsync(req, ct);
            code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode) return Body(await resp.Content.ReadAsStringAsync(ct));
            if (code == 404) return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { code = 0; }
        throw new Fworker.Workflows.Delivery.Verbs.VerbReject(20, "octopus-error", $"Octopus answered {code} to GET {path}", new JsonObject { ["retryAfterSeconds"] = 30 });
    }

    public async Task<string> AppActionsPermission(CancellationToken ct)
    {
        var id = env("FACTORY_APP_AUTHOR_ID"); var installation = env("FACTORY_APP_AUTHOR_INSTALLATION"); var pem = env("FACTORY_APP_AUTHOR_KEY");
        if (string.IsNullOrWhiteSpace(pem) || pem.Trim() == "unset" || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(installation)) return "";
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var pay = B64(Encoding.UTF8.GetBytes(new JsonObject { ["iat"] = now - 60, ["exp"] = now + 540, ["iss"] = id }.ToJsonString()));
        string jwt;
        using (var rsa = RSA.Create())
        {
            rsa.ImportFromPem(pem.Replace("\\n", "\n"));
            jwt = $"{head}.{pay}.{B64(rsa.SignData(Encoding.UTF8.GetBytes($"{head}.{pay}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(github.BaseAddress!, $"app/installations/{installation}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await github.SendAsync(req, ct);
        if ((int)resp.StatusCode != 200) return "";
        var p = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["permissions"]?["actions"]?.ToString();
        return string.IsNullOrEmpty(p) ? "none" : p;
    }
}
