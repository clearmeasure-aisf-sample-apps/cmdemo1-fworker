using System.Text.Json.Nodes;

// The CI release of the Copilot agent's held workflow runs (CopilotCiRelease.cs, docs/design/copilot-cloud-agent.md) as a hosted service (task C5c,
// gateway-composition.md 3 E), moved out of Program.cs's Task.Run unchanged: every api.copilot.ciReleaseSeconds, by the acting gateway, while the
// Copilot mode is on; the same candidates, the same rule verb and journal lines, the same error line. It ends at ApplicationStopping, as the
// Task.Run did, or at the host's stopping token, whichever comes first. Registered (AddGatewayBackground) where the Copilot runtime is on and
// api.copilot.ciRelease is true.
sealed class CopilotCiReleaseService : BackgroundService
{
    readonly CopilotCiRelease release;
    readonly RuntimeManager manager;
    readonly CopilotCloudRuntime? runtime;
    readonly Plant plant;
    readonly Runner runner;
    readonly ILeadership leader;
    readonly DeliveryVerbBridge? bridge;   // W9.4: where the engine acts, the verb ci-release is answered by its v2 route
    readonly CancellationToken applicationStopping;
    public TimeSpan Interval { get; }

    public CopilotCiReleaseService(CopilotCiRelease release, RuntimeManager manager, CopilotCloudRuntime? runtime, Plant plant, Runner runner, ILeadership leader, CancellationToken applicationStopping, TimeSpan interval, DeliveryVerbBridge? bridge = null)
    { this.bridge = bridge; this.release = release; this.manager = manager; this.runtime = runtime; this.plant = plant; this.runner = runner; this.leader = leader; this.applicationStopping = applicationStopping; Interval = interval; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, applicationStopping);
        var ct = stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { return; }
            try
            {
                if (!leader.MayAct || !manager.On) continue;
                // every pass runs, with or without candidates: the held runs are listed and the App's permission is read (an unlabelled item routed
                // to copilot-cloud by runtimes.routing is a candidate through its held run's pull request: routeAll)
                // (with the owned items: an unlabelled item a Copilot task owns, from before tdd's default became cursor-cloud)
                var items = plant.CopilotItems().Concat(runtime?.Owners().Select(o => o.Item) ?? Enumerable.Empty<int>()).Distinct().Order().ToArray();
                var routeAll = manager.Settings.RouteFor("Development", "") == "copilot-cloud";
                await release.Pass(items, routeAll, (verb, stdin) => bridge is null ? runner.RunAsync(verb, stdin, TimeSpan.FromSeconds(120)) : bridge.RunAsync(verb, stdin, TimeSpan.FromSeconds(120)),
                    async (item, fields, key) => await runner.RunAsync("journal", new JsonObject { ["item"] = item, ["event"] = fields["refused"] is null ? "ci-released" : "ci-release-refused", ["fields"] = fields, ["key"] = key }.ToJsonString(), TimeSpan.FromSeconds(60)));
            }
            catch (Exception e) { Console.Error.WriteLine($"ci-release: {e.GetType().Name}"); }
        }
    }
}
