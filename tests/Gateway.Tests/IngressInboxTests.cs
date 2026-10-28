using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Correlation;
using Wolverine;
using Xunit;

namespace Gateway.Tests;

/// <summary>W4.1: the gateway side of the inbox: the ingress decorator (v1 first, then the inbox) and the handler's history record.</summary>
public sealed class IngressInboxGatewayTests
{
    sealed class Recorder : IEventIngress { public readonly List<IngressEvent> Seen = new(); public void Ingest(IngressEvent ev) => Seen.Add(ev); }

    [Fact]
    public async Task TheDecorator_LetsV1Act_ThenPublishesWebhookEventsOnly()
    {
        var v1 = new Recorder(); var sent = new List<IngressInboxMessage>(); var got = new TaskCompletionSource();
        var ingress = new InboxEventIngress(v1, new IngressInboxPort(m => { sent.Add(m); got.TrySetResult(); return Task.FromResult(true); }));
        ingress.Ingest(new SweepRequested(3));
        ingress.Ingest(new WebhookEventIngested("{\"event\":\"pr.opened\"}"));
        await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, v1.Seen.Count);
        var m = Assert.Single(sent);
        Assert.Equal(("webhook", "{\"event\":\"pr.opened\"}"), (m.Source, m.EventJson));
    }

    [Fact]
    public async Task TheDecorator_NeverThrows_WhenTheHostIsDownOrFails()
    {
        var v1 = new Recorder();
        new InboxEventIngress(v1, new IngressInboxPort(_ => throw new InvalidOperationException("db"))).Ingest(new WebhookEventIngested("{}"));
        new InboxEventIngress(v1, new IngressInboxPort(_ => Task.FromResult(false))).Ingest(new WebhookEventIngested("{}"));
        await Task.Delay(100);
        Assert.Equal(2, v1.Seen.Count);
    }

    [Fact]
    public void TheHandler_ReturnsAHistoryRecordForAnAcceptedEvent_AndNothingForADuplicate()
    {
        var p = new IngressInboxProcessor(new InMemoryIngressDedup(), new InMemoryCorrelationMap(), new RepoId("acme", "orders"));
        var json = new JsonObject { ["provider"] = "github", ["event"] = "pr.opened", ["item"] = 7, ["ref"] = "d-1", ["pr"] = 9, ["sha"] = "abc" }.ToJsonString();
        var r = IngressInboxHandler.Handle(new IngressInboxMessage("webhook", json), p, new Envelope());
        Assert.NotNull(r);
        Assert.Equal(("acme/orders#7", "ingress.inbox.webhook.pr.opened"), (r!.ItemKey, r.Type));
        Assert.Null(IngressInboxHandler.Handle(new IngressInboxMessage("webhook", json), p, new Envelope()));
    }
}
