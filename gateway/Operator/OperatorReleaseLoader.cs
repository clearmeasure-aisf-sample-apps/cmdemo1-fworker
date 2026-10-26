using System.Globalization;
using System.Text.Json.Nodes;

// The operator session's recorded releases (Operator.cs, docs/design/operator-session.md, gateway-composition.md 3 E): the records are refs in
// GitHub, written by the rule scripts, because the restart that a release causes would otherwise empty the count; they are read once at start,
// and until that read succeeded both operator checks answer 503 (they never answer "allowed" on a count they do not know). Moved out of
// Program.cs unchanged (C4b): the same rule script and input, filter, pause and log lines.
interface IOperatorReleaseLoader
{
    // completes once the day's releases are read, or at once where there is nothing to read; never fails
    Task Loaded { get; }
    bool IsLoaded { get; }
}

// A hosted service only to start the read with the host: the read is never gated by leadership and, as before, is not stopped with the host.
sealed class OperatorReleaseLoader : IOperatorReleaseLoader, IHostedService
{
    readonly Func<string, string, TimeSpan, Task<(int Code, string Output)>> run;
    readonly Action<DateTime, int, string> addRelease;
    readonly TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int started;

    // the pause after a failed read; a test shortens it
    internal double PauseSeconds { get; init; } = 5.0;

    // the UTC day of the release refs (refs/factory/<instance>/operator-release/<yyyyMMdd>/...)
    public static string Day(DateTime d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    // operatorEnabled: operator.enabled in the configuration; revocationsOff: FACTORY_TEST_SKIP_REVOCATIONS (a test gateway whose GitHub is a port
    // nothing listens on). Either way there is nothing to read. run: the rule-script call (Runner.RunAsync); addRelease: Plant.AddRelease.
    public OperatorReleaseLoader(bool operatorEnabled, bool revocationsOff, Func<string, string, TimeSpan, Task<(int Code, string Output)>> run, Action<DateTime, int, string> addRelease)
    {
        this.run = run; this.addRelease = addRelease;
        if (!operatorEnabled || revocationsOff) loaded.SetResult();
    }

    public Task Loaded => loaded.Task;
    public bool IsLoaded => loaded.Task.IsCompleted;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!loaded.Task.IsCompleted && Interlocked.Exchange(ref started, 1) == 0) _ = Task.Run(ReadLoop);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    async Task ReadLoop()
    {
        while (true)
        {
            try
            {
                var day = DateTime.UtcNow;
                var (code, output) = await run("operator-releases", new JsonObject { ["day"] = Day(day) }.ToJsonString(), TimeSpan.FromSeconds(120));
                if (code == 0 && JsonNode.Parse(output)?["releases"] is JsonArray list)
                {
                    foreach (var r in list)
                        if (r?["pr"] is JsonValue pv && pv.TryGetValue<int>(out var pr) && pr > 0 && r["version"]?.ToString() is { } ver && Plant.ValidVersion(ver)) addRelease(day, pr, ver);
                    loaded.SetResult(); return;
                }
                Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"operatorReleases\":\"not-loaded\",\"exit\":{code}}}");
            }
            catch (Exception e) { Console.Error.WriteLine($"operator releases: {e.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(PauseSeconds));
        }
    }
}
