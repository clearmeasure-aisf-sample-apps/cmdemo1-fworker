using System.Text.Json.Nodes;

namespace Fworker.Workflows.WorkItem.Verbs;

// The claim of an item and the sessions that hold it (Verbs.Item.ps1 Verb_claim_item; Owner.ps1 Verb_accept, Verb_release, Verb_expire_claim, and Verb_model_tier of Models.ps1 as the accept uses it).

public sealed partial class ItemVerbs
{
    // Verb_claim_item
    Task<JsonObject> ClaimItem(ItemCall c) => ClaimCore(c, c.Args);

    async Task<JsonObject> ClaimCore(ItemCall c, JsonObject a)
    {
        var n = Ps.Int(a, "item");
        var holder = Ps.Has(a, "holder") ? Ps.Text(a, "holder") : "anonymous";
        var info = await board.GetItemInfo(n, c.Ct);
        var st = await store.Read(n, c.Ct); var s = st.State;
        if (Ps.Has(a, "release") && Ps.TruthyKey(a, "release"))
        {
            await board.RemoveClaimRef(n, c.Ct);
            if (Ps.Has(s, "claim") && (Ps.Eq(Ps.Text(s["claim"], "holder"), holder) || Ps.Has(a, "force"))) { s.Remove("claim"); await store.Save(n, st, c.Ct); }
            return new JsonObject { ["ok"] = true, ["id"] = n, ["released"] = true };
        }
        AssertOpen(info);
        if (!info.HasLabel(Cfg.Label)) throw new ItemRefusal(10, "no-worker-label", $"item does not carry the label '{Cfg.Label}'");
        if (info.HasLabel(Cfg.RivalLabel)) throw new ItemRefusal(10, "ambiguous-owner", $"item carries both '{Cfg.Label}' and '{Cfg.RivalLabel}'; one owner per item");
        if (info.HasLabel(Cfg.DemoLabel)) throw new ItemRefusal(10, "demo-exclusion", $"item carries the '{Cfg.DemoLabel}' label");
        var holdLabel = Cfg.HoldLabelOf(info.Labels);
        if (holdLabel.Length > 0) throw new ItemRefusal(10, "held-by-label", $"item carries the '{holdLabel}' label: a person holds it; no session takes it until the label is removed", new JsonObject { ["label"] = holdLabel });
        if (!Ps.NameIn(info.ColumnText, Cfg.ColumnsAi)) throw new ItemRefusal(10, "not-ai-column", $"item is in '{info.ColumnText}', which this worker does not work");
        if (Ps.Has(a, "column") && Ps.Truthy(a["column"]) && !Ps.Eq(Ps.Text(a, "column"), info.ColumnText))
        {
            if (board.ColumnIndex(info.ColumnText) > board.ColumnIndex(Ps.Text(a, "column"))) throw new ItemRefusal(11, "column-changed", $"item moved on to '{info.ColumnText}'");
        }
        var renew = Ps.Has(a, "renew") && Ps.TruthyKey(a, "renew");
        if (renew)
        {
            if (!Ps.Has(s, "claim") || !Ps.Eq(Ps.Text(s["claim"], "holder"), holder)) throw new ItemRefusal(40, "claim-not-held", "this session does not hold the claim");
            var claim0 = (JsonObject)s["claim"]!;
            if (Ps.Has(claim0, "taken_back")) throw new ItemRefusal(40, "claim-not-held", $"the gateway took this item back ({Ps.Text(claim0, "taken_back")}); it is offered again");
            claim0["lease_until"] = Ps.Iso(Now.AddMinutes(Cfg.LeaseMinutes)); claim0["heartbeat"] = Ps.Iso(Now);
            await store.Save(n, st, c.Ct);
            return new JsonObject { ["ok"] = true, ["id"] = n, ["renewed"] = true, ["lease_until"] = claim0["lease_until"]!.DeepClone() };
        }
        var park = ItemRules.GetPark(s, Now);
        if (park is not null && !(Ps.Has(a, "force") && Ps.TruthyKey(a, "force")))
            throw new ItemRefusal(10, "parked", $"item is parked: {Ps.Text(park, "reason")}", new JsonObject { ["park"] = park.DeepClone() });
        if (Ps.Eq(info.ColumnText, Cfg.ColumnValidation) && Ps.Has(s, "verdict") && !Ps.TruthyKey(s["verdict"], "processed") && !Ps.Has(a, "force"))
            throw new ItemRefusal(10, "verdict-already-recorded", "a validation verdict is already recorded for this round; nothing is left for a session to do (a PASS under merge policy auto is merged by the gateway)");
        var launches = 0; var mode = "first"; var sessionLaunches = 0;
        if (Ps.Has(s, "claim"))
        {
            var claim = (JsonObject)s["claim"]!;
            if (ItemRules.ClaimLive(s, Now) && !Ps.Eq(Ps.Text(claim, "holder"), holder))
                throw new ItemRefusal(40, "claim-held", $"claim held by {Ps.Text(claim, "holder")} until {Ps.Text(claim, "lease_until")}", new JsonObject { ["holder"] = claim["holder"]?.DeepClone(), ["lease_until"] = claim["lease_until"]?.DeepClone() });
            if (ItemRules.ClaimLive(s, Now) && Ps.Eq(Ps.Text(claim, "holder"), holder))
                return new JsonObject { ["ok"] = true, ["id"] = n, ["claimed"] = true, ["already"] = true, ["column"] = info.Column, ["lease_until"] = claim["lease_until"]?.DeepClone() };
            // expired: ended without signal
            launches = Ps.Int(claim, "launches"); mode = "relaunch";
            // A claim the gateway took back ended through session trouble, not through the item: such a relaunch does not spend the item's budget (maxRelaunches); it counts against maxSessionRelaunches instead,
            // whose park says so (session-relaunch-budget) and is lifted by the sweep once a worker is free.
            var sessionCause = Ps.Has(claim, "taken_back");
            sessionLaunches = Ps.Has(claim, "session_launches") ? Ps.Int(claim, "session_launches") : 0;
            if (sessionCause) { sessionLaunches++; launches = Math.Max(0, launches - 1); } else sessionLaunches = 0;
            var maxSession = Cfg.MaxSessionRelaunches;
            string? code = null;
            if (sessionCause && sessionLaunches > maxSession) code = "session-relaunch-budget";
            else if (launches > Cfg.MaxRelaunches)
                // a Functional Testing round whose sessions all ended without a verdict is parked for that
                code = Ps.Eq(info.ColumnText, Cfg.ColumnValidation) && !Ps.Has(s, "verdict") ? "validation-no-verdict" : "watchdog-relaunch-budget";
            if (code is not null)
            {
                var was = sessionCause ? Ps.Text(claim, "taken_back") : "";
                ItemRules.SetPark(s, code, Cfg.ParkMinutes, Now);
                var p = (JsonObject)s["park"]!;
                p["launches"] = launches; p["session_launches"] = sessionLaunches; if (was.Length > 0) p["last_take_back"] = was;
                s.Remove("claim"); await store.Save(n, st, c.Ct); await board.RemoveClaimRef(n, c.Ct);
                var why = code switch
                {
                    "session-relaunch-budget" => $"{sessionLaunches} sessions in a row lost **{info.ColumnText}** to session trouble (last: {was}), over the limit of {maxSession}; parked until a worker is free (the sweep lifts it).",
                    "validation-no-verdict" => $"relaunch budget ({Cfg.MaxRelaunches}) spent in **{info.ColumnText}** without a validation verdict; parked for a human.",
                    _ => $"relaunch budget ({Cfg.MaxRelaunches}) spent in **{info.ColumnText}**; parked for a human.",
                };
                await AddComment(n, why, c);
                throw new ItemRefusal(10, "parked", code, new JsonObject { ["park"] = s["park"]!.DeepClone() });
            }
            await board.RemoveClaimRef(n, c.Ct);
        }
        var head = Ps.Text(Ps.Get((await gh.Get($"{gh.RepoPath}/git/ref/heads/{Cfg.DefaultBranch}", c.Ct)), "object"), "sha");
        var refBody = new JsonObject { ["ref"] = $"refs/{board.ClaimRefPath}/{n}", ["sha"] = head };
        var r = await gh.Allow("POST", $"{gh.RepoPath}/git/refs", [422], refBody.DeepClone(), c.Ct);
        var tookOver = false;
        if (r.Allowed && r.Status == 422)
        {
            // The ref exists but the state holds no live claim (a live one was answered above): an orphan, left by a claim cut off between the ref and the state. This verb holds the item lock and only this gateway
            // writes claim refs, so the ref is taken over: removed and created again. A second 422 means another writer did win the ref meanwhile, and that is refused as before.
            await board.RemoveClaimRef(n, c.Ct);
            r = await gh.Allow("POST", $"{gh.RepoPath}/git/refs", [422], refBody.DeepClone(), c.Ct);
            if (r.Allowed && r.Status == 422)
                throw new ItemRefusal(40, "claim-held", "another session created the claim first", new JsonObject { ["detail"] = Ps.Text(r.Body, "message") });
            tookOver = true;
        }
        if (!Ps.Has(s, "dispatched_at")) s["dispatched_at"] = new JsonObject();
        Ps.CiSet((JsonObject)s["dispatched_at"]!, info.ColumnText, Ps.Iso(Now));
        ItemRules.AddFlowStamp(s, info.ColumnText, Now);   // (W0.8: the stage hook of a claim)
        if (!Ps.Has(s, "reviews")) s["reviews"] = new JsonObject();
        Ps.CiRemove((JsonObject)s["reviews"]!, info.ColumnText);
        if (Ps.Has(s, "park")) s.Remove("park");
        s.Remove("handoff");
        if (Ps.Eq(info.ColumnText, Cfg.ColumnDevelopment) && Ps.Has(s, "pr") && Ps.Has(s["pr"], "rejection")) mode = "fix-forward";
        if (mode != "relaunch") await refusals.Reset(n, c.Ct);
        var newClaim = new JsonObject
        {
            ["holder"] = holder, ["column"] = info.Column, ["started"] = Ps.Iso(Now), ["heartbeat"] = Ps.Iso(Now),
            ["lease_until"] = Ps.Iso(Now.AddMinutes(Cfg.LeaseMinutes)), ["launches"] = launches + 1,
        };
        s["claim"] = newClaim;
        if (sessionLaunches > 0) newClaim["session_launches"] = sessionLaunches;
        if (Ps.Has(a, "owner") && Ps.TruthyKey(a, "owner")) { newClaim["owner"] = true; newClaim["worker"] = Ps.Text(a, "worker"); newClaim["assignment"] = Ps.Text(a, "assignment"); if (mode == "relaunch") mode = "resume"; }
        // timing or statistical criteria are flagged once, at Technical Design (the design column), on the item and in the answer; never a block
        var timing = new List<string>();
        if (Cfg.HasSection(info.ColumnText))
        {
            timing = ItemRules.TimingCriteria(info, Cfg);
            if (timing.Count > 0 && !(Ps.Has(s, "timing_flagged") && Ps.TruthyKey(s, "timing_flagged")))
            {
                s["timing_flagged"] = true;
                var list = string.Join("\n", timing.Select(x => $"- {x}"));
                await AddComment(n, $"**Timing or statistical acceptance criteria** (flagged at {info.ColumnText}):\n\n{list}\n\nThese are hard to verify in one functional test and often fail twice. The design should say exactly how each is measured (environment, sample size, threshold, what counts as a pass), or the owner should reword it. Two consecutive failures that cite the same criterion park the item with a question to the owner.", c);
            }
        }
        await store.Save(n, st, c.Ct);
        var o = new JsonObject { ["ok"] = true, ["id"] = n, ["claimed"] = true, ["column"] = info.Column, ["mode"] = mode, ["launches"] = launches + 1, ["lease_until"] = newClaim["lease_until"]!.DeepClone() };
        if (tookOver) o["orphan_ref_taken_over"] = true;
        if (timing.Count > 0) o["timing_criteria"] = Ps.Array(timing);
        return o;
    }

