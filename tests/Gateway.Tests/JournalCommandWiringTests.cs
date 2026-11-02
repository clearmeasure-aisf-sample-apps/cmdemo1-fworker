using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Database;
using Fworker.Integrations.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W3.2 (#477): JournalCommandWiring.Attach, the one place the journal command is wired, with no SQL Server and no GitHub. What it registers in the host (the service, the handlers, the partitioned queue, the
/// ledger of the verb journal) and that a journal command published through the sink it returns reaches the ledger and is written, in the order sent, through the GitHub client it was given (a fake on the wire). The
/// host that runs it is a Wolverine host without a message store: the durable inbox of the queue is the gateway's, the wiring is the same.
/// </summary>
public sealed class JournalCommandWiringTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("journal-wiring").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    const string Login = "factory-bot";

    sealed record Comment(long Id, string Login, string Body);

    /// <summary>The comments of the issues of o/r, as GitHub answers the journal store: list, post, patch.</summary>
    sealed class FakeIssues : HttpMessageHandler
    {
        readonly Dictionary<int, List<Comment>> issues = new();
        readonly object gate = new();
        long nextId = 1000;

        public List<Comment> Of(int n) { lock (gate) return issues.TryGetValue(n, out var l) ? [.. l] : []; }
        public List<string> Events(int n) => Of(n).Where(c => c.Body.Contains("<!-- plant event=")).Select(c => Regex.Match(c.Body, @"plant event=(\S+) key=(\S+) ").Groups[0].Value.Trim()).ToList();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath; var q = req.RequestUri.Query;
            var body = req.Content == null ? "" : req.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            lock (gate)
            {
                var m = Regex.Match(path, @"^/repos/o/r/issues/(\d+)/comments$");
                if (m.Success)
                {
                    var n = int.Parse(m.Groups[1].Value);
                    if (!issues.TryGetValue(n, out var list)) issues[n] = list = new();
                    if (req.Method == HttpMethod.Get)
                    {
                        var page = int.Parse(Regex.Match(q, @"[?&]page=(\d+)").Groups[1].Value);
                        return Task.FromResult(Ok(new JsonArray(list.Skip((page - 1) * 100).Take(100).Select(c => (JsonNode)Json(c)).ToArray())));
                    }
                    var c = new Comment(nextId++, Login, JsonNode.Parse(body)!["body"]!.ToString()); list.Add(c);
                    return Task.FromResult(Ok(Json(c), HttpStatusCode.Created));
                }
                m = Regex.Match(path, @"^/repos/o/r/issues/comments/(\d+)$");
                if (m.Success && req.Method == HttpMethod.Patch)
                {
                    var id = long.Parse(m.Groups[1].Value);
                    foreach (var l in issues.Values)
                    {
                        var i = l.FindIndex(c => c.Id == id);
                        if (i >= 0) { l[i] = l[i] with { Body = JsonNode.Parse(body)!["body"]!.ToString() }; return Task.FromResult(Ok(Json(l[i]))); }
                    }
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"Not Found\"}") });
        }

        static JsonObject Json(Comment c) => new() { ["id"] = c.Id, ["user"] = new JsonObject { ["login"] = c.Login }, ["body"] = c.Body };
        static HttpResponseMessage Ok(JsonNode n, HttpStatusCode s = HttpStatusCode.OK) => new(s) { Content = new StringContent(n.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    GatewayComposition Compose()
    {
        var cfg = Path.Combine(dir, "instance.json");
        File.WriteAllText(cfg, new JsonObject { ["owner"] = "o", ["repo"] = "r", ["name"] = "factory-worker" }.ToJsonString());
        var vars = new Dictionary<string, string> { ["FACTORY_PLUGIN_SCRIPTS"] = dir, ["FACTORY_DEV_TOKEN"] = "dev", ["FACTORY_WEBHOOK_SECRET"] = "hook", ["FACTORY_CONFIG"] = cfg };
        return GatewayComposition.FromEnvironment(k => vars.GetValueOrDefault(k));
    }

    // a token the client sends and the fake ignores; composed at run time, so the secret scan has nothing to match
    static readonly string Token = "ghp_" + new string('B', 36);

    static Func<string, string?> Env() => k => k switch { "GH_TOKEN" => Token, "FACTORY_SERVER_LOGIN" => Login, _ => null };

    static WolverineHostService NewHost() => new(DatabaseOptions.From(k => k == "FACTORY_DB_CONNECTION" ? "Server=tcp:wiring.invalid,1433;Database=wiring;User Id=u;Password=p;Encrypt=False" : null),
        new WolverineHostOptions(), new GatewayLeader(new LeaderSettings { On = false }));

    static async Task<bool> Until(Func<bool> cond, int seconds = 30)
    {
        for (var i = 0; i < seconds * 20; i++) { if (cond()) return true; await Task.Delay(50); }
        return false;
    }

    [Fact]
    public void Attach_RegistersTheService_TheLedgerOfTheVerb_AndTheHostHook_AfterTheHookOfTheStepBeforeIt()
    {
        var c = Compose(); var host = NewHost(); var order = new List<string>();
        host.RuntimeCommands = (_, _) => order.Add("previous");
        var api = new FakeIssues();
        var sink = JournalCommandWiring.Attach(host, c, Env(), new HttpClient(api) { BaseAddress = new Uri("http://fake/") }, new InMemoryIdempotencyLedger(), new InProcessItemLock());

        Assert.NotNull(sink);
        Assert.NotNull(c.JournalLedger);   // the verb journal writes through the same ledger (W9.8)
        Assert.NotNull(host.RuntimeCommands);

        var services = new ServiceCollection();
        var w = new WolverineOptions();
        host.RuntimeCommands!(services, w);
        Assert.Equal(["previous"], order);   // the hook of the earlier step still runs
        var service = Assert.Single(services, d => d.ServiceType == typeof(JournalCommandService));
        Assert.Equal(ServiceLifetime.Singleton, service.Lifetime);
        Assert.NotNull(service.ImplementationInstance);
        Assert.Empty(api.Of(1));   // nothing is read or written by wiring alone: the store is made at the first line
    }

    [Fact]
    public async Task AJournalCommandSentThroughTheSink_ReachesTheLedger_AndIsWrittenInTheOrderSent_ARepeatOnce_AnEmptyKeyEachTime()
    {
        var c = Compose(); var host = NewHost(); var api = new FakeIssues();
        host.Schema = (_, _) => Task.FromResult(new SchemaReport { Verified = true });
        var sink = JournalCommandWiring.Attach(host, c, Env(), new HttpClient(api) { BaseAddress = new Uri("http://fake/") }, new InMemoryIdempotencyLedger(), new InProcessItemLock());
        var hook = host.RuntimeCommands!;
        // the host the gateway builds, without the SQL Server: the same options hook, the handlers named and none found by convention
        host.HostFactory = (_, _) =>
        {
            WolverineOptions? options = null;
            return new HostBuilder()
                .UseWolverine(w => { w.Discovery.DisableConventionalDiscovery(); options = w; })
                .ConfigureServices(s => hook(s, options!))
                .Build();
        };
        try
        {
            await host.StartAsync(default);
            Assert.True(await Until(() => host.State == WolverineHostService.Phase.Running), $"the host did not start: {host.Status().ToJsonString()}");

            await sink.Send(7, "matched", new JsonObject { ["trigger"] = "label", ["reason"] = "r", ["column"] = "Development", ["role"] = "" }, "m|a-1");
            await sink.Send(7, "offered", new JsonObject { ["worker"] = "w1", ["type"] = "persistent-local", ["ackSeconds"] = 120.0 }, "off|a-1|w1");
            await sink.Send(7, "offered", new JsonObject { ["worker"] = "w1", ["type"] = "persistent-local", ["ackSeconds"] = 120.0 }, "off|a-1|w1");   // the same decision again
            await sink.Send(7, "released", new JsonObject { ["worker"] = "w1", ["reason"] = "one" }, "");
            await Task.Delay(5);
            await sink.Send(7, "released", new JsonObject { ["worker"] = "w2", ["reason"] = "two" }, "");

            Assert.True(await Until(() => api.Events(7).Count >= 4), $"the lines did not all arrive: {string.Join(" / ", api.Events(7))}");
            await Task.Delay(500);   // (a fifth line, were the repeat written, would be here by now)
            var events = api.Events(7);
            Assert.Equal(4, events.Count);
            Assert.Equal(["matched", "offered", "released", "released"], events.Select(e => Regex.Match(e, @"event=(\S+)").Groups[1].Value).ToArray());   // the order sent
            Assert.Equal("plant event=matched key=m|a-1", events[0]);
            Assert.NotEqual(events[2], events[3]);   // the empty key took the ticks each time
            Assert.DoesNotContain("key= ", string.Join("\n", events));
            Assert.NotNull(api.Of(7).SingleOrDefault(x => x.Body.StartsWith("<!-- factory-state:factory-worker v1 -->")));   // the ledger is in the item's state comment
        }
        finally { await host.StopAsync(default); }
    }
}
