using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Owner leases kept alive by the plant, and owners that stopped working found early (docs/design/plant-manager.md 6b). Three rules:
// 1. An offer the stream client accepted by itself (accept with accepted_by "client") must be taken up by the session: if no verb or poll of the
//    owner for that item arrives before the lease the accept answered (or schedule.clientStartMinutes), the item is taken back.
// 2. The owner lease is renewed by the gateway: after any verb call of the owner for the item, and at every heartbeat whose activity summary names
//    the item ("#N"); at most once per schedule.leaseRenewMinutes per item (issue 48).
// 3. An owned item with no verb or poll of its owner for schedule.staleItemMinutes and an activity summary that did not change meanwhile gets one
//    item.check trigger; nothing from the owner within schedule.staleConfirmMinutes after it, and the item is taken back.
// Taking back = the rule script expire-claim ends the claim's lease (the claim stays, marked taken_back), the plant forgets the owner and the next
// sweep offers the item again with mode resume, first to another worker. Everything here is in memory, like the rest of the plant.
sealed class LeaseSettings
{
    public double LeaseRenewMinutes = 5, StaleItemMinutes = 15, StaleConfirmMinutes = 3, ClientStartMinutes = 5;

    public static LeaseSettings Load(string scriptsDir)
    {
        var path = Environment.GetEnvironmentVariable("FACTORY_CONFIG") ?? Path.Combine(scriptsDir, "..", "..", "config", "instance.json");
        JsonNode? root = null; try { root = JsonNode.Parse(File.ReadAllText(path)); } catch { }
        var s = root?["schedule"];
        static bool Num(string? text, out double v) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) && v >= 0;
        double D(string k, double d) => Num(Environment.GetEnvironmentVariable("FACTORY_TIMING_" + k), out var e) ? e : Num(s?[k]?.ToString(), out var v) ? v : d;
        return new LeaseSettings
        {
            LeaseRenewMinutes = D("leaseRenewMinutes", 5), StaleItemMinutes = D("staleItemMinutes", 15), StaleConfirmMinutes = D("staleConfirmMinutes", 3),
            ClientStartMinutes = D("clientStartMinutes", 5)
        };
    }
}

sealed partial class Plant
{
    public LeaseSettings Lease { get; set; } = new();
    // Run outside the gate: renew the owner's claim (claim-item --renew) and end it (expire-claim). Set by Program.cs; nothing happens while unset.
    public Func<int, string, Task<DateTime?>>? OnRenew { get; set; }
    public Func<int, string, string, Task<bool>>? OnTakeBack { get; set; }

    // per item: the owner the plant saw accept it through the verb (only these and the owners a sweep listed with a lease are watched, never a
    // test seam's), the last call of the owner for it, the last renewal, an item.check sent and not yet answered, the deadline of a client
    // acceptance, and the worker an item was taken back from (it is not offered the item again first)
    readonly Dictionary<int, string> acceptedOwner = new();
    readonly Dictionary<int, DateTime> lastCall = new(), lastRenew = new();
    readonly Dictionary<int, (string Name, DateTime At, DateTime SummaryAt)> checks = new();
    readonly Dictionary<int, (string Name, string Assignment, DateTime StartBy)> clientAccepted = new();
    readonly Dictionary<int, string> takenBackFrom = new();
    long renewals, renewFailures, checksSent, takenBack;
    static readonly Regex ItemRefRx = new(@"#(\d{1,7})\b", RegexOptions.CultureInvariant);
    // the triggers after which an owner waits for something else without calling a verb: no item.check then
    static readonly HashSet<string> LeaseWaits = new() { "item.parked", "qc.requested", "qc.reset", "item.merged", "inspector.escalated" };

    // A call of an item token (a verb, or a poll of wait). verb is "" for a poll. Returns nothing; renewals it makes due run outside the gate.
    public void NoteItemCall(string name, int item, string verb, JsonObject? request = null, JsonObject? response = null, bool ok = false)
    {
        if (item <= 0) return;
        string? renewFor = null;
        lock (gate)
        {
            var now = DateTime.UtcNow;
            var byClient = verb == "accept" && request?["accepted_by"]?.ToString() == "client";
            // (called after OnVerb, so an accepted own item already names its owner)
            if (verb == "accept" && ok && ownerOf.TryGetValue(item, out var accepter) && accepter == name)
            {
                acceptedOwner[item] = name; takenBackFrom.Remove(item); checks.Remove(item); lastRenew[item] = now; lastCall[item] = now;
                if (byClient)
                {
                    var lease = Time(response?["lease_until"]);
                    var by = Lease.ClientStartMinutes > 0 ? now.AddMinutes(Lease.ClientStartMinutes) : lease ?? now.AddMinutes(30);
                    clientAccepted[item] = (name, response?["assignment"]?.ToString() ?? "", by);
                }
            }
            if (!(ownerOf.TryGetValue(item, out var owner) && owner == name)) return;
            if (byClient) return;                                 // the client's own accept is not the session at work
            lastCall[item] = now; checks.Remove(item); clientAccepted.Remove(item);
            if (verb is "" or "accept" or "claim-item" or "release") return;
            if (RenewDue(item, now)) renewFor = name;
        }
        if (renewFor != null) Renew(item, renewFor);
    }

