using System.Text;
using System.Text.Json.Nodes;
using static HttpResultsX;

// Until the operator's recorded releases are read (gateway/Operator/OperatorReleaseLoader.cs) both checks answer 503: the check never answers
// "allowed" on a count it does not know. The last guard of this module's endpoints.
sealed class ReleasesLoadedFilter(IOperatorReleaseLoader loader) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        loader.IsLoaded ? await next(context)
            : Results.Json(new { ok = false, code = "starting", reason = "the gateway is still reading the recorded releases; try again", retryAfterSeconds = 5, exit = 20 }, statusCode: 503);
}

// The operator session's two enforced checks (Operator.cs, docs/design/operator-session.md), for the active operator session and the operator. The
// release records are refs in GitHub, written by the rule scripts and read once at start. Guards, in this order: the operator session enabled (then
// owner mode and messaging), the admin or the active operator session, the records read.
sealed class OperatorReleaseEndpoints(Plant plant, Runner runner, RevocationService revocations) : IEndpointModule
{
    // its own gate (a flood of records must never delay a revoke)
    readonly SemaphoreSlim releaseGate = new(1, 1);

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/operator/release-check", (HttpContext c) => Check()).WithOperatorGuards();
        routes.MapPost("/api/v1/operator/record-release", async (HttpContext c) => { return await Record(c); }).WithOperatorGuards();
    }

    // GET /operator/release-check: 200 when the plant is quiet and the day's limit is not reached; otherwise 409 not-quiet (with every reason) or daily-limit.
    IResult Check()
    {
        var r = plant.ReleaseCheck();
        return Results.Text(r.ToJsonString(), "application/json", Encoding.UTF8, r["ok"]!.GetValue<bool>() ? 200 : 409);
    }

    // POST /operator/record-release { "pr": N, "version": "1.0.N" }: one self-merged release, counted against operator.maxSelfMergesPerDay. A release
    // is always recorded, also beyond the limit (the record is the truth; the next release-check refuses). Idempotent per pull request.
    async Task<IResult> Record(HttpContext c)
    {
        if (ReleaseRecordPolicy.CheckOn(revocations.Off) is { } off) return Fail(off.Status, off.Code, off.Reason, off.Exit);
        var (text, tooLarge) = await ReadBody(c, 4096);
        JsonObject body; try { body = tooLarge ? new JsonObject() : Messenger.ParseObject(text); } catch { body = new JsonObject(); }
        if (ReleaseRecordPolicy.ParseBody(body) is not { } parsed) return Fail(400, "usage", ReleaseRecordPolicy.UsageReason, 2);
        var (pr, version) = parsed;
        var now = DateTime.UtcNow;
        // a cap on the writes of one day
        await releaseGate.WaitAsync();
        try
        {
            if (!plant.HasRelease(pr))
            {
                if (!ReleaseRecordPolicy.WriteAllowed(false, plant.ReleaseRecordsToday())) return Results.Json(new { ok = false, code = "record-limit", reason = $"{Plant.ReleaseWritesPerDay} releases are recorded today; no more are written", retryAfterSeconds = 3600, exit = 20 }, statusCode: 429);
                var (code, _) = await runner.RunAsync("operator-release", new JsonObject { ["day"] = OperatorReleaseLoader.Day(now), ["pr"] = pr, ["version"] = version }.ToJsonString(), TimeSpan.FromSeconds(120));
                if (code != 0) return Results.Json(new { ok = false, code = "not-recorded", reason = "the release could not be written to GitHub, so it is not counted; try again", retryAfterSeconds = 15, exit = 20 }, statusCode: 503);
                plant.AddRelease(now, pr, version);
                Console.WriteLine(new JsonObject { ["at"] = now.ToString("o"), ["operatorRelease"] = pr, ["version"] = version, ["by"] = c.Caller().By }.ToJsonString());
            }
        }
        finally { releaseGate.Release(); }
        var st = plant.OperatorStatus();
        return Results.Json(new { ok = true, pr, version, releasesToday = st["releasesToday"]!.GetValue<int>(), limit = st["limit"]!.GetValue<int>(), exit = 0 });
    }
}

static class OperatorReleaseGuards
{
    // the guards both checks share, in the order they had as one helper (OperatorOff) before C7a
    public static RouteHandlerBuilder WithOperatorGuards(this RouteHandlerBuilder b) => b
        .OperatorOn()
        .Admin(CallerView.Admitted, "this check belongs to the active operator session and the operator")
        .AddEndpointFilter<ReleasesLoadedFilter>();
}
