// The sweep of the worker sessions at /mcp (WorkerSessions.Sweep, gateway-composition.md 3 E, task C5d): every 30 s, sessions without a call for
// timing.mcpIdleMinutes end and ended ones are forgotten after 24 hours. Never gated on the lease: every process ends its own idle sessions.
// Moved out of Program.cs, where it was a `while (true)` with no cancellation; it now ends with the host's stopping token. The interval and the
// error line are today's.
sealed class McpSessionSweeper : BackgroundService
{
    readonly Func<WorkerSessions?> sessions;
    public TimeSpan Interval { get; }

    // sessions: the worker sessions the MCP tools use (a singleton of the container, AddGatewayMcp), resolved at each pass.
    public McpSessionSweeper(Func<WorkerSessions?> sessions, TimeSpan interval) { this.sessions = sessions; Interval = interval; }

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
            try { sessions()?.Sweep(); } catch (Exception e) { Console.Error.WriteLine($"mcp sessions: {e.Message}"); }
        }
    }
}
