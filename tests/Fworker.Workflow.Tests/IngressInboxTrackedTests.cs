using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Correlation;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W4.1 on a Wolverine tracked session: IngressInboxMessage goes through the durable local queue to a handler that runs the processor on the SQL stores (the gateway's
/// handler, gateway/Ports/IngressInbox.cs, is the same call). They return at once where FACTORY_TEST_SQL is unset.
/// </summary>
public class IngressInboxTrackedTests
{
    static readonly RepoId Home = new("acme", "orders");

    sealed class InboxHandler(IngressInboxProcessor processor, List<string> outcomes) : MessageHandler<IngressInboxMessage>
    {
        protected override Task HandleAsync(IngressInboxMessage m, MessageContext context, CancellationToken cancellation)
        {
            var o = processor.Process(m, context.Envelope!.Id);
            lock (outcomes) outcomes.Add(o.Reason);
            return Task.CompletedTask;
        }
    }

    static Func<string, Action<WolverineOptions>> Wire(List<string> outcomes) => cs => w =>
    {
        w.AddMessageHandler(new InboxHandler(new IngressInboxProcessor(new SqlIngressDedup(cs), new SqlCorrelationMap(cs), Home), outcomes));
        w.PublishMessage<IngressInboxMessage>().ToLocalQueue("harness");
    };

    static string Ev(string name, string delivery) =>
        new JsonObject { ["provider"] = "github", ["event"] = name, ["item"] = 7, ["ref"] = delivery, ["pr"] = 9, ["sha"] = "abc123" }.ToJsonString();

    [Fact]
    public async Task WebhookTwiceAndThenPoll_AreTakenInOnce_WithOneCorrelationPerThing()
    {
        if (!TrackedSessionHost.Available) return;
        var outcomes = new List<string>();
        await using var h = await TrackedSessionHost.StartAsync(Wire(outcomes));
        await h.TrackSend(new IngressInboxMessage("webhook", Ev("pr.opened", "d-1")));
        await h.TrackSend(new IngressInboxMessage("webhook", Ev("pr.opened", "d-1")));
        await h.TrackSend(new IngressInboxMessage("poll", Ev("pr.opened", "poll:sweep-1")));
        Assert.Equal(["accepted", "duplicate-delivery", "duplicate-fact"], outcomes);
        Assert.Equal(2, await h.Count("SELECT COUNT(*) FROM factory.Correlations WHERE ItemKey = 'acme/orders#7'"));
        Assert.Equal(2, await h.Count("SELECT COUNT(*) FROM factory.IngressDedup"));
        Assert.Equal("acme/orders#7", new SqlCorrelationMap(h.ConnectionString).Resolve("pr", "acme/orders!9"));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    [Fact]
    public async Task ARedeliveryOfOneMessage_PassesTheClaims_AndADifferentMessageDoesNot()
    {
        if (!TrackedSessionHost.Available) return;
        await using var h = await TrackedSessionHost.StartAsync((Action<WolverineOptions>?)null);
        var dedup = new SqlIngressDedup(h.ConnectionString); var a = Guid.NewGuid();
        Assert.True(dedup.Claim("delivery", "github|x", a));
        Assert.True(dedup.Claim("delivery", "github|x", a));
        Assert.False(dedup.Claim("delivery", "github|x", Guid.NewGuid()));
        Assert.Equal(1, dedup.Purge(TimeSpan.Zero));
    }
}
