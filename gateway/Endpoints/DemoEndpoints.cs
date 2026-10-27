using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// The demo video of a `video` item (task C7b, issue #42): the owner sends the MP4 as the raw request body with its item token; the gateway streams it
// to a temp file (never into memory, a log or the verb's JSON) and runs the rule verb publish-demo, which creates release demo-<item> if missing and
// uploads or replaces the asset with the gateway's GitHub credential. Not POST /api/v1/verbs: that route reads the body as text and pipes it to
// the rule script as JSON, and Kestrel caps a body at about 30 MB by default. The right to publish goes with the right to record the verdict
// (validation-verdict of the owner role) or an explicit publish-demo in the role's verb list. Not held under the plant's gate; bounded by a timeout.
sealed class DemoEndpoints(GatewayOptions gateway, Plant plant, Runner runner) : IEndpointModule
{
    const long DemoMaxBytes = 100L * 1024 * 1024;
    // at most two uploads at once (disk: 2 x 100 MB, two runner slots); a third is refused busy, not queued
    readonly SemaphoreSlim demoGate = new(2, 2);

    public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/v1/demo/{item:int}", async (int item, HttpContext c) => { return await Publish(item, c); });

    async Task<IResult> Publish(int item, HttpContext ctx)
    {
        IResult No(int status, string code, string reason, int exit) => Results.Text(JsonSerializer.Serialize(new { ok = false, verb = "publish-demo", code, reason, exit }), "application/json", Encoding.UTF8, status);
        var who = ctx.Caller().Who;
        var probe = new JsonObject { ["item"] = item };
        var (authorized, refusal) = Auth.AuthorizeVerb(who, "publish-demo", probe);
        if (authorized == null && who.Kind == "item") (authorized, refusal) = Auth.AuthorizeVerb(who, "validation-verdict", new JsonObject { ["item"] = item });
        if (authorized == null)
        {
            Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"publish-demo\",\"caller\":\"{who.Kind}:{who.Name}\",\"refused\":{JsonSerializer.Serialize(refusal)}}}");
            return No(403, "forbidden", refusal ?? "forbidden", 30);
        }
        if (ctx.Request.ContentLength is > DemoMaxBytes) return No(413, "too-large", $"the video is larger than {DemoMaxBytes / 1024 / 1024} MB", 2);
        if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } lim) lim.MaxRequestBodySize = DemoMaxBytes + 1;
        if (!await demoGate.WaitAsync(0)) return Results.Text(JsonSerializer.Serialize(new { ok = false, verb = "publish-demo", code = "busy", reason = "another demo upload is running; retry", retryAfterSeconds = 30, exit = 20 }), "application/json", Encoding.UTF8, 429);
        var tmp = Path.Combine(Path.GetTempPath(), $"demo-{item}-{Guid.NewGuid():N}.mp4");
        try
        {
            try { if (new DriveInfo(Path.GetTempPath()).AvailableFreeSpace < 2 * DemoMaxBytes) return No(507, "no-space", "the gateway has too little free space for a video now; retry later", 20); } catch (Exception) { }
            long size = 0; var head = new byte[12]; var headLen = 0;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted))
            {
                cts.CancelAfter(TimeSpan.FromMinutes(10));
                await using var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                var buf = new byte[81920]; int n;
                try
                {
                    while ((n = await ctx.Request.Body.ReadAsync(buf, cts.Token)) > 0)
                    {
                        size += n; if (size > DemoMaxBytes) return No(413, "too-large", $"the video is larger than {DemoMaxBytes / 1024 / 1024} MB", 2);
                        if (headLen < head.Length) { var k = Math.Min(head.Length - headLen, n); Array.Copy(buf, 0, head, headLen, k); headLen += k; }
                        await fs.WriteAsync(buf.AsMemory(0, n), cts.Token);
                    }
                }
                catch (Microsoft.AspNetCore.Http.BadHttpRequestException) { return No(413, "too-large", $"the video is larger than {DemoMaxBytes / 1024 / 1024} MB", 2); }
                catch (OperationCanceledException) { return No(408, "upload-timeout", "the video did not arrive within 10 minutes", 20); }
            }
            // an MP4 (ISO base media file) starts with a box whose type, bytes 4 to 7, is "ftyp"
            if (size == 0) return No(400, "empty", "the request body holds no video: send the MP4 file as the body", 2);
            if (headLen < 8 || Encoding.ASCII.GetString(head, 4, 4) != "ftyp") return No(415, "not-mp4", "the file is not an MP4 (its header has no ftyp box)", 2);
            authorized["file"] = tmp; authorized["size"] = size;
            if (gateway.OwnerMode && who.Kind == "item") plant.NoteActivity(who.Name, who.Item, true);
            var (code, output) = await runner.RunAsync("publish-demo", authorized.ToJsonString(), TimeSpan.FromMinutes(10));
            Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"publish-demo\",\"item\":\"{item}\",\"holder\":\"{authorized["holder"]}\",\"caller\":\"{who.Kind}:{who.Role}\",\"bytes\":{size},\"exit\":{code}}}");
            output = RuleScriptResult.OrNoOutput(output, code);
            // here an exit of 30 (forbidden) is a 403 as well
            var status = code == 30 ? 403 : RuleScriptResult.Status(code);
            return Results.Text(output, "application/json", Encoding.UTF8, status);
        }
        finally { try { File.Delete(tmp); } catch { } demoGate.Release(); }
    }
}