    // A heartbeat of a name: the items its activity summary names that it owns get their lease renewed (when due).
    public void NoteHeartbeat(string name)
    {
        var due = new List<int>();
        lock (gate)
        {
            if (!store.Recipients.TryGetValue(name, out var r) || r.Summary == "") return;
            var now = DateTime.UtcNow;
            foreach (Match m in ItemRefRx.Matches(r.Summary))
                if (int.TryParse(m.Groups[1].Value, out var item) && ownerOf.TryGetValue(item, out var o) && o == name && !due.Contains(item) && RenewDue(item, now)) due.Add(item);
        }
        foreach (var item in due) Renew(item, name);
    }

    bool RenewDue(int item, DateTime now)
    {
        if (OnRenew == null) return false;
        if (lastRenew.TryGetValue(item, out var at) && (now - at).TotalMinutes < Lease.LeaseRenewMinutes) return false;
        lastRenew[item] = now; return true;
    }

    void Renew(int item, string name)
    {
        var f = OnRenew; if (f == null) return;
        _ = Task.Run(async () =>
        {
            DateTime? lease = null;
            try { lease = await f(item, name); } catch (Exception e) { Console.Error.WriteLine($"lease renew #{item}: {e.Message}"); }
            lock (gate)
            {
                if (lease is { } l)
                {
                    renewals++;
                    if (ownerOf.TryGetValue(item, out var o) && o == name)
                        ownedInfo[item] = ownedInfo.TryGetValue(item, out var known) ? known with { LeaseUntil = l, At = DateTime.UtcNow } : new OwnedInfo(name, "", l, null, false, DateTime.UtcNow);
                }
                else renewFailures++;
            }
        });
    }

    // Every second, from the ticker: the client acceptances that were not taken up, the item.check triggers, and the take-backs.
    public void LeaseTick()
    {
        var back = new List<(int Item, string Name, string Reason)>();
        lock (gate)
        {
            var now = DateTime.UtcNow;
            foreach (var (item, ca) in clientAccepted.ToList())
            {
                if (takingBack.Contains(item)) continue;
                if (!(ownerOf.TryGetValue(item, out var o) && o == ca.Name)) { clientAccepted.Remove(item); continue; }
                if (now >= ca.StartBy) { clientAccepted.Remove(item); back.Add((item, ca.Name, "client-accepted-not-started")); }
            }
            foreach (var (item, owner) in ownerOf.ToList())
            {
                if (back.Any(b => b.Item == item) || takingBack.Contains(item) || seamOwned.Contains(item)) continue;
                if (!(acceptedOwner.TryGetValue(item, out var acc) && acc == owner) && !(ownedInfo.TryGetValue(item, out var oi) && oi.LeaseUntil != null && oi.Holder == owner)) continue;
                if (clientAccepted.ContainsKey(item)) continue;   // rule 1 decides about it
                if (ownedInfo.TryGetValue(item, out var info) && info.Parked) { checks.Remove(item); continue; }
                if (lastRouted.TryGetValue(item, out var ev) && LeaseWaits.Contains(ev)) { checks.Remove(item); continue; }
                var summaryAt = store.Recipients.TryGetValue(owner, out var r) ? r.SummaryAt : default;
                var lastAct = lastCall.TryGetValue(item, out var lc) ? lc : ownedSince.TryGetValue(item, out var os) ? os : now;
                var since = summaryAt > lastAct ? summaryAt : lastAct;   // quiet since the later of the last call and the last change of the summary
                if (checks.TryGetValue(item, out var ck))
                {
                    if (ck.Name != owner || summaryAt > ck.SummaryAt) { checks.Remove(item); lastCall[item] = now; continue; }
                    if ((now - ck.At).TotalMinutes >= Lease.StaleConfirmMinutes) { checks.Remove(item); back.Add((item, owner, "no-answer-to-item-check")); }
                    continue;
                }
                if ((now - since).TotalMinutes < Lease.StaleItemMinutes) continue;
                checks[item] = (owner, now, summaryAt); checksSent++;
                var hadRouted = lastRouted.TryGetValue(item, out var prevRouted);
                Route(item, new JsonObject
                {
                    ["provider"] = "gateway", ["event"] = "item.check", ["item"] = item, ["quietMinutes"] = (int)(now - since).TotalMinutes,
                    ["reason"] = $"no verb for item #{item} for {Lease.StaleItemMinutes:0.#} min: call any verb for it (factory.ps1 claim-item --item {item} --renew) within {Lease.StaleConfirmMinutes:0.#} min, or it is offered again"
                });
                if (hadRouted) lastRouted[item] = prevRouted!; else lastRouted.Remove(item);
            }
            // the plant lets go only after expire-claim has ended the lease (Finish below): if the script fails, the claim is still live and a sweep
            // would name the owner again, so the owner stays and the item is tried again at a later tick
            foreach (var b in back) { takingBack.Add(b.Item); if (b.Reason == "client-accepted-not-started") retryClient[b.Item] = clientAcceptedOf(b); }
        }
        var f = OnTakeBack;
        foreach (var b in back) _ = Task.Run(async () =>
        {
            var ok = false;
            try { ok = f != null && await f(b.Item, b.Name, b.Reason); } catch (Exception e) { Console.Error.WriteLine($"lease take-back #{b.Item}: {e.Message}"); }
            Finish(b.Item, b.Name, b.Reason, ok);
        });
    }

