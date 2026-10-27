using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// Owner mode with messaging, the backlog keeper, the supervisor (two steps an hour) and the operator session, each holding its slot: the
/// gateway the verb route is pinned on (task C7d, pinned from master before the route moved out of Program.cs into VerbEndpoints and VerbPipeline).
/// </summary>
public sealed class C7dVerbGateway : C7cGatewayBase
{
    protected override void Configure(GatewayEnvironment env, JsonObject cfg)
    {
        cfg["operator"] = new JsonObject { ["enabled"] = true, ["allowedNames"] = new JsonArray("operator-session") };
        cfg["keeper"] = new JsonObject { ["enabled"] = true, ["allowedNames"] = new JsonArray("backlog-keeper", "second-keeper") };
        cfg["manager"] = new JsonObject { ["enabled"] = true, ["stepBudgetPerHour"] = 2, ["allowedNames"] = new JsonArray("supervisor", "other-supervisor") };
    }

    protected override void Started(GatewayHost host)
    {
        var plant = host.Services.GetRequiredService<Plant>();
        Assert.True(plant.RegisterOperator("operator-session", "claude-opus-5-5", "low", 0).Ok);
        Assert.True(plant.RegisterSupervisor("supervisor", "claude-opus-5-5", "high", 0).Ok);
        Assert.True(plant.RegisterKeeper("backlog-keeper", "claude-opus-5-5", "medium", 0).Ok);
    }
}

/// <summary>
/// POST /api/v1/verbs/{verb} (task C7d): the answer of every refusal the route makes before a rule script runs, the exit-to-HTTP mapping of the
/// rule script's answer, the input the rule script is given, and the plant's part of a call. The caller matrix, the case variants, the route table and
/// the admission pins cover who may call at all; these cover what the route does with a call that got in. Every expectation was recorded on master.
/// </summary>
public class C7dVerbPinTests(C7dVerbGateway gw) : IClassFixture<C7dVerbGateway>
{
    const int Api = GatewayEnvironment.ApiPort;
    static readonly string Admin = GatewayEnvironment.AdminToken;

    string Secret => gw.Env.TokenSecret;
    Plant Plant => gw.Host.Services.GetRequiredService<Plant>();
    string Owner(int item = 7, string name = "olga") => Auth.MintItemToken(Secret, item, "owner", "a" + item, name, TimeSpan.FromHours(1));

    Task<Answer> Verb(string verb, string? token, string? body = "{}") => gw.Host.SendAsync("POST", "/api/v1/verbs/" + verb, Api, token, body);
    int Calls(string verb) => StubPwsh.Read(gw.Env.StubLog).Count(c => c.Verb == verb);
    static void Is(Answer a, int status, string code, int exit) { Assert.Equal(status, a.Status); Assert.Equal(code, a.Code); Assert.Equal(exit, a.Json?["exit"]?.GetValue<int>()); }

    [Fact]
    public async Task BadVerbName_IsRefusedBeforeTheBodyIsRead_AndNothingRuns()
    {
        if (!StubPwsh.Supported) return;
        foreach (var verb in new[] { "Status", "get_item", "item2", "get-Item", "a.b" })
        {
            var a = await Verb(verb, Admin, "not json");   // bad-verb comes before bad-json
            Is(a, 400, "bad-verb", 2);
            Assert.Equal("{\"ok\":false,\"code\":\"bad-verb\",\"exit\":2}", a.Body);
            Assert.Equal(0, Calls(verb));
        }
    }

    [Fact]
    public async Task BadJson_IsRefused_AndABodyThatIsNotAnObject_IsAnEmptyOne()
    {
        if (!StubPwsh.Supported) return;
        var bad = await Verb("get-item", Admin, "{nope");
        Is(bad, 400, "bad-json", 2);
        Assert.Equal("{\"ok\":false,\"code\":\"bad-json\",\"exit\":2}", bad.Body);
        var before = StubPwsh.Inputs(gw.Env.StubLog, "get-item").Count;
        foreach (var body in new[] { "[1,2]", "3", "null", "\"text\"", "   ", "" })
            Assert.Equal(200, (await Verb("get-item", Admin, body)).Status);
        var inputs = StubPwsh.Inputs(gw.Env.StubLog, "get-item").Skip(before).Select(i => i.Text).ToList();
        Assert.Equal(6, inputs.Count);
        Assert.All(inputs, t => Assert.Equal("{}", t.Trim()));
    }

