using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Integrations.Octopus;
using Fworker.Workflows.ReadModels;

// W9.5 (#357): the gateway's side of the read models (Fworker.Workflows.ReadModels): what they read and write through, as the rule scripts do, and the handlers of the verbs. Only where the engine acts (the route file
// ReadModelRoutes registers them in V2VerbRoutes.Build). Nothing here is a new rule: the wire to GitHub with the gateway's identity (the same token the rule scripts use: GH_TOKEN, else the author App's installation token), with the
// headers of the rule scripts (so the fake GitHub of the Tier 1 suites tells their writes from the gateway's own), the rate-limit numbers handed to the gateway's budget; Octopus's read API with the server's key; the item's state
// comment through the journal store (Get-State and Save-State, the revision check included); the item lock and the refusal counts in the state directory of the rule scripts, so that a verb v1 still runs and one v2 answers see one
// another's files; and the handlers, which give each verb the time the rule script is given.

/// <summary>Adds what the rule scripts send with every request: their User-Agent and, in the test mode, the cause id of the call being served (X-Fake-Cause); and reports the rate limit of every answer to the gateway's budget.</summary>
sealed class RuleIdentityHandler(HttpMessageHandler inner, Action<string, string?, string?, string?>? observe) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!request.Headers.UserAgent.Any()) request.Headers.UserAgent.ParseAdd("factory-worker-gateway");
        if (TestCause.ForWrite is { } cause && !request.Headers.Contains("X-Fake-Cause")) request.Headers.TryAddWithoutValidation("X-Fake-Cause", cause);
        var resp = await base.SendAsync(request, ct);
        static string? H(HttpResponseMessage r, string n) => r.Headers.TryGetValues(n, out var v) ? v.FirstOrDefault() : null;
        // only the gateway's own identity spends the gateway's budget (a redirect to a store that is not GitHub's API carries no rate headers)
        if (H(resp, "x-ratelimit-resource") is { } resource) observe?.Invoke(resource, H(resp, "x-ratelimit-remaining"), H(resp, "x-ratelimit-reset"), H(resp, "x-ratelimit-limit"));
        return resp;
    }
}

/// <summary><see cref="IGitHubApi"/> over HTTP: <c>Invoke-GhRaw</c> (Core.ps1): the headers, a body as compact JSON, the tries on a network fault or a 502 to 504 with the pauses of two seconds a try, and a body that goes to a file when asked.</summary>
sealed class HttpGitHubApi(HttpClient http, Func<TimeSpan, CancellationToken, Task>? pause = null, HttpClient? plain = null) : IGitHubApi
{
    readonly Func<TimeSpan, CancellationToken, Task> wait = pause ?? ((t, ct) => Task.Delay(t, ct));
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(100);
    const int MaxRedirects = 5;

    /// <summary>
    /// The client of the redirects. A job log or an artifact is a redirect to a signed address of another store; as <c>Invoke-RestMethod</c> does, the Authorization header is not sent there (the credential stays with GitHub), whatever the address.
    /// <paramref name="http"/> must not follow redirects by itself; the client of a hop is <paramref name="plain"/>, else <paramref name="http"/> (a test's, which has no credential to send).
    /// </summary>
    HttpClient Hop => plain ?? http;

    public async Task<GhAnswer> Send(GhCall call, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(new HttpMethod(call.Method), call.Path);
            req.Headers.Accept.ParseAdd(call.Accept);
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (call.Body is not null) req.Content = new StringContent(call.Body.ToJsonString(), Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(call.Timeout ?? DefaultTimeout);
            try
            {
                var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                for (var hops = 0; req.Method == HttpMethod.Get && resp.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                                   && resp.Headers.Location is { } to && hops < MaxRedirects; hops++)
                {
                    var target = to.IsAbsoluteUri ? to : new Uri(req.RequestUri!.IsAbsoluteUri ? req.RequestUri : new Uri(http.BaseAddress!, req.RequestUri!), to);
                    resp.Dispose();
                    using var next = new HttpRequestMessage(HttpMethod.Get, target);
                    resp = await Hop.SendAsync(next, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                }
                using var _ = resp;
                var status = (int)resp.StatusCode;
                if (status is 502 or 503 or 504 && attempt < call.Tries) { await wait(TimeSpan.FromSeconds(2 * attempt), ct); continue; }
                JsonNode? body = null;
                if (call.OutFile is { } file)
                {
                    // the body goes to the file (a job log, an artifact archive); only a success keeps it
                    if (status is >= 200 and < 300) { await using var fs = File.Create(file); await resp.Content.CopyToAsync(fs, cts.Token); }
                }
                else
                {
                    var text = await resp.Content.ReadAsStringAsync(cts.Token);
                    body = Parse(text);
                }
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in resp.Headers) headers[h.Key] = h.Value.FirstOrDefault() ?? "";
                foreach (var h in resp.Content.Headers) headers[h.Key] = h.Value.FirstOrDefault() ?? "";
                return new GhAnswer(status, body, headers);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or GitHubTokenException or IOException)
            {
                if (e is GitHubTokenException) throw new VerbRefusal(30, "secret-missing", e.Message);
                if (attempt >= call.Tries) throw new VerbRefusal(20, "github-unreachable", e is OperationCanceledException ? "the request to GitHub timed out" : e.Message, new JsonObject { ["retryAfterSeconds"] = 30 });
                await wait(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }

    // what Invoke-RestMethod makes of an answer: JSON as an object, any other text as the text itself, nothing as nothing
    static JsonNode? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); } catch (JsonException) { return JsonValue.Create(text); }
    }
}

