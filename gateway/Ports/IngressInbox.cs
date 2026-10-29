using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Correlation;
using Wolverine;

// W4.1 (docs/design/fworker-wolverine-implementation.md W4): seam S3 into the Wolverine durable inbox. Where the engine acts (Engine.V2Acts) and, since W5.2, in the shadow run of the delivery train (tdd); v1
// registers none of it. v1's ingress does its part first, exactly as before; then each webhook event is published to the durable local queue "ingress-inbox", whose
// handler deduplicates it (the delivery, then the fact: IngressDedup) and writes the correlations the event implies (CorrelationWriters), and records the accepted
// event in ItemHistory through the same durable queue the shadow history uses. The poll of the board reconciler (W4.3) publishes the same message with source "poll",
// so a transition seen by both is taken in once. The correlation map is also written from verbs and runtimes (IngressCorrelations below).

/// <summary>The way into the host's inbox queue; registered only where the engine acts.</summary>
sealed class IngressInboxPort(Func<IngressInboxMessage, Task<bool>> publish) { public Task<bool> Publish(IngressInboxMessage m) => publish(m); }

/// <summary>
/// S3 with the inbox: v1's ingress first, then the event goes to the durable inbox. A problem with the inbox never reaches the caller. The publish happens after the webhook was answered, so it
/// is tried again (#485): an event waits in a bounded queue, one at a time in the order it came, and is offered to the host with growing pauses (a host that is restarting, a leader change,
/// a database that does not answer) until it is stored or <c>maxAttempts</c> are spent; then it is dropped with a line in the log, and the board poll (W4.3) is the backstop for a merge. Once
/// <see cref="IngressInboxPort.Publish"/> answers true the event is in the host's durable inbox; what is still lost is an event that was waiting in this queue when the process died.
/// </summary>
sealed class InboxEventIngress : IEventIngress, IDisposable
{
    public const int DefaultCapacity = 1000, DefaultAttempts = 12;
    public static readonly TimeSpan RetryFirst = TimeSpan.FromSeconds(1), RetryMax = TimeSpan.FromSeconds(30);

    readonly IEventIngress v1;
    readonly CancellationTokenSource stop = new();
    readonly System.Threading.Channels.Channel<string> queue;
    long stored, dropped;

    public InboxEventIngress(IEventIngress v1, IngressInboxPort inbox, int capacity = DefaultCapacity, TimeSpan? retryFirst = null, TimeSpan? retryMax = null, int maxAttempts = DefaultAttempts)
    {
        this.v1 = v1;
        queue = System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(capacity) { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait, SingleReader = true });
        var first = retryFirst ?? RetryFirst; var max = retryMax ?? RetryMax;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var json in queue.Reader.ReadAllAsync(stop.Token))
                {
                    var done = false; var pause = first;
                    for (var attempt = 1; !done && attempt <= maxAttempts && !stop.IsCancellationRequested; attempt++)
                    {
                        try
                        {
                            if (await inbox.Publish(new IngressInboxMessage("webhook", json))) { done = true; Interlocked.Increment(ref stored); break; }
                            Console.Error.WriteLine($"ingress inbox: host not up, event not stored yet (attempt {attempt} of {maxAttempts})");
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"ingress inbox: {e.GetType().Name} (attempt {attempt} of {maxAttempts})"); }
                        if (attempt == maxAttempts) break;
                        await Task.Delay(pause, stop.Token);
                        pause = TimeSpan.FromTicks(Math.Min(max.Ticks, pause.Ticks * 2));
                    }
                    if (!done && !stop.IsCancellationRequested) { Interlocked.Increment(ref dropped); Console.Error.WriteLine("ingress inbox: event dropped after its attempts, the board poll is the backstop"); }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Events stored in the host's inbox, and events given up (attempts spent, or the queue full).</summary>
    public long Stored => Interlocked.Read(ref stored);
    public long Dropped => Interlocked.Read(ref dropped);

    public void Dispose() { stop.Cancel(); (v1 as IDisposable)?.Dispose(); }

    public void Ingest(IngressEvent ev)
    {
        v1.Ingest(ev);
        if (ev is not WebhookEventIngested w) return;   // a sweep request is the sweeper's, not an event of an item
        // a full queue (a host that has been down for long) drops the event; v1 has done its part and the poll finds the merge again
        if (!queue.Writer.TryWrite(w.Json)) { Interlocked.Increment(ref dropped); Console.Error.WriteLine($"ingress inbox: queue full ({queue.Reader.Count} events), event not stored"); }
    }
}