    // Verb_accept: the answer to a directed assignment. Own-item: the per-item claim (every claim rule applies). Bot job: a separate bot lease. A QC environment's accept is W9.1's (Invoke-QcAccept).
    async Task<JsonObject> Accept(ItemCall c)
    {
        var n = c.Item; var assignment = c.NeedText("assignment"); var holder = c.NeedText("holder");
        var kind = c.Has("kind") ? c.Text("kind") : "own-item";
        if (Ps.Eq(kind, "qc-env")) throw ToV1("the accept of a QC environment is the QC gate's (W9.1)");
        if (Ps.Eq(kind, "bot-job"))
        {
            var role = c.Has("role") ? c.Text("role") : "inspector";
            var info = await board.GetItemInfo(n, c.Ct); AssertOpen(info);
            var st = await store.Read(n, c.Ct); var s = st.State;
            if (!Ps.Has(s, "verdict") || !Ps.TruthyKey(s["verdict"], "passed") || Ps.TruthyKey(s["verdict"], "processed")) throw new ItemRefusal(10, "no-pass-verdict", "no unprocessed PASS verdict is recorded for this item");
            if (ItemRules.BotLive(s, Now) && !Ps.Eq(Ps.Text(s["bot"], "assignment"), assignment))
                throw new ItemRefusal(40, "bot-claimed", $"another {role} job holds this item until {Ps.Text(s["bot"], "lease_until")}");
            s["bot"] = new JsonObject { ["holder"] = holder, ["assignment"] = assignment, ["role"] = role, ["started"] = Ps.Iso(Now), ["lease_until"] = Ps.Iso(Now.AddMinutes(Cfg.LeaseMinutes)) };
            await AddJournal(n, st, "assigned", new JsonObject { ["worker"] = holder, ["role"] = role, ["pr"] = s["verdict"]!["pr"]?.DeepClone(), ["url"] = c.Has("url") ? c.Text("url") : "" }, $"bot|{assignment}", c);
            await store.Save(n, st, c.Ct);
            return new JsonObject { ["ok"] = true, ["id"] = n, ["accepted"] = true, ["kind"] = "bot-job", ["role"] = role, ["pr"] = s["verdict"]!["pr"]?.DeepClone(), ["lease_until"] = s["bot"]!["lease_until"]!.DeepClone() };
        }
        var claimArgs = Ps.NewObject();
        claimArgs["item"] = n; claimArgs["holder"] = holder; claimArgs["owner"] = true; claimArgs["worker"] = holder; claimArgs["assignment"] = assignment;
        if (c.Has("column")) claimArgs["column"] = c.Args["column"]!.DeepClone();
        var r = await ClaimCore(c, claimArgs);
        if (!Ps.Has(r, "already") || !Ps.TruthyKey(r, "already"))
        {
            var tier = await ModelTier(n, null, c);
            r["journal"] = new JsonObject
            {
                ["event"] = "assigned", ["key"] = $"owner|{assignment}",
                ["fields"] = new JsonObject
                {
                    ["worker"] = holder, ["column"] = r["column"]?.DeepClone(), ["mode"] = r["mode"]?.DeepClone(), ["attempt"] = r["launches"]?.DeepClone(), ["lease"] = r["lease_until"]?.DeepClone(),
                    ["by"] = c.Has("accepted_by") && Ps.Eq(c.Text("accepted_by"), "client") ? "client" : "session",
                    ["model"] = c.Has("model") ? c.Text("model") : "", ["effort"] = c.Has("effort") ? c.Text("effort") : "", ["tier"] = tier,
                    ["url"] = c.Has("url") ? c.Text("url") : "",
                },
            };
        }
        r["kind"] = "own-item"; r["assignment"] = assignment;
        return r;
    }

