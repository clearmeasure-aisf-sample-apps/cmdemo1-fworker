using Fworker.Contracts.Commands;
using Fworker.Integrations.Commands;
using Wolverine;
using Wolverine.ErrorHandling;
using Fworker.Contracts.Seams;

// W3.1: the host side of the runtime integration commands. The commands (StartRuntimeSession, StopRuntimeSession, RunRunbook) are published to the durable local
// queue "runtime-commands" by the RuntimeManager when the engine mode is V2; the handlers below run them through the command base of Fworker.Integrations
// (classification, idempotency key) and publish the result message, whose handlers call back into the RuntimeManager. v1 never reaches this code.
// The handlers are public static because Wolverine's generated code refers to them; the service they take is the host's (WolverineHostService registers it).
public sealed class RuntimeCommandService
{
    readonly StartRuntimeSessionHandler start;
    readonly StopRuntimeSessionHandler stop;
    readonly RunRunbookHandler runbook;
    readonly Func<string, IRuntimeManager?> managerOf;

    internal RuntimeCommandService(StartRuntimeSessionHandler start, StopRuntimeSessionHandler stop, RunRunbookHandler runbook, Func<string, IRuntimeManager?> managerOf)
    { this.start = start; this.stop = stop; this.runbook = runbook; this.managerOf = managerOf; }

    public Task RunStart(StartRuntimeSession c, IMessageBus bus, CancellationToken ct) => Deliver(c, start.Process(c, ct), bus);
    public Task RunStop(StopRuntimeSession c, IMessageBus bus, CancellationToken ct) => Deliver(c, stop.Process(c, ct), bus);
    public Task RunRunbook(RunRunbook c, IMessageBus bus, CancellationToken ct) => Deliver(c, runbook.Process(c, ct), bus);

    // A failure that is tried again throws, and Wolverine's rule for CommandRetryException retries the envelope; otherwise the result message goes back through the outbox.
    static async Task Deliver(IntegrationCommand c, Task<RuntimeOutcome> run, IMessageBus bus)
    {
        var o = await run;
        if (o.Retry) throw new CommandRetryException($"{c.Name}: {o.Result.Failure} {o.Result.Error}");
        if (o.Failed != null) await bus.PublishAsync(o.Failed);
        if (o.Message != null) await bus.PublishAsync(o.Message);
    }

    public Task Started(RuntimeSessionStarted m) => managerOf(m.Runtime)?.CompleteStart(m.WorkerName, m.Item, m.Phase, new ProvisionResult(true, m.Execution, "", m.Url)) ?? Task.CompletedTask;
    public Task StartFailed(RuntimeStartFailed m) => managerOf(m.Runtime)?.CompleteStart(m.WorkerName, m.Item, m.Phase, new ProvisionResult(false, "", m.Error)) ?? Task.CompletedTask;
    public void Stopped(RuntimeSessionStopped m) => managerOf(m.Runtime)?.CompleteStop(m.WorkerName, m.Ok, m.Error);
}

public static class RuntimeCommandHandlers
{
    public const string Queue = "runtime-commands";
    public static Task Handle(StartRuntimeSession m, RuntimeCommandService s, IMessageBus bus, CancellationToken ct) => s.RunStart(m, bus, ct);
    public static Task Handle(StopRuntimeSession m, RuntimeCommandService s, IMessageBus bus, CancellationToken ct) => s.RunStop(m, bus, ct);
    public static Task Handle(RunRunbook m, RuntimeCommandService s, IMessageBus bus, CancellationToken ct) => s.RunRunbook(m, bus, ct);
    public static Task Handle(RuntimeSessionStarted m, RuntimeCommandService s) => s.Started(m);
    public static Task Handle(RuntimeStartFailed m, RuntimeCommandService s) => s.StartFailed(m);
    public static void Handle(RuntimeSessionStopped m, RuntimeCommandService s) => s.Stopped(m);
    // the run of a runbook has no manager to tell yet (the first caller is a later task), and a failure for the saga is logged until the sagas read it
    public static void Handle(RunbookRunFinished m) => Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "runtime-command", ["event"] = "runbook-run", ["ok"] = m.Ok, ["runbook"] = m.Runbook, ["error"] = m.Error }.ToJsonString());
    public static void Handle(IntegrationFailed m) => Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "runtime-command", ["event"] = "integration-failed", ["command"] = m.CommandName, ["kind"] = m.Kind.ToString(), ["error"] = m.Error }.ToJsonString());
}

