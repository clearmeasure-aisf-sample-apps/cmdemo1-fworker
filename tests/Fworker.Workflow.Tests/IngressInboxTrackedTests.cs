using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Correlation;
using Microsoft.EntityFrameworkCore;
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
        w.AddMessageHandler(new InboxHandler(new IngressInboxProcessor(new SqlIngressDedup(cs), new GenerationStampingMap(new SqlCorrelationMap(cs), new SqlItemGenerations(cs)), Home), outcomes));
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
    public async Task W43_ReRunAndCardPolls_AreTakenIn_AndThePurgeForgetsOldClaims()
    {
        if (!TrackedSessionHost.Available) return;
        var outcomes = new List<string>();
        await using var h = await TrackedSessionHost.StartAsync(Wire(outcomes));
        string Ci(string run, string d) => new JsonObject { ["provider"] = "github", ["event"] = "build.branch_completed", ["item"] = 7, ["ref"] = d, ["sha"] = "abc123", ["conclusion"] = "failure", ["run_id"] = 5, ["run_attempt"] = run }.ToJsonString();
        string Card(string col) => new JsonObject { ["provider"] = "github", ["event"] = "board.card", ["item"] = 7, ["repository"] = "acme/orders", ["column"] = col, ["ref"] = $"poll:board:acme/orders#7:{col}:open" }.ToJsonString();
        await h.TrackSend(new IngressInboxMessage("webhook", Ci("1", "d-1")));
        await h.TrackSend(new IngressInboxMessage("webhook", Ci("2", "d-2")));
        await h.TrackSend(new IngressInboxMessage("poll", Card("Doing")));
        await h.TrackSend(new IngressInboxMessage("poll", Card("Doing")));   // the pass publishes a card only when it moved; the inbox does not hide a repeat
        Assert.Equal(["accepted", "accepted", "accepted", "accepted"], outcomes);
        Assert.Equal(2, await h.Count("SELECT COUNT(*) FROM factory.IngressDedup WHERE Layer = 'fact'"));
        Assert.Equal(4, new SqlIngressDedup(h.ConnectionString).Purge(TimeSpan.Zero));   // two deliveries and two facts
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM factory.IngressDedup"));
    }

    [Fact]
    public async Task TheRowsCarryTheGenerationOfTheOpenInstance_ZeroForAnItemWithNone_AndForOneThatIsComplete()
    {
        if (!TrackedSessionHost.Available) return;
        var outcomes = new List<string>();
        await using var h = await TrackedSessionHost.StartAsync(Wire(outcomes));
        await using (var db = new Fworker.Persistence.FactoryDbContext(new DbContextOptionsBuilder<Fworker.Persistence.FactoryDbContext>().UseSqlServer(h.ConnectionString).Options))
        {
            db.OpenInstances.Add(new Fworker.Persistence.OpenInstance { ItemKey = "acme/orders#7", Generation = 2, OpenedAt = DateTimeOffset.UtcNow });
            db.OpenInstances.Add(new Fworker.Persistence.OpenInstance { ItemKey = "acme/orders#8", Generation = 1, OpenedAt = DateTimeOffset.UtcNow.AddHours(-1), ClosedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        string Ev(int item, int pr, string d) => new JsonObject { ["provider"] = "github", ["event"] = "pr.opened", ["item"] = item, ["ref"] = d, ["pr"] = pr, ["sha"] = "abc" + item }.ToJsonString();
        await h.TrackSend(new IngressInboxMessage("webhook", Ev(7, 9, "d-1")));
        await h.TrackSend(new IngressInboxMessage("webhook", Ev(8, 10, "d-2")));   // its instance is complete
        await h.TrackSend(new IngressInboxMessage("webhook", Ev(9, 11, "d-3")));   // no instance at all
        Assert.Equal(2, await h.Count("SELECT COUNT(*) FROM factory.Correlations WHERE ItemKey = 'acme/orders#7' AND Generation = 2"));
        Assert.Equal(4, await h.Count("SELECT COUNT(*) FROM factory.Correlations WHERE ItemKey IN ('acme/orders#8', 'acme/orders#9') AND Generation = 0"));
        var gens = new SqlItemGenerations(h.ConnectionString);
        Assert.Equal((2, 0, 0), (gens.CurrentGeneration("acme/orders#7"), gens.CurrentGeneration("acme/orders#8"), gens.CurrentGeneration("acme/orders#99")));
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