    /// <summary><c>Get-TierFor</c>: the tier a value reaches against the thresholds of a signal.</summary>
    static int TierFor(double value, JsonNode? thresholds)
    {
        var t = 0; var list = Ps.Strings(thresholds).ToList();
        for (var i = 0; i < list.Count; i++) if (value >= double.Parse(list[i], System.Globalization.CultureInfo.InvariantCulture)) t = i + 1;
        return t;
    }

    /// <summary><c>Verb_model_tier</c> (Models.ps1): when to escalate. An item's history turned into a tier number, never lower than before in the same column; the tier is kept in the state.</summary>
    async Task<int> ModelTier(int n, string? columnArg, ItemCall c)
    {
        var e = Cfg.Escalation;
        var info = await board.GetItemInfo(n, c.Ct); var st = await store.Read(n, c.Ct); var s = st.State;
        var col = !string.IsNullOrEmpty(columnArg) ? columnArg : info.ColumnText;
        var sig = new List<(string Name, int Value, int Tier)>();
        var launches = Ps.Has(s, "claim") && Ps.Has(s["claim"], "launches") ? Ps.Int(s["claim"], "launches") : 0;
        sig.Add(("launches", launches, TierFor(launches, e["launches"])));
        var rounds = 0;
        if (Ps.Obj(s, "reviews") is { } reviews && Ps.CiGet(reviews, col) is JsonObject rv && Ps.Text(rv, "last") == "FINDINGS") rounds = Ps.Int(rv, "round");
        sig.Add(("findingsRounds", rounds, TierFor(rounds, e["findingsRounds"])));
        var dem = Ps.Has(s, "demotions") ? Ps.Int(s, "demotions") : 0;
        sig.Add(("demotions", dem, TierFor(dem, e["demotions"])));
        var ff = Ps.Has(s, "fix_forward_rounds") ? Ps.Int(s, "fix_forward_rounds") : 0;
        sig.Add(("fixForwardRounds", ff, TierFor(ff, e["fixForwardRounds"])));
        var rf = await refusals.Count(n, c.Ct);
        sig.Add(("refusals", rf, TierFor(rf, e["refusals"])));
        var floor = 0;
        if (e["minTierByColumn"] is JsonObject minTier && Ps.CiGet(minTier, col) is { } mt && Ps.Has(minTier, Ps.CiKey(minTier, col) ?? "")) floor = Ps.Int(mt);
        var tier = floor; foreach (var sg in sig) if (sg.Tier > tier) tier = sg.Tier;
        var prev = Ps.Has(s, "model_tier") && Ps.Eq(Ps.Text(s["model_tier"], "column"), col) ? Ps.Int(s["model_tier"], "tier") : 0;
        if (prev > tier) tier = prev;
        if (!Ps.Has(s, "model_tier") || !Ps.Eq(Ps.Text(s["model_tier"], "column"), col) || Ps.Int(s["model_tier"], "tier") != tier)
        {
            s["model_tier"] = new JsonObject { ["column"] = col, ["tier"] = tier, ["at"] = Ps.Iso(Now), ["signals"] = Ps.Array(sig.Where(x => x.Tier > 0).Select(x => x.Name)) };
            await store.Save(n, st, c.Ct);
        }
        return tier;
    }

