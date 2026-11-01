using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fworker.Integrations.GitHub;

// W9.7 (#359): the GitHub calls of the service verbs (revocations, revoke-worker, git-credential, operator-release(s), metrics) on v2, with the semantics of the rule scripts' Core.ps1 and nothing else:
// the same URL (GITHUB_API_URL or api.github.com), the same headers (the user agent too, so the fake GitHub's write record names the same source), the same retries (a network error or a 502 to 504, 3 tries,
// a pause of 2 s then 4 s), the same refusals (Invoke-Gh: 404 not found, a rate limit 20, a refused permission 30, any other status 20) and the same record of the rate limit for the gateway's budget.
// The credential is the rule scripts' too (Get-GhToken): the author App's installation token where FACTORY_AUTHOR_APP is 1, else GH_TOKEN, and nothing else; it is never logged and never part of an answer.

/// <summary>What the rule scripts' <c>Reject</c> threw: the exit code, the code and reason of the answer and the fields beside them.</summary>
sealed class VerbReject(int exit, string code, string reason, JsonObject? extra = null) : Exception(reason)
{
    public int Exit { get; } = exit;
    public string Code { get; } = code;
    public JsonObject Extra { get; } = extra ?? new JsonObject();
}

/// <summary>An answer of GitHub as <c>Invoke-GhRaw</c> returns it: the status, the body (parsed when it is JSON, else its text) and the headers.</summary>
sealed record GhResponse(int Status, JsonNode? Json, string Text, IReadOnlyDictionary<string, string> Headers, bool Allowed = false)
{
    public bool Success => Status is >= 200 and < 300;
}

/// <summary>The configuration and credential a service verb reads, as the rule scripts read them (<c>Get-Cfg</c>, <c>Get-GhToken</c>, <c>Get-AppInfo</c>).</summary>
sealed class ServiceGitHub
{
    public static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public const string UserAgent = "factory-worker-gateway";

    readonly Func<string, string?> env;
    readonly InstanceConfig instance;
    readonly GitHubTokenProvider? tokens;
    internal HttpClient Http { get; }
    internal Func<TimeSpan, Task> Pause { get; init; } = d => Task.Delay(d);
    // the operator's keyring (secret-tool): the last place the App's key is looked for; tests give none
    internal Func<string, string, string?> Keyring { get; init; } = GitCredentialVerb.SecretTool;

