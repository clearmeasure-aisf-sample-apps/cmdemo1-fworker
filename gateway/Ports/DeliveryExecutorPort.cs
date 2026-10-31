using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Messaging;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Integrations.Octopus;
using Fworker.Workflows.Delivery;
using Wolverine;
using Wolverine.ErrorHandling;

// W9.4 (#356): the delivery train where the engine acts. In the shadow run (DeliveryShadowWiring) the train decides and its commands are only recorded; here the train is the promoter: the router and the train's handlers
// are attached, and PromoteRelease and AnswerSignOff are executed against Octopus (Fworker.Integrations.Octopus.OctopusPromoter, ported from Verbs.Promote.ps1) from a durable queue of their own, with the runtime
// commands' retry rule. v1's loop that called the rule script auto-promote (AutoPromoter) starts only for the environments the train does not promote (W9.10p, AutoPromoter.Runs): the app's uat and prod stay with it until delivery.autoPromote.trainActs, and the train decides them in shadow (DeliveryPolicy.Shadow; the ShadowCommand it sends is recorded by DeliveryActingShadowHandlers, nothing executes it), so exactly one of them promotes an environment. The events of the Octopus watcher (W4.2) reach the
// train through the same port the shadow run uses (ShadowOctopusPort) and the ingress that wraps it; what the train decides goes to the history. Nothing is registered unless the engine acts.

/// <summary>A delivery command was settled: executed, or refused for good (then <see cref="IntegrationFailed"/> goes with it). Public because Wolverine's generated code refers to it.</summary>
[MessageIdentity("delivery.command-finished")]
public sealed record DeliveryCommandFinished(string IdempotencyKey, string Name, bool Ok, string Error);

/// <summary>The runs of the delivery commands: the handler of the command, the retry of a transient failure, the messages of the outcome.</summary>
public sealed class DeliveryExecutorService(PromoteReleaseHandler promote, AnswerSignOffHandler answer, PromotionStateWriter? recorder = null)
{
    static RuntimeOutcome Settle<T>(T c, CommandResult r) where T : IntegrationCommand
    {
        if (r.Ok) return new(r, new DeliveryCommandFinished(c.IdempotencyKey, c.Name, true, ""), null);
        var failed = IntegrationCommandHandler<T>.FailureMessage(c, r);
        return new(r, failed == null ? null : new DeliveryCommandFinished(c.IdempotencyKey, c.Name, false, r.Error), failed);
    }

    static RuntimeOutcome Exhausted(IntegrationCommand c, string error)
    {
        var r = RuntimeOutcome.ExhaustedResult(error);
        return new(r, new DeliveryCommandFinished(c.IdempotencyKey, c.Name, false, r.Error), IntegrationFailed.From(c, r));
    }

    internal async Task<IReadOnlyList<object>> PromoteMessages(PromoteRelease c, int attempts, CancellationToken ct)
    {
        CommandResult? result = null;
        var messages = await RuntimeCommandService.Outcome(c, async () => Settle(c, result = await promote.Handle(c, ct)), e => Exhausted(c, e), attempts);
        // W9.10i: the deployment (made now, or found: either way the train's own request) is recorded on the state comments of the release's items, where v1's promotion reads that it is the gateway's. A fault here
        // is thrown, so the handler runs again: the ledger answers the command from its record and only the write is tried again, which is idempotent
        if (recorder is not null && result is { Ok: true, Execution.Length: > 0 }) await recorder.Promoted(c, result.Execution, DateTimeOffset.UtcNow, ct);
        return messages;
    }

    internal Task<IReadOnlyList<object>> AnswerMessages(AnswerSignOff c, int attempts, CancellationToken ct) =>
        RuntimeCommandService.Outcome(c, async () => Settle(c, await answer.Handle(c, ct)), e => Exhausted(c, e), attempts);

    public async Task RunPromote(PromoteRelease c, IMessageBus bus, int attempts, CancellationToken ct) { foreach (var m in await PromoteMessages(c, attempts, ct)) await bus.PublishAsync(m); }
    public async Task RunAnswer(AnswerSignOff c, IMessageBus bus, int attempts, CancellationToken ct) { foreach (var m in await AnswerMessages(c, attempts, ct)) await bus.PublishAsync(m); }
}