/// <summary>The handler of the durable queue "ingress-inbox". Public because Wolverine's generated code refers to it.</summary>
public static class IngressInboxHandler
{
    public const string Queue = "ingress-inbox";

    /// <summary>A duplicate returns nothing; an accepted event returns its history record, which Wolverine cascades through the outbox.</summary>
    public static ShadowHistoryRecord? Handle(IngressInboxMessage message, IngressInboxProcessor processor, Envelope envelope)
    {
        var outcome = processor.Process(message, envelope.Id);
        if (!outcome.Accepted) return null;
        var ev = JsonNode.Parse(message.EventJson) as JsonObject;
        var name = ev?["event"]?.ToString() is { Length: > 0 } n ? n : "unknown";
        var item = ev?["item"] is JsonValue v && v.TryGetValue<int>(out var i) && i > 0 ? i : 0;
        // a poll's card names its repository (W4.3: the reconciler reads the cards of every repository of the catalog); a webhook event is the home repository's
        var repo = ev?["repository"]?.ToString() is { } rs && Fworker.Contracts.Ids.RepoId.TryParse(rs, out var rid) ? rid : processor.Home;
        var key = item > 0 ? new ItemId(repo, item).ToString() : "board";
        return new ShadowHistoryRecord(key, $"ingress.inbox.{message.Source}.{(name.Length <= 100 ? name : name[..100])}", message.EventJson.Length <= ShadowHistory.MaxDetailChars ? message.EventJson : message.EventJson[..ShadowHistory.MaxDetailChars], DateTimeOffset.UtcNow);
    }
}

static class IngressInboxWiring
{
    public static void Attach(WolverineHostService host, IngressInboxProcessor processor)
    {
        var previous = host.RuntimeCommands;
        host.RuntimeCommands = (services, w) =>
        {
            previous?.Invoke(services, w);
            services.AddSingleton(processor);
            w.Discovery.IncludeType(typeof(IngressInboxHandler));
            w.PublishMessage<IngressInboxMessage>().ToLocalQueue(IngressInboxHandler.Queue);
            w.LocalQueue(IngressInboxHandler.Queue).UseDurableInbox().MaximumParallelMessages(2);
        };
    }
}

/// <summary>
/// The correlation-map writers of the verbs and the runtimes (W4.1). Off the caller's thread: a database that does not answer never fails the verb. A failed write is tried again with growing
/// pauses (#485), each mapping on its own, until it is stored or the attempts are spent (the log says so); a pull request mapping that stays unwritten is written again when the inbox takes in the
/// next event that names it, the other kinds are not. The returned task ends when the rows are stored or given up (the callers let it run).
/// </summary>
sealed class IngressCorrelations(ICorrelationMap map, RepoId home, Func<TimeSpan, Task>? delay = null, int maxAttempts = IngressCorrelations.DefaultAttempts)
{
    public const int DefaultAttempts = 8;
    readonly Func<TimeSpan, Task> wait = delay ?? (t => Task.Delay(t));

    public Task FromVerb(string verb, int item, JsonObject output) => Write(CorrelationWriters.FromVerb(verb, item, output, home));
    public Task FromRuntime(string runtime, string execution, int item) => Write(CorrelationWriters.FromRuntime(runtime, execution, item, home));

    Task Write(IReadOnlyList<CorrelationRef> rows)
    {
        if (rows.Count == 0) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            foreach (var r in rows) await WriteOne(r);
        });
    }

    async Task WriteOne(CorrelationRef r)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { map.TryAdd(r); return; }
            catch (Exception e)
            {
                Console.Error.WriteLine($"correlation {r.Kind}: {e.GetType().Name} (attempt {attempt} of {maxAttempts})");
                if (attempt >= maxAttempts) return;
            }
            await wait(TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(attempt - 1, 6))));
        }
    }
}
