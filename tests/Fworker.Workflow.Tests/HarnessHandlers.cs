using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Fworker.Workflow.Tests;

// The harness's handlers: pre-built MessageHandler<T> classes (no code generation, see TrackedSessionHost), trivial and test-only.

sealed class PingHandler(HarnessProbe probe) : MessageHandler<HarnessPing>
{
    protected override async Task HandleAsync(HarnessPing m, MessageContext context, CancellationToken cancellation)
    {
        probe.NextAttempt(m.Key);
        await context.PublishAsync(new HarnessPong(m.Key));   // through the durable outbox
    }
}

sealed class PongHandler(HarnessProbe probe) : MessageHandler<HarnessPong>
{
    protected override Task HandleAsync(HarnessPong m, MessageContext context, CancellationToken cancellation)
    {
        probe.Delivered(m.Key);
        return Task.CompletedTask;
    }
}

sealed class FlakyHandler(HarnessProbe probe) : MessageHandler<HarnessFlaky>
{
    protected override Task HandleAsync(HarnessFlaky m, MessageContext context, CancellationToken cancellation)
    {
        if (probe.NextAttempt(m.Key) <= m.FailTimes) throw new HarnessTransientException($"attempt failed on purpose ({m.Key})");
        probe.Delivered(m.Key);
        return Task.CompletedTask;
    }
}

sealed class PoisonHandler(HarnessProbe probe) : MessageHandler<HarnessPoison>
{
    protected override Task HandleAsync(HarnessPoison m, MessageContext context, CancellationToken cancellation)
    {
        probe.NextAttempt(m.Key);
        throw new HarnessPoisonException($"poison ({m.Key})");
    }
}

// The shape W3's handlers have: the history row carries Wolverine's envelope id, and a redelivery that finds the row already there does nothing more.
sealed class RecordHandler(HarnessProbe probe, TrackedSessionHost sessions) : MessageHandler<HarnessRecord>
{
    protected override async Task HandleAsync(HarnessRecord m, MessageContext context, CancellationToken cancellation)
    {
        var attempt = probe.NextAttempt(m.ItemKey);
        await using (var db = sessions.Open())
        {
            db.ItemHistory.Add(new ItemHistoryEntry { ItemKey = m.ItemKey, At = DateTimeOffset.UtcNow, Kind = "event", Type = nameof(HarnessRecord), MessageId = context.Envelope!.Id });
            try { await db.SaveChangesAsync(cancellation); }
            catch (DbUpdateException) { /* the unique (MessageId, Kind) index: written by an earlier attempt */ }
        }
        if (m.FailOnceAfterWrite && attempt == 1) throw new HarnessTransientException("failed after the write, on purpose");
        probe.Delivered(m.ItemKey);
    }
}