    /// <param name="env">The environment (tests give their own).</param>
    /// <param name="http">The client; its BaseAddress is the API root. Null: one made from GITHUB_API_URL.</param>
    public ServiceGitHub(InstanceConfig instance, Func<string, string?> env, HttpClient? http = null)
    {
        this.instance = instance; this.env = env;
        var api = env("GITHUB_API_URL");
        Http = http ?? new HttpClient(new GitHubUserAgentHandler()) { BaseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/"), Timeout = TimeSpan.FromSeconds(100) };
        // the identity of the rule scripts: the author App where FACTORY_AUTHOR_APP is 1 (GH_TOKEN is then not looked at), else GH_TOKEN alone (the App is then not looked at)
        tokens = FakeApps && AuthorApp ? null : GitHubTokenProvider.FromEnvironment(AuthorApp ? n => n == "GH_TOKEN" ? null : env(n) : n => n.StartsWith("FACTORY_APP_AUTHOR_", StringComparison.Ordinal) ? null : env(n));
    }

    public bool AuthorApp => env("FACTORY_AUTHOR_APP") == "1";
    public bool FakeApps => !string.IsNullOrEmpty(env("FACTORY_FAKE_APPS"));
    public string? Env(string name) => env(name);
    public string ApiBase => Http.BaseAddress!.ToString().TrimEnd('/');

    // ---- Get-Cfg ----
    JsonNode Root => instance.Root ?? throw new VerbReject(30, "config-missing", $"instance config not found: {instance.Path}");
    public string Cfg(string name) { var v = Root[name]; return v is null ? "" : v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToJsonString(); }
    public string Owner => Cfg("owner");
    public string Repo => Cfg("repo");
    public string Name => Cfg("name");
    public string DefaultBranch => Cfg("defaultBranch");
    public JsonNode? CfgNode(string name) => Root[name];
    public string RepoPath => $"repos/{Owner}/{Repo}";

    // ---- Get-GhToken ----
    async ValueTask<string> Token(CancellationToken ct)
    {
        if (FakeApps && AuthorApp) return "fake-author";
        if (tokens is null) throw new VerbReject(30, "secret-missing", AuthorApp ? "no private key for the 'author' GitHub App (FACTORY_APP_AUTHOR_KEY or the keyring)" : "GH_TOKEN is not set on the gateway");
        try { return await tokens.Token(ct); }
        catch (GitHubTokenException e) { throw new VerbReject(20, "app-token-failed", e.Message, new JsonObject { ["retryAfterSeconds"] = 30 }); }
    }

    public CallScope Open(CancellationToken ct) => new(this, ct);

    /// <summary>The GitHub calls of one verb run: they share the cancellation and the record of the rate limit this run saw (the rule script's <c>_github</c> field).</summary>
    public sealed class CallScope(ServiceGitHub gh, CancellationToken ct)
    {
        readonly Dictionary<string, (long Remaining, long? Limit, long? Reset, long At)> rate = new(StringComparer.Ordinal);
        public ServiceGitHub Gh => gh;
        public CancellationToken Ct => ct;

        /// <summary>The rate limit the answers reported, in the shape of the rule scripts' <c>_github</c> field; null when none did.</summary>
        public JsonObject? RateReport()
        {
            if (rate.Count == 0) return null;
            var o = new JsonObject();
            foreach (var kv in rate) o[kv.Key] = new JsonObject { ["remaining"] = kv.Value.Remaining, ["limit"] = kv.Value.Limit, ["reset"] = kv.Value.Reset, ["at"] = kv.Value.At };
            return o;
        }

        static string? Header(IReadOnlyDictionary<string, string> h, string n) => h.TryGetValue(n, out var v) ? v : null;
        static bool Digits(string? s) => s is { Length: > 0 and <= 18 } && s.All(char.IsAsciiDigit);
        static long? Num(string? s) => Digits(s) ? long.Parse(s!, CultureInfo.InvariantCulture) : null;

        // Add-GhRate: the latest numbers of each resource, unless an earlier answer of the same window said less
        void AddRate(IReadOnlyDictionary<string, string> h)
        {
            var rem = Num(Header(h, "x-ratelimit-remaining")); if (rem is null) return;
            var res = Header(h, "x-ratelimit-resource"); if (res is null || res.Length is 0 or > 30 || !res.All(c => c is >= 'a' and <= 'z' or '_')) res = "core";
            var reset = Num(Header(h, "x-ratelimit-reset"));
            if (rate.TryGetValue(res, out var prev) && prev.Reset == reset && prev.Remaining < rem) return;
            rate[res] = (rem.Value, Num(Header(h, "x-ratelimit-limit")), reset, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        /// <summary>Invoke-GhRaw: the request with its retries. Never refuses on a status; a network error that stays is 20 github-unreachable.</summary>
        public async Task<GhResponse> Raw(HttpMethod method, string path, JsonNode? body = null, string accept = "application/vnd.github+json")
        {
            const int tries = 3;
            var token = await gh.Token(ct);
            for (var attempt = 1; attempt <= tries; attempt++)
            {
                using var req = new HttpRequestMessage(method, path.TrimStart('/'));
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                req.Headers.TryAddWithoutValidation("Accept", accept);
                req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                if (Cause() is { } cause) req.Headers.TryAddWithoutValidation("X-Fake-Cause", cause);   // test-only (the golden writes' cause id); nothing outside FACTORY_TEST_CAUSE_HEADER=1
                if (body is not null) req.Content = new StringContent(body.ToJsonString(Relaxed), Encoding.UTF8, "application/json");
                HttpResponseMessage resp;
                try { resp = await gh.Http.SendAsync(req, ct); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    if (attempt >= tries) throw new VerbReject(20, "github-unreachable", e.Message, new JsonObject { ["retryAfterSeconds"] = 30 });
                    await gh.Pause(TimeSpan.FromSeconds(2 * attempt)); continue;
                }
                using (resp)
                {
                    var text = await resp.Content.ReadAsStringAsync(ct);
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var h in resp.Headers) headers[h.Key] = string.Join(", ", h.Value);
                    foreach (var h in resp.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);
                    AddRate(headers);
                    var status = (int)resp.StatusCode;
                    if (status is 502 or 503 or 504 && attempt < tries) { await gh.Pause(TimeSpan.FromSeconds(2 * attempt)); continue; }
                    JsonNode? json = null;
                    var trimmed = text.TrimStart();
                    if (trimmed.Length > 0 && trimmed[0] is '{' or '[') { try { json = JsonNode.Parse(text); } catch (JsonException) { } }
                    return new GhResponse(status, json, text, headers);
                }
            }
            throw new InvalidOperationException("unreachable");
        }

        static string? Cause()
        {
            if (!TestCause.On) return null;
            try { return JsonNode.Parse(TestCause.Inject("{}"))?["_cause"]?.ToString(); } catch (JsonException) { return null; }
        }

        /// <summary>Invoke-Gh: the body of a 2xx answer; a status in <paramref name="allow"/> is returned as it is (Allowed); anything else is refused as the rule scripts refuse it.</summary>
        public async Task<GhResponse> Call(HttpMethod method, string path, JsonNode? body = null, int[]? allow = null, string accept = "application/vnd.github+json")
        {
            var r = await Raw(method, path, body, accept);
            if (r.Success) return r;
            if (allow is not null && allow.Contains(r.Status)) return r with { Allowed = true };
            var detail = r.Json is not null ? r.Json.ToJsonString(Relaxed) : r.Text;
            if (detail.Length > 400) detail = detail[..400];
            switch (r.Status)
            {
                case 404:
                    throw new VerbReject(12, "github-not-found", $"GitHub 404 on {method.Method} {path}", new JsonObject { ["detail"] = detail });
                case 403 or 429:
                    var msg = r.Json is JsonObject jo && jo.ContainsKey("message") ? jo["message"]?.ToString() ?? "" : detail;
                    var wait = RateLimitWait(r.Status, r.Headers, msg);
                    if (wait >= 0)
                    {
                        var x = new JsonObject { ["retryAfterSeconds"] = wait, ["detail"] = detail };
                        if (Digits(Header(r.Headers, "x-ratelimit-reset"))) x["resetAt"] = DateTimeOffset.FromUnixTimeSeconds(long.Parse(Header(r.Headers, "x-ratelimit-reset")!, CultureInfo.InvariantCulture)).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
                        throw new VerbReject(20, "github-rate-limited", $"GitHub {r.Status} on {method.Method} {path}", x);
                    }
                    // a refusal of permission, not a rate limit
                    throw new VerbReject(30, "github-forbidden", $"GitHub refused {method.Method} {path} (403): {msg}", new JsonObject { ["status"] = 403, ["detail"] = detail });
                default:
                    throw new VerbReject(20, "github-error", $"GitHub {r.Status} on {method.Method} {path}", new JsonObject { ["status"] = r.Status, ["detail"] = detail, ["retryAfterSeconds"] = 30 });
            }
        }

        public Task<GhResponse> Get(string path) => Call(HttpMethod.Get, path);

        /// <summary>Get-GhRateLimitWait: the seconds to wait when a 403 or 429 is a rate limit, else -1 (a refusal of permission).</summary>
        static int RateLimitWait(int status, IReadOnlyDictionary<string, string> h, string message)
        {
            var ra = Header(h, "retry-after");
            if (Digits(ra) && ra!.Length <= 9) return Math.Max(1, int.Parse(ra, CultureInfo.InvariantCulture));
            var rem = Header(h, "x-ratelimit-remaining"); var reset = Header(h, "x-ratelimit-reset");
            if (rem == "0") return Digits(reset) ? (int)Math.Max(1, long.Parse(reset!, CultureInfo.InvariantCulture) - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) : 60;
            if (status == 429 || System.Text.RegularExpressions.Regex.IsMatch(message, "rate limit|abuse detection|secondary rate", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return 60;
            return -1;
        }

        /// <summary>Invoke-GhGraphQL: the data of the answer.</summary>
        public async Task<JsonNode?> GraphQl(string query, JsonObject variables)
        {
            var r = await Raw(HttpMethod.Post, "graphql", new JsonObject { ["query"] = query, ["variables"] = variables });
            if (r.Status != 200) throw new VerbReject(20, "github-graphql-error", $"GraphQL HTTP {r.Status}", new JsonObject { ["retryAfterSeconds"] = 30 });
            if (r.Json is JsonObject o && o["errors"] is JsonArray { Count: > 0 } errors)
            {
                var msg = string.Join("; ", errors.Select(e => e?["message"]?.ToString() ?? ""));
                if (errors.Any(e => e?["type"]?.ToString() == "RATE_LIMITED"))
                {
                    var reset = Header(r.Headers, "x-ratelimit-reset");
                    var wait = Digits(reset) ? (int)Math.Max(1, long.Parse(reset!, CultureInfo.InvariantCulture) - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) : 60;
                    var x = new JsonObject { ["retryAfterSeconds"] = wait };
                    if (Digits(reset)) x["resetAt"] = DateTimeOffset.FromUnixTimeSeconds(long.Parse(reset!, CultureInfo.InvariantCulture)).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
                    throw new VerbReject(20, "github-rate-limited", msg, x);
                }
                throw new VerbReject(20, "github-graphql-error", msg, new JsonObject { ["retryAfterSeconds"] = 30 });
            }
            return (r.Json as JsonObject)?["data"];
        }
    }

    // ---- Get-AppInfo (Apps.ps1) ----
    /// <summary>The ids of the author App: from the environment (FACTORY_APP_AUTHOR_ID, _INSTALLATION, _SLUG), else the operator's file ~/.config/factory-worker/apps/author.json; where FACTORY_FAKE_APPS is set the fake's.</summary>
    public (JsonNode? Id, string? Installation, string? Slug) AppInfo()
    {
        if (FakeApps) return (JsonValue.Create(0), "0", "factory-worker-author");
        var id = env("FACTORY_APP_AUTHOR_ID");
        if (!string.IsNullOrEmpty(id)) return (JsonValue.Create(id), env("FACTORY_APP_AUTHOR_INSTALLATION"), env("FACTORY_APP_AUTHOR_SLUG"));
        var home = new[] { env("HOME"), env("USERPROFILE"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }.FirstOrDefault(h => !string.IsNullOrEmpty(h) && Directory.Exists(h));
        var file = home is null ? null : Path.Combine(home, ".config", "factory-worker", "apps", "author.json");
        if (file is null || !File.Exists(file)) throw new VerbReject(30, "app-not-configured", "the 'author' GitHub App is not set up on this gateway");
        var o = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new VerbReject(30, "app-not-configured", "the 'author' GitHub App is not set up on this gateway");
        string? Str(string k) => o[k] is { } v ? (v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : null;
        return (o["id"]?.DeepClone(), Str("installation_id"), Str("slug"));
    }

    /// <summary>The login of the gateway's identity (Get-ServerLogin): <c>slug[bot]</c> of the author App, else the user the GH_TOKEN belongs to.</summary>
    public async Task<string> ServerLogin(CallScope scope)
    {
        if (AuthorApp) return AppInfo().Slug + "[bot]";
        var r = await scope.Call(HttpMethod.Get, "user");
        return (r.Json as JsonObject)?["login"]?.ToString() ?? throw new InvalidOperationException("the property 'login' cannot be found on this object");
    }
}
