using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Correlation;
using Wolverine;

// W4.1 (docs/design/fworker-wolverine-implementation.md W4): seam S3 into the Wolverine durable inbox. Only where the engine acts (Engine.V2Acts); tdd (shadow) and v1
// register none of it. v1's ingress does its part first, exactly as before; then each webhook event is published to the durable local queue "ingress-inbox", whose
// handler deduplicates it (the delivery, then the fact: IngressDedup) and writes the correlations the event implies (CorrelationWriters), and records the accepted
// event in ItemHistory through the same durable queue the shadow history uses. The poll of the board reconciler (W4.3) publishes the same message with source "poll",
// so a transition seen by both is taken in once. The correlation map is also written from verbs and runtimes (IngressCorrelations below).

/// <summary>The way into the host's inbox queue; registered only where the engine acts.</summary>
sealed class IngressInboxPort(Func<IngressInboxMessage, Task<bool>> publish) { public Task<bool> Publish(IngressInboxMessage m) => publish(m); }

/// <summary>S3 with the inbox: v1's ingress first, then the event goes to the durable inbox. A problem with the inbox never reaches the caller.</summary>
sealed class InboxEventIngress(IEventIngress v1, IngressInboxPort inbox) : IEventIngress
{
    public void Ingest(IngressEvent ev)
    {
        v1.Ingest(ev);
        if (ev is not WebhookEventIngested w) return;   // a sweep request is the sweeper's, not an event of an item
        _ = Task.Run(async () =>
        {
            try { if (!await inbox.Publish(new IngressInboxMessage("webhook", w.Json))) Console.Error.WriteLine("ingress inbox: host not up, event not stored"); }
            catch (Exception e) { Console.Error.WriteLine($"ingress inbox: {e.GetType().Name}"); }
        });
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

/// <summary>The correlation-map writers of the verbs and the runtimes (W4.1). Best effort and off the caller's thread: a database that does not answer drops the row, never the verb.</summary>
sealed class IngressCorrelations(ICorrelationMap map, RepoId home)
{
    public void FromVerb(string verb, int item, JsonObject output) => Write(CorrelationWriters.FromVerb(verb, item, output, home));
    public void FromRuntime(string runtime, string execution, int item) => Write(CorrelationWriters.FromRuntime(runtime, execution, item, home));

    void Write(IReadOnlyList<CorrelationRef> rows)
    {
        if (rows.Count == 0) return;
        _ = Task.Run(() =>
        {
            foreach (var r in rows)
                try { map.TryAdd(r); }
                catch (Exception e) { Console.Error.WriteLine($"correlation {r.Kind}: {e.GetType().Name}"); }
        });
    }
}
