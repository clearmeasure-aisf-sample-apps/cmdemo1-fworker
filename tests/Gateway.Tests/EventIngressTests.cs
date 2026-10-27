using Fworker.Contracts.Seams;
using Xunit;

namespace Gateway.Tests;

/// <summary>Seam S3 (task W1.6, gateway/EventIngress.cs): v1 hands a webhook event to the hub's stream and a sweep request to its queue, as the module and the pipeline did.</summary>
public sealed class EventIngressTests
{
    [Fact]
    public void ASweepRequestOfAnItemQueuesThatItem()
    {
        var hub = new EventHub(); IEventIngress ingress = new V1EventIngress(hub);
        ingress.Ingest(new SweepRequested(7)); ingress.Ingest(new SweepRequested(7)); ingress.Ingest(new SweepRequested(3));
        Assert.Equal((false, 2), hub.PendingSweep());
        Assert.Equal(new List<int> { 3, 7 }, hub.TakeSweepItems());
        var s = hub.SweepStatus(0);
        Assert.Equal(3L, (long)s["requests"]!); Assert.Equal(1L, (long)s["coalesced"]!);
    }

    [Fact]
    public void ASweepRequestOfNoItemAsksForAFullSweep()
    {
        var hub = new EventHub(); IEventIngress ingress = new V1EventIngress(hub);
        ingress.Ingest(new SweepRequested());
        Assert.Equal((true, 0), hub.PendingSweep());
    }

    [Fact]
    public void AWebhookEventIsPublishedOnTheHubUnchanged()
    {
        var hub = new EventHub(); IEventIngress ingress = new V1EventIngress(hub);
        ingress.Ingest(new WebhookEventIngested("{\"event\":\"pr.opened\",\"item\":5}"));
        var (_, backlog, _) = hub.Subscribe(0);
        Assert.Equal("{\"event\":\"pr.opened\",\"item\":5}", Assert.Single(backlog).Item2);
    }

    [Fact]
    public void TheWebhookModuleAndThePipelineTakeTheIngressNotTheHub()
    {
        foreach (var t in new[] { typeof(WebhookEndpoints), typeof(VerbPipeline) })
        {
            var types = t.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Single().GetParameters().Select(p => p.ParameterType).ToList();
            Assert.Contains(typeof(IEventIngress), types);
            Assert.DoesNotContain(typeof(EventHub), types);
        }
    }
}