    // Verb_release: the owner (or an admin) gives the item up. The next trigger assigns it again and a new owner resumes from the item state.
    async Task<JsonObject> Release(ItemCall c)
    {
        var n = c.Item; var holder = c.NeedText("holder");
        var reason = c.Has("reason") ? c.Text("reason") : "released";
        var st = await store.Read(n, c.Ct); var s = st.State;
        if (!Ps.Has(s, "claim")) return new JsonObject { ["ok"] = true, ["id"] = n, ["released"] = false, ["reason"] = "no claim" };
        if (!Ps.Eq(Ps.Text(s["claim"], "holder"), holder) && !(c.Has("force") && c.Truthy("force"))) throw new ItemRefusal(40, "claim-not-held", $"the claim is held by {Ps.Text(s["claim"], "holder")}");
        s.Remove("claim");
        await AddJournal(n, st, "released", new JsonObject { ["worker"] = holder, ["reason"] = reason }, $"rel|{Now.Ticks}", c);
        await store.Save(n, st, c.Ct); await board.RemoveClaimRef(n, c.Ct);
        return new JsonObject { ["ok"] = true, ["id"] = n, ["released"] = true };
    }

    // Verb_expire_claim: the gateway takes an owned item back (gateway/Lease.cs). The claim stays and its lease ends now, marked taken_back, so the next sweep offers the item with mode resume and claim-item --renew of the old owner is refused.
    async Task<JsonObject> ExpireClaim(ItemCall c)
    {
        var n = c.Item; var holder = c.NeedText("holder");
        var reason = c.Has("reason") ? c.Text("reason") : "taken-back";
        var st = await store.Read(n, c.Ct); var s = st.State;
        if (!ItemRules.OwnedClaim(s) || !Ps.Eq(Ps.Text(s["claim"], "holder"), holder))
            return new JsonObject { ["ok"] = true, ["id"] = n, ["expired"] = false, ["reason"] = "the claim is not held by that owner" };
        var claim = (JsonObject)s["claim"]!;
        claim["lease_until"] = Ps.Iso(Now.AddSeconds(-1)); claim["taken_back"] = reason;
        await AddJournal(n, st, "lease-expired", new JsonObject { ["worker"] = holder, ["reason"] = reason }, $"exp|{Now.Ticks}", c);
        await store.Save(n, st, c.Ct);
        return new JsonObject { ["ok"] = true, ["id"] = n, ["expired"] = true };
    }
}