    readonly HashSet<int> takingBack = new();
    readonly Dictionary<int, (string Name, string Assignment, DateTime StartBy)> retryClient = new();
    static (string, string, DateTime) clientAcceptedOf((int Item, string Name, string Reason) b) => (b.Name, "", DateTime.UtcNow.AddMinutes(1));

    void Finish(int item, string name, string reason, bool ok)
    {
        lock (gate)
        {
            takingBack.Remove(item);
            var retry = retryClient.Remove(item, out var rc);
            if (!ok)
            {   // the lease was not ended: keep the owner; rule 1 tries again in a minute, rule 3 sends a new check when it is still quiet
                Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["lease"] = "take-back-failed", ["item"] = item, ["holder"] = name }.ToJsonString());
                if (retry && ownerOf.TryGetValue(item, out var o0) && o0 == name) clientAccepted[item] = rc;
                return;
            }
            var now = DateTime.UtcNow;
            // (rule 3 needs no retry of its own: its check was removed before the take-back was queued, so a still quiet owner is checked again)
            if (!(ownerOf.TryGetValue(item, out var cur) && cur == name)) { RequestSweepAfterTakeBack?.Invoke(); return; }   // released or re-owned meanwhile
            {
                takenBack++;
                if (workers.TryGetValue(name, out var w)) Push(w, new JsonObject { ["provider"] = "gateway", ["event"] = "item.taken_back", ["item"] = item, ["reason"] = reason }.ToJsonString());
                DropOwner(item); acceptedOwner.Remove(item); lastCall.Remove(item); lastRenew.Remove(item); checks.Remove(item);
                foreach (var k in assignments.Keys.Where(k => k.StartsWith(item + "|own-item|", StringComparison.Ordinal)).ToList()) assignments.Remove(k);
                takenBackFrom[item] = name;
                Journal(item, "reassigned", new JsonObject { ["reason"] = reason == "client-accepted-not-started" ? $"{name} accepted by its client but did not start within the lease; offered again" : $"{name} did not answer the check after {Lease.StaleItemMinutes:0.#} min without a verb; offered again" }, "back|" + item + "|" + now.Ticks);
            }
        }
        RequestSweepAfterTakeBack?.Invoke();
    }
    public Action? RequestSweepAfterTakeBack { get; set; }

    // Plan's hook for a new own-item assignment: the worker it was just taken back from is not offered it first.
    // Only while another session could take it: a plant with that one worker offers the item to it again rather than to nobody.
    void DeclineTakenBack(Assignment a)
    {
        if (a.Kind != "own-item" || !takenBackFrom.Remove(a.Item, out var was)) return;
        if (workers.Values.Any(w => w.Name != was && w.Connected && !w.Draining && w.Roles.Contains("owner"))) a.Declined.Add(was);
    }

    public JsonObject LeaseStatus()
    {
        lock (gate)
            return new JsonObject
            {
                ["renewals"] = renewals, ["renewFailures"] = renewFailures, ["checksSent"] = checksSent, ["takenBack"] = takenBack,
                ["checksOpen"] = new JsonArray(checks.Select(kv => (JsonNode)new JsonObject { ["item"] = kv.Key, ["worker"] = kv.Value.Name, ["at"] = kv.Value.At.ToString("o") }).ToArray()),
                ["clientAccepted"] = new JsonArray(clientAccepted.Select(kv => (JsonNode)new JsonObject { ["item"] = kv.Key, ["worker"] = kv.Value.Name, ["acceptedBy"] = "client", ["startBy"] = kv.Value.StartBy.ToString("o") }).ToArray())
            };
    }
}