    [Fact]
    public async Task ExitCodeOfTheRuleScript_IsMappedToTheHttpStatus_AndItsAnswerIsPassedThrough()
    {
        if (!StubPwsh.Supported) return;
        var map = new (int Exit, int Status)[] { (0, 200), (21, 202), (2, 400), (12, 404), (10, 409), (11, 409), (40, 409), (20, 503), (1, 500), (30, 500), (99, 500) };
        foreach (var (exit, status) in map)
        {
            // item-busy is the rule script's own lock (Core.ps1 Use-ItemLock): the gateway passes its exit 10 on as it passes any other
            var body = exit == 10 ? "{\"ok\":false,\"code\":\"item-busy\",\"reason\":\"another operation on item 5 is running\",\"retryAfterSeconds\":5,\"exit\":10}"
                                  : "{\"ok\":" + (exit == 0 ? "true" : "false") + ",\"code\":\"c" + exit + "\",\"exit\":" + exit + "}";
            StubPwsh.Answer(gw.Env.Dir, "pin-exit-map", body, exit);
            var a = await Verb("pin-exit-map", Admin, "{\"item\":5}");
            Assert.Equal(status, a.Status);
            Assert.Equal(body, a.Body);
        }
    }

    [Fact]
    public async Task NoOutput_IsAnAnswerOfTheGateway_WithTheExitOfTheCall()
    {
        if (!StubPwsh.Supported) return;
        StubPwsh.Answer(gw.Env.Dir, "pin-no-output", "", 0);
        var zero = await Verb("pin-no-output", Admin);
        Assert.Equal(200, zero.Status);   // the status follows the exit code (0), the answer says exit 20
        Assert.Equal("{\"ok\":false,\"code\":\"no-output\",\"reason\":\"the rule script produced no output\",\"exit\":20}", zero.Body);
        StubPwsh.Answer(gw.Env.Dir, "pin-no-output", "", 12);
        var twelve = await Verb("pin-no-output", Admin);
        Assert.Equal(404, twelve.Status);
        Assert.Equal("{\"ok\":false,\"code\":\"no-output\",\"reason\":\"the rule script produced no output\",\"exit\":12}", twelve.Body);
    }

    [Fact]
    public async Task VerbsOfTheGatewayItself_AreRefusedAsUseEndpoint_AndNothingRuns()
    {
        if (!StubPwsh.Supported) return;
        var verbs = new Dictionary<string, string>
        {
            ["revoke-worker"] = "use POST /api/v1/workers/{name}/revoke", ["revocations"] = "use POST /api/v1/workers/{name}/revoke",
            ["plant-snapshot-save"] = "the plant snapshot is written and read by the gateway itself, at its stop and its start",
            ["plant-snapshot-load"] = "the plant snapshot is written and read by the gateway itself, at its stop and its start",
            ["plant-snapshot-clear"] = "the plant snapshot is written and read by the gateway itself, at its stop and its start",
            ["plant-snapshot-await"] = "the plant snapshot is written and read by the gateway itself, at its stop and its start",
            ["leader-lease"] = "the plant snapshot is written and read by the gateway itself, at its stop and its start",
            ["transcript-save"] = "the plant snapshot is written and read by the gateway itself, at its stop and its start",
            ["transcript-load"] = "the plant snapshot is written and read by the gateway itself, at its stop and its start",
            ["transcript-prune"] = "the plant snapshot is written and read by the gateway itself, at its stop and its start",
            ["operator-release"] = "use POST /api/v1/operator/record-release", ["operator-releases"] = "use POST /api/v1/operator/record-release",
            ["ci-release"] = "the gateway releases the Copilot agent's held workflow runs by itself",
            ["park-item"] = "the gateway parks an item runtime-failed by itself",
            ["copilot-owner"] = "the gateway records a Copilot task's ownership of an item by itself",
            ["copilot-settle"] = "the gateway settles a Copilot Development task by itself, at its launch and its end",
            ["unpark-item"] = "use POST /api/v1/items/{n}/unpark (client: factory.ps1 unpark --item N --reason \"...\")",
            ["resettle-item"] = "use POST /api/v1/items/{n}/resettle",
            ["publish-demo"] = "send the MP4 as the body of POST /api/v1/demo/{item} (client: factory.ps1 publish-demo --item N --file F)",
        };
        foreach (var (verb, reason) in verbs)
        {
            var before = Calls(verb);
            var a = await Verb(verb, Admin);
            Is(a, 400, "use-endpoint", 2);
            Assert.Equal(verb, a.Json?["verb"]?.ToString());
            Assert.Equal(reason, a.Json?["reason"]?.ToString());
            Assert.Equal(before, Calls(verb));
        }
    }

