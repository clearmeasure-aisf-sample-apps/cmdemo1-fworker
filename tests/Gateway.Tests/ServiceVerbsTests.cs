using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.7 (#359): the service verbs of v2 (gateway/Identity/ServiceVerbs.cs, RevocationVerbs.cs, GitCredentialVerb.cs, gateway/Operator/OperatorReleaseVerbs.cs, MetricsVerb.cs) against a GitHub that lives in memory. What
/// ServiceVerbParityTests cannot reach: the credential the verbs use, the retries, the timeout and the budget, every way the scope of a git credential can come back wrong, the services that call the verbs
/// (RevocationService, OperatorReleaseLoader), the route table, and the order of the refusals before a verb reaches v2. The cases that compare the answers with the rule scripts are in ServiceVerbParityTests.
/// </summary>
public sealed class ServiceVerbsTests : IDisposable
{
    const string Owner = "o", Repo = "r", Name = "fw";
    readonly string dir = Directory.CreateTempSubdirectory("service-verbs-").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    /// <summary>A GitHub in memory: the refs of one repository and a script for everything else. Every request is kept, with its headers.</summary>
    sealed class Net : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Path, string Body, Dictionary<string, string> Headers)> Requests = new();
        public readonly SortedSet<string> Refs = new(StringComparer.Ordinal);
        /// <summary>Answers a request itself (return a response), or lets the default answer it (return null).</summary>
        public Func<HttpRequestMessage, string, HttpResponseMessage?>? Script;
        /// <summary>Holds every request until the caller gives up (its cancellation).</summary>
        public Func<CancellationToken, Task>? Hang;
        public Dictionary<string, string> RateHeaders = new();
        public string Master = "0123456789abcdef0123456789abcdef01234567";

        public static HttpResponseMessage Json(int status, string json) => new((System.Net.HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests) Requests.Add((request.Method, path + request.RequestUri.Query, body, headers));
            if (Hang is not null) await Hang(ct);
            var response = Script?.Invoke(request, body) ?? Default(request, path, body);
            foreach (var h in RateHeaders) response.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return response;
        }

        HttpResponseMessage Default(HttpRequestMessage req, string path, string body)
        {
            var root = $"/repos/{Owner}/{Repo}/";
            if (path == root + "git/ref/heads/master") return Json(200, $"{{\"object\":{{\"sha\":\"{Master}\"}}}}");
            if (path == root + "git/refs" && req.Method == HttpMethod.Post)
            {
                var r = JsonNode.Parse(body)!["ref"]!.ToString();
                return Refs.Add(r) ? Json(201, $"{{\"ref\":\"{r}\"}}") : Json(422, "{\"message\":\"Reference already exists\"}");
            }
            if (path.StartsWith(root + "git/matching-refs/") && req.Method == HttpMethod.Get)
            {
                var prefix = "refs/" + path[(root + "git/matching-refs/").Length..];
                return Json(200, new JsonArray(Refs.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).Select(x => (JsonNode)new JsonObject { ["ref"] = x }).ToArray()).ToJsonString());
            }
            if (path.StartsWith(root + "git/refs/") && req.Method == HttpMethod.Delete)
                return Refs.Remove("refs/" + path[(root + "git/refs/").Length..]) ? new HttpResponseMessage(System.Net.HttpStatusCode.NoContent) : Json(422, "{\"message\":\"Reference does not exist\"}");
            return Json(404, "{\"message\":\"Not Found\"}");
        }
    }

    string Config(string extra = "")
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, $$"""{"name":"{{Name}}","label":"factory worker","owner":"{{Owner}}","repo":"{{Repo}}","project":7,"defaultBranch":"master"{{extra}}}""");
        return path;
    }

    ServiceGitHub Gh(Net net, Dictionary<string, string?>? env = null, string extraConfig = "", Func<string, string, string?>? keyring = null)
    {
        var e = new Dictionary<string, string?> { ["GH_TOKEN"] = "tok-123", ["GITHUB_API_URL"] = "http://gh.test" };
        foreach (var kv in env ?? new()) e[kv.Key] = kv.Value;
        Func<string, string?> get = n => e.GetValueOrDefault(n);
        return new ServiceGitHub(InstanceConfig.Load(Config(extraConfig), get), get, new HttpClient(net) { BaseAddress = new Uri("http://gh.test/") }) { Pause = _ => Task.CompletedTask, Keyring = keyring ?? ((_, _) => null) };
    }

    static async Task<(int Exit, JsonObject Json)> Run(ServiceVerbs verbs, string verb, string stdin = "{}", TimeSpan? timeout = null)
    {
        var a = await verbs.RunAsync(verb, stdin, timeout ?? TimeSpan.FromSeconds(30));
        return (a.Code, (JsonObject)JsonNode.Parse(a.Output)!);
    }

    (ServiceVerbs Verbs, Net Net) Make(Dictionary<string, string?>? env = null, GitHubBudget? budget = null, string extraConfig = "", Func<string, string, string?>? keyring = null)
    {
        var net = new Net();
        return (new ServiceVerbs(Gh(net, env, extraConfig, keyring), budget), net);
    }

    // ---- the arguments, as the rule scripts read them ----
    [Theory]
    [InlineData("""{"x":"abc"}""", "abc")]
    [InlineData("""{"x":5}""", "5")]
    [InlineData("""{"x":1.5}""", "1.5")]
    [InlineData("""{"x":true}""", "True")]
    [InlineData("""{"x":null}""", "")]
    [InlineData("""{}""", "")]
    public void Text_IsWhatPowerShellMakesOfAJsonValue(string json, string expected) => Assert.Equal(expected, VerbArgs.Text(VerbArgs.Parse(json).Get("x")));

    [Theory]
    [InlineData("7", 7)]
    [InlineData("\"7\"", 7)]
    [InlineData("\" 7 \"", 7)]
    [InlineData("2.5", 2)]      // halves go to the even whole, as [int] does
    [InlineData("3.5", 4)]
    [InlineData("\"0x10\"", 16)]
    [InlineData("true", 1)]
    [InlineData("-3", -3)]
    public void Int_IsTheCastOfPowerShell(string json, int expected) => Assert.Equal(expected, VerbArgs.Int(JsonNode.Parse(json)));

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("\"\"")]
    [InlineData("5000000000")]
    [InlineData("[1]")]
    public void Int_OfWhatIsNoNumber_Throws(string json) => Assert.ThrowsAny<Exception>(() => VerbArgs.Int(JsonNode.Parse(json)));

    [Fact]
    public void Arguments_NamesAreCaseSensitive_NullIsAbsent_NeedRefusesWithUsage()
    {
        var a = VerbArgs.Parse("""{"Name":"x","gone":null,"empty":""}""");
        Assert.False(a.Has("name")); Assert.True(a.Has("Name")); Assert.False(a.Has("gone")); Assert.True(a.Has("empty"));
        foreach (var k in new[] { "name", "gone", "empty", "nothing" })
        {
            var r = Assert.Throws<VerbReject>(() => a.Need(k));
            Assert.Equal((2, "usage", $"missing argument: {k}"), (r.Exit, r.Code, r.Message));
        }
        Assert.False(VerbArgs.Parse("[1,2]").Has("0")); Assert.False(VerbArgs.Parse("").Has("x")); Assert.False(VerbArgs.Parse("5").Has("x"));
        Assert.ThrowsAny<Exception>(() => VerbArgs.Parse("{nope"));
    }

    // ---- the answer's shape: ok, verb, exit; a refusal is code and reason; what nobody expected is an internal error ----
    [Fact]
    public async Task AnAnswer_CarriesVerbOkAndExit_AndARefusalItsCodeAndReason()
    {
        var (verbs, _) = Make();
        var (exit, ok) = await Run(verbs, "revocations");
        Assert.Equal(0, exit);
        Assert.Equal((true, "revocations", 0), (ok["ok"]!.GetValue<bool>(), ok["verb"]!.ToString(), ok["exit"]!.GetValue<int>()));
        var (e2, no) = await Run(verbs, "revoke-worker", "{}");
        Assert.Equal(2, e2);
        Assert.Equal((false, "revoke-worker", "usage", "missing argument: name", 2), (no["ok"]!.GetValue<bool>(), no["verb"]!.ToString(), no["code"]!.ToString(), no["reason"]!.ToString(), no["exit"]!.GetValue<int>()));
    }

    [Fact]
    public async Task AnErrorNobodyExpected_IsAnInternalError_Exit20_NeverACrash()
    {
        var (verbs, net) = Make();
        net.Script = (_, _) => Net.Json(200, "{\"object\":{}}");   // a master branch without a sha
        var (exit, o) = await Run(verbs, "revoke-worker", """{"name":"a","generation":1}""");
        Assert.Equal(20, exit);
        Assert.Equal(("internal-error", false, 30), (o["code"]!.ToString(), o["ok"]!.GetValue<bool>(), o["retryAfterSeconds"]!.GetValue<int>()));
        var (e2, bad) = await Run(verbs, "revocations", "{broken json");
        Assert.Equal((20, "internal-error"), (e2, bad["code"]!.ToString()));
    }

    [Fact]
    public async Task AVerbThatDoesNotFinishInTime_IsATimeout_Exit20()
    {
        var (verbs, net) = Make();
        net.Hang = ct => Task.Delay(Timeout.Infinite, ct);
        var (exit, o) = await Run(verbs, "revocations", "{}", TimeSpan.FromMilliseconds(50));
        Assert.Equal(20, exit);
        Assert.Equal(("timeout", 30, false), (o["code"]!.ToString(), o["retryAfterSeconds"]!.GetValue<int>(), o["ok"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task AnUnknownVerb_IsRefused()
    {
        var (verbs, _) = Make();
        var (exit, o) = await Run(verbs, "plant-snapshot-save");
        Assert.Equal((2, "unknown-verb"), (exit, o["code"]!.ToString()));
    }

    // ---- the credential of the rule scripts ----
    [Fact]
    public async Task EveryCall_CarriesTheBearerTheAcceptTheVersionAndTheUserAgentOfTheRuleScripts()
    {
        var (verbs, net) = Make();
        await Run(verbs, "revocations");
        var h = net.Requests[0].Headers;
        Assert.Equal("Bearer tok-123", h["Authorization"]);
        Assert.Equal("application/vnd.github+json", h["Accept"]);
        Assert.Equal("2022-11-28", h["X-GitHub-Api-Version"]);
        Assert.Equal("factory-worker-gateway", h["User-Agent"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unset")]
    public async Task WithoutAToken_NothingIsAskedOfGitHub_30SecretMissing(string? token)
    {
        var (verbs, net) = Make(new() { ["GH_TOKEN"] = token });
        var (exit, o) = await Run(verbs, "revocations");
        Assert.Equal((30, "secret-missing"), (exit, o["code"]!.ToString()));
        Assert.Empty(net.Requests);
    }

    [Fact]
    public async Task TheAuthorApp_IsNotUsed_WhenAuthorAppIsNotOne_AndGhTokenIsNotUsed_WhenItIs()
    {
        // FACTORY_AUTHOR_APP unset: GH_TOKEN alone, whatever App is configured (Get-GhToken)
        var (a, na) = Make(new() { ["GH_TOKEN"] = null, ["FACTORY_APP_AUTHOR_ID"] = "1", ["FACTORY_APP_AUTHOR_INSTALLATION"] = "2", ["FACTORY_APP_AUTHOR_KEY"] = NewKey() });
        Assert.Equal(30, (await Run(a, "revocations")).Exit); Assert.Empty(na.Requests);
        // FACTORY_AUTHOR_APP=1: the App alone, GH_TOKEN is not looked at: without its key the call is refused and no request goes out with GH_TOKEN
        var (b, nb) = Make(new() { ["FACTORY_AUTHOR_APP"] = "1", ["GH_TOKEN"] = "tok-123" });
        var (exit, o) = await Run(b, "revocations");
        Assert.Equal((30, "secret-missing"), (exit, o["code"]!.ToString())); Assert.Empty(nb.Requests);
    }

    static string NewKey() { using var rsa = RSA.Create(2048); return rsa.ExportPkcs8PrivateKeyPem(); }

    // ---- the calls to GitHub: retries and refusals ----
    [Fact]
    public async Task ABadGateway_IsTriedThreeTimes_WithPausesOfTwoAndFourSeconds()
    {
        var net = new Net(); var pauses = new List<TimeSpan>(); var calls = 0;
        net.Script = (_, _) => ++calls < 3 ? Net.Json(502, "{}") : null;
        var e = new Dictionary<string, string?> { ["GH_TOKEN"] = "t", ["GITHUB_API_URL"] = "http://gh.test" };
        Func<string, string?> get = n => e.GetValueOrDefault(n);
        var gh = new ServiceGitHub(InstanceConfig.Load(Config(), get), get, new HttpClient(net) { BaseAddress = new Uri("http://gh.test/") }) { Pause = d => { pauses.Add(d); return Task.CompletedTask; } };
        var (exit, o) = await Run(new ServiceVerbs(gh, null), "revocations");
        Assert.Equal(0, exit); Assert.Equal(3, net.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], pauses);
    }

    [Theory]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task AGatewayErrorThatStays_IsGithubError20_AfterThreeTries(int status)
    {
        var (verbs, net) = Make(); net.Script = (_, _) => Net.Json(status, "{\"message\":\"down\"}");
        var (exit, o) = await Run(verbs, "revocations");
        Assert.Equal((20, "github-error", status, 30), (exit, o["code"]!.ToString(), o["status"]!.GetValue<int>(), o["retryAfterSeconds"]!.GetValue<int>()));
        Assert.Equal(3, net.Requests.Count);
    }

    [Fact]
    public async Task ANetworkErrorThatStays_IsGithubUnreachable20_AfterThreeTries_AndOneThatPassesIsRetried()
    {
        var (verbs, net) = Make(); net.Script = (_, _) => throw new HttpRequestException("connection refused");
        var (exit, o) = await Run(verbs, "revocations");
        Assert.Equal((20, "github-unreachable", 30), (exit, o["code"]!.ToString(), o["retryAfterSeconds"]!.GetValue<int>()));
        Assert.Equal(3, net.Requests.Count);
        var (v2, n2) = Make(); var n = 0; n2.Script = (_, _) => ++n == 1 ? throw new HttpRequestException("reset") : null;
        Assert.Equal(0, (await Run(v2, "revocations")).Exit);
    }

    [Theory]
    [InlineData(500, 20, "github-error")]
    [InlineData(404, 12, "github-not-found")]
    [InlineData(401, 20, "github-error")]
    [InlineData(409, 20, "github-error")]
    public async Task AStatusIsRefusedAsTheRuleScriptsRefuseIt(int status, int exit, string code)
    {
        var (verbs, net) = Make(); net.Script = (_, _) => Net.Json(status, "{\"message\":\"no\"}");
        var (e, o) = await Run(verbs, "revocations");
        Assert.Equal((exit, code), (e, o["code"]!.ToString()));
        Assert.Equal("{\"message\":\"no\"}", o["detail"]!.ToString());
    }

    [Fact]
    public async Task A403_IsARateLimit_OnlyWhenGitHubSaysSo_ElseARefusedPermission()
    {
        var (verbs, net) = Make();
        net.Script = (_, _) => Net.Json(403, "{\"message\":\"Resource not accessible by integration\"}");
        var (e, o) = await Run(verbs, "revocations");
        Assert.Equal((30, "github-forbidden", 403), (e, o["code"]!.ToString(), o["status"]!.GetValue<int>()));
        Assert.Contains("Resource not accessible", o["reason"]!.ToString());
        // a Retry-After, no requests left, the message of a primary or secondary limit, or a 429, is a limit
        foreach (var (status, message, headers) in new[] { (403, "x", new Dictionary<string, string> { ["Retry-After"] = "17" }), (403, "API rate limit exceeded", new Dictionary<string, string>()), (403, "You have exceeded a secondary rate limit", new Dictionary<string, string>()), (429, "slow down", new Dictionary<string, string>()), (403, "x", new Dictionary<string, string> { ["x-ratelimit-remaining"] = "0", ["x-ratelimit-reset"] = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120).ToString() }) })
        {
            var (v, n) = Make(); n.Script = (_, _) => { var r = Net.Json(status, $"{{\"message\":\"{message}\"}}"); foreach (var h in headers) r.Headers.TryAddWithoutValidation(h.Key, h.Value); return r; };
            var (ex, lim) = await Run(v, "revocations");
            Assert.Equal((20, "github-rate-limited"), (ex, lim["code"]!.ToString()));
            Assert.True(lim["retryAfterSeconds"]!.GetValue<int>() >= 1);
            if (headers.ContainsKey("Retry-After")) Assert.Equal(17, lim["retryAfterSeconds"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task TheRateLimitOfEachAnswer_GoesToTheBudget_AsTheGithubFieldOfARuleScriptDid()
    {
        var budget = new GitHubBudget(300);
        var (verbs, net) = Make(budget: budget);
        net.RateHeaders = new() { ["x-ratelimit-remaining"] = "42", ["x-ratelimit-limit"] = "5000", ["x-ratelimit-reset"] = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600).ToString(), ["x-ratelimit-resource"] = "core" };
        await Run(verbs, "revocations");
        Assert.True(budget.Low().Low);
        Assert.Equal("core", budget.Low().Resource);
    }

    [Fact]
    public async Task Metrics_IsNotStartedWhileTheBudgetIsLow_AsTheRunnerDoesNotStartIt()
    {
        var budget = new GitHubBudget(300);
        budget.Observe(new JsonObject { ["core"] = new JsonObject { ["remaining"] = 10, ["limit"] = 5000, ["reset"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300 } });
        var (verbs, net) = Make(budget: budget);
        var (exit, o) = await Run(verbs, "metrics", "{}");
        Assert.Equal((20, "github-budget", "core"), (exit, o["code"]!.ToString(), Resource(o)));
        Assert.Empty(net.Requests);
        // the others are essential for the gateway to start: they run
        Assert.Equal(0, (await Run(verbs, "revocations")).Exit);
    }

    static string Resource(JsonObject o) => System.Text.RegularExpressions.Regex.Match(o["reason"]!.ToString(), @"\((\w+)\)").Groups[1].Value;

    // ---- revocations ----
    [Fact]
    public async Task Revocations_AreReadFromTheRefs_TheHighestGenerationOfEachName_AndNothingElse()
    {
        var (verbs, net) = Make();
        foreach (var r in new[] { "alice/1", "alice/3", "bob/2", "alice/x", "bob/2/9", "has space/1", "carol/1234567890" }) net.Refs.Add($"refs/factory/{Name}/revoked/{r}");
        net.Refs.Add($"refs/factory/{Name}/operator-release/20261026/7/1.0.1"); net.Refs.Add("refs/factory/other/revoked/dave/4");
        var (exit, o) = await Run(verbs, "revocations");
        Assert.Equal(0, exit);
        Assert.Equal("""{"alice":3,"bob":2}""", o["revoked"]!.ToJsonString());
    }

    [Fact]
    public async Task Revocations_ARefOfANameLongerThan40_OrAGenerationOfTenDigits_IsNotARevocation()
    {
        var (verbs, net) = Make();
        net.Refs.Add($"refs/factory/{Name}/revoked/{new string('a', 41)}/1");
        net.Refs.Add($"refs/factory/{Name}/revoked/{new string('b', 40)}/2");
        net.Refs.Add($"refs/factory/{Name}/revoked/zed/1000000000");
        var (_, o) = await Run(verbs, "revocations");
        Assert.Equal($"{{\"{new string('b', 40)}\":2}}", o["revoked"]!.ToJsonString());
    }

    [Fact]
    public async Task Revocations_AMatchingRefsThatAnswersTheSamePageAgain_EndsTheRead()
    {
        var (verbs, net) = Make(); net.Refs.Add($"refs/factory/{Name}/revoked/a/1");
        var (exit, _) = await Run(verbs, "revocations");
        Assert.Equal(0, exit);
        Assert.Equal(2, net.Requests.Count);   // the page, and the page again with nothing new
    }

    [Fact]
    public async Task Revocations_AreReadByTheInstanceNameAndTheDefaultRepositoryOnly()
    {
        var (verbs, net) = Make();
        await Run(verbs, "revocations");
        Assert.Equal($"/repos/{Owner}/{Repo}/git/matching-refs/factory/{Name}/revoked/", net.Requests[0].Path.Split('?')[0]);
        Assert.Equal("?per_page=100&page=1", "?" + net.Requests[0].Path.Split('?')[1]);
    }

    // ---- revoke-worker ----
    [Fact]
    public async Task RevokeWorker_WritesTheGenerationAsARefOfTheDefaultBranchHead_AndRemovesTheLowerOnes()
    {
        var (verbs, net) = Make();
        net.Refs.Add($"refs/factory/{Name}/revoked/alice/1"); net.Refs.Add($"refs/factory/{Name}/revoked/alice/2"); net.Refs.Add($"refs/factory/{Name}/revoked/bob/1");
        var (exit, o) = await Run(verbs, "revoke-worker", """{"name":"alice","generation":3}""");
        Assert.Equal(0, exit);
        Assert.Equal(("alice", 3, "[1,2]", "[]"), (o["name"]!.ToString(), o["generation"]!.GetValue<int>(), o["removed"]!.ToJsonString(), o["left"]!.ToJsonString()));
        Assert.Equal([$"refs/factory/{Name}/revoked/alice/3", $"refs/factory/{Name}/revoked/bob/1"], net.Refs.ToArray());
        var post = net.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal(new JsonObject { ["ref"] = $"refs/factory/{Name}/revoked/alice/3", ["sha"] = net.Master }.ToJsonString(), JsonNode.Parse(post.Body)!.ToJsonString());
    }

    [Fact]
    public async Task RevokeWorker_NeverRemovesALowerGenerationBeforeTheHigherOneIsSeenOnGitHub()
    {
        var (verbs, net) = Make();
        net.Refs.Add($"refs/factory/{Name}/revoked/alice/1");
        // the write is answered 201 and not kept: the read-back does not show it
        net.Script = (req, _) => req.Method == HttpMethod.Post ? Net.Json(201, "{}") : null;
        var (exit, o) = await Run(verbs, "revoke-worker", """{"name":"alice","generation":2}""");
        Assert.Equal((20, "not-recorded", 15), (exit, o["code"]!.ToString(), o["retryAfterSeconds"]!.GetValue<int>()));
        Assert.Equal([$"refs/factory/{Name}/revoked/alice/1"], net.Refs.ToArray());
        Assert.DoesNotContain(net.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task RevokeWorker_ALowerGenerationThatCannotBeRemoved_IsLeft_TheRevocationStands()
    {
        var (verbs, net) = Make();
        net.Refs.Add($"refs/factory/{Name}/revoked/alice/1");
        net.Script = (req, _) => req.Method == HttpMethod.Delete ? Net.Json(500, "{}") : null;
        var (exit, o) = await Run(verbs, "revoke-worker", """{"name":"alice","generation":2}""");
        Assert.Equal(0, exit);
        Assert.Equal(("[]", "[1]"), (o["removed"]!.ToJsonString(), o["left"]!.ToJsonString()));
        Assert.Contains($"refs/factory/{Name}/revoked/alice/2", net.Refs);
    }

    [Theory]
    [InlineData("""{"name":"a/b","generation":1}""")]
    [InlineData("""{"name":"a b","generation":1}""")]
    [InlineData("{\"name\":\"ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNO\",\"generation\":1}")]   // 41 characters
    [InlineData("""{"name":"alice","generation":0}""")]
    [InlineData("""{"name":"alice","generation":-1}""")]
    public async Task RevokeWorker_ABadNameOrGeneration_IsUsage_AndAsksGitHubForNothing(string stdin)
    {
        var (verbs, net) = Make();
        var (exit, o) = await Run(verbs, "revoke-worker", stdin);
        Assert.Equal((2, "usage", "name is a worker name and generation is 1 or more"), (exit, o["code"]!.ToString(), o["reason"]!.ToString()));
        Assert.Empty(net.Requests);
    }

    [Fact]
    public async Task RevokeWorker_A422ThatIsNoAlreadyExists_IsRefused_AndRemovesNothing()
    {
        var (verbs, net) = Make();
        net.Refs.Add($"refs/factory/{Name}/revoked/alice/1");
        net.Script = (req, _) => req.Method == HttpMethod.Post ? Net.Json(422, "{\"message\":\"Validation Failed\"}") : null;
        var (exit, o) = await Run(verbs, "revoke-worker", """{"name":"alice","generation":2}""");
        Assert.Equal((20, "github-error", "GitHub refused the revocation ref", "Validation Failed"), (exit, o["code"]!.ToString(), o["reason"]!.ToString(), o["detail"]!.ToString()));
        Assert.DoesNotContain(net.Requests, r => r.Method == HttpMethod.Delete);
    }

    // ---- operator releases ----
    [Fact]
    public async Task OperatorReleases_OnePerPullRequest_OfThatDay_ValidRowsOnly()
    {
        var (verbs, net) = Make();
        foreach (var r in new[] { "20261026/101/1.0.40", "20261026/102/1.0.41", "20261026/101/1.0.99", "20261026/103/bad", "20261026/104/1", "20261026/105/1.2.3.4.5", "20261027/106/1.0.1", "20261026/x/1.0.1" }) net.Refs.Add($"refs/factory/{Name}/operator-release/{r}");
        var (exit, o) = await Run(verbs, "operator-releases", """{"day":"20261026"}""");
        Assert.Equal(0, exit);
        Assert.Equal("""[{"pr":101,"version":"1.0.40"},{"pr":102,"version":"1.0.41"}]""", o["releases"]!.ToJsonString());
        Assert.Equal("20261026", o["day"]!.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"day":"2026-10-26"}""")]
    [InlineData("""{"day":"202610"}""")]
    [InlineData("""{"day":"202610261"}""")]
    public async Task OperatorReleases_ABadDay_IsUsage(string stdin)
    {
        var (verbs, net) = Make();
        var (exit, o) = await Run(verbs, "operator-releases", stdin);
        Assert.Equal(2, exit); Assert.Equal("usage", o["code"]!.ToString()); Assert.Empty(net.Requests);
    }

    [Fact]
    public async Task OperatorRelease_WritesTheRef_Idempotently()
    {
        var (verbs, net) = Make();
        for (var i = 0; i < 2; i++)
        {
            var (exit, o) = await Run(verbs, "operator-release", """{"day":"20261026","pr":101,"version":"1.0.40"}""");
            Assert.Equal(0, exit); Assert.Equal(("20261026", 101, "1.0.40"), (o["day"]!.ToString(), o["pr"]!.GetValue<int>(), o["version"]!.ToString()));
        }
        Assert.Equal([$"refs/factory/{Name}/operator-release/20261026/101/1.0.40"], net.Refs.ToArray());
    }

    [Theory]
    [InlineData("""{"day":"20261026","pr":0,"version":"1.0.1"}""")]
    [InlineData("""{"day":"20261026","pr":101,"version":"v1"}""")]
    [InlineData("""{"day":"20261026","pr":101,"version":"1.0.1.2.3"}""")]
    [InlineData("""{"day":"1","pr":101,"version":"1.0.1"}""")]
    public async Task OperatorRelease_ABadDayPrOrVersion_IsUsage_AndWritesNothing(string stdin)
    {
        var (verbs, net) = Make();
        var (exit, o) = await Run(verbs, "operator-release", stdin);
        Assert.Equal((2, "usage"), (exit, o["code"]!.ToString())); Assert.Empty(net.Requests); Assert.Empty(net.Refs);
    }

    [Fact]
    public async Task OperatorRelease_A422ThatIsNoAlreadyExists_IsRefused()
    {
        var (verbs, net) = Make(); net.Script = (req, _) => req.Method == HttpMethod.Post ? Net.Json(422, "{\"message\":\"Validation Failed\"}") : null;
        var (exit, o) = await Run(verbs, "operator-release", """{"day":"20261026","pr":101,"version":"1.0.40"}""");
        Assert.Equal((20, "github-error", "GitHub refused the release ref"), (exit, o["code"]!.ToString(), o["reason"]!.ToString()));
    }

    // ---- git-credential ----
    const string Perm = """{"contents":"write","metadata":"read"}""";

    static HttpResponseMessage Minted(string permissions = Perm, string repos = """[{"name":"r","full_name":"o/r"}]""") =>
        Net.Json(201, $"{{\"token\":\"ghs_secret_token\",\"expires_at\":\"2026-10-29T23:02:29Z\",\"permissions\":{permissions},\"repositories\":{repos}}}");

    (ServiceVerbs Verbs, Net Net, RSA Key) AppGateway(Func<HttpRequestMessage, string, HttpResponseMessage?>? script = null, Dictionary<string, string?>? env = null)
    {
        var rsa = RSA.Create(2048);
        var e = new Dictionary<string, string?> { ["GH_TOKEN"] = "gh-token-not-for-this", ["FACTORY_APP_AUTHOR_ID"] = "4321", ["FACTORY_APP_AUTHOR_INSTALLATION"] = "99", ["FACTORY_APP_AUTHOR_SLUG"] = "factory-author", ["FACTORY_APP_AUTHOR_KEY"] = rsa.ExportPkcs8PrivateKeyPem() };
        foreach (var kv in env ?? new()) e[kv.Key] = kv.Value;
        var (verbs, net) = Make(e);
        net.Script = script ?? ((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/access_tokens") ? Minted() : null);
        return (verbs, net, rsa);
    }

    [Fact]
    public async Task GitCredential_AsksForOneRepositoryAndContentsWrite_WithAJwtOfTheAppSignedByItsKey()
    {
        var (verbs, net, rsa) = AppGateway();
        var (exit, o) = await Run(verbs, "git-credential", """{"item":7}""");
        Assert.Equal(0, exit);
        Assert.Equal(("ghs_secret_token", "o/r", "x-access-token", 7, """{"contents":"write"}"""), (o["token"]!.ToString(), o["repository"]!.ToString(), o["username"]!.ToString(), o["item"]!.GetValue<int>(), o["permissions"]!.ToJsonString()));
        Assert.Matches(@"^\d\d/\d\d/\d{4} \d\d:\d\d:\d\d$", o["expires_at"]!.ToString());
        var mint = net.Requests.Single();
        Assert.Equal((HttpMethod.Post, "/app/installations/99/access_tokens"), (mint.Method, mint.Path));
        Assert.Equal("""{"repositories":["r"],"permissions":{"contents":"write"}}""", JsonNode.Parse(mint.Body)!.ToJsonString());
        // the bearer is a JWT of the App, not GH_TOKEN
        var jwt = mint.Headers["Authorization"]["Bearer ".Length..];
        Assert.NotEqual("gh-token-not-for-this", jwt);
        var parts = jwt.Split('.'); Assert.Equal(3, parts.Length);
        static byte[] Un(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));
        Assert.Equal("""{"alg":"RS256","typ":"JWT"}""", Encoding.UTF8.GetString(Un(parts[0])));
        var claims = JsonNode.Parse(Encoding.UTF8.GetString(Un(parts[1])))!;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Equal("4321", claims["iss"]!.ToString());
        Assert.InRange(claims["iat"]!.GetValue<long>(), now - 70, now - 50); Assert.InRange(claims["exp"]!.GetValue<long>(), now + 530, now + 550);
        Assert.True(rsa.VerifyData(Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]), Un(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task GitCredential_TheTokenIsInTheAnswerOnce_AndInNoRefusal()
    {
        var (verbs, net, _) = AppGateway();
        var (_, ok) = await Run(verbs, "git-credential", """{"item":7}""");
        Assert.Equal(1, ok.ToJsonString().Split("ghs_secret_token").Length - 1);
        foreach (var stdin in new[] { """{"item":"x"}""", "{}" }) Assert.DoesNotContain("ghs_secret_token", (await Run(verbs, "git-credential", stdin)).Json.ToJsonString());
        Assert.DoesNotContain(net.Requests, r => r.Body.Contains("PRIVATE KEY") || r.Path.Contains("ghs_secret_token"));
    }

    [Theory]
    [InlineData("""{"contents":"write","metadata":"read","issues":"read"}""")]          // wider: another permission
    [InlineData("""{"contents":"write","metadata":"write"}""")]                           // metadata beyond read
    [InlineData("""{"contents":"write","administration":"write"}""")]
    [InlineData("""{"contents":"read","metadata":"read"}""")]                              // narrower than asked
    [InlineData("""{"metadata":"read"}""")]
    [InlineData("""{}""")]
    public async Task GitCredential_ATokenWhosePermissionsAreNotExactlyContentsWrite_IsRevoked_AndNotHandedOut(string permissions)
    {
        var (verbs, net, _) = AppGateway((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/access_tokens") ? Minted(permissions) : req.Method == HttpMethod.Delete ? new HttpResponseMessage(System.Net.HttpStatusCode.NoContent) : null);
        var (exit, o) = await Run(verbs, "git-credential", """{"item":7}""");
        Assert.Equal((20, "git-credential-scope"), (exit, o["code"]!.ToString()));
        Assert.False(o.ContainsKey("token")); Assert.DoesNotContain("ghs_secret_token", o.ToJsonString());
        var revoke = net.Requests.Single(r => r.Method == HttpMethod.Delete);
        Assert.Equal("/installation/token", revoke.Path); Assert.Equal("Bearer ghs_secret_token", revoke.Headers["Authorization"]);
    }

    [Theory]
    [InlineData("""[{"name":"r","full_name":"o/r"},{"name":"s","full_name":"o/s"}]""")]   // two repositories
    [InlineData("""[{"name":"other","full_name":"o/other"}]""")]
    [InlineData("""[{"name":"r","full_name":"x/r"}]""")]                                      // the right name of another owner
    [InlineData("""[]""")]
    public async Task GitCredential_ATokenForAnotherRepositoryOrMoreThanOne_IsRevoked_AndNotHandedOut(string repos)
    {
        var (verbs, net, _) = AppGateway((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/access_tokens") ? Minted(repos: repos) : req.Method == HttpMethod.Delete ? new HttpResponseMessage(System.Net.HttpStatusCode.NoContent) : null);
        var (exit, o) = await Run(verbs, "git-credential", """{"item":7}""");
        Assert.Equal((20, "git-credential-scope"), (exit, o["code"]!.ToString()));
        Assert.Single(net.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task GitCredential_ARevokeThatFails_StillRefuses()
    {
        var (verbs, net, _) = AppGateway((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/access_tokens") ? Minted("""{"contents":"write","issues":"write"}""") : throw new HttpRequestException("down"));
        var (exit, o) = await Run(verbs, "git-credential", """{"item":7}""");
        Assert.Equal((20, "git-credential-scope"), (exit, o["code"]!.ToString()));
    }

    [Fact]
    public async Task GitCredential_AFullNameIsNotRequired_ButIfGivenMustMatch()
    {
        var (verbs, _, _) = AppGateway((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/access_tokens") ? Minted(repos: """[{"name":"R"}]""") : null);
        Assert.Equal(0, (await Run(verbs, "git-credential", """{"item":7}""")).Exit);   // the name is not case sensitive
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    [InlineData(500)]
    [InlineData(200)]
    public async Task GitCredential_AMintThatIsNot201_IsAppTokenFailed20(int status)
    {
        var (verbs, _, _) = AppGateway((req, _) => Net.Json(status, "{\"token\":\"ghs_secret_token\"}"));
        var (exit, o) = await Run(verbs, "git-credential", """{"item":7}""");
        Assert.Equal((20, "app-token-failed", 30, $"GitHub answered {status} when minting the item's git credential"), (exit, o["code"]!.ToString(), o["retryAfterSeconds"]!.GetValue<int>(), o["reason"]!.ToString()));
        Assert.DoesNotContain("ghs_secret_token", o.ToJsonString());
    }

    [Fact]
    public async Task GitCredential_WithoutTheAppsIdsKeyOrInstallation_IsRefusedWithoutAskingGitHub()
    {
        // no App at all: not configured (the operator's file is looked for under HOME; none here)
        var (a, na) = Make(new() { ["HOME"] = dir, ["USERPROFILE"] = dir });
        var (e1, o1) = await Run(a, "git-credential", """{"item":7}""");
        Assert.Equal((30, "app-not-configured"), (e1, o1["code"]!.ToString())); Assert.Empty(na.Requests);
        // an App without an installation
        var (b, nb) = Make(new() { ["FACTORY_APP_AUTHOR_ID"] = "1", ["FACTORY_APP_AUTHOR_KEY"] = NewKey() });
        var (e2, o2) = await Run(b, "git-credential", """{"item":7}""");
        Assert.Equal((30, "app-not-installed"), (e2, o2["code"]!.ToString())); Assert.Empty(nb.Requests);
        // an App without a key, or with the vault placeholder, and nothing in the keyring
        foreach (var key in new string?[] { null, "", "unset", " unset " })
        {
            var (c, nc) = Make(new() { ["FACTORY_APP_AUTHOR_ID"] = "1", ["FACTORY_APP_AUTHOR_INSTALLATION"] = "2", ["FACTORY_APP_AUTHOR_KEY"] = key });
            var (e3, o3) = await Run(c, "git-credential", """{"item":7}""");
            Assert.Equal((30, "secret-missing", "no private key for the author GitHub App"), (e3, o3["code"]!.ToString(), o3["reason"]!.ToString()));
            Assert.Empty(nc.Requests);
        }
    }

    [Fact]
    public async Task GitCredential_TheKeyringIsLookedAtLast_ForTheAuthorAppsKey_AndItsPlaceholderCountsAsNone()
    {
        var asked = new List<(string Kind, string App)>();
        using var rsa = RSA.Create(2048);
        var (verbs, net) = Make(new() { ["FACTORY_APP_AUTHOR_ID"] = "1", ["FACTORY_APP_AUTHOR_INSTALLATION"] = "2" }, keyring: (k, a) => { asked.Add((k, a)); return rsa.ExportPkcs8PrivateKeyPem(); });
        net.Script = (req, _) => req.RequestUri!.AbsolutePath.EndsWith("/access_tokens") ? Minted() : null;
        Assert.Equal(0, (await Run(verbs, "git-credential", """{"item":7}""")).Exit);
        Assert.Equal([("github-app-private-key", "author")], asked);
        var (unset, _) = Make(new() { ["FACTORY_APP_AUTHOR_ID"] = "1", ["FACTORY_APP_AUTHOR_INSTALLATION"] = "2" }, keyring: (_, _) => "unset");
        Assert.Equal((30, "secret-missing"), ((await Run(unset, "git-credential", """{"item":7}""")).Exit, (await Run(unset, "git-credential", """{"item":7}""")).Json["code"]!.ToString()));
        // a keyring that cannot be asked (no secret-tool) is an internal error, as in the script
        var (broken, nb) = Make(new() { ["FACTORY_APP_AUTHOR_ID"] = "1", ["FACTORY_APP_AUTHOR_INSTALLATION"] = "2" }, keyring: (_, _) => throw new System.ComponentModel.Win32Exception("secret-tool"));
        var (e, o) = await Run(broken, "git-credential", """{"item":7}""");
        Assert.Equal((20, "internal-error"), (e, o["code"]!.ToString())); Assert.Empty(nb.Requests);
    }

    [Fact]
    public async Task GitCredential_AKeyThatIsNoKey_IsAnInternalError_AndAsksGitHubForNothing()
    {
        var (verbs, net, _) = AppGateway(env: new() { ["FACTORY_APP_AUTHOR_KEY"] = "not a pem" });
        var (exit, o) = await Run(verbs, "git-credential", """{"item":7}""");
        Assert.Equal((20, "internal-error"), (exit, o["code"]!.ToString())); Assert.Empty(net.Requests);
        Assert.DoesNotContain("not a pem", o.ToJsonString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"item":""}""")]
    [InlineData("""{"item":null}""")]
    [InlineData("""{"item":"x"}""")]
    public async Task GitCredential_WithoutAnItemNumber_IsRefused_BeforeAnythingIsAsked(string stdin)
    {
        var (verbs, net, _) = AppGateway();
        var (exit, o) = await Run(verbs, "git-credential", stdin);
        Assert.True(exit is 2 or 20); Assert.Equal(exit == 2 ? "usage" : "internal-error", o["code"]!.ToString()); Assert.Empty(net.Requests);
    }

    [Fact]
    public async Task GitCredential_FakeApps_UseTheFakesPlaceholderJwt_AndNoKey()
    {
        var (verbs, net) = Make(new() { ["FACTORY_FAKE_APPS"] = "1" });
        net.Script = (req, _) => req.RequestUri!.AbsolutePath == "/app/installations/0/access_tokens" ? Minted() : null;
        var (exit, o) = await Run(verbs, "git-credential", """{"item":7}""");
        Assert.Equal(0, exit);
        Assert.StartsWith("Bearer eyJhbGciOiJSUzI1NiJ9.", net.Requests[0].Headers["Authorization"]); Assert.EndsWith(".fake", net.Requests[0].Headers["Authorization"]);
    }

    // ---- metrics ----
    [Theory]
    [InlineData("", -7 * 24 * 60)]
    [InlineData("30m", -30)]
    [InlineData("24h", -24 * 60)]
    [InlineData(" 7 d ", -7 * 24 * 60)]
    [InlineData("0d", 0)]
    [InlineData("9999d", -90 * 24 * 60)]      // never more than 90 days back
    public void Since_IsADurationBackFromNow(string since, int minutes)
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddMinutes(minutes), MetricsVerb.Since(since, now));
    }

    [Fact]
    public void Since_IsAnIsoTime_UtcWhereItNamesNoZone_AndNeverBeforeNinetyDays()
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc), MetricsVerb.Since("2026-10-20T08:00:00Z", now));
        Assert.Equal(new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc), MetricsVerb.Since("2026-10-20 08:00:00", now));
        Assert.Equal(new DateTime(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc), MetricsVerb.Since("2026-10-20T08:00:00+02:00", now));
        Assert.Equal(now.AddDays(-90), MetricsVerb.Since("2000-01-01", now));
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("7D")]
    [InlineData("12345d")]
    [InlineData("-7d")]
    [InlineData("2026-13-45")]
    public void Since_AnythingElse_IsBadSince(string since)
    {
        var r = Assert.Throws<VerbReject>(() => MetricsVerb.Since(since, DateTime.UtcNow));
        Assert.Equal((2, "bad-since", $"since is an ISO date or a duration like 30m, 24h, 7d; got '{since}'"), (r.Exit, r.Code, r.Message));
    }

    [Fact]
    public void Stats_AreCountMedianP90MinMax_RoundedToTheEvenSecond()
    {
        Assert.Equal("""{"count":0}""", MetricsVerb.Stats([]).ToJsonString());
        Assert.Equal("""{"count":1,"median":5,"p90":5,"min":5,"max":5}""", MetricsVerb.Stats([5]).ToJsonString());
        Assert.Equal("""{"count":4,"median":2,"p90":4,"min":1,"max":4}""", MetricsVerb.Stats([4, 1, 3, 2]).ToJsonString());   // the mean of 2 and 3 is 2.5, which goes to the even whole: 2
    }

    [Fact]
    public async Task Metrics_AnEmptyBoard_IsAnEmptyAnswer()
    {
        var (verbs, net) = Make();
        net.Script = (req, _) => req.RequestUri!.AbsolutePath == "/user" ? Net.Json(200, "{\"login\":\"bot\"}") : req.RequestUri.AbsolutePath == "/graphql" ? Net.Json(200, """{"data":{"organization":{"projectV2":{"items":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}""") : null;
        var (exit, o) = await Run(verbs, "metrics", """{"since":"7d"}""");
        Assert.Equal(0, exit);
        Assert.Equal((0, "seconds", "[]", "[]"), (o["scanned"]!.GetValue<int>(), o["unit"]!.ToString(), o["items"]!.ToJsonString(), o["unreadable"]!.ToJsonString()));
    }

    [Fact]
    public async Task Metrics_ReadsTheBoardPageByPage_AsTheScriptDid()
    {
        var (verbs, net) = Make(); var pages = new List<string>();
        net.Script = (req, body) =>
        {
            if (req.RequestUri!.AbsolutePath == "/user") return Net.Json(200, "{\"login\":\"bot\"}");
            if (req.RequestUri.AbsolutePath != "/graphql") return null;
            var after = JsonNode.Parse(body)!["variables"]!["after"]?.ToString(); pages.Add(after ?? "");
            return after is null
                ? Net.Json(200, """{"data":{"organization":{"projectV2":{"items":{"pageInfo":{"hasNextPage":true,"endCursor":"c1"},"nodes":[]}}}}}""")
                : Net.Json(200, """{"data":{"organization":{"projectV2":{"items":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}""");
        };
        await Run(verbs, "metrics", "{}");
        Assert.Equal(["", "c1"], pages);
        var vars = JsonNode.Parse(net.Requests.First(r => r.Path == "/graphql").Body)!["variables"]!;
        Assert.Equal((Owner, 7), (vars["o"]!.ToString(), vars["n"]!.GetValue<int>()));
    }

    // ---- the services that call the verbs, on v2 ----
    (ServiceVerbCalls Calls, Net Net, EngineOptions Engine) CallsOf(bool v2, Dictionary<string, string?>? env = null)
    {
        var (verbs, net) = Make(env);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, EngineOptionsTests.BlockFor(v2 ? "\"prod\"" : "", ""));
        Func<string, string?> none = _ => null;
        var engine = EngineOptions.From(InstanceConfig.Load(path, none), "prod", none);
        return (new ServiceVerbCalls(new Runner("/nonexistent/factory.ps1"), verbs, engine), net, engine);
    }

    [Theory]
    [InlineData(true, "revocations", true)]
    [InlineData(true, "revoke-worker", true)]
    [InlineData(true, "git-credential", true)]
    [InlineData(true, "operator-releases", true)]
    [InlineData(true, "operator-release", true)]
    [InlineData(true, "metrics", true)]
    [InlineData(true, "get-item", false)]
    [InlineData(true, "plant-snapshot-save", false)]
    [InlineData(false, "revocations", false)]
    [InlineData(false, "metrics", false)]
    [InlineData(false, "git-credential", false)]
    public void TheCallsOfTheGateway_GoToV2_OnlyWhereTheEngineActs_AndOnlyForTheseSixVerbs(bool v2, string verb, bool onV2) =>
        Assert.Equal(onV2, CallsOf(v2).Calls.OnV2(verb));

    [Fact]
    public async Task OnV2_ACallOfTheGateway_IsAnsweredWithoutARuleScript()
    {
        var (calls, net, _) = CallsOf(true);
        net.Refs.Add($"refs/factory/{Name}/revoked/alice/2");
        var (code, output) = await calls.RunAsync("revocations", "{}", TimeSpan.FromSeconds(5));   // the Runner of this test points at no script: a rule script could not answer
        Assert.Equal(0, code);
        Assert.Equal(2, (int)JsonNode.Parse(output)!["revoked"]!["alice"]!);
    }

    [Fact]
    public async Task RevocationService_OnV2_ReadsTheGenerationsFromTheRefs_AndRaisesThemBeforeItIsLoaded()
    {
        var (calls, net, _) = CallsOf(true);
        net.Refs.Add($"refs/factory/{Name}/revoked/alice/2"); net.Refs.Add($"refs/factory/{Name}/revoked/bob/1");
        var gens = new Revocations();
        var service = new RevocationService(new RevocationOptions { GitHubConfigured = true, AttemptSeconds = 5, AlarmSeconds = 60 }, true, "secret-for-the-test", false, true, gens, calls.RunAsync, new RuntimeNonces(), _ => false) { FirstPauseSeconds = 0.05 };
        Assert.False(service.IsLoaded);
        await service.StartAsync(CancellationToken.None);
        await service.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((2, 1, 0), (gens.Of("alice"), gens.Of("bob"), gens.Of("carol")));
    }

    [Fact]
    public async Task RevocationService_OnV2_IsNotLoaded_WhileGitHubFails_AndLoadsWhenItAnswers()
    {
        var (calls, net, _) = CallsOf(true);
        net.Refs.Add($"refs/factory/{Name}/revoked/alice/2");
        var fail = true; var inner = net.Script;
        net.Script = (req, _) => fail ? Net.Json(500, "{}") : null;
        var gens = new Revocations();
        var service = new RevocationService(new RevocationOptions { GitHubConfigured = true, AttemptSeconds = 5, AlarmSeconds = 60 }, true, "secret-for-the-test", false, true, gens, calls.RunAsync, new RuntimeNonces(), _ => false) { FirstPauseSeconds = 0.05 };
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(400);
        Assert.False(service.IsLoaded); Assert.Equal(0, gens.Of("alice"));   // never fail open: nothing is loaded, no generation is guessed
        Assert.True(service.FailedAttempts >= 1);
        fail = false;
        await service.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, gens.Of("alice"));
    }

    [Fact]
    public async Task RevocationService_OnV2_RevokeWritesTheRefFirst_ThenRaisesTheGeneration_AndEndsTheStream()
    {
        var (calls, net, _) = CallsOf(true);
        var gens = new Revocations(); var closed = new List<string>();
        var service = new RevocationService(new RevocationOptions { SkipRevocations = false, GitHubConfigured = true }, true, "secret-for-the-test", false, true, gens, calls.RunAsync, new RuntimeNonces(), n => { closed.Add(n); return true; });
        await service.StartAsync(CancellationToken.None); await service.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((true, 1, true), await service.RevokeAsync("alice", "operator"));
        Assert.Equal(1, gens.Of("alice")); Assert.Equal(["alice"], closed);
        Assert.Contains($"refs/factory/{Name}/revoked/alice/1", net.Refs);
        Assert.Equal((true, 2, true), await service.RevokeAsync("alice", "operator"));
        Assert.Equal([$"refs/factory/{Name}/revoked/alice/2"], net.Refs.ToArray());   // one ref per name
    }

    [Fact]
    public async Task RevocationService_OnV2_ARevocationGitHubDoesNotKeep_IsNotAnyRevocation_AtAll()
    {
        var (calls, net, _) = CallsOf(true);
        var gens = new Revocations(); var closed = new List<string>();
        var service = new RevocationService(new RevocationOptions { GitHubConfigured = true }, true, "secret-for-the-test", false, true, gens, calls.RunAsync, new RuntimeNonces(), n => { closed.Add(n); return true; });
        await service.StartAsync(CancellationToken.None); await service.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var failure in new Func<HttpRequestMessage, string, HttpResponseMessage?>[]
        {
            (req, _) => req.Method == HttpMethod.Post ? Net.Json(500, "{}") : null,                   // the write is refused
            (req, _) => req.Method == HttpMethod.Post ? Net.Json(403, "{\"message\":\"no\"}") : null,
            (req, _) => req.Method == HttpMethod.Post ? Net.Json(201, "{}") : null,                    // answered and not kept
            (req, _) => throw new HttpRequestException("down"),
        })
        {
            net.Script = failure;
            Assert.Equal((false, 0, false), await service.RevokeAsync("alice", "operator"));
            Assert.Equal(0, gens.Of("alice")); Assert.Empty(closed);
        }
    }

    [Fact]
    public async Task OperatorReleaseLoader_OnV2_ReadsTheDaysReleases_AndKeepsTheValidOnes()
    {
        var (calls, net, _) = CallsOf(true);
        var today = OperatorReleaseLoader.Day(DateTime.UtcNow);
        net.Refs.Add($"refs/factory/{Name}/operator-release/{today}/101/1.0.5"); net.Refs.Add($"refs/factory/{Name}/operator-release/{today}/102/1.0.6");
        var added = new List<(int Pr, string Version)>();
        var loader = new OperatorReleaseLoader(true, false, calls.RunAsync, (_, pr, v) => { lock (added) added.Add((pr, v)); }) { PauseSeconds = 0.05 };
        await loader.StartAsync(CancellationToken.None); await loader.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([(101, "1.0.5"), (102, "1.0.6")], added.OrderBy(x => x.Pr).ToArray());
    }

    // ---- the route table ----
    [Fact]
    public async Task TheRoutes_RegisterTheSixVerbsOfTheGroup_WhereTheServicesAreComposed_AndNothingWhereTheyAreNot()
    {
        var (verbs, net) = Make(); net.Refs.Add($"refs/factory/{Name}/revoked/alice/2");
        var routes = new V2VerbRoutes();
        ServiceRoutes.Register(routes, new ServiceCollection().BuildServiceProvider());
        Assert.Equal(0, routes.Count);
        var composed = new ServiceCollection().AddSingleton(verbs).BuildServiceProvider();
        ServiceRoutes.Register(routes, composed);
        Assert.Equal(ServiceRoutes.Verbs.Order().ToArray(), routes.Verbs.Order().ToArray());
        Assert.Equal(ServiceVerbs.Verbs, ServiceRoutes.Verbs);
        Assert.True(routes.TryGet("revocations", out var handler));
        var answer = await handler("{}", TimeSpan.FromSeconds(5));
        Assert.Equal(0, answer.Code); Assert.Equal(2, (int)JsonNode.Parse(answer.Output)!["revoked"]!["alice"]!);
        Assert.False(routes.TryGet("get-item", out _));
    }

    // ---- the refusals before a verb reaches v2 ----
    static string Source(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gateway", "Program.cs"))) return File.ReadAllText(Path.Combine(d.FullName, relative));
        throw new InvalidOperationException("the repository root was not found");
    }

    [Fact]
    public void TheVerbPipeline_RefusesBeforeItCallsTheEngine_SoNoRouteOfV2CanBeReachedAround()
    {
        var pipeline = Source("gateway/Verbs/VerbPipeline.cs");
        var engine = pipeline.IndexOf("engine.RunAsync(", StringComparison.Ordinal);
        Assert.True(engine > 0);
        foreach (var refusal in new[] { "VerbPolicy.AuthorizeVerb(", "verb == \"git-credential\" && who.Kind == \"item\" && !(ownerMode && plant.OwnsAccepted(", "VerbPolicy.UseEndpointReason(verb)", "GitHubBudget.EssentialVerbs.Contains(verb)" })
        {
            var at = pipeline.IndexOf(refusal, StringComparison.Ordinal);
            Assert.True(at > 0 && at < engine, $"{refusal} must come before the engine is called");
        }
    }

    [Theory]
    [InlineData("revocations", "use POST /api/v1/workers/{name}/revoke")]
    [InlineData("revoke-worker", "use POST /api/v1/workers/{name}/revoke")]
    [InlineData("operator-releases", "use POST /api/v1/operator/record-release")]
    [InlineData("operator-release", "use POST /api/v1/operator/record-release")]
    public void TheVerbsWithAnEndpoint_AreRefusedToEveryCallerOfTheVerbRoute_OnV2AsOnV1(string verb, string reason) => Assert.Equal(reason, VerbPolicy.UseEndpointReason(verb));

    [Fact]
    public void TheCallsOfTheGatewayItself_AllGoThroughServiceVerbCalls()
    {
        Assert.Contains("sp.GetRequiredService<ServiceVerbCalls>().RunAsync, runtimeNonces", Source("gateway/Composition/GatewayServiceCollectionExtensions.cs"));
        Assert.Contains("sp.GetRequiredService<ServiceVerbCalls>().RunAsync, sp.GetRequiredService<Plant>().AddRelease", Source("gateway/Composition/GatewayServiceCollectionExtensions.cs"));
        foreach (var file in new[] { "gateway/Endpoints/OperatorReleaseEndpoints.cs", "gateway/Endpoints/MetricsEndpoints.cs" })
        {
            var text = Source(file);
            Assert.Contains("ServiceVerbCalls verbs", text); Assert.DoesNotContain("Runner runner", text); Assert.DoesNotContain("runner.RunAsync", text);
        }
    }
}
