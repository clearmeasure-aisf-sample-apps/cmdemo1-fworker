using System.Text.Json.Nodes;

// W9.10k: what the plant takes from and gives to the engine's state, beside its own hand-over (PlantSnapshot.cs, the plant state store in the database).
//   - SagaAttentionSource: the parked items of V2ItemViews, which the patrol digest reports beside the parks the owners' triggers announced (a saga item parks without a trigger to its owner);
//   - SagaFlightSource: what the saga has in flight (a merge call asked, a slot granted whose session the plant does not hold yet), which the operator's release check counts as not quiet;
//   - the correction of a restored plant at start: the items the plant holds something for, the forgetting of what belongs to an item whose saga completed, and the owner the claim says.
// Only a gateway where the engine acts sets the sources and corrects; for every other the members are never called.
sealed partial class Plant
{
    public Func<IReadOnlyList<SagaAttention>>? SagaAttentionSource { get; set; }
    public Func<IReadOnlyList<SagaFlight>>? SagaFlightSource { get; set; }

    // the parked items of the saga, as the digest's parks (a park of the last 24 hours the owners' triggers announced is the same entry: one key, parked|item|code)
    void AddSagaParks(DigestInput d)
    {
        IReadOnlyList<SagaAttention> rows;
        try { rows = SagaAttentionSource?.Invoke() ?? []; } catch (Exception e) { Console.Error.WriteLine($"saga attention: {e.GetType().Name}"); return; }
        foreach (var a in rows.Where(a => a.Kind == "parked" && a.Item > 0).OrderBy(a => a.Item))
        {
            var code = GatedCode(a.Code);
            if (!d.Parks.Any(p => p.Item == a.Item && p.Code == code)) d.Parks.Add((a.Item, code, a.Since > d.Now ? d.Now : a.Since));
        }
    }

    // the release check's view of the saga (under the gate): a merge call the saga asked for and has no answer to, a slot granted for which the plant holds neither an owner nor an assignment yet
    void AddSagaFlight(JsonArray reasons)
    {
        IReadOnlyList<SagaFlight> rows;
        try { rows = SagaFlightSource?.Invoke() ?? []; } catch (Exception e) { Console.Error.WriteLine($"saga flight: {e.GetType().Name}"); return; }
        foreach (var f in rows)
        {
            if (f.Code == "merge-requested") reasons.Add(new JsonObject { ["code"] = "saga-merge-in-flight", ["item"] = f.Item, ["step"] = f.Step });
            else if (f.Code == "slot-granted" && !ownerOf.ContainsKey(f.Item) && !assignments.Values.Any(a => a.Item == f.Item && !IsQc(a)))
                reasons.Add(new JsonObject { ["code"] = "saga-session-starting", ["item"] = f.Item, ["step"] = f.Step });
        }
    }

    /// <summary>The items the plant holds something for (an assignment other than a QC job, an owner, a bot holder).</summary>
    public IReadOnlyList<int> HeldItems()
    {
        lock (gate) return assignments.Values.Where(a => !IsQc(a)).Select(a => a.Item).Concat(ownerOf.Keys).Concat(botOf.Keys).Distinct().Order().ToList();
    }

    /// <summary>The owner session the plant has for the item, or null.</summary>
    public string? OwnerNameOf(int item) { lock (gate) return ownerOf.TryGetValue(item, out var o) ? o : null; }

    /// <summary>The owner session the plant has for each of these items (items without one are left out).</summary>
    public IReadOnlyDictionary<int, string> OwnersOf(IEnumerable<int> items)
    {
        lock (gate) return items.Where(ownerOf.ContainsKey).Distinct().ToDictionary(i => i, i => ownerOf[i]);
    }

    /// <summary>Forgets everything the plant holds for these items: their assignments (not QC jobs), owner and bot holder. For items whose saga completed while the plant was down. Returns how many items held something.</summary>
    public int ForgetItems(IEnumerable<int> items)
    {
        var n = 0;
        lock (gate)
            foreach (var item in items.Distinct())
            {
                var gone = assignments.Keys.Where(k => k.StartsWith(item + "|", StringComparison.Ordinal) && !IsQc(assignments[k])).ToList();
                var held = gone.Count > 0 || ownerOf.ContainsKey(item) || botOf.ContainsKey(item);
                foreach (var k in gone) assignments.Remove(k);
                DropOwner(item); botOf.Remove(item);
                if (held) n++;
            }
        return n;
    }

    /// <summary>
    /// The owner the claim of the item says (the correction of a restored plant): empty means the claim is gone, so the plant's owner and the accepted assignment of that session go; another name replaces the owner (the session
    /// that held it loses its accepted assignment). The same holder changes nothing. Returns whether the plant changed.
    /// </summary>
    public bool CorrectOwner(int item, string holder)
    {
        lock (gate)
        {
            var had = ownerOf.TryGetValue(item, out var cur) ? cur : null;
            if (had == null && holder == "") return false;
            if (had == holder) return false;
            var key = $"{item}|own-item|owner";
            if (assignments.TryGetValue(key, out var a) && a.Accepted && a.Worker != holder) assignments.Remove(key);
            if (holder == "") DropOwner(item);
            else if (Auth.ValidName(holder)) { ownerOf[item] = holder; ownedSince[item] = DateTime.UtcNow; ownedInfo.Remove(item); ownedMisses.Remove(item); }
            else return false;
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["plant"] = "owner-corrected", ["item"] = item, ["was"] = had ?? "", ["owner"] = holder }.ToJsonString());
            return true;
        }
    }
}