    [Fact]
    public async Task Refusal_OfTheCredential_Is403Forbidden_WithTheVerb_AndNothingRuns()
    {
        if (!StubPwsh.Supported) return;
        var wrongItem = await Verb("get-item", Owner(7), "{\"item\":8}");
        Is(wrongItem, 403, "forbidden", 30);
        Assert.Equal("get-item", wrongItem.Json?["verb"]?.ToString());
        Assert.Equal("this token is for item #7 only", wrongItem.Json?["reason"]?.ToString());
        Assert.False(wrongItem.Json?["ok"]?.GetValue<bool>());
        var role = await Verb("accept-nothing", Owner(7), "{\"item\":7}");
        Is(role, 403, "forbidden", 30);
        Assert.Equal("the owner role cannot call 'accept-nothing'", role.Json?["reason"]?.ToString());
        Assert.Equal(0, Calls("accept-nothing"));
    }

    [Fact]
    public async Task GitCredential_OfAnItemThatTheSessionDoesNotOwn_IsNotOwner()
    {
        if (!StubPwsh.Supported) return;
        var token = Auth.MintItemToken(Secret, 7, "owner", "a7", "olga", TimeSpan.FromHours(1), 0, "cursor-cloud");
        var a = await Verb("git-credential", token, "{\"item\":7}");
        Is(a, 409, "not-owner", 10);
        Assert.Equal("this session does not own item #7 through an accepted assignment: no git credential", a.Json?["reason"]?.ToString());
        Assert.Equal(0, Calls("git-credential"));
        // without a runtime the role check of the authorization comes first
        var plain = await Verb("git-credential", Owner(7), "{\"item\":7}");
        Is(plain, 403, "forbidden", 30);
    }

    [Fact]
    public async Task TheRuleScript_GetsTheBodyAsTheAuthorizationLeftIt_AndTheSessionLinkOnlyForASession()
    {
        if (!StubPwsh.Supported) return;
        var before = StubPwsh.Inputs(gw.Env.StubLog, "get-item").Count;
        // the admin token: the body as sent, a _session of the caller's own making taken out
        Assert.Equal(200, (await Verb("get-item", Admin, "{\"item\":5,\"_session\":{\"name\":\"forged\"},\"x\":1}")).Status);
        // an owner's item token: holder from the credential, a _session of the gateway's own (the session's name, url empty without a page)
        Assert.Equal(200, (await Verb("get-item", Owner(9, "ivy"), "{\"item\":9,\"holder\":\"someone-else\",\"_session\":{\"name\":\"forged\"}}")).Status);
        var inputs = StubPwsh.Inputs(gw.Env.StubLog, "get-item").Skip(before).Select(i => JsonNode.Parse(i.Text)!.AsObject()).ToList();
        Assert.Equal(2, inputs.Count);
        Assert.Equal(5, inputs[0]["item"]!.GetValue<int>());
        Assert.False(inputs[0].ContainsKey("_session"));
        Assert.Equal(1, inputs[0]["x"]!.GetValue<int>());
        Assert.Equal("ivy", inputs[1]["holder"]!.ToString());
        Assert.Equal("ivy", inputs[1]["_session"]?["name"]?.ToString());
    }