// W3.1: the one place the runtime commands are wired, and only where the engine mode is V2 (tdd is Shadow, prod and uat are v1: nothing changes there).
// The managers then dispatch their start and stop through the host's outbox; the host gets the handlers, the durable queue and the retry rule.
// The idempotency ledger is the SQL one the gateway passes (#481); in memory only without a database (tests).
static class RuntimeCommandWiring
{
    public static void Attach(WolverineHostService host, RuntimeRegistry runtimes, Fworker.Contracts.Seams.ICommandPort adapters, IIdempotencyLedger? ledger = null)
    {
        foreach (var m in runtimes.Managers)
        {
            var kind = m.Kind;
            m.DispatchStart = t => Publish(host, new StartRuntimeSession(RuntimeKeys.Start(kind, t.Name), kind, t.Name, t.Item, t.Phase, t.Profile, t.Nonce, t.Role));
            m.DispatchStop = (name, reason) => Publish(host, new StopRuntimeSession(RuntimeKeys.Stop(kind, name), kind, name, reason));
        }
        ledger ??= new InMemoryIdempotencyLedger();   // SQL where the gateway has its database (SqlCommandStores)
        IRunbookRunner runner = new OctopusRunner(runtimes.OctopusRuntime);
        var service = new RuntimeCommandService(new StartRuntimeSessionHandler(ledger, adapters), new StopRuntimeSessionHandler(ledger, adapters), new RunRunbookHandler(ledger, runner), kind => runtimes.ByKind(kind));
        host.RuntimeCommands = (services, w) =>
        {
            services.AddSingleton(service);
            w.Discovery.IncludeType(typeof(RuntimeCommandHandlers));
            w.PublishMessage<StartRuntimeSession>().ToLocalQueue(RuntimeCommandHandlers.Queue);
            w.PublishMessage<StopRuntimeSession>().ToLocalQueue(RuntimeCommandHandlers.Queue);
            w.PublishMessage<RunRunbook>().ToLocalQueue(RuntimeCommandHandlers.Queue);
            w.PublishMessage<RuntimeSessionStarted>().ToLocalQueue(RuntimeCommandHandlers.Queue);
            w.PublishMessage<RuntimeStartFailed>().ToLocalQueue(RuntimeCommandHandlers.Queue);
            w.PublishMessage<RuntimeSessionStopped>().ToLocalQueue(RuntimeCommandHandlers.Queue);
            w.PublishMessage<RunbookRunFinished>().ToLocalQueue(RuntimeCommandHandlers.Queue);
            w.PublishMessage<IntegrationFailed>().ToLocalQueue(RuntimeCommandHandlers.Queue);
            w.LocalQueue(RuntimeCommandHandlers.Queue).UseDurableInbox();
            // a transient or rate-limited command is tried again with growing pauses, then dead-lettered
            w.Policies.OnException<CommandRetryException>().RetryWithCooldown(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)).Then.MoveToErrorQueue();
        };
    }

    static async Task Publish<T>(WolverineHostService host, T command) where T : notnull
    {
        if (!await host.PublishAsync(command)) throw new InvalidOperationException("the Wolverine host is not up");
    }

    sealed class OctopusRunner(OctopusRunbookRuntime? runtime) : IRunbookRunner
    {
        public Task<CommandResult> RunAsync(RunRunbook command, CancellationToken ct) =>
            runtime == null ? Task.FromResult(CommandResult.Failed(FailureKind.Permanent, "runtime off: octopus")) : runtime.RunCommand(command, ct);
    }
}

// W3.3: the Copilot launch under a reservation (src/Fworker.Integrations/Commands/CopilotLaunch.cs). The port wraps the adapters' port for the Copilot runtime only;
// the lookup asks GitHub's agent tasks for the task of an item whose launch outcome is unknown. The reservations are in SQL (#481): an unsettled one survives a restart and
// the reconciler settles it.
sealed class CopilotTaskLookup(RuntimeRegistry runtimes) : ICopilotTaskLookup
{
    public async Task<(string TaskId, string Url)?> FindAsync(int item, string phase, DateTime since, CancellationToken ct) =>
        runtimes.CopilotRuntime == null ? null : await runtimes.CopilotRuntime.TaskForItem(item, since, ct);
}

sealed class CopilotReconcileService(CopilotReconciler reconciler) : BackgroundService
{
    public static readonly TimeSpan MinAge = TimeSpan.FromMinutes(2), Every = TimeSpan.FromMinutes(1);
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Every);
        while (await timer.WaitForNextTickAsync(stop))
        {
            try
            {
                foreach (var r in await reconciler.Run(MinAge, stop))
                    Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "copilot-reservation", ["item"] = r.Item, ["phase"] = r.Phase, ["outcome"] = r.Outcome.ToString() }.ToJsonString());
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) { Console.Error.WriteLine($"copilot reconcile: {e.GetType().Name}"); }
        }
    }
}
