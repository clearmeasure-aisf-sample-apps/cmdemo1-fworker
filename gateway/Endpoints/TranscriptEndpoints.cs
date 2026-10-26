using System.Text;
using System.Text.Json.Nodes;
using static HttpResultsX;

// The transcripts of runtime sessions (task C7b, gateway/Runtimes/Transcripts.cs, runner/fwrunner.sh): a runtime session posts its own lines, the
// operator reads them, and the page that shows them live.
sealed class TranscriptEndpoints(TranscriptStore transcripts) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/runtime/transcript", async (HttpContext c) => { return await Post(c); });
        routes.MapGet("/api/v1/sessions/{name}", async (HttpContext c, string name) => { return await Read(c, name); })
            .Admin(CallerView.Who, "session transcripts are read with the operator token");
        routes.MapGet("/sessions/{name}", (string name) => Page(name));
    }

    // The container posts its redacted lines with its own credential (w2 of a worker, r1 of a manager patrol); a credential posts to its own name only.
    // {lines:[...]}: at most 500 lines and 256 KiB a batch.
    async Task<IResult> Post(HttpContext ctx)
    {
        var who = ctx.Caller().Who;
        if (who.Kind is not ("worker" or "manager") || !TranscriptStore.ValidName(who.Name)) return Fail(403, "forbidden", "a runtime session posts its own transcript with its own credential", 30);
        var buf = new byte[256 * 1024 + 1]; var n = 0; int r0;
        while (n < buf.Length && (r0 = await ctx.Request.Body.ReadAsync(buf.AsMemory(n, buf.Length - n))) > 0) n += r0;
        if (n > 256 * 1024) return Fail(413, "too-large", "at most 256 KiB a batch", 2);
        List<string> lines = [];
        try { if ((JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, n)) as JsonObject)?["lines"] is JsonArray a) lines = a.Select(x => x?.ToString() ?? "").ToList(); } catch { }
        if (lines.Count == 0) return Fail(400, "usage", "the body is {\"lines\": [\"...\"]}", 2);
        if (lines.Count > transcripts.MaxBatchLines) return Fail(413, "too-large", $"at most {transcripts.MaxBatchLines} lines a batch", 2);
        var (taken, total) = await transcripts.Append(who.Name, lines);
        if (total < 0) return Results.Json(new { ok = false, code = "busy", reason = "the transcript store is full of unsaved sessions; try again", retryAfterSeconds = 30, exit = 20 }, statusCode: 503);
        return Results.Json(new { ok = true, taken, total, exit = 0 });
    }

    // The operator reads a session's transcript: after=N answers the lines from number N on (the page polls with it)
    async Task<IResult> Read(HttpContext ctx, string name)
    {
        if (!TranscriptStore.ValidName(name)) return Fail(400, "usage", "not a session name", 2);
        var after = int.TryParse(ctx.Request.Query["after"], out var af) && af > 0 ? af : 0;
        var o = await transcripts.Read(name, after);
        if (o == null) return Fail(404, "not-found", "no transcript of this session (none was posted, or it is older than the retention)", 12);
        o["exit"] = 0;
        return Results.Text(o.ToJsonString(), "application/json");
    }

    // The page of a session: the operator token from #token=<value> or this tab's connection to the dashboard; live while the session runs
    static IResult Page(string name) => TranscriptStore.ValidName(name) ? Results.Text(SessionPage.Html, "text/html; charset=utf-8") : Results.NotFound();
}