/// <summary>The handlers of the acting train's outputs. Public because Wolverine's generated code refers to them.</summary>
public static class DeliveryExecutorHandlers
{
    public const string Queue = "delivery-commands";
    public static Task Handle(PromoteRelease m, DeliveryExecutorService s, IMessageBus bus, Envelope envelope, CancellationToken ct) => s.RunPromote(m, bus, envelope.Attempts, ct);
    public static Task Handle(AnswerSignOff m, DeliveryExecutorService s, IMessageBus bus, Envelope envelope, CancellationToken ct) => s.RunAnswer(m, bus, envelope.Attempts, ct);

    public static void Handle(DeliveryCommandFinished m) =>
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "delivery-command", ["command"] = m.Name, ["key"] = m.IdempotencyKey, ["ok"] = m.Ok, ["error"] = m.Ok ? null : m.Error }.ToJsonString());

    /// <summary>What the train decided, once per change, for the history (the shadow run keeps it in the comparison; here it is the record of why a release waited or went).</summary>
    public static ShadowHistoryRecord Handle(TrainDecided m)
    {
        var detail = new JsonObject
        {
            ["action"] = m.Action, ["release"] = m.Release, ["items"] = new JsonArray(m.Items.Select(i => (JsonNode?)i).ToArray()), ["reasons"] = new JsonArray(m.Reasons.Select(r => (JsonNode?)r).ToArray()), ["replay"] = m.Replay,
        }.ToJsonString();
        return new ShadowHistoryRecord(m.Key, "delivery.train." + m.Action, detail.Length <= ShadowHistory.MaxDetailChars ? detail : detail[..ShadowHistory.MaxDetailChars], m.At);
    }
}

/// <summary>
/// W9.10i: the Sign-off a deployment of the train waits at, recorded on the release's first item (<c>merge.promoted.&lt;environment&gt;.signoff_wait_since</c>), so that after FACTORY_V2_OFF v1 counts the wait from when it began
/// and not from its own first look. The train publishes "signoff-waiting" once per change; the sign-off's task and since are read from the train's state. Public because Wolverine's generated code refers to it.
/// </summary>
public sealed class SignOffWaitRecorder(ITrainStore trains, PromotionStateWriter writer)
{
    public async Task Record(TrainDecided m, CancellationToken ct)
    {
        if (m.Action != "signoff-waiting" || m.Items.Count == 0) return;
        if ((await trains.Load(m.Key, ct))?.State.SignOffs.FirstOrDefault(x => x.Version == m.Release && !x.Answered) is not { } signOff) return;
        await writer.SignOffWaiting(m.Items, TrainKey.Split(m.Key).Environment, signOff.TaskId, signOff.Since, ct);
    }
}

/// <summary>The handler of the train's decisions for the Sign-off wait. Public because Wolverine's generated code refers to it.</summary>
public static class SignOffWaitHandler
{
    public static Task Handle(TrainDecided m, SignOffWaitRecorder recorder, CancellationToken ct) => recorder.Record(m, ct);
}

