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

    static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(5);

    static async Task<bool> Until(Func<bool> cond)
    {
        for (var i = 0; i < 500 && !cond(); i++) await Task.Delay(20);
        return cond();
    }

    [Fact]
    public async Task TheDecorator_NeverThrows_WhenTheHostIsDownOrFails()
    {
        var v1 = new Recorder();
        using var a = new InboxEventIngress(v1, new IngressInboxPort(_ => throw new InvalidOperationException("db")), retryFirst: Quick, retryMax: Quick, maxAttempts: 3);
        using var b = new InboxEventIngress(v1, new IngressInboxPort(_ => Task.FromResult(false)), retryFirst: Quick, retryMax: Quick, maxAttempts: 3);
        a.Ingest(new WebhookEventIngested("{}"));
        b.Ingest(new WebhookEventIngested("{}"));
        Assert.True(await Until(() => a.Dropped == 1 && b.Dropped == 1));   // given up after the attempts, the caller never saw a thing
        Assert.Equal(2, v1.Seen.Count);
    }

    [Fact]
    public async Task APublishThatFailsAtFirst_IsTriedAgain_AndEveryEventIsStoredOnceInOrder()
    {
        // #485: the publish runs after the webhook was answered; a host that is not up yet, or a database that does not answer, must not lose the event
        var v1 = new Recorder(); var stored = new List<string>(); var calls = 0;
        using var ingress = new InboxEventIngress(v1, new IngressInboxPort(m =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1) return Task.FromResult(false);                          // the host is not up
            if (n == 2) throw new TimeoutException("the database did not answer");
            lock (stored) stored.Add(m.EventJson);
            return Task.FromResult(true);
        }), retryFirst: Quick, retryMax: Quick);
        ingress.Ingest(new WebhookEventIngested("{\"n\":1}"));
        ingress.Ingest(new WebhookEventIngested("{\"n\":2}"));
        Assert.True(await Until(() => ingress.Stored == 2));
        lock (stored) Assert.Equal(["{\"n\":1}", "{\"n\":2}"], stored);   // the second event waited for the first: the order of the webhook holds
        Assert.Equal(0, ingress.Dropped);
    }

    [Fact]
    public async Task AnEventTheHostNeverTakes_IsGivenUpAfterItsAttempts_AndDoesNotBlockTheNext()
    {
        var stored = new List<string>(); var polluted = 0;
        using var ingress = new InboxEventIngress(new Recorder(), new IngressInboxPort(m =>
        {
            if (m.EventJson == "poison") { Interlocked.Increment(ref polluted); throw new InvalidOperationException("always"); }
            lock (stored) stored.Add(m.EventJson);
            return Task.FromResult(true);
        }), retryFirst: Quick, retryMax: Quick, maxAttempts: 4);
        ingress.Ingest(new WebhookEventIngested("poison"));
        ingress.Ingest(new WebhookEventIngested("good"));
        Assert.True(await Until(() => ingress.Stored == 1));
        Assert.Equal((4, 1L), (polluted, ingress.Dropped));
        lock (stored) Assert.Equal(["good"], stored);
    }

    [Fact]
    public async Task AFullQueue_DropsTheEventWithoutThrowing_AndCountsIt()
    {
        var gate = new TaskCompletionSource<bool>(); var inFlight = false;
        using var ingress = new InboxEventIngress(new Recorder(), new IngressInboxPort(_ => { inFlight = true; return gate.Task; }), capacity: 2, retryFirst: Quick, retryMax: Quick);
        ingress.Ingest(new WebhookEventIngested("{}"));
        Assert.True(await Until(() => inFlight));
        for (var i = 0; i < 9; i++) ingress.Ingest(new WebhookEventIngested("{}"));   // one in flight, two queued, the other seven do not fit
        Assert.Equal(7, ingress.Dropped);
        gate.SetResult(true);
        Assert.True(await Until(() => ingress.Stored == 3));
    }

    sealed class FlakyMap(int failures) : ICorrelationMap
    {
        public readonly List<CorrelationRef> Added = [];
        public int Calls;
        public bool TryAdd(CorrelationRef r)
        {
            if (Interlocked.Increment(ref Calls) <= failures) throw new TimeoutException("the database did not answer");
            lock (Added) Added.Add(r);
            return true;
        }
        public string? Resolve(string kind, string key) => null;
    }

    [Fact]
    public async Task ACorrelationWrite_ThatFails_IsTriedAgain_PerMapping_AndGivenUpAfterItsAttempts()
    {
        var home = new RepoId("acme", "orders");
        var waits = new List<TimeSpan>();
        var map = new FlakyMap(failures: 3);
        var writers = new IngressCorrelations(map, home, t => { lock (waits) waits.Add(t); return Task.CompletedTask; });
        await writers.FromVerb("open-pr", 7, new JsonObject { ["pr"] = 9, ["branch"] = "bot/7-x" });
        Assert.Equal(["pr", "branch"], map.Added.Select(r => r.Kind));   // both mappings stored although the first failed three times
        Assert.Equal([1, 2, 4], waits.Select(w => (int)w.TotalSeconds));   // growing pauses
        // a database that never answers: the verb is not failed, the rows are given up after the attempts
        var never = new FlakyMap(failures: int.MaxValue);
        await new IngressCorrelations(never, home, _ => Task.CompletedTask, maxAttempts: 4).FromRuntime("copilot", "task-1", 7);
        Assert.Equal((4, 0), (never.Calls, never.Added.Count));
    }

    [Fact]
    public async Task ACorrelationWrite_OfAVerbWithNothingToMap_IsNoWork()
    {
        var map = new FlakyMap(0);
        await new IngressCorrelations(map, new RepoId("acme", "orders")).FromVerb("sync-board", 7, new JsonObject());
        await new IngressCorrelations(map, new RepoId("acme", "orders")).FromRuntime("copilot", "", 7);
        Assert.Equal(0, map.Calls);
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
