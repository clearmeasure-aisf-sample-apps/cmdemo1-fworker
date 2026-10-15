using System.Text.Json.Nodes;

// Three plant rules of 2026-10-13 (issues 33, 30 and 31 of the gateway repository):
// - the gateway merges a PASS by itself (Verbs.Merge.ps1 -Gateway, run by validation-verdict and the sweep). The sweep reports what it could not
//   merge yet as pass_unmerged; the plant keeps that list for /status and the patrol digest (kind pass-unmerged, after timing.passUnmergedDigestMinutes);
// - an accept is honoured only for the offer that stands for that session now (a late accept of a withdrawn offer is refused, stale-assignment);
// - a session that let timing.offerMissLimit offers in a row lapse is not offered more work for timing.offerCoolDownMinutes, or until it calls a verb.
sealed partial class Plant
{
    public double PassUnmergedDigestMinutes { get; set; } = 5;
    public int OfferMissLimit { get; set; } = 2;
    public double OfferCoolDownMinutes { get; set; } = 5;

    readonly Dictionary<int, (string Code, DateTime Since, int Pr)> passUnmerged = new();

    // The sweep's pass_unmerged replaces the list (an item the sweep merged, or that left the state, is gone from it).
    // scope (issue 92): the items of an incremental pass; only their entries are replaced
    public void SetPassUnmerged(JsonArray? list, IReadOnlyCollection<int>? scope = null)
    {
        lock (gate)
        {
            if (scope == null) passUnmerged.Clear(); else foreach (var i in scope) passUnmerged.Remove(i);
            foreach (var x in list ?? new JsonArray())
                if (x is JsonObject && x["item"] is JsonValue v && v.TryGetValue<int>(out var item) && item > 0)
                    passUnmerged[item] = (GatedCode(x["code"]?.ToString()) is { Length: > 0 } c ? c : "other", Time(x["since"]) ?? DateTime.UtcNow, int.TryParse(x["pr"]?.ToString(), out var pr) ? pr : 0);
        }
    }

    // The item's pull request was merged (by the gateway, by the merge verb, or by a person): its open assignments are done; the owner keeps the
    // item until item.complete (master green), as after the merge verb.
    public void Merged(int item)
    {
        lock (gate)
        {
            foreach (var k in assignments.Keys.Where(k => k.StartsWith(item + "|") && !IsQc(assignments[k])).ToList()) assignments.Remove(k);
            passUnmerged.Remove(item);
        }
    }

    // For the digest (under the gate): the items that waited longer than the threshold; the grace before the first try is no wait.
    void AddPassUnmerged(DigestInput d, DateTime now)
    {
        d.PassUnmergedMinutes = PassUnmergedDigestMinutes;
        foreach (var (item, p) in passUnmerged.OrderBy(kv => kv.Key))
            if (p.Code != "grace" && (now - p.Since).TotalMinutes >= PassUnmergedDigestMinutes) d.PassUnmerged.Add((item, p.Code, p.Pr, p.Since));
    }

    JsonArray PassUnmergedJson(DateTime now) => new(passUnmerged.OrderBy(kv => kv.Key).Select(kv => (JsonNode)new JsonObject
    {
        ["item"] = kv.Key, ["pr"] = kv.Value.Pr, ["code"] = kv.Value.Code, ["waitingMinutes"] = (int)Math.Max(0, Math.Floor((now - kv.Value.Since).TotalMinutes))
    }).ToArray());

    // Issue 30. Called before the accept verb runs: the offer must still stand for this session (or be accepted by it already). A withdrawn offer,
    // one that went to another session, or one the plant no longer knows (dropped, or made before a restart) is refused with exit 11, so the session
    // drops the item and never holds more than its capacity. A pinned item may be accepted between two offers to its own worker, if it has a free slot.
    // An accepted call marks the offer as being accepted, so the offer's window does not lapse while the rule script records the claim.
    public (int Exit, string Code, string Reason) BeginAccept(Principal who)
    {
        lock (gate)
        {
            if (who.Role == "qc") return (0, "", "");
            var a = assignments.Values.FirstOrDefault(x => x.Id == who.Assignment && !IsQc(x));
            const string stale = "stale-assignment";
            if (a == null) return (11, stale, "this offer is no longer standing (it was withdrawn, replaced, or made before a gateway restart); drop the item: if it is still yours to take, a new offer arrives");
            if (a.Accepted) return a.Worker == who.Name ? (0, "", "") : (11, stale, "this offer was accepted by another session; drop the item");
            if (a.Worker == who.Name) { a.Accepting = DateTime.UtcNow; return (0, "", ""); }
            if (a.Worker == "" && a.Pin != "" && a.Pin == who.Name && workers.TryGetValue(who.Name, out var pw) && Owned(pw.Name) < pw.Capacity) { a.Accepting = DateTime.UtcNow; return (0, "", ""); }
            return (11, stale, $"this offer was withdrawn: it was not accepted within {ackSeconds:0} s and went back to the plan{(a.Worker != "" ? " (it is offered to another session now)" : "")}; drop the item");
        }
    }
    public void EndAccept(string assignment) { lock (gate) foreach (var a in assignments.Values.Where(x => x.Id == assignment)) a.Accepting = null; }

    // Issue 31: any verb a session calls successfully shows it is answering; its count of lapsed offers starts again and a cool-down ends.
    public void NoteAnswered(string name)
    {
        lock (gate) { if (workers.TryGetValue(name, out var w)) { w.Missed = 0; w.CoolUntil = DateTime.MinValue; } }
    }

    // Under the gate, from Tick: the session let an (unpinned) offer lapse. Returns the text added to the journal line when a cool-down starts.
    string NoteMissed(string name)
    {
        if (!workers.TryGetValue(name, out var w)) return "";
        w.Missed++;
        if (OfferMissLimit <= 0 || w.Missed < OfferMissLimit) return "";
        w.Missed = 0; w.CoolUntil = DateTime.UtcNow.AddMinutes(OfferCoolDownMinutes);
        Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"plant\":\"cool-down\",\"worker\":\"{name}\",\"until\":\"{w.CoolUntil:o}\"}}");
        return $"; {name} let {OfferMissLimit} offers in a row lapse and is offered nothing new for {OfferCoolDownMinutes:0.#} min, or until it calls a verb";
    }
    static bool Cooling(WorkerInfo w) => w.CoolUntil > DateTime.UtcNow;
}