    [Fact]
    public async Task AKeeper_NotHoldingTheSlot_IsRefused_AndAFlagIsABoolean_AndTheDayHasALimit()
    {
        if (!StubPwsh.Supported) return;
        var keeper = gw.Host.Services.GetRequiredService<KeeperSettings>();
        var other = await Verb("create-item", Auth.KeeperToken(Secret, "second-keeper", keeper.TokenGeneration), "{}");
        Is(other, 403, "not-keeper", 30);
        Assert.Equal("this credential is not the active backlog keeper: register with subscribe --own --keeper first", other.Json?["reason"]?.ToString());
        var token = Auth.KeeperToken(Secret, "backlog-keeper", keeper.TokenGeneration);
        StubPwsh.Answer(gw.Env.Dir, "create-item", "{\"ok\":true}");
        Is(await Verb("amend-criteria", token, "{\"item\":3,\"follow_up\":\"yes\"}"), 400, "usage", 2);
        var created = Calls("create-item");
        Is(await Verb("split-item", token, "{\"item\":3,\"children\":\"two\"}"), 400, "usage", 2);
        var tooMany = await Verb("split-item", token, "{\"item\":3,\"children\":[" + string.Join(",", Enumerable.Repeat("{}", keeper.MaxNewItemsPerDay + 1)) + "]}");
        Is(tooMany, 429, "daily-item-limit", 20);
        Assert.Equal(3600, tooMany.Json?["retryAfterSeconds"]?.GetValue<int>());
        Assert.Equal($"the backlog keeper may create {keeper.MaxNewItemsPerDay} new items in 24 hours; this would need {keeper.MaxNewItemsPerDay + 1} and {Plant.KeeperItemsLeft()} are left", tooMany.Json?["reason"]?.ToString());
        // a pin of next-free is the worker the plant would offer to; with none free it is refused (another test may have registered one)
        var free = Plant.NextFreeWorker();
        var pin = await Verb("create-item", token, "{\"pin\":\"next-free\",\"title\":\"t\"}");
        if (free == "") { Is(pin, 409, "no-free-worker", 10); Assert.Equal(created, Calls("create-item")); }
        else
        {
            Assert.Equal(200, pin.Status);
            Assert.Equal(free, JsonNode.Parse(StubPwsh.Inputs(gw.Env.StubLog, "create-item").Last().Text)!["pin"]!.ToString());
        }
        Assert.Equal(0, Calls("amend-criteria"));
    }

    [Fact]
    public async Task AKeeper_CreatingAnItem_SpendsTheDay_ByWhatTheAnswerCreated()
    {
        if (!StubPwsh.Supported) return;
        var keeper = gw.Host.Services.GetRequiredService<KeeperSettings>();
        var token = Auth.KeeperToken(Secret, "backlog-keeper", keeper.TokenGeneration);
        var left = Plant.KeeperItemsLeft();
        StubPwsh.Answer(gw.Env.Dir, "create-item", "{\"ok\":true,\"created\":[41,42]}");
        Assert.Equal(200, (await Verb("create-item", token, "{\"title\":\"t\",\"unknown\":1}")).Status);
        Assert.Equal(left - 2, Plant.KeeperItemsLeft());
        var input = JsonNode.Parse(StubPwsh.Inputs(gw.Env.StubLog, "create-item").Last().Text)!.AsObject();
        Assert.Equal("backlog-keeper", input["by"]!.ToString());
        Assert.False(input.ContainsKey("unknown"));
        // a failed call spends nothing
        StubPwsh.Answer(gw.Env.Dir, "create-item", "{\"ok\":false,\"created\":[43],\"code\":\"x\",\"exit\":10}", 10);
        Assert.Equal(409, (await Verb("create-item", token, "{\"title\":\"t\"}")).Status);
        Assert.Equal(left - 2, Plant.KeeperItemsLeft());
    }

