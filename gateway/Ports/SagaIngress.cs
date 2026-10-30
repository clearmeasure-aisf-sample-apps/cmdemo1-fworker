using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Integrations.Correlation;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.WorkItem;
using Wolverine;

// W9.10h: what a person does on GitHub reaches the saga. The ingress inbox (W4.1) takes in the webhook's events and the board reconciler's polls; until now the saga heard of none of them but the merge (ItemMerged).
// Four kinds of event are the saga's, for an item that has a saga only (an item without one costs one read of the saga's table, and the message ends there):
//  - item.closed (webhook) and board.card with open false (poll): ItemClosed. The saga completes the instance if no merge has happened and the merge step does not decide (WorkItemSaga: a merge closes its own issue).
//  - item.reopened (webhook): ReopenWorkItem. Only the webhook says "reopened": a poll reports every open card the process has not seen yet, after a restart too, and must not start the next generation of an item
//    whose instance v1 took back.
//  - item.column_changed (webhook) and board.card with open true (poll): CardMoved, when the card is where a person put it and not where the projection did. The card projection (DocsProjection) records what it wrote;
//    an event that is that write (ProjectionEcho: the newest write, and for a webhook the few latest within two minutes) is not news, and neither is any event before the projection applied the version of the step the
//    item is on (the card is then still where the earlier step put it). A move back to the column of an earlier step sends the item back to that step (WorkItemSaga).
//  - item.labeled (webhook), item.column_changed and board.card: HoldObserved, read from the issue's labels (one REST read, for an item the saga holds open and not parked for another reason). The webhook's translation
//    makes an event for the factory label only (Verbs.Sweep.ps1 and its port), so a hold label added later is seen at the next card event or merge-time check, not at once: widening the translation is a change of the
//    events v1 consumes, left to W9.10g's reconciler.
// Every input carries a key made of the event's delivery and time, so a redelivered message derives the same inputs and the saga applies each once.

/// <summary>Derives the saga's inputs from an inbox event. Public because Wolverine's generated code refers to it (the handler below).</summary>
public sealed class SagaIngress(IWorkItemStore store, RepoId home, DocsPlanSettings settings, IProjectionVersions versions, ProjectionEcho echo, IItemIssueReader? issues = null)
{
    /// <summary>A webhook's event for a write of the projection arrives within seconds; this long after a write its event is still the projection's.</summary>
    public static readonly TimeSpan WebhookEchoWindow = TimeSpan.FromMinutes(2);

    /// <summary>Whether the event can be the saga's at all: the inbox hands every event here and most are not.</summary>
    public static bool Relevant(JsonObject ev) => ev["event"]?.ToString() is "item.closed" or "item.reopened" or "item.labeled" or "item.column_changed" or "board.card";

    public async Task<IReadOnlyList<WorkItemInput>> Derive(string source, string eventJson, CancellationToken ct)
    {
        if (JsonNode.Parse(eventJson) is not JsonObject ev || !Relevant(ev)) return [];
        var number = Int(ev["item"]);
        if (number <= 0) return [];
        var repo = ev["repository"]?.ToString() is { } rs && RepoId.TryParse(rs, out var r) ? r : home;
        var id = new ItemId(repo, number);
        if ((await store.Load(id.ToString(), ct))?.State is not { } state) return [];   // no saga: nothing of this is the saga's
        var name = ev["event"]!.ToString();
        var tag = Tag(ev);
        var inputs = new List<WorkItemInput>();
        switch (name)
        {
            case "item.closed":
                inputs.Add(new ItemClosed(id, $"closed|{tag}", source));
                break;
            case "item.reopened":
                if (!state.IsOpen) inputs.Add(new ReopenWorkItem(id, "the issue was reopened", $"reopened|{tag}"));
                break;
            case "board.card" when ev["open"] is JsonValue o && o.TryGetValue<bool>(out var open) && !open:
                inputs.Add(new ItemClosed(id, $"closed|{tag}", source));
                break;
            case "board.card" or "item.column_changed":
                if (ev["column"]?.ToString() is { Length: > 0 } column && await Moved(id, state, column, webhook: name == "item.column_changed", tag, ct) is { } moved) inputs.Add(moved);
                if (await Hold(id, state, tag, ct) is { } hold) inputs.Add(hold);
                break;
            case "item.labeled":
                if (await Hold(id, state, tag, ct) is { } held) inputs.Add(held);
                break;
        }
        return inputs;
    }

    // A person's move of the card, or null: the item is not on a step a card stands for, the column is no step's, the projection has not applied this step's card yet, or the event is the projection's own write.
    async Task<WorkItemInput?> Moved(ItemId id, WorkItemState state, string column, bool webhook, string tag, CancellationToken ct)
    {
        if (!state.IsOpen || state.Parked || state.Step is not (StepKind.Design or StepKind.Develop or StepKind.Verify)) return null;
        StepKind? step = null;
        foreach (var (kind, name) in settings.Columns) if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase)) { step = kind; break; }
        if (step is null) return null;
        var key = id.ToString();
        if (await versions.Applied(key, ProjectionTargets.Card, ct) < state.CardSeq) return null;
        var fingerprint = ProjectionEcho.ColumnFingerprint(column);
        if (await echo.IsEcho(key, ProjectionTargets.Card, fingerprint, ct)) return null;
        if (webhook && echo.WroteRecently(key, ProjectionTargets.Card, fingerprint, WebhookEchoWindow)) return null;
        return new CardMoved(id, state.Generation, $"card|{tag}", column, step);
    }

    // The hold label the item carries now, as an input, or null when the saga has nothing to change by it.
    async Task<WorkItemInput?> Hold(ItemId id, WorkItemState state, string tag, CancellationToken ct)
    {
        if (issues is null || !state.IsOpen || state.Status == WorkItemStatus.Settling) return null;
        // an item parked for another reason is a person's or the saga's park: a label neither sets nor lifts it
        if (state.Parked && !state.ParkReason.StartsWith(WorkItemSaga.HoldParkPrefix, StringComparison.Ordinal)) return null;
        string? hold;
        try { hold = (await issues.ReadIssue(id, ct))?.Labels.FirstOrDefault(settings.IsHold); }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"v2 hold label read of {id}: {e.GetType().Name}"); return null; }   // the next card event looks again
        if ((hold is not null) == state.Parked) return null;   // already as the labels say (a parked item here is parked for a hold, see above)
        return new HoldObserved(id, state.Generation, $"hold|{hold ?? "-"}|{tag}", hold);
    }

    // the event's delivery and time, short: the keys of the inputs it makes
    static string Tag(JsonObject ev) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{ev["ref"]}|{ev["at"]}|{ev["column"]}")))[..16].ToLowerInvariant();

    static int Int(JsonNode? n) => n is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<string>(out var s) && int.TryParse(s, out i))) ? i : 0;
}

/// <summary>The handler of the saga's inputs from the inbox: a second handler of IngressInboxMessage beside IngressInboxHandler and the merge derivation. Public because Wolverine's generated code refers to it.</summary>
public static class SagaIngressHandler
{
    public static async Task<OutgoingMessages> Handle(IngressInboxMessage message, SagaIngress ingress, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        foreach (var input in await ingress.Derive(message.Source, message.EventJson, ct)) outgoing.Add(input);
        return outgoing;
    }
}
