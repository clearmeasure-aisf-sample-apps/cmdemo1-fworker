using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Workflows.WorkItem;

// W9.10k: a plant restored from the database is corrected at start, before the first sweep, from what is durable. The database holds what the plant knew when it was last written; a restart may have been long, and the board moved
// meanwhile. Two corrections, both only for the items the plant holds something for:
//   - an item whose saga completed (WorkItems says the instance is over) keeps no assignment, owner or bot holder: the plant forgets them (Plant.ForgetItems), as it forgets an item at its merge;
//   - an item whose saga is open and for which the plant restored an owner keeps that owner only if the claim says so (the claim ref and the item's state comment, through the verb get-item): the claim's holder replaces the plant's
//     owner, and no claim (released, taken back, never made) drops it. A claim that cannot be read leaves the owner as restored: the first sweeps correct what they read, and an item that has no saga is theirs from the start.
// What the correction did is the "corrected" block of the status's restore (items looked at, forgotten, owners corrected or dropped, claims not read).
sealed class PlantRestoreCorrection(Plant plant, IWorkItemStore store, RepoId home, Func<int, CancellationToken, Task<string?>> claimHolder)
{
    public async Task<JsonObject> Run(CancellationToken ct = default)
    {
        var items = plant.HeldItems();
        var forgotten = new List<int>(); var owners = new JsonObject(); var unread = new List<int>(); var open = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var record = await store.Load(new ItemId(home, item).ToString(), ct);
            if (record is null) continue;   // an item without a saga: v1's, the sweep corrects it
            if (!record.State.IsOpen) { forgotten.Add(item); continue; }
            open++;
            if (plant.OwnerNameOf(item) is not { } restored) continue;
            var holder = await claimHolder(item, ct);
            if (holder is null) { unread.Add(item); continue; }
            if (plant.CorrectOwner(item, holder)) owners[item.ToString()] = holder.Length == 0 ? "dropped" : holder;
        }
        if (forgotten.Count > 0) plant.ForgetItems(forgotten);
        return new JsonObject
        {
            ["looked"] = items.Count, ["open"] = open, ["forgotten"] = new JsonArray(forgotten.Select(i => (JsonNode)i).ToArray()), ["owners"] = owners, ["unread"] = new JsonArray(unread.Select(i => (JsonNode)i).ToArray())
        };
    }

    /// <summary>
    /// The holder of the item's claim through the verb get-item: "" when there is none (or it was taken back), null when it could not be read (the caller keeps the owner as restored). An item that is not there answers "".
    /// </summary>
    public static async Task<string?> HolderOf(IVerbRouter? verbs, int item, CancellationToken ct)
    {
        if (verbs is null) return null;
        try
        {
            var got = await verbs.RunAsync("get-item", new JsonObject { ["item"] = item }.ToJsonString(), TimeSpan.FromSeconds(60));
            if (got.Code is 10 or 11 or 12) return "";
            if (got.Code != 0) return null;
            var claim = (JsonNode.Parse(got.Output) as JsonObject)?["state"]?["claim"] as JsonObject;
            if (claim is null || !string.IsNullOrEmpty(claim["taken_back"]?.ToString())) return "";
            return claim["holder"]?.ToString() ?? "";
        }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"restore correction: the claim of #{item} could not be read ({e.GetType().Name})"); return null; }
    }
}