static class DeliveryActingWiring
{
    /// <summary>
    /// The acting train for an engine that acts: the same parts the shadow run is made of (policies, the reads, the delivered-to table), the executor of its commands and the port for the watcher's events.
    /// The returned port is the one the ingress publishes the Octopus events to; it is disabled when the train cannot be built (no GH_TOKEN or catalog).
    /// </summary>
    public static ShadowOctopusPort Attach(WolverineHostService host, GatewayComposition c, Func<Fworker.Contracts.Catalog.ISystemCatalog?> catalog, string connectionString, Func<string, string?> env, Func<bool> mayAct, IIdempotencyLedger ledger, DeliveryShadowHolder? shadowHolder = null, PromotionStateWriter? promotions = null)
    {
        var parts = catalog() is { } cat ? DeliveryShadowWiring.Make(c, cat, connectionString, env) : null;
        if (parts is null) return new ShadowOctopusPort(_ => Task.FromResult(false), false);
        var root = c.Instance.Root as JsonObject ?? new JsonObject();
        var octopus = root["octopus"] as JsonObject ?? new JsonObject();
        var names = (octopus["environments"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>();
        var http = new HttpClient(new CauseHandler { InnerHandler = new HttpClientHandler() }) { BaseAddress = new Uri((octopus["url"]?.ToString() ?? "http://localhost").TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        // the key that may create deployments and answer interruptions: the runbook service account's, else the reader key
        var settings = new OctopusPromoteSettings(octopus["space"]?.ToString() ?? "", names, () => env("OCTOPUS_RUNBOOK_API_KEY") is { Length: > 0 } k ? k : env("OCTOPUS_API_KEY"), mayAct);
        var promoter = new OctopusPromoter(http, settings);
        var service = new DeliveryExecutorService(new PromoteReleaseHandler(ledger, promoter), new AnswerSignOffHandler(ledger, promoter), promotions);
        // W9.10p: the shadow of the environments v1's AutoPromoter still promotes (uat and prod until delivery.autoPromote.trainActs): the AutoPromoter's passes (AddGatewayBackground) and the status endpoints find it through the holder
        if (shadowHolder is not null) shadowHolder.Shadow = parts.Shadow;
        RollbackWiring.Attach(host, c, env, mayAct, ledger);   // W8.3: the executor of the train's incident rollback (RollbackRelease) and the migration probe it takes; the executor refuses every environment but tdd
        var previous = host.RuntimeCommands;
        host.RuntimeCommands = (services, w) =>
        {
            previous?.Invoke(services, w);
            Configure(services, w, parts, new SqlTrainStore(() => ItemMergedWiring.Open(parts.ConnectionString)), service, promotions: promotions);
        };
        return new ShadowOctopusPort(ev => DeliveryShadowWiring.Publish(host, ev), true);
    }

    /// <summary>
    /// What the acting train adds to the Wolverine host: its services, its handlers, the executor of its commands and the recording of the train's shadow (<see cref="DeliveryActingShadowHandlers"/>: the decisions and
    /// the ShadowCommands of an environment v1 still promotes, which nothing executes). Apart from <see cref="Attach"/> so that a host test builds the same registration with a store of its own.
    /// </summary>
    internal static void Configure(IServiceCollection services, WolverineOptions w, DeliveryShadowParts parts, ITrainStore store, DeliveryExecutorService service, bool durable = true, PromotionStateWriter? promotions = null)
    {
        services.AddSingleton(parts.Policies);
        services.AddSingleton(parts.Reads);
        services.AddSingleton<IDeliveredLookup>(parts.Delivered);
        services.AddSingleton(parts.Shadow);
        services.AddSingleton(store);
        services.AddSingleton<DeliveryTrainService>();
        services.AddSingleton<DeliveryTrainRouter>();
        services.AddSingleton(service);
        // W9.10i: where there is a state comment to write, a Sign-off the train waits at is recorded for v1 (the deployments themselves are recorded by the executor)
        if (promotions is not null) { services.AddSingleton(new SignOffWaitRecorder(store, promotions)); w.Discovery.IncludeType(typeof(SignOffWaitHandler)); }
        w.Discovery.IncludeType(typeof(DeliveryTrainHandlers));
        w.Discovery.IncludeType(typeof(DeliveryTrainRouting));
        w.Discovery.IncludeType(typeof(DeliveryExecutorHandlers));
        w.Discovery.IncludeType(typeof(DeliveryActingShadowHandlers));
        // the Octopus events and the trains' inputs each travel one queue, one at a time and in order (DeliveryTopology says why)
        DeliveryTopology.Route(w);
        w.PublishMessage<PromoteRelease>().ToLocalQueue(DeliveryExecutorHandlers.Queue);
        w.PublishMessage<AnswerSignOff>().ToLocalQueue(DeliveryExecutorHandlers.Queue);
        w.PublishMessage<DeliveryCommandFinished>().ToLocalQueue(DeliveryExecutorHandlers.Queue);
        if (durable) w.LocalQueue(DeliveryExecutorHandlers.Queue).UseDurableInbox();
        // the same backstop as the shadow run: GitHub or the app not answering is tried again with growing pauses and then scheduled again every few minutes, never to the dead letters
        w.Policies.OnException<HttpRequestException>().Or<TaskCanceledException>().Or<TrainConflictException>().Or<TimeoutException>().Or<System.Data.Common.DbException>().RetryWithCooldown(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)).Then.ScheduleRetry(TimeSpan.FromMinutes(5));
    }
}
