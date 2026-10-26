using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// A gateway for the caller-kind matrix: the route fixture's environment plus a revocation read (mallory revoked at generation 3, so a revoked
/// credential is refused as in production), the operator session (operator-session holds the slot, operator-two does not), the backlog keeper
/// and the supervisor switched on (none of them registered).
/// </summary>
public sealed class CallerMatrixGateway : IAsyncLifetime
{
    public GatewayEnvironment Env { get; } = new();
    public GatewayHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!StubPwsh.Supported) return;
        Env.Set("FACTORY_TEST_SKIP_REVOCATIONS", null);
        Env.Set("GH_TOKEN", "fake");                 // a GitHub identity: the revoked generations are read (from the stub)
        Env.Set("FACTORY_TRANSCRIPTS", "0");
        Env.Apply();
        StubPwsh.Answer(Env.Dir, "revocations", "{\"ok\":true,\"revoked\":{\"mallory\":3}}");
        // the operator's recorded releases are never read here (each read fails), so their checks answer "starting" for the whole run, not by a race
        StubPwsh.Answer(Env.Dir, "operator-releases", "{\"ok\":false,\"code\":\"stub-failing\"}", 1);
        var cfgPath = Path.Combine(Env.Dir, "instance.test.json");
        var cfg = (JsonObject)JsonNode.Parse(File.ReadAllText(cfgPath))!;
        cfg["operator"]!["enabled"] = true;
        cfg["operator"]!["allowedNames"] = new JsonArray("operator-session", "operator-two");
        cfg["keeper"]!["enabled"] = true;
        cfg["manager"]!["enabled"] = true;
        File.WriteAllText(cfgPath, cfg.ToJsonString());
        Host = new GatewayHost();
        _ = Host.Server;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20) && (await Host.SendAsync("GET", "/ready", GatewayEnvironment.ApiPort)).Status != 200) await Task.Delay(100);
        // the operator session holds the slot, as after `subscribe --own --operator`
        Assert.True(Host.Services.GetRequiredService<Plant>().RegisterOperator("operator-session", "claude-opus-5-5", "low", 0).Ok);
    }

    public async Task DisposeAsync()
    {
        try { if (Host != null) await Host.DisposeAsync(); }
        finally { Env.Dispose(); }
    }
}

/// <summary>
/// The caller-kind matrix (task C6, pinned from master before the middleware, admission and caller context moved out of Program.cs): every
/// route of the route-and-auth table that answers at once, asked by every kind of caller the gateway tells apart (no credential, a malformed
/// or forged one, a QC link, worker, runtime worker, item tokens of each role, the operator session holding the slot and not, supervisor,
/// keeper, role tokens, revoked and reinstated names, the admin), on the API port, in a fixed order. Each answer's status, JSON code, exit and
/// reason must stay what master answered (Pins/caller-matrix.json). The two streams (/api/v1/events, /api/v1/wait) are left out: they do not
/// answer at once; the middleware's part of them is the same as any /api route's. The callers ask in a fixed order, the admin last, and some
/// answers depend on it (the build slot goes to the first workers, the admin's revoke of alice comes after every other caller).
/// </summary>
public sealed class CallerMatrixTests(CallerMatrixGateway gw) : IClassFixture<CallerMatrixGateway>
{
    static readonly HashSet<string> Streams = new(StringComparer.Ordinal) { "GET /api/v1/events", "GET /api/v1/wait" };

    // a body each POST gets: an empty JSON object, except /mcp (a JSON-RPC tools/list); the webhook gets {} unsigned and is refused 401
    static string? BodyOf(string method, string pattern) => method != "POST" ? null
        : pattern == "/mcp/" ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}"
        : "{}";

    [Fact]
    public async Task EveryCallerKind_EveryRoute_AnswersAsOnMaster()
    {
        if (!StubPwsh.Supported) return;
        var routes = RouteTable.Rows.Where(r => !Streams.Contains($"{r.Method} {r.Pattern}")).ToList();
        var lines = new List<KeyValuePair<string, string>>();
        foreach (var (caller, auth) in AuthPins.Callers(gw.Host, gw.Env.TokenSecret))
            foreach (var r in routes)
            {
                var path = RouteTable.PathOf(r.Pattern);
                var a = await AuthPins.Raw(gw.Host, r.Method, path, GatewayEnvironment.ApiPort, auth, BodyOf(r.Method, r.Pattern));
                // /mcp answers a JSON-RPC result whose tool list is not a refusal: its status is what the gate decides
                lines.Add(new($"{caller} {r.Method} {path}", r.Pattern == "/mcp/" && a.Status == 200 ? "200 mcp" : AuthPins.Line(a)));
            }
        AuthPins.Compare("caller-matrix.json", lines);
    }
}
