using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Workflows.WorkItem;
using static HttpResultsX;

// V3.2 test seam: POST /api/v1/_test/advance {"item": N, "pr": N?, "head": "sha"?} completes the current step of the item's saga, as the session working it would (a complete review and the step's completion; on Functional Testing a
// PASS verdict for the head). The Tier 1 scenarios of the Copilot and Cursor runtimes used to move the card forward by hand and let v1's sweep follow it; the saga follows only its own inputs, and this is the one a scenario sends.
// It exists ONLY when FACTORY_TEST_HOOKS is 1 and the gateway talks to a GitHub that is not api.github.com (the fake of the suites): without both it is not mapped, the route answers 404, and the deployed configuration
// (config/instance.json with config/deploy.overrides.json) sets neither. The operator token is required like on every operator route.
sealed class TestAdvanceEndpoint(IServiceProvider services, Fworker.Contracts.Ids.RepoId? home) : IEndpointModule
{
    /// <summary>Whether the route is mapped: the variable is 1 and the GitHub base is a host other than api.github.com (a missing base is the real GitHub).</summary>
    public static bool Enabled(Func<string, string?> env)
    {
        if (env("FACTORY_TEST_HOOKS") != "1") return false;
        if (env("GITHUB_API_URL") is not { Length: > 0 } baseUrl || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;
        return !string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase);
    }

    public void Map(IEndpointRouteBuilder routes)
    {
        if (!Enabled(Environment.GetEnvironmentVariable)) return;
        routes.MapPost("/api/v1/_test/advance", async (HttpContext c) => { return await Advance(c); }).Admin(CallerView.Who);
    }

    async Task<IResult> Advance(HttpContext ctx)
    {
        JsonObject? b;
        try { b = await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject; } catch { b = null; }
        if (b is null || !int.TryParse(b["item"]?.ToString(), out var number) || number <= 0) return Results.Json(new { ok = false, code = "usage", reason = "the body is a JSON object with item" }, statusCode: 400);
        var parts = services.GetService<WorkItemParts>();
        if (parts is null || home is null) return Results.Json(new { ok = false, code = "no-engine", reason = "no saga runs here" }, statusCode: 409);
        var id = new ItemId(home.Value, number);
        var state = (await parts.Store.Load(id.ToString(), ctx.RequestAborted))?.State;
        if (state is null || !state.IsOpen) return Results.Json(new { ok = false, code = "no-saga", reason = "the item has no open saga" }, statusCode: 409);
        var step = state.Step; var applied = new List<string>();
        // through the Wolverine host, as the verb feed sends the same inputs: the saga's outgoing messages (the card's move, the timers) leave with the transition
        var host = services.GetService<WolverineHostService>();
        if (host is null) return Results.Json(new { ok = false, code = "no-engine", reason = "the Wolverine host is not here" }, statusCode: 409);
        async Task Send(WorkItemInput input) { var taken = await host.PublishAsync(input); applied.Add($"{input.GetType().Name}:{(taken ? "published" : "refused")}"); }
        var stamp = Guid.NewGuid().ToString("N")[..8];
        switch (step)
        {
            case StepKind.Design:
            case StepKind.Develop:
                await Send(new ReviewRecorded(id, state.Generation, state.ArmId, $"adv-review|{stamp}", true));
                if (step == StepKind.Develop && int.TryParse(b["pr"]?.ToString(), out var pr) && pr > 0)
                    await Send(new PullRequestChanged(id, state.Generation, $"adv-pr|{stamp}", pr, b["head"]?.ToString() ?? "", []));
                await Send(new StepCompleted(id, state.Generation, state.ArmId, $"adv-done|{stamp}"));
                break;
            case StepKind.Verify:
            {
                var pull = int.TryParse(b["pr"]?.ToString(), out var vp) && vp > 0 ? vp : state.PullRequest;
                var head = b["head"]?.ToString() is { Length: > 0 } h ? h : state.Head;
                await Send(new VerdictRecorded(id, state.Generation, state.ArmId, $"adv-verdict|{stamp}", true, pull, head, "advanced by the test hook"));
                break;
            }
            default:
                return Results.Json(new { ok = false, code = "not-advanceable", reason = $"the item is on {step}" }, statusCode: 409);
        }
        return Results.Json(new { ok = true, from = step.ToString(), applied });
    }
}
