using Microsoft.EntityFrameworkCore;
using Wolverine.Tracking;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// Examples of Wolverine tracked sessions (W2.4) on the harness's trivial messages, against the SQL Server of FACTORY_TEST_SQL with the durable outbox.
/// They return at once where FACTORY_TEST_SQL is unset; the src and unit jobs of tests.yml set it. The same shapes carry the tests of the sagas and handlers of W3 on.
/// </summary>
public class TrackedSessionTests
{
    static bool NoServer => !TrackedSessionHost.Available;

    [Fact]
    public async Task Outbox_ACascadedMessage_IsDeliveredThroughTheDurableOutbox_AndLeavesNoEnvelopeBehind()
    {
        if (NoServer) return;
        await using var h = await TrackedSessionHost.StartAsync();
        var session = await h.Track(new HarnessPing("ob-1"));
        Assert.NotEmpty(session.Sent.MessagesOf<HarnessPong>());
        Assert.Single(session.Executed.MessagesOf<HarnessPong>());
        Assert.Equal(1, h.Probe.DeliveredCount("ob-1"));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_outgoing_envelopes"));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    [Fact]
    public async Task Send_AMessagePublishedToTheDurableQueue_IsHandledOnce()
    {
        if (NoServer) return;
        await using var h = await TrackedSessionHost.StartAsync();
        var session = await h.TrackSend(new HarnessPing("send-1"));
        Assert.Single(session.Executed.MessagesOf<HarnessPing>());
        Assert.Equal(1, h.Probe.Attempts("send-1"));
    }

    [Fact]
    public async Task Retry_ATransientFailure_IsRetried_AndTheMessageSucceeds()
    {
        if (NoServer) return;
        await using var h = await TrackedSessionHost.StartAsync();
        await h.Track(new HarnessFlaky("retry-1", FailTimes: 2));
        Assert.Equal(3, h.Probe.Attempts("retry-1"));
        Assert.Equal(1, h.Probe.DeliveredCount("retry-1"));
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }

    [Fact]
    public async Task DeadLetter_APoisonMessage_MovesToTheDeadLetterTableAfterThreeAttempts()
    {
        if (NoServer) return;
        await using var h = await TrackedSessionHost.StartAsync();
        await h.TrackSend(new HarnessPoison("poison-1"));
        Assert.Equal(3, h.Probe.Attempts("poison-1"));
        Assert.Equal(0, h.Probe.DeliveredCount("poison-1"));
        Assert.Equal(1, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters WHERE exception_type LIKE '%HarnessPoisonException%'"));
    }

    [Fact]
    public async Task Redelivery_IntoItemHistory_WritesTheRowOnce()
    {
        if (NoServer) return;
        await using var h = await TrackedSessionHost.StartAsync();
        // the first attempt writes the row and fails; Wolverine redelivers the same envelope, whose second insert hits the unique (MessageId, Kind) index
        await h.TrackSend(new HarnessRecord("acme/shop#9", FailOnceAfterWrite: true));
        Assert.Equal(2, h.Probe.Attempts("acme/shop#9"));
        Assert.Equal(1, h.Probe.DeliveredCount("acme/shop#9"));
        await using var db = h.Open();
        var row = Assert.Single(await db.ItemHistory.Where(x => x.ItemKey == "acme/shop#9").ToListAsync());
        Assert.NotNull(row.MessageId);
        Assert.Equal(0, await h.Count("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters"));
    }
}