/// <summary>The wire when the gateway has no GitHub identity (neither GH_TOKEN nor the author App): every call is the refusal the rule scripts give.</summary>
sealed class NoGitHubIdentity : IGitHubApi
{
    public Task<GhAnswer> Send(GhCall call, CancellationToken ct) => throw new VerbRefusal(30, "secret-missing", "GH_TOKEN is not set on the gateway");
}

/// <summary><see cref="IOctopusApi"/> over <see cref="HttpOctopusReader"/>: the key is read at every call (as <c>Invoke-Octo</c> reads it), a missing one is exit 30, a 404 is null, anything else is exit 20 octopus-error.</summary>
sealed class HttpOctopusApi(HttpClient http, string baseUrl, Func<string, string?> env) : IOctopusApi
{
    public async Task<JsonNode?> Get(string path, CancellationToken ct)
    {
        var key = env("OCTOPUS_API_KEY");
        if (string.IsNullOrEmpty(key)) throw new VerbRefusal(30, "secret-missing", "OCTOPUS_API_KEY is not set on the gateway");
        try { return await new HttpOctopusReader(http, baseUrl, key).Get(path, ct); }
        catch (OctopusReadException e) { throw new VerbRefusal(20, "octopus-error", $"Octopus answered {e.Status} to GET {path}", new JsonObject { ["retryAfterSeconds"] = 30 }); }
    }
}

/// <summary><see cref="IItemStateStore"/> over the journal store: <c>Get-State</c> and <c>Save-State</c> with the revision check; a state that moved is the script's exit 11 state-conflict.</summary>
sealed class JournalStateStore(Func<IJournalStore> store) : IItemStateStore
{
    public async Task<JsonObject> Read(int item, CancellationToken ct) => (await store().Read(item, ct)).State;

    public async Task<bool> Update(int item, Func<JsonObject, Task<bool>> mutate, CancellationToken ct, int tries = 1)
    {
        var s = store();
        for (var attempt = 1; ; attempt++)
        {
            var holder = await s.Read(item, ct);
            if (!await mutate(holder.State)) return false;
            try { await s.Save(item, holder, ct); return true; }
            catch (StateConflictException e) when (attempt < tries) { _ = e; }   // read again and apply again, as Update-State does
            catch (StateConflictException e)
            {
                var m = Regex.Match(e.Message, @"revision (\d+), now (\d+)");
                var x = new JsonObject { ["item"] = item };
                if (m.Success) { x["readRev"] = int.Parse(m.Groups[1].Value); x["currentRev"] = int.Parse(m.Groups[2].Value); }
                x["retryAfterSeconds"] = 1;
                throw new VerbRefusal(11, "state-conflict", e.Message, x);
            }
        }
    }
}

/// <summary><see cref="IRefusalCounts"/> from <c>refusals.json</c> of the state directory ({ "12": 3 }); none when the file is missing or cannot be read.</summary>
sealed class RefusalFile(string stateDir) : IRefusalCounts
{
    public int For(int item)
    {
        try
        {
            var path = Path.Combine(stateDir, "refusals.json");
            if (!File.Exists(path)) return 0;
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject o && o.TryGetPropertyValue(item.ToString(), out var v) && v is not null ? Ps.ToInt(v) : 0;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or FormatException) { return 0; }
    }
}

/// <summary>
/// The read models of this process, wired: one wire to GitHub, one to Octopus, the journal store for the state, the lock and the refusals of the state directory. Built from the instance configuration and the environment
/// (the pieces can be replaced for a test).
/// </summary>
sealed class ReadModelParts
{
    public required ChecksReadModel Checks { get; init; }
    public required InspectReadModel Inspect { get; init; }
    public required ModelTierReadModel Tier { get; init; }
    public required DeliveryReadModel Delivery { get; init; }
    public required SecretsReadModel Secrets { get; init; }
    public required ReadModelSettings Settings { get; init; }