    [Fact]
    public async Task ASupervisor_NotHoldingTheSlot_IsRefused_AndTheActiveOneSpendsAStepPerCall()
    {
        if (!StubPwsh.Supported) return;
        var msg = gw.Host.Services.GetRequiredService<MessagingSettings>();
        var other = await Verb("get-item", Auth.ManagerToken(Secret, "other-supervisor", msg.TokenGeneration), "{\"item\":1}");
        var gets = Calls("get-item");
        Is(other, 403, "not-supervisor", 30);
        Assert.Equal("this credential is not the active supervisor: register with subscribe --own --supervisor first", other.Json?["reason"]?.ToString());
        Assert.Equal(gets, Calls("get-item"));
        var token = Auth.ManagerToken(Secret, "supervisor", msg.TokenGeneration);
        Assert.Equal(200, (await Verb("get-item", token, "{\"item\":1}")).Status);
        Assert.Equal(200, (await Verb("get-item", token, "{\"item\":1}")).Status);
        var third = await Verb("get-item", token, "{\"item\":1}");
        Is(third, 429, "step-budget", 20);
        Assert.True(third.Json?["retryAfterSeconds"]?.GetValue<int>() > 0);
        Assert.Equal("the supervisor's step budget of this hour is spent; keep waiting for events and do not work around it", third.Json?["reason"]?.ToString());
    }

    [Fact]
    public async Task ManagerAction_IsCheckedBeforeAnythingChanges()
    {
        if (!StubPwsh.Supported) return;
        var policy = Plant.MgrActions.Policies.First();
        var before = Calls("manager-action");
        Is(await Verb("manager-action", Admin, "{\"action\":\"explode\",\"policy\":\"" + policy + "\"}"), 400, "usage", 2);
        Is(await Verb("manager-action", Admin, "{\"action\":\"requeue\",\"policy\":\"no-such-policy\",\"item\":5}"), 400, "unknown-policy", 2);
        Is(await Verb("manager-action", Admin, "{\"action\":\"requeue\",\"policy\":\"" + policy + "\",\"item\":5,\"reason\":" + new string('x', 301).Insert(0, "\"") + "\"}"), 400, "usage", 2);
        Is(await Verb("manager-action", Admin, "{\"action\":\"requeue\",\"policy\":\"" + policy + "\",\"item\":5,\"reason\":7}"), 400, "usage", 2);
        Is(await Verb("manager-action", Admin, "{\"action\":\"requeue\",\"policy\":\"" + policy + "\",\"item\":5,\"reason\":\"token " + "ghp_" + new string('A', 26) + "\"}"), 409, "secret-in-message", 10);
        Is(await Verb("manager-action", Admin, "{\"action\":\"requeue\",\"policy\":\"" + policy + "\"}"), 400, "usage", 2);
        Is(await Verb("manager-action", Admin, "{\"action\":\"requeue\",\"policy\":\"" + policy + "\",\"item\":0}"), 400, "usage", 2);
        Is(await Verb("manager-action", Admin, "{\"action\":\"pin\",\"policy\":\"" + policy + "\",\"item\":5,\"worker\":\"nobody-knows\"}"), 404, "no-such-worker", 12);
        Is(await Verb("manager-action", Admin, "{\"action\":\"drain\",\"policy\":\"" + policy + "\",\"worker\":\"operator\"}"), 400, "usage", 2);
        Is(await Verb("manager-action", Admin, "{\"action\":\"drain\",\"policy\":\"" + policy + "\",\"worker\":\"bad name\"}"), 400, "usage", 2);
        Assert.Equal(before, Calls("manager-action"));
    }

