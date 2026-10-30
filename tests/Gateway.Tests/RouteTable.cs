namespace Gateway.Tests;

/// <summary>
/// Every route the gateway maps (owner mode, messaging test hooks on: 53 production routes, 6 QC routes, 16 test hooks, MCP), with what it
/// answers on the API port to a request without credentials (two-port mode; in single-port mode the same). On the webhook port of two-port
/// mode every route answers 404 except POST /webhooks/github. The values are today's, pinned: a change of a route or of its guard changes
/// this table in the same pull request.
/// </summary>
public static class RouteTable
{
    /// <summary>One mapped route: method, pattern (RawText), and the status and JSON code without credentials (code null: not checked).</summary>
    public sealed record Row(string Method, string Pattern, int Status, string? Code = null);

    const int Unauthorized = 401;

    public static readonly IReadOnlyList<Row> Rows = new Row[]
    {
        // anonymous: the handler decides
        new("GET", "/health", 200),
        new("GET", "/alive", 200),
        new("GET", "/ready", 200),
        new("GET", "/", 302),
        new("GET", "/_build", 200),
        new("GET", "/dashboard", 200),
        new("GET", "/fworker-mcp.mjs", 200),
        new("GET", "/sessions/{name}", 200),
        new("POST", "/webhooks/github", 401),
        new("POST", "/webhooks/octopus", 404),   // W4.2: 404 unless the Octopus watcher is on (v2 acting)
        new("POST", "/webhooks/azure-monitor", 404),   // W8.2: 404 unless the ingress inbox exists (v2 acting)
        // past the middleware: the endpoint checks its own credential
        new("POST", "/api/v1/runtime/redeem", 401, "unauthorized"),
        new("POST", "/api/v1/copilot/exchange", 401, "unauthorized"),
        new("GET", "/api/v1/runtime/octopus/desired", 404, "no-runtime"),
        new("POST", "/api/v1/runtime/octopus/report", 404, "no-runtime"),
        // QC (owner mode): the pages and the click check their own token
        new("GET", "/qc/{pr}", 403),   // the page (HTML), no link token
        new("GET", "/qc/{pr}/status", 403, "bad-link"),
        new("GET", "/qc/{pr}/go", 403, "bad-link"),
        new("POST", "/qc/{pr}/{action}", 404),   // no such action

        // MCP (stateless Streamable HTTP: POST only, the pattern as MapMcp writes it): authenticated by the middleware like /api
        new("POST", "/mcp/", Unauthorized, "unauthorized"),
        // the API: the middleware refuses a request without a credential
        new("GET", "/api/v1/status", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/metrics", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/plugin", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/dashboard", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/sessions/{name}", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/manager/actions", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/delivery-shadow", Unauthorized, "unauthorized"),   // W5.2: the delivery train's shadow report (404 where the train does not run in shadow)
        new("GET", "/api/v1/wait", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/events", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/peers", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/messages", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/messages/{id}", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/inbox", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/questions", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/digest", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/operator/release-check", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/qc", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/base-update", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/release-queue/promotion-request", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/workers/{name}/credential", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/workers/{name}/revoke", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/runtimes/{kind}/reset", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/verbs/{verb}", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/copilot/bootstrap-token", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/runtime/transcript", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/manager/mode", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/items/{n:int}/unpark", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/items/{n:int}/resettle", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/demo/{item:int}", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/workers/{name}/{action}", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/build-slot", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/presence", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/messages", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/messages/read", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/messages/ack", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/questions/{id}/answer", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/supervisor/patrol-ack", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/supervisor/patrol", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/supervisor/budget", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/messaging/{action}", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/operator/record-release", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/qc/{pr}/{action}", Unauthorized, "unauthorized"),
        // the test hooks (messaging.testHooks)
        new("GET", "/api/v1/_test/plant-data", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/_test/plant-snapshot", Unauthorized, "unauthorized"),
        new("GET", "/api/v1/_test/stores", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/_test/octopus-provision", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/_test/runtime-nonce", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/_test/role-token", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/_test/deliver", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/_test/answer", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/_test/plan", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/_test/release-queue", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/_test/refusal", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/_test/fact", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/_test/plant-restore", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/_test/roles", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/_test/inflight", Unauthorized, "unauthorized"),
        new("POST", "/api/v1/_test/mode", Unauthorized, "unauthorized"),
    };

    /// <summary>The sample values of the route parameters.</summary>
    static readonly (string Token, string Value)[] Samples =
    {
        ("{name}", "alice"), ("{kind}", "octopus"), ("{verb}", "status"), ("{n:int}", "1"), ("{item:int}", "1"), ("{action}", "ping"), ("{id}", "x"), ("{pr}", "1")
    };

    /// <summary>A request path for the pattern, with the sample values.</summary>
    public static string PathOf(string pattern)
    {
        var p = pattern;
        foreach (var (t, v) in Samples) p = p.Replace(t, v, StringComparison.Ordinal);
        return p;
    }

    /// <summary>Theory rows: method, pattern, port; every route on both ports of two-port mode.</summary>
    public static IEnumerable<object[]> TwoPortCases() =>
        Rows.SelectMany(r => new[] { new object[] { r.Method, r.Pattern, GatewayEnvironment.ApiPort }, new object[] { r.Method, r.Pattern, GatewayEnvironment.HookPort } });

    /// <summary>Theory rows: method, pattern; every route on the one port of single-port mode.</summary>
    public static IEnumerable<object[]> SinglePortCases() => Rows.Select(r => new object[] { r.Method, r.Pattern });

    public static Row Find(string method, string pattern) => Rows.Single(r => r.Method == method && r.Pattern == pattern);

    /// <summary>The answer has the row's status and, where the row names one, its JSON code.</summary>
    public static void AssertAnswer(Row row, Answer a) =>
        Xunit.Assert.True(a.Status == row.Status && (row.Code == null || row.Code == a.Code),
            $"{row.Method} {PathOf(row.Pattern)}: wanted {row.Status} {row.Code}, got {a.Status} {a.Code}: {a.Body[..Math.Min(200, a.Body.Length)]}");
}