    public static ReadModelParts Build(InstanceConfig instance, Action<string, string?, string?, string?>? observe, Func<string, string?>? env = null, HttpClient? github = null, HttpClient? octopusHttp = null, IGitHubApi? api = null,
        IItemStateStore? states = null, IItemLock? locks = null, IRefusalCounts? refusals = null, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTime>? clock = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var settings = ReadModelSettings.From(instance.Root, env);
        var stateDir = RuleStateDirectory.Resolve(env);
        var tokens = GitHubTokenProvider.FromEnvironment(env);   // GH_TOKEN, else the author App's installation token (the token of the rule scripts)
        var baseUrl = (string.IsNullOrWhiteSpace(env("GITHUB_API_URL")) ? "https://api.github.com" : env("GITHUB_API_URL")!.TrimEnd('/')) + "/";
        // two clients with the identity of the rule scripts: the verbs' own reads follow redirects themselves (without the credential), the state store's calls are plain API calls
        HttpClient Client(bool follow) => new(new RuleIdentityHandler(new GitHubAuthHandler(tokens!, new HttpClientHandler { AllowAutoRedirect = follow }), observe)) { BaseAddress = new Uri(baseUrl), Timeout = follow ? TimeSpan.FromSeconds(100) : Timeout.InfiniteTimeSpan };   // (the wire of the verbs sets the time of each call itself)
        var journalHttp = github ?? (tokens is null ? null : Client(true));
        if (api is null) api = github is not null ? new HttpGitHubApi(github, delay) : tokens is null ? new NoGitHubIdentity() : new HttpGitHubApi(Client(false), delay, new HttpClient(new GitHubUserAgentHandler(new HttpClientHandler { AllowAutoRedirect = false })));
        var gh = new RuleGitHub(api, settings, clock);
        states ??= journalHttp is null || tokens is null ? new NoStateStore("secret-missing", "GH_TOKEN is not set on the gateway")
            : instance.Home is not { } home ? new NoStateStore("config-missing", "the instance configuration names no owner and repo") : MakeStates(settings, instance, journalHttp, tokens, home, env);
        var reads = new ItemReads(gh, states);
        locks ??= new FileItemLock(stateDir, delay);
        var checks = new ChecksReadModel(reads, states, locks, delay);
        octopusHttp ??= new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var octopus = new HttpOctopusApi(octopusHttp, settings.Octopus.Url, env);
        return new ReadModelParts
        {
            Settings = settings, Checks = checks, Inspect = new InspectReadModel(reads, checks), Tier = new ModelTierReadModel(reads, states, locks, refusals ?? new RefusalFile(stateDir)),
            Delivery = new DeliveryReadModel(reads, octopus), Secrets = new SecretsReadModel(reads, octopus),
        };
    }

    static IItemStateStore MakeStates(ReadModelSettings settings, InstanceConfig instance, HttpClient github, GitHubTokenProvider tokens, Fworker.Contracts.Ids.RepoId home, Func<string, string?> env)
    {
        var factory = new GitHubClientFactory(github, new ProviderCredentialSource(tokens), new Fworker.Integrations.GitHub.GitHubBudget());
        var authors = instance.Root?["stateAuthors"] is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
        string? login = env("FACTORY_SERVER_LOGIN");
        var identity = new JournalIdentity(settings.Name, async ct =>
        {
            if (!string.IsNullOrEmpty(login)) return login;
            // an installation token cannot read /user: the login of an App is its slug plus [bot]
            if (tokens.AppLogin is { } bot) return login = bot;
            using var req = new HttpRequestMessage(HttpMethod.Get, "user");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await github.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return login = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["login"]?.ToString() ?? throw new JournalRefusedException("GitHub did not name the gateway's login");
        }, authors);
        // the client of the store is made at the first use (a gateway without the installation of its owner starts all the same)
        IJournalStore? made = null;
        return new JournalStateStore(() => made ??= new GitHubJournalStore(factory.For(home).GetAwaiter().GetResult(), identity));
    }
}

/// <summary>The store when there is no GitHub identity (or no repository to read the state of): the refusal the rule scripts give.</summary>
sealed class NoStateStore(string code, string reason) : IItemStateStore
{
    public Task<JsonObject> Read(int item, CancellationToken ct) => throw new VerbRefusal(30, code, reason);
    public Task<bool> Update(int item, Func<JsonObject, Task<bool>> mutate, CancellationToken ct, int tries = 1) => throw new VerbRefusal(30, code, reason);
}

/// <summary>The handlers of the verbs of the read models: the time the rule script is given, the body as the script reads it, the answer as the script prints it.</summary>
sealed class ReadModelHandlers(ReadModelParts parts)
{
    public ReadModelParts Parts => parts;

    static readonly string[] GatewayFields = ["_session", "_cause", "_lease"];

    /// <summary>One verb call: the arguments (without the fields the gateway adds), the body within <paramref name="timeout"/>, and the answer; a call that is not done in time is the runner's timeout answer.</summary>
    public async Task<VerbAnswer> Run(string verb, string stdin, TimeSpan timeout, Func<VerbArgs, CancellationToken, Task<JsonObject>> body)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var (code, json) = await VerbReply.Run(verb, () =>
            {
                var args = VerbArgs.Parse(stdin);
                foreach (var f in GatewayFields) args.Body.Remove(f);
                return body(args, cts.Token);
            });
            return new VerbAnswer(code, json);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return new VerbAnswer(20, JsonSerializer.Serialize(new { ok = false, verb, code = "timeout", reason = "the rule script did not finish in time", retryAfterSeconds = 30, exit = 20 }));
        }
    }
}
