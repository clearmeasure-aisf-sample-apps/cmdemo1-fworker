using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.7 (#359): the service verbs on v2 answer exactly as their rule scripts do. Each case runs the real rule script (pwsh plugin/scripts/factory.ps1 --server VERB, the PowerShell of the PATH) and the C# verb
/// (ServiceVerbs) against the same fake GitHub (tests/FakeGitHub, built beside these tests) with the same input, and compares the answer (every field, the exit code), the calls each made to GitHub (the method and path of
/// every request, in order) and what each left in GitHub (the refs). The refusals are the injected faults of the fake (a 404, a refused permission, a rate limit, a 500, a 422 that is no "already exists", a write that is
/// answered and not kept) and the usage errors of the input. Where pwsh or the fake is not built on the machine the cases return at once (the CI shards that run these tests have both).
/// </summary>
public sealed class ServiceVerbParityTests : IClassFixture<ServiceVerbParityTests.Fake>
{
    public sealed class Fake : IDisposable
    {
        public readonly string Dir = Directory.CreateTempSubdirectory("service-verb-parity-").FullName;
        public readonly string Root = FindRoot();
        public readonly int Port;
        public readonly HttpClient Http;
        public readonly string? Pwsh = OnPath("pwsh");
        readonly Process? process;
        public bool Available => process is { HasExited: false } && Pwsh is not null;

        public Fake()
        {
            var dll = new[] { "Release", "Debug" }.Select(c => Path.Combine(Root, "tests", "FakeGitHub", "bin", c, "net10.0", "FakeGitHub.dll")).FirstOrDefault(File.Exists);
            using (var probe = new TcpListener(System.Net.IPAddress.Loopback, 0)) { probe.Start(); Port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; }
            Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}/") };
            if (dll is null || Pwsh is null) return;
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(dll); psi.Environment["FAKE_PORT"] = Port.ToString();
            process = Process.Start(psi);
            process!.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            for (var i = 0; i < 100; i++)
            {
                try { if (Http.GetAsync("_fake/log").GetAwaiter().GetResult().IsSuccessStatusCode) return; } catch (HttpRequestException) { }
                Thread.Sleep(100);
            }
        }

        public void Dispose()
        {
            try { process?.Kill(true); } catch { }
            try { Directory.Delete(Dir, true); } catch { }
        }

        static string? OnPath(string exe) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);

        static string FindRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "gateway", "Program.cs")) && File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) return d.FullName;
            throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
        }
    }

    const string Owner = "clearmeasure-aisf-sample-apps", Repo = "cmdemo1-workorders", Name = "factory-worker";
    readonly Fake fake;
    public ServiceVerbParityTests(Fake fake) => this.fake = fake;

    // ---- the fake ----
    async Task<JsonNode?> Control(string what, JsonNode? body = null)
    {
        var resp = await fake.Http.PostAsync("_fake/" + what, new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        return text.Length > 0 ? JsonNode.Parse(text) : null;
    }
    async Task<JsonNode?> Rest(HttpMethod method, string path, JsonNode? body = null)
    {
        using var req = new HttpRequestMessage(method, $"repos/{Owner}/{Repo}/{path}") { Content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        var resp = await fake.Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return text.Length > 0 && text.TrimStart()[0] is '{' or '[' ? JsonNode.Parse(text) : null;
    }
    Task Reset() => Control("reset");
    async Task<List<string>> Log() => ((await fake.Http.GetStringAsync("_fake/log")) is { } t ? JsonNode.Parse(t)!.AsArray().Select(x => x!.ToString()).ToList() : []);
    async Task<List<string>> Refs() => ((JsonArray)(JsonNode.Parse(await fake.Http.GetStringAsync("_fake/state"))!["refs"]!)).Select(x => x!.ToString()).Order(StringComparer.Ordinal).ToList();
    async Task Ref(string name) => await Rest(HttpMethod.Post, "git/refs", new JsonObject { ["ref"] = name, ["sha"] = "0123456789abcdef0123456789abcdef01234567" });
    Task Fault(string match, int status, string body = "{\"message\":\"injected fault\"}", int count = 1) => Control("fault", new JsonObject { ["match"] = match, ["status"] = status, ["body"] = body, ["count"] = count });

    // ---- the two sides ----
    sealed record Ran(int Exit, JsonNode? Json, List<string> Calls, List<string> Refs);

    async Task<Ran> V1(string verb, string stdin, string extraEnv = "")
    {
        var before = (await Log()).Count;
        var psi = new ProcessStartInfo(fake.Pwsh!) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = fake.Root };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(fake.Root, "plugin", "scripts", "factory.ps1"), "--server", verb }) psi.ArgumentList.Add(a);
        RunnerEnv.Apply(psi);
        foreach (var kv in Env(extraEnv)) psi.Environment[kv.Key] = kv.Value;
        psi.Environment["FACTORY_DATA_DIR"] = Path.Combine(fake.Dir, "data");
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(); var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(); var output = (await outTask).Trim(); await errTask;
        return new Ran(p.ExitCode, output.Length > 0 ? JsonNode.Parse(output) : null, (await Log()).Skip(before).ToList(), await Refs());
    }

    async Task<Ran> V2(string verb, string stdin, string extraEnv = "")
    {
        var before = (await Log()).Count;
        var env = Env(extraEnv);
        Func<string, string?> get = n => env.TryGetValue(n, out var v) ? v : null;
        var gh = new ServiceGitHub(InstanceConfig.Load(Path.Combine(fake.Root, "config", "instance.json"), get), get, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{fake.Port}/") }) { Pause = _ => Task.CompletedTask, Keyring = (_, _) => null };
        var answer = await new ServiceVerbs(gh, null).RunAsync(verb, stdin, TimeSpan.FromSeconds(60));
        return new Ran(answer.Code, JsonNode.Parse(answer.Output), (await Log()).Skip(before).ToList(), await Refs());
    }

    // GH_TOKEN of the rule scripts' environment, the fake as GitHub; "FACTORY_AUTHOR_APP=1" style extras as "A=1;B=2"
    Dictionary<string, string> Env(string extra)
    {
        var env = new Dictionary<string, string> { ["GH_TOKEN"] = "test-token", ["GITHUB_API_URL"] = $"http://127.0.0.1:{fake.Port}", ["FACTORY_FAKE_APPS"] = "1", ["FACTORY_ENVIRONMENT"] = "tdd" };
        foreach (var part in extra.Split(';', StringSplitOptions.RemoveEmptyEntries)) { var kv = part.Split('=', 2); env[kv[0]] = kv[1]; }
        return env;
    }

    // ---- the comparison ----
    static readonly Regex UsDate = new(@"^\d\d/\d\d/\d{4} \d\d:\d\d:\d\d$");

    // the answer as a canonical string: the keys sorted, the rate report of the script (taken out by the gateway's Runner) removed, and the fields that hold the time of the run masked
    static string Canon(JsonNode? node, string[] mask)
    {
        static JsonNode? Walk(JsonNode? n, string[] mask, string? key) => n switch
        {
            // the text of an error nobody expected is the runtime's (PowerShell's or .NET's); its code, exit and everything else are compared
            JsonObject o => new JsonObject(o.Where(kv => kv.Key != "_github").OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => KeyValuePair.Create(kv.Key, kv.Key == "reason" && o["code"]?.ToString() == "internal-error" ? JsonValue.Create("<reason>") : Walk(kv.Value, mask, kv.Key)))),
            JsonArray a => new JsonArray(a.Select(x => Walk(x, mask, null)).ToArray()),
            JsonValue v when key is not null && mask.Contains(key) => JsonValue.Create("<" + key + ">"),
            JsonValue v => Number(v),
            _ => null,
        };
        return Walk(node, mask, null)?.ToJsonString() ?? "null";
    }

    // where the two answers first differ, with the text around it
    static string Difference(string a, string b)
    {
        var i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        string Around(string t) => t[Math.Max(0, i - 120)..Math.Min(t.Length, i + 240)];
        return $"the answers differ at character {i}\nthe rule script: ...{Around(a)}...\nv2:              ...{Around(b)}...";
    }

    // a whole number is a whole number, whatever the script wrote (330 or 330.0)
    static JsonNode? Number(JsonValue v)
    {
        var t = v.ToJsonString();
        return decimal.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d == decimal.Truncate(d) && Math.Abs(d) < 1e15m ? JsonValue.Create((long)d) : JsonNode.Parse(t);
    }

    // readOnly: the verb writes nothing, so both sides read the one state (the comments carry the time they were written at); faults are armed again before each side
    async Task Same(string verb, string stdin, Func<Task>? arrange = null, string[]? mask = null, string extraEnv = "", Action<Ran>? also = null, bool readOnly = false, Func<Task>? faults = null)
    {
        if (!fake.Available) return;
        mask = [.. mask ?? [], "resetAt"];   // (a rate limit's reset time is the clock's, to the second)
        await Reset(); if (arrange != null) await arrange();
        if (faults != null) await faults();
        var v1 = await V1(verb, stdin, extraEnv);
        var v1Refs = v1.Refs;
        if (!readOnly) { await Reset(); if (arrange != null) await arrange(); }
        if (faults != null) await faults();
        var v2 = await V2(verb, stdin, extraEnv);
        Assert.Equal(v1.Exit, v2.Exit);
        var (c1, c2) = (Canon(v1.Json, mask), Canon(v2.Json, mask));
        Assert.True(c1 == c2, Difference(c1, c2));
        Assert.Equal(v1.Calls, v2.Calls);
        Assert.Equal(v1Refs, v2.Refs);
        also?.Invoke(v2);
    }

    // ---- revocations ----
    [Fact]
    public Task Revocations_None() => Same("revocations", "{}");

    [Fact]
    public Task Revocations_TheHighestGenerationOfEachName_AndNoOtherRef() => Same("revocations", "{}", async () =>
    {
        foreach (var r in new[] { "alice/1", "alice/3", "bob/2", "alice/x", "bob/2/9", "carol/1234567890", "has space/1" }) await Ref($"refs/factory/{Name}/revoked/{r}");
        await Ref($"refs/factory/{Name}/operator-release/20261026/7/1.0.1");   // another tree
        await Ref("refs/factory/other-instance/revoked/dave/4");
    }, also: v2 => Assert.Equal(3, (int)v2.Json!["revoked"]!["alice"]!));

    [Fact]
    public Task Revocations_AMatchingRefsThatPages_IsReadToItsEnd() => Same("revocations", "{}", async () =>
    {
        await Control("ref-page", new JsonObject { ["size"] = 2 });
        foreach (var r in new[] { "a/1", "b/2", "c/3", "d/4", "e/5" }) await Ref($"refs/factory/{Name}/revoked/{r}");
    });

    [Fact]
    public async Task Revocations_NamesThatDifferByCaseOnly_AreNotMerged_TheOneDeliberateDifference()
    {
        if (!fake.Available) return;
        await Reset();
        foreach (var r in new[] { "Alice/3", "alice/1" }) await Ref($"refs/factory/{Name}/revoked/{r}");
        var v1 = await V1("revocations", "{}"); var v2 = await V2("revocations", "{}");
        // v1 made a PowerShell hashtable of them, which does not tell the two names apart (one entry, the first spelling); the generations of the gateway are per exact name
        Assert.Single(v1.Json!["revoked"]!.AsObject());
        Assert.Equal(["Alice", "alice"], v2.Json!["revoked"]!.AsObject().Select(kv => kv.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(3, (int)v2.Json["revoked"]!["Alice"]!); Assert.Equal(1, (int)v2.Json["revoked"]!["alice"]!);
    }

    [Theory]
    [InlineData("GET .*matching-refs.*", 404)]    // not found
    [InlineData("GET .*matching-refs.*", 500)]    // GitHub error
    [InlineData("GET .*matching-refs.*", 403)]    // refused permission (the body names no rate limit)
    public Task Revocations_GitHubRefuses(string match, int status) => Same("revocations", "{}", () => Fault(match, status));

    [Fact]
    public Task Revocations_RateLimited_IsATry_AgainLater() => Same("revocations", "{}", () => Fault("GET .*matching-refs.*", 403, "{\"message\":\"API rate limit exceeded\"}"));

    [Fact]
    public Task Revocations_ABadGateway_IsTriedThreeTimes() => Same("revocations", "{}", () => Fault("GET .*matching-refs.*", 502, count: 5));

    // ---- revoke-worker ----
    [Fact]
    public Task RevokeWorker_FirstGeneration() => Same("revoke-worker", "{\"name\":\"alice\",\"generation\":1}");

    [Fact]
    public Task RevokeWorker_ANewerGeneration_RemovesTheOlderOnes() => Same("revoke-worker", "{\"name\":\"alice\",\"generation\":3}", async () =>
    {
        foreach (var g in new[] { 1, 2 }) await Ref($"refs/factory/{Name}/revoked/alice/{g}");
        await Ref($"refs/factory/{Name}/revoked/bob/1");
    }, also: v2 => { Assert.Equal([1, 2], v2.Json!["removed"]!.AsArray().Select(x => (int)x!).ToArray()); Assert.Empty(v2.Json["left"]!.AsArray()); });

    [Fact]
    public Task RevokeWorker_TheSameGenerationAgain_IsIdempotent() => Same("revoke-worker", "{\"name\":\"alice\",\"generation\":2}", () => Ref($"refs/factory/{Name}/revoked/alice/2"));

    [Fact]
    public Task RevokeWorker_AnOlderGeneration_LeavesTheNewerOne() => Same("revoke-worker", "{\"name\":\"alice\",\"generation\":1}", () => Ref($"refs/factory/{Name}/revoked/alice/5"));

    [Fact]
    public Task RevokeWorker_AnOlderGenerationThatCannotBeRemoved_IsLeft() => Same("revoke-worker", "{\"name\":\"alice\",\"generation\":3}", async () =>
    {
        await Ref($"refs/factory/{Name}/revoked/alice/1");
        await Fault("DELETE .*", 500);
    }, also: v2 => Assert.Equal([1], v2.Json!["left"]!.AsArray().Select(x => (int)x!).ToArray()));

    [Theory]
    [InlineData("{}")]                                              // missing name
    [InlineData("{\"name\":\"alice\"}")]                             // missing generation
    [InlineData("{\"name\":\"\",\"generation\":1}")]                 // empty name
    [InlineData("{\"name\":\"bad name\",\"generation\":1}")]         // not a worker name
    [InlineData("{\"name\":\"a/b\",\"generation\":1}")]              // a path in the name
    [InlineData("{\"name\":\"alice\",\"generation\":0}")]            // a generation below 1
    [InlineData("{\"name\":\"alice\",\"generation\":-3}")]
    [InlineData("{\"name\":\"alice\",\"generation\":\"abc\"}")]      // not a number: an internal error of the script
    [InlineData("{\"Name\":\"alice\",\"GENERATION\":\"2\"}")]        // names are not case sensitive; text that is a number counts
    [InlineData("{\"name\":null,\"generation\":1}")]                 // null is absent
    [InlineData("[1,2]")]                                            // not an object
    public Task RevokeWorker_TheUsageOfTheInput(string stdin) => Same("revoke-worker", stdin);

    [Theory]
    [InlineData("GET .*git/ref/heads/master", 404)]
    [InlineData("POST .*git/refs", 403)]
    [InlineData("POST .*git/refs", 500)]
    [InlineData("POST .*git/refs", 404)]
    public Task RevokeWorker_GitHubRefuses(string match, int status) => Same("revoke-worker", "{\"name\":\"alice\",\"generation\":1}", () => Fault(match, status));

    [Fact]
    public Task RevokeWorker_A422ThatIsNotAlreadyExists_IsRefused() => Same("revoke-worker", "{\"name\":\"alice\",\"generation\":1}", () => Fault("POST .*git/refs", 422, "{\"message\":\"Validation Failed\"}"));

    [Fact]
    public Task RevokeWorker_A422AlreadyExists_IsAccepted() => Same("revoke-worker", "{\"name\":\"alice\",\"generation\":1}", () => Fault("POST .*git/refs", 422, "{\"message\":\"Reference already exists\"}"));

    [Fact]
    public Task RevokeWorker_ARefThatIsAnsweredAndNotKept_IsNotRecorded() => Same("revoke-worker", "{\"name\":\"alice\",\"generation\":1}", () => Fault("POST .*git/refs", 201, "{\"ref\":\"x\"}"));

    [Fact]
    public Task RevokeWorker_RateLimited() => Same("revoke-worker", "{\"name\":\"alice\",\"generation\":1}", () => Fault("POST .*git/refs", 429, "{\"message\":\"secondary rate limit\"}"));

    // ---- operator-releases / operator-release ----
    [Fact]
    public Task OperatorReleases_OnePerPullRequest_OfThatDay() => Same("operator-releases", "{\"day\":\"20261026\"}", async () =>
    {
        foreach (var r in new[] { "20261026/101/1.0.40", "20261026/102/1.0.41", "20261026/101/1.0.99", "20261026/103/bad", "20261026/104/1.0", "20261026/105/1.2.3.4.5", "20261027/106/1.0.1" }) await Ref($"refs/factory/{Name}/operator-release/{r}");
    }, also: v2 => Assert.NotEmpty(v2.Json!["releases"]!.AsArray()));

    [Fact]
    public Task OperatorReleases_None() => Same("operator-releases", "{\"day\":\"20261026\"}");

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"day\":\"2026-10-26\"}")]
    [InlineData("{\"day\":\"2026102\"}")]
    public Task OperatorReleases_TheUsageOfTheInput(string stdin) => Same("operator-releases", stdin);

    [Theory]
    [InlineData("GET .*matching-refs.*", 404)]
    [InlineData("GET .*matching-refs.*", 500)]
    [InlineData("GET .*matching-refs.*", 403)]
    public Task OperatorReleases_GitHubRefuses(string match, int status) => Same("operator-releases", "{\"day\":\"20261026\"}", () => Fault(match, status));

    [Fact]
    public Task OperatorRelease_RecordsARelease() => Same("operator-release", "{\"day\":\"20261026\",\"pr\":101,\"version\":\"1.0.40\"}");

    [Fact]
    public Task OperatorRelease_IsIdempotent() => Same("operator-release", "{\"day\":\"20261026\",\"pr\":101,\"version\":\"1.0.40\"}", () => Ref($"refs/factory/{Name}/operator-release/20261026/101/1.0.40"));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"day\":\"20261026\"}")]
    [InlineData("{\"day\":\"20261026\",\"pr\":101}")]
    [InlineData("{\"day\":\"2026\",\"pr\":101,\"version\":\"1.0.1\"}")]
    [InlineData("{\"day\":\"20261026\",\"pr\":0,\"version\":\"1.0.1\"}")]
    [InlineData("{\"day\":\"20261026\",\"pr\":-5,\"version\":\"1.0.1\"}")]
    [InlineData("{\"day\":\"20261026\",\"pr\":\"x\",\"version\":\"1.0.1\"}")]       // not a number: an internal error of the script
    [InlineData("{\"day\":\"20261026\",\"pr\":101,\"version\":\"v1\"}")]
    [InlineData("{\"day\":\"20261026\",\"pr\":101,\"version\":\"1.0.1.2.3\"}")]
    [InlineData("{\"day\":\"20261026\",\"pr\":101,\"version\":\"1\"}")]
    [InlineData("{\"day\":\"20261026\",\"pr\":\"101\",\"version\":\"1.0.1\"}")]       // text that is a number counts
    [InlineData("{\"day\":\"20261026\",\"pr\":101.5,\"version\":\"1.0.1\"}")]         // rounded to the even whole: 102
    public Task OperatorRelease_TheUsageOfTheInput(string stdin) => Same("operator-release", stdin);

    [Theory]
    [InlineData("GET .*git/ref/heads/master", 404)]
    [InlineData("POST .*git/refs", 403)]
    [InlineData("POST .*git/refs", 500)]
    public Task OperatorRelease_GitHubRefuses(string match, int status) => Same("operator-release", "{\"day\":\"20261026\",\"pr\":101,\"version\":\"1.0.40\"}", () => Fault(match, status));

    [Fact]
    public Task OperatorRelease_A422ThatIsNotAlreadyExists_IsRefused() => Same("operator-release", "{\"day\":\"20261026\",\"pr\":101,\"version\":\"1.0.40\"}", () => Fault("POST .*git/refs", 422, "{\"message\":\"Validation Failed\"}"));

    // ---- git-credential ----
    [Fact]
    public Task GitCredential_MintsAOneRepositoryContentsWriteToken() => Same("git-credential", "{\"item\":5}", mask: ["expires_at"], also: v2 =>
    {
        Assert.Matches(UsDate, v2.Json!["expires_at"]!.ToString());
        Assert.Equal($"{Owner}/{Repo}", v2.Json["repository"]!.ToString());
        Assert.Equal("x-access-token", v2.Json["username"]!.ToString());
    });

    [Fact]
    public async Task GitCredential_AsksGitHubForThisRepositoryAndContentsWriteOnly()
    {
        if (!fake.Available) return;
        await Reset();
        var state = async () => ((JsonArray)JsonNode.Parse(await fake.Http.GetStringAsync("_fake/state"))!["scopedMints"]!).Select(x => x!.ToString()).ToList();   // (the fake keeps them across a reset)
        var before = (await state()).Count;
        await V1("git-credential", "{\"item\":5}"); var afterV1 = await state();
        await V2("git-credential", "{\"item\":5}"); var afterV2 = await state();
        Assert.Equal(before + 2, afterV2.Count);
        Assert.Equal(Canon(JsonNode.Parse(afterV1[^1]), []), Canon(JsonNode.Parse(afterV2[^1]), []));
        Assert.Equal(Canon(JsonNode.Parse(afterV2[^1]), []), Canon(JsonNode.Parse($"{{\"repositories\":[\"{Repo}\"],\"permissions\":{{\"contents\":\"write\"}}}}"), []));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"item\":\"\"}")]
    [InlineData("{\"item\":\"x\"}")]
    [InlineData("{\"item\":null}")]
    public Task GitCredential_TheUsageOfTheInput(string stdin) => Same("git-credential", stdin);

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    [InlineData(500)]
    public Task GitCredential_GitHubRefusesTheMint(int status) => Same("git-credential", "{\"item\":5}", () => Fault("POST .*access_tokens", status));

    // ---- metrics ----
    const string StateMarker = $"<!-- factory-state:{Name} v1 -->";

    async Task<int> BoardItem(string title, string[] labels, string column, JsonObject? state, params string[] journal)
    {
        var n = (int)(await Control("issue", new JsonObject { ["title"] = title, ["labels"] = new JsonArray(labels.Select(l => (JsonNode)l).ToArray()), ["column"] = column }))!["number"]!;
        foreach (var j in journal) await Rest(HttpMethod.Post, $"issues/{n}/comments", new JsonObject { ["body"] = $"\U0001F916 {Name}: x\n\n<!-- factory-journal:{Name} -->\n<!-- plant event={j} -->" });
        if (state is not null) await Rest(HttpMethod.Post, $"issues/{n}/comments", new JsonObject { ["body"] = $"{StateMarker}\nstate of #{n}\n\n```json\n{state.ToJsonString()}\n```\n" });
        return n;
    }

    static JsonObject FlowState(string at0) => new()
    {
        ["schema"] = 1,
        ["dispatched_at"] = new JsonObject { ["Technical Design"] = at0, ["Development"] = "2026-10-20T10:10:00Z", ["Functional Testing"] = "2026-10-20T10:40:00Z" },
        ["flow"] = new JsonArray(new JsonObject { ["c"] = "Technical Design", ["at"] = at0 }, new JsonObject { ["c"] = "Development", ["at"] = "2026-10-20T10:10:00Z" }, new JsonObject { ["c"] = "Functional Testing", ["at"] = "2026-10-20T10:40:00Z" }),
        ["merge"] = new JsonObject { ["pr"] = 77, ["merged_at"] = "2026-10-20T11:00:00Z", ["done_at"] = "2026-10-20T11:05:30Z" },
    };

    static readonly string[] MetricsMask = ["computedAt", "since"];   // the time of the run, and a duration counted back from it (MetricsVerb.Since is tested on its own)

    int firstItem;   // the number of the first item ArrangeBoard made (the fake counts on across its resets)

    async Task ArrangeBoard()
    {
        firstItem = await BoardItem("a", ["factory worker"], "Done", FlowState("2026-10-20T10:00:00Z"), "matched key=k1", "offered key=k2", "assigned key=owner|w1");
        await BoardItem("b", ["factory worker", "Auto Merge"], "Development", new JsonObject { ["schema"] = 1, ["dispatched_at"] = new JsonObject { ["Development"] = "2026-10-21T09:00:00Z" } }, "matched key=k1");
        await BoardItem("c", ["factory worker"], "Functional Testing", FlowState("2026-10-19T10:00:00.500Z"), "assigned key=helper|w2", "matched key=k9");
        await BoardItem("not ours", ["something else"], "Development", FlowState("2026-10-20T10:00:00Z"), "matched key=k1");
        await BoardItem("no journal", ["factory worker"], "Todo", null);
        await BoardItem("odd", ["factory worker"], "Todo", new JsonObject { ["schema"] = 1, ["flow"] = new JsonArray(new JsonObject { ["c"] = "Development" }, new JsonObject { ["at"] = "2026-10-20T10:00:00Z" }) }, "matched key=k1");
    }

    [Fact]
    public Task Metrics_TheCycleTimesOfTheBoardsItems() => Same("metrics", "{\"since\":\"2020-01-01T00:00:00Z\"}", ArrangeBoard, MetricsMask, readOnly: true, also: v2 =>
    {
        Assert.True(v2.Json!["items"]!.AsArray().Count >= 3);
        Assert.Single(v2.Json["unreadable"]!.AsArray());   // the item whose flow entries lack a time: unreadable in the script, so in v2
        Assert.Equal("seconds", v2.Json["unit"]!.ToString());
    });

    [Theory]
    [InlineData("{\"since\":\"2020-01-01T00:00:00Z\",\"max\":2}")]    // the newest two
    [InlineData("{\"since\":\"2020-01-01\",\"max\":0}")]               // at least one
    [InlineData("{\"since\":\"2020-01-01\",\"max\":\"1000\"}")]        // at most 300
    [InlineData("{\"since\":\"2020-01-01 00:00:00\"}")]
    [InlineData("{\"since\":\"30d\"}")]                                // none of them is in the last 30 days
    [InlineData("{\"since\":\"  24 h \"}")]
    [InlineData("{\"since\":\"9999d\"}")]                              // never more than 90 days back
    [InlineData("{\"since\":\"2000-01-01\"}")]
    public Task Metrics_TheQuery(string stdin) => Same("metrics", stdin, ArrangeBoard, MetricsMask, readOnly: true, also: v2 => Assert.Equal("seconds", v2.Json!["unit"]!.ToString()));

    [Theory]
    [InlineData("{\"since\":\"yesterday\"}")]
    [InlineData("{\"since\":\"7D\"}")]                                 // case matters for a duration
    [InlineData("{\"since\":\"12345d\"}")]
    [InlineData("{\"since\":\"2020-13-45\"}")]
    [InlineData("{\"since\":\"2020-01-01\",\"max\":\"x\"}")]           // an internal error of the script
    public Task Metrics_TheUsageOfTheInput(string stdin) => Same("metrics", stdin, ArrangeBoard, MetricsMask, readOnly: true);

    [Fact]
    public Task Metrics_AnItemWhoseCommentsCannotBeRead_IsUnreadable_NotAFailure() => Same("metrics", "{\"since\":\"2020-01-01\"}", ArrangeBoard, MetricsMask, readOnly: true, faults: () => Fault($"GET .*issues/{firstItem}/comments", 500), also: v2 => Assert.Contains(firstItem, v2.Json!["unreadable"]!.AsArray().Select(x => (int)x!)));

    [Fact]
    public Task Metrics_TheBoardCannotBeRead() => Same("metrics", "{\"since\":\"2020-01-01\"}", ArrangeBoard, MetricsMask, readOnly: true, faults: () => Fault("POST .*graphql", 500));

    [Fact]
    public Task Metrics_TheBoardIsRateLimited() => Same("metrics", "{\"since\":\"2020-01-01\"}", ArrangeBoard, MetricsMask, readOnly: true, faults: () => Fault("POST .*graphql", 200, "{\"errors\":[{\"type\":\"RATE_LIMITED\",\"message\":\"API rate limit exceeded\"}]}"));

    [Fact]
    public Task Metrics_TheBoardAnswersAnError() => Same("metrics", "{\"since\":\"2020-01-01\"}", ArrangeBoard, MetricsMask, readOnly: true, faults: () => Fault("POST .*graphql", 200, "{\"errors\":[{\"type\":\"NOT_FOUND\",\"message\":\"Could not resolve to an Organization\"}]}"));

    [Fact]
    public Task Metrics_ScopedToTheEnvironment_SeesOnlyItsOwnItems() => Same("metrics", "{\"since\":\"2020-01-01\"}", async () =>
    {
        await BoardItem("tdd one", ["factory worker", "env:tdd"], "Done", FlowState("2026-10-20T10:00:00Z"), "matched key=k1");
        await BoardItem("uat one", ["factory worker", "env:uat"], "Done", FlowState("2026-10-20T10:00:00Z"), "matched key=k1");
        await BoardItem("both", ["factory worker", "env:tdd", "env:uat"], "Done", FlowState("2026-10-20T10:00:00Z"), "matched key=k1");
        await BoardItem("none", ["factory worker"], "Done", FlowState("2026-10-20T10:00:00Z"), "matched key=k1");
    }, MetricsMask, extraEnv: "FACTORY_ENV_SCOPE=1", readOnly: true, also: v2 => Assert.Single(v2.Json!["items"]!.AsArray()));
}