    [Fact]
    public async Task ManagerAction_OfAnItem_RunsTheRuleVerb_WithTheCleanedReasonAndTheCaller()
    {
        if (!StubPwsh.Supported) return;
        var policy = Plant.MgrActions.Policies.First();
        StubPwsh.Answer(gw.Env.Dir, "manager-action", "{\"ok\":true,\"journaled\":true}");
        var a = await Verb("manager-action", Admin, "{\"action\":\"requeue\",\"policy\":\"" + policy + "\",\"item\":5,\"reason\":\"see <b>https://x.y</b>  @bob!  now\",\"session\":\"forged\",\"by\":\"someone\"}");
        Assert.Equal(200, a.Status);
        var input = JsonNode.Parse(StubPwsh.Inputs(gw.Env.StubLog, "manager-action").Last().Text)!.AsObject();
        Assert.Equal("operator", input["by"]!.ToString());
        Assert.Equal("see bhttps x.y/b bob now", input["reason"]!.ToString().Replace("  ", " "));
        Assert.False(input.ContainsKey("session"));
        // the rule script refused: the call is not counted and the answer is the script's
        StubPwsh.Answer(gw.Env.Dir, "manager-action", "{\"ok\":false,\"code\":\"owner-hold\",\"exit\":10}", 10);
        var refused = await Verb("manager-action", Admin, "{\"action\":\"requeue\",\"policy\":\"" + policy + "\",\"item\":6}");
        Is(refused, 409, "owner-hold", 10);
    }

    [Fact]
    public async Task ManagerAction_OnAName_IsTheGatewaysOwn_AndNeverRunsTheRuleVerb()
    {
        if (!StubPwsh.Supported) return;
        var policy = Plant.MgrActions.Policies.First();
        var before = Calls("manager-action");
        var a = await Verb("manager-action", Admin, "{\"action\":\"drain\",\"policy\":\"" + policy + "\",\"worker\":\"never-registered\"}");
        Assert.Equal(404, a.Status);   // the plant does not know the name (exit 12)
        Assert.Equal("drain", a.Json?["action"]?.ToString());
        Assert.Equal(12, a.Json?["exit"]?.GetValue<int>());
        Assert.Equal(before, Calls("manager-action"));
    }

    [Fact]
    public async Task TextFromAMessage_IsMarkedByTheGateway_AndTheMarkerAloneIsRefused()
    {
        if (!StubPwsh.Supported) return;
        var owner = Owner(7, "olga");
        var forged = await Verb("append-body", owner, "{\"item\":7,\"content\":\"x <!-- aisf:assumption source=m from=y --> z\"}");
        Is(forged, 409, "reserved-marker", 10);
        Is(await Verb("comment", owner, "{\"item\":7,\"body\":\"AISF:Assumption\"}"), 409, "reserved-marker", 10);
        Is(await Verb("comment", owner, "{\"item\":7,\"body\":\"x\",\"from_message\":\"nope\"}"), 400, "usage", 2);
        Is(await Verb("comment", owner, "{\"item\":7,\"from_message\":\"m-0123abcd-0001\"}"), 400, "usage", 2);
        Is(await Verb("comment", owner, "{\"item\":7,\"body\":\"x\",\"from_message\":\"m-0123abcd-0001\"}"), 404, "no-such-message", 12);
        Is(await Verb("comment", Admin, "{\"item\":7,\"body\":\"x\",\"from_message\":\"m-0123abcd-0001\"}"), 400, "usage", 2);
        // a null from_message is no from_message; the text without the marker goes on
        Assert.Equal(200, (await Verb("comment", owner, "{\"item\":7,\"body\":\"plain\",\"from_message\":null}")).Status);
        // the rule is for these two verbs only
        Assert.Equal(200, (await Verb("get-item", owner, "{\"item\":7,\"body\":\"aisf:assumption\"}")).Status);
    }

