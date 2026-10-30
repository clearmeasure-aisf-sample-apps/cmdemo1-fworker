using System.Text;
using System.Text.Json.Nodes;
using static HttpResultsX;

// The cycle-time metrics' answers (task C7a): cached per since|max, computed one at a time. A good answer (exit 0, or 2 for a usage error) is kept
// for CacheSeconds, a failed one (any other exit, a timeout included) for FailSeconds: a caller that retries cannot make the gateway read GitHub
// again and again.
sealed class MetricsCache
{
    public const int CacheSeconds = 120, FailSeconds = 30;
    public readonly Dictionary<string, (DateTime At, int Code, string Json)> Entries = new();
    public readonly SemaphoreSlim Gate = new(1, 1);

    public static bool Fresh((DateTime At, int Code, string Json) e) => (DateTime.UtcNow - e.At).TotalSeconds < (e.Code is 0 or 2 ? CacheSeconds : FailSeconds);
    public static int StatusOf(int code) => code == 0 ? 200 : code == 2 ? 400 : code == 20 ? 503 : 502;
}

// Who may read the metrics, and with which queries (an endpoint filter of this module, ahead of SupervisorBudget, so a refused call spends no step).
// The rule itself is MetricsAccessPolicy (gateway/Policies/, task C8); the filter reads the plant's slots and turns its refusal into the answer.
sealed class MetricsAccessFilter(GatewayOptions gateway, MessagingSettings messaging, Plant plant) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var c = context.HttpContext;
        var mw = c.Caller().Admitted;
        var q = c.Request.Query;
        var refusal = MetricsAccessPolicy.Check(mw.Kind, mw.Name, gateway.OwnerMode, messaging.ManagerEnabled, () => plant.ActiveSupervisor().Name, plant.ActiveKeeper,
            q["fresh"].ToString(), q["max"].ToString(), q["since"].ToString());
        if (refusal != null) return refusal.IsBare ? Results.StatusCode(refusal.Status) : Fail(refusal.Status, refusal.Code, refusal.Reason, refusal.Exit);
        return await next(context);
    }
}

// Cycle-time metrics (enhancement 13), read-only: computed by the rule script `metrics` from the items' journal comments and state comments on GitHub,
// so they survive a restart. since: an ISO date or 30m, 24h, 7d (default 7d, at most 90d); max: items read, newest first (default 100, at most 300);
// fresh=1 skips the cache. A failed computation is cached too, for every caller except the operator token's fresh=1. The operator session may ask only
// since=24h or 7d (none is read as 7d, the script's default), with no max and no fresh: its answers come from two cache entries, so it causes at most
// one computation per entry in 120 s while they succeed, and one in 30 s while they fail. The supervisor's call is a step of its hourly budget, like a
// read verb (SupervisorBudget).
sealed class MetricsEndpoints(MetricsCache cache, ServiceVerbCalls verbs) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/metrics", async (HttpContext c) => { return await Metrics(c); })
            .AddEndpointFilter<MetricsAccessFilter>()
            .SupervisorBudget();

    async Task<IResult> Metrics(HttpContext c)
    {
        var since = c.Request.Query["since"].ToString().Trim(); var maxText = c.Request.Query["max"].ToString().Trim();
        if (c.Caller().OperatorActive)
        {
            if (MetricsAccessPolicy.OperatorQueryError(since, maxText, c.Request.Query.ContainsKey("fresh")) is { } operatorQueryError)
                return Results.Json(new { ok = false, code = "usage", reason = operatorQueryError, exit = 2 }, statusCode: 400);
            if (since == "") since = "7d";   // the script's default: one cache entry for both spellings
        }
        if (since.Length > 40 || maxText.Length > 4 || (maxText != "" && !int.TryParse(maxText, out _))) return Results.Json(new { ok = false, code = "usage", reason = "since is an ISO date or 30m, 24h, 7d; max a number", exit = 2 }, statusCode: 400);
        var key = since + "|" + maxText;
        var fresh = c.Request.Query["fresh"].ToString() == "1";
        var entries = cache.Entries;
        // a cached answer does not wait behind a computation that is running
        lock (entries) if (!fresh && entries.TryGetValue(key, out var hit0) && MetricsCache.Fresh(hit0)) return Results.Text(hit0.Json, "application/json", Encoding.UTF8, MetricsCache.StatusOf(hit0.Code));
        await cache.Gate.WaitAsync(c.RequestAborted);
        try
        {
            lock (entries) if (!fresh && entries.TryGetValue(key, out var hit) && MetricsCache.Fresh(hit)) return Results.Text(hit.Json, "application/json", Encoding.UTF8, MetricsCache.StatusOf(hit.Code));
            var args = new JsonObject(); if (since != "") args["since"] = since; if (maxText != "") args["max"] = int.Parse(maxText);
            var (code, output) = await verbs.RunAsync("metrics", args.ToJsonString(), TimeSpan.FromMinutes(3));
            lock (entries)
            {
                foreach (var k in entries.Where(kv => !MetricsCache.Fresh(kv.Value)).Select(kv => kv.Key).ToList()) entries.Remove(k);
                // a failed fresh=1 of the operator token never replaces a good entry
                if (!fresh || code is 0 or 2 || !entries.ContainsKey(key)) entries[key] = (DateTime.UtcNow, code, output);
            }
            return Results.Text(output, "application/json", Encoding.UTF8, MetricsCache.StatusOf(code));
        }
        finally { cache.Gate.Release(); }
    }
}
