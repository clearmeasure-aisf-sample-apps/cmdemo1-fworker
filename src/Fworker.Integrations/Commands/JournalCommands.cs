using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Messaging;

namespace Fworker.Integrations.Commands;

// W3.2: one line of an item's journal as a v2 integration command (docs/design/fworker-wolverine-implementation.md W3). The plant decides the line (event,
// fields, key) as it does today and hands it to the outbox; the handler writes it through the JournalLedger, which is the C# port of Add-Journal. The lines of
// one item run one after the other in the order the plant decided them (the host partitions the queue by item), and the plant's chain only publishes: a
// launch that is still starting holds its place in the chain (Plant.ReserveLaunched), so the launched line is published before the assigned line.

/// <summary>
/// Writes one journal line of an item. <see cref="Event"/> and <see cref="Fields"/> are what the plant sent to the journal verb; <see cref="Key"/> is the line's
/// dedup key (never empty: the producer takes the ticks of the decision for a line that has none, as the verb takes the ticks of the write).
/// </summary>
public sealed record WriteJournalLine(string IdempotencyKey, int Item, string Event, JsonObject Fields, string Key) : IntegrationCommand(IdempotencyKey)
{
    public override string Name => "WriteJournalLine";
    public override CommandTarget Target => CommandTarget.GitHub;
}

/// <summary>A journal line was handled: <see cref="Posted"/> when a comment was written or an open fold edited; <see cref="Reason"/> says why not (a repeat, the cap, the waiting rate limit).</summary>
[MessageIdentity("journal.line-written")]
public sealed record JournalLineWritten(string IdempotencyKey, int Item, string Event, bool Posted, string Reason);

public static class JournalKeys
{
    /// <summary>One command per (item, event, key): the ledger in the item's state comment holds the same key, so a replay after a restart does nothing twice.</summary>
    public static string For(int item, string ev, string key) => IdempotencyKeys.For("WriteJournalLine", item, ev, key);

    /// <summary>
    /// The one rule for a line without a key, the same in both writers (Add-Journal of plugin/scripts/lib/Journal.ps1, and the JournalLedger with the sink's command): an empty key is the ticks of now, so two different
    /// events of one kind are never taken for one. The alternative, the key "event|" for every keyless line, would drop the second distinct event of that kind for good (the ledger keeps the key).
    /// </summary>
    public static string OrTicks(string? key, DateTime utcNow) =>
        string.IsNullOrEmpty(key) ? utcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : key;
}

public sealed class WriteJournalLineHandler : IntegrationCommandHandler<WriteJournalLine>
{
    readonly JournalLedger ledger;
    // W9.10c: an earlier handling that was cut off may have posted the line: its key is in the item's journal ledger (the comment itself carries no key)
    public WriteJournalLineHandler(IIdempotencyLedger idempotency, JournalLedger ledger) : base(idempotency, async (c, ct) =>
        await ledger.Recorded(c.Item, c.Event, c.Key, ct) ? EffectLookup.Found(CommandResult.Success("recorded")) : EffectLookup.Absent) { this.ledger = ledger; }

    protected override async Task<CommandResult> Execute(WriteJournalLine c, CancellationToken ct)
    {
        var o = await ledger.Append(c.Item, c.Event, JournalLedger.FieldsOf(c.Fields), c.Key, ct);
        // the Execution of a result is the outcome's word: posted, recorded, or the reason it was not
        return CommandResult.Success(o.Posted ? "posted" : o.Recorded ? "recorded" : o.Reason is { Length: > 0 } r ? r : "repeat");
    }

    public async Task<JournalOutcomeMessages> Process(WriteJournalLine c, CancellationToken ct = default)
    {
        var r = await Handle(c, ct);
        var failed = FailureMessage(c, r);
        // a retryable failure sends nothing: the caller runs the command again; a line that is refused for good goes to the saga and is logged, never retried
        return new(r, r.Ok ? new JournalLineWritten(c.IdempotencyKey, c.Item, c.Event, r.Execution == "posted", r.Execution == "posted" ? "" : r.Execution) : null, failed);
    }
}

/// <summary>What the journal handler produced: the result, the message to send (null when the command will be retried or failed) and the failure for the saga.</summary>
public sealed record JournalOutcomeMessages(CommandResult Result, JournalLineWritten? Message, IntegrationFailed? Failed)
{
    public bool Retry => Message == null && Failed == null && !Result.Ok;
}
