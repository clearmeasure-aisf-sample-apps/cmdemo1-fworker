// The tick of the plant manager on demand (ManagerCopilot.cs, gateway-composition.md 3 E, task C5d): every few seconds, by the acting gateway only,
// one ManagerCopilot.Tick (follow the task that runs, or launch one when the trigger and the limits allow it). Moved out of Program.cs unchanged:
// the same interval (5 s when api.copilot.listSeconds is below 5, else 30 s), the same gate (ILeadership.MayAct), the same error line. It ends at
// ApplicationStopping, as the Task.Run in Program.cs did, or at the host's stopping token, whichever comes first.
// Registered (AddGatewayBackground) only where a ManagerCopilot is: the Copilot runtime on, owner mode, manager.enabled and messaging.enabled.
sealed class ManagerCopilotService : BackgroundService
{
    readonly ManagerCopilot manager;
    readonly ILeadership leader;
    readonly CancellationToken applicationStopping;
    public TimeSpan Interval { get; }

    public ManagerCopilotService(ManagerCopilot manager, ILeadership leader, CancellationToken applicationStopping, TimeSpan interval)
    { this.manager = manager; this.leader = leader; this.applicationStopping = applicationStopping; Interval = interval; }

    // the interval of today's loop: the Copilot runtime's list interval below 5 s means a test plant, ticked every 5 s
    public static TimeSpan IntervalFor(double copilotListSeconds) => TimeSpan.FromSeconds(copilotListSeconds < 5 ? 5 : 30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, applicationStopping);
        var ct = stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { return; }
            try { if (leader.MayAct) await manager.Tick(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { Console.Error.WriteLine($"manager copilot: {e.GetType().Name}"); }
        }
    }
}