    [Fact]
    public async Task TextFromAMessage_ThatTheCallerSent_IsQuoted_CleanedAndCapped()
    {
        if (!StubPwsh.Supported) return;
        var owner = Owner(7, "olga");
        Plant.Register("bob", "claude", Array.Empty<string>(), 1, Array.Empty<string>(), 0);
        var sent = await gw.Host.SendAsync("POST", "/api/v1/messages", Api, owner, "{\"to\":\"bob\",\"type\":\"note\",\"item\":7,\"text\":\"hello\"}");
        var id = sent.Json?["id"]?.ToString() ?? sent.Json?["message"]?["id"]?.ToString();
        Assert.True(id is { Length: > 0 }, "the message was not sent: " + sent.Body);
        var text = "`tick` <!-- hide --> a\r\nline two " + new string('q', 600);
        var a = await Verb("comment", owner, new JsonObject { ["item"] = 7, ["body"] = text, ["from_message"] = id }.ToJsonString());
        Assert.Equal(200, a.Status);
        var input = JsonNode.Parse(StubPwsh.Inputs(gw.Env.StubLog, "comment").Last().Text)!.AsObject();
        Assert.False(input.ContainsKey("from_message"));
        var body = input["body"]!.ToString();
        var lines = body.Split('\n');
        Assert.Equal($"<!-- aisf:assumption source={id} from=olga -->", lines[0]);
        Assert.Equal("> tick  hide  a", lines[1]);
        Assert.StartsWith("> line two q", lines[2]);
        Assert.EndsWith("\n<!-- /aisf:assumption -->", body);
        Assert.Equal(4, lines.Length);
        // 500 characters of the cleaned text: "tick  hide  a\nline two " and then the q's
        Assert.Equal(500, "tick  hide  a".Length + 1 + lines[2][2..].Length);
    }

    [Fact]
    public async Task QcVerbs_OfTheGatewaysOwnStore_AreAnsweredWithoutARuleScript()
    {
        if (!StubPwsh.Supported) return;
        var a = await Verb("qc-touch", Admin, "{\"item\":5,\"holder\":\"h\"}");
        Assert.Equal(0, Calls("qc-touch"));
        Assert.True(a.Status is >= 200 and < 600);
        Assert.NotNull(a.Json);
    }
}

/// <summary>The same route with the GitHub budget below its reserve: a gateway of its own, since the budget stays low until its reset.</summary>
public class C7dVerbBudgetPinTests(C7dVerbGateway gw) : IClassFixture<C7dVerbGateway>
{
    const int Api = GatewayEnvironment.ApiPort;
    static readonly string Admin = GatewayEnvironment.AdminToken;
    string Secret => gw.Env.TokenSecret;
    Task<Answer> Verb(string verb, string? token, string? body = "{}") => gw.Host.SendAsync("POST", "/api/v1/verbs/" + verb, Api, token, body);
    int Calls(string verb) => StubPwsh.Read(gw.Env.StubLog).Count(c => c.Verb == verb);
    static void Is(Answer a, int status, string code, int exit) { Assert.Equal(status, a.Status); Assert.Equal(code, a.Code); Assert.Equal(exit, a.Json?["exit"]?.GetValue<int>()); }

    [Fact]
    public async Task AcceptWithoutAnOffer_IsRefused_AndGitHubBudgetBelowItsReserve_HoldsAllButTheEssentialVerbs()
    {
        if (!StubPwsh.Supported) return;
        var owner = Auth.MintItemToken(Secret, 11, "owner", "a11", "una", TimeSpan.FromHours(1));
        var accept = await Verb("accept", owner, "{\"item\":11}");
        Assert.Equal(409, accept.Status);
        Assert.Equal(0, Calls("accept"));
        Assert.Equal("accept", accept.Json?["verb"]?.ToString());
        // a rule script reports its remaining GitHub budget; below the reserve every other verb waits for the reset
        var reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        StubPwsh.Answer(gw.Env.Dir, "pin-budget", "{\"ok\":true,\"_github\":{\"core\":{\"limit\":5000,\"remaining\":3,\"reset\":" + reset + "}}}");
        var first = await Verb("pin-budget", Admin);
        Assert.Equal(200, first.Status);
        Assert.DoesNotContain("_github", first.Body);
        var held = await Verb("get-item", Admin, "{\"item\":1}");
        Is(held, 503, "github-budget", 20);
        Assert.Equal("core", held.Json?["reason"]?.ToString()!.Split('(', ')')[1]);
        Assert.True(held.Json?["retryAfterSeconds"]?.GetValue<int>() > 0);
        Assert.NotNull(held.Json?["resetAt"]);
        Assert.Equal(0, Calls("get-item"));
        // the essential verbs run (my-items), and nothing else
        Assert.Equal(200, (await Verb("my-items", Auth.WorkerToken(Secret, "una-worker"), "{}")).Status);
        Assert.Equal(503, (await Verb("pin-budget", Admin)).Status);
    }
}
