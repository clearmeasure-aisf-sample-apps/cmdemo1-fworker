using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static HttpResultsX;

// The single path a verb call takes (task C7d, seam S1 of docs/design/fworker-wolverine-refactor.md), moved verbatim out of Program.cs: the verb name and
// body checks, the caller's authorization, the refusals the gateway itself makes (verbs that have an endpoint, the git credential, the backlog keeper,
// the supervisor's budget, the plant manager's flow verb, text derived from a message, QC, the GitHub budget, the accept of an offer), the run of the
// rule script and the plant's hooks after it. The order of the checks is the order Program.cs had; each answer is the one it gave.
sealed class VerbPipeline(GatewayOptions gateway, MessagingSettings messaging, KeeperSettings keeper, Plant plant, Messenger messenger, QcHub qc,
    Runner runner, EventHub hub, GitHubBudget githubBudget, IServiceProvider services)
{
    public async Task<IResult> Run(string verb, HttpContext ctx)
    {
        var ownerMode = gateway.OwnerMode;
        if (verb.Any(ch => !(char.IsAsciiLetterLower(ch) || ch == '-'))) return Results.BadRequest(new { ok = false, code = "bad-verb", exit = 2 });
        string text; using (var rd = new StreamReader(ctx.Request.Body)) text = await rd.ReadToEndAsync();
        JsonObject body; try { body = string.IsNullOrWhiteSpace(text) ? new JsonObject() : (JsonNode.Parse(text) as JsonObject ?? new JsonObject()); } catch { return Results.BadRequest(new { ok = false, code = "bad-json", exit = 2 }); }
        var who = ctx.Caller().Who;
        var (authorized, refusal) = Auth.AuthorizeVerb(who, verb, body);
        if (authorized == null)
        {
            Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"{verb}\",\"caller\":\"{who.Kind}:{who.Name}\",\"refused\":{JsonSerializer.Serialize(refusal)}}}");
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "forbidden", reason = refusal, exit = 30 }), "application/json", Encoding.UTF8, 403);
        }
        if (verb == "git-credential" && who.Kind == "item" && !(ownerMode && plant.OwnsAccepted(who.Name, who.Item)))
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "not-owner", reason = $"this session does not own item #{who.Item} through an accepted assignment: no git credential", exit = 10 }), "application/json", Encoding.UTF8, 409);
        // the rule scripts of a revocation run only inside POST /workers/{name}/revoke, which keeps the stored generation and this process in step
        if (verb is "revoke-worker" or "revocations")
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "use POST /api/v1/workers/{name}/revoke", exit = 2 }), "application/json", Encoding.UTF8, 400);
        // likewise the release records of the operator session: only POST /api/v1/operator/record-release keeps the stored refs and this process in step
        if (verb is "plant-snapshot-save" or "plant-snapshot-load" or "plant-snapshot-clear" or "plant-snapshot-await" or "leader-lease" or "transcript-save" or "transcript-load" or "transcript-prune")
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "the plant snapshot is written and read by the gateway itself, at its stop and its start", exit = 2 }), "application/json", Encoding.UTF8, 400);
        if (verb is "operator-release" or "operator-releases")
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "use POST /api/v1/operator/record-release", exit = 2 }), "application/json", Encoding.UTF8, 400);
        // a person releases a park early only through POST /api/v1/items/{n}/unpark (operator token, with a reason)
        if (verb is "ci-release")
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "the gateway releases the Copilot agent's held workflow runs by itself", exit = 2 }), "application/json", Encoding.UTF8, 400);
        if (verb is "park-item")
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "the gateway parks an item runtime-failed by itself", exit = 2 }), "application/json", Encoding.UTF8, 400);
        if (verb is "copilot-owner")
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "the gateway records a Copilot task's ownership of an item by itself", exit = 2 }), "application/json", Encoding.UTF8, 400);
        if (verb is "copilot-settle")
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "the gateway settles a Copilot Development task by itself, at its launch and its end", exit = 2 }), "application/json", Encoding.UTF8, 400);
        if (verb is "unpark-item")
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "use POST /api/v1/items/{n}/unpark (client: factory.ps1 unpark --item N --reason \"...\")", exit = 2 }), "application/json", Encoding.UTF8, 400);
        if (verb is "resettle-item")
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "use POST /api/v1/items/{n}/resettle", exit = 2 }), "application/json", Encoding.UTF8, 400);
        // the demo video arrives as a file: only POST /api/v1/demo/{item} streams it to the place the rule script reads it from
        if (verb is "publish-demo")
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "use-endpoint", reason = "send the MP4 as the body of POST /api/v1/demo/{item} (client: factory.ps1 publish-demo --item N --file F)", exit = 2 }), "application/json", Encoding.UTF8, 400);
        // the backlog keeper (Keeper.cs): only the name that holds the slot; the day's limit of new items before a verb that creates one; a pin
        // "next-free" resolved to the worker the plan would offer an unpinned item to
        if (who.Kind == "keeper")
        {
            if (!ownerMode || plant.ActiveKeeper() != who.Name) return Fail(403, "not-keeper", "this credential is not the active backlog keeper: register with subscribe --own --keeper first", 30);
            // flags are JSON booleans and children a JSON array: the rule verb reads exactly what was counted here
            foreach (var bf in new[] { "follow_up", "unpark", "remove", "chain" })
                if (authorized[bf] is { } bv && bv.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) return Fail(400, "usage", $"{bf} is true or false", 2);
            if (authorized["children"] is { } cv && cv is not JsonArray) return Fail(400, "usage", "children is a JSON array", 2);
            var need = verb switch
            {
                "create-item" => 1,
                "split-item" => authorized["children"] is JsonArray ka ? ka.Count : 2,
                "amend-criteria" => authorized["follow_up"] is JsonValue fu && fu.TryGetValue<bool>(out var fb) && fb ? 1 : 0,
                _ => 0
            };
            if (need > 0 && plant.KeeperItemsLeft() < need)
                return Results.Json(new { ok = false, verb, code = "daily-item-limit", reason = $"the backlog keeper may create {keeper.MaxNewItemsPerDay} new items in 24 hours; this would need {need} and {plant.KeeperItemsLeft()} are left", retryAfterSeconds = 3600, exit = 20 }, statusCode: 429);
            if (verb == "create-item" && authorized["pin"]?.ToString() == "next-free")
            {
                var free = plant.NextFreeWorker();
                if (free == "") return Fail(409, "no-free-worker", "no connected worker has a free slot for a pin of next-free: create it without a pin, or name the worker", 10);
                authorized["pin"] = free;
            }
        }
        // every verb call of the supervisor is a step of its hourly budget (worker-messaging.md 3.9); at the budget its read verbs answer 429 step-budget
        // (only the active supervisor spends steps: another supervisor name's credential is refused before anything is counted)
        if (ownerMode && who.Kind == "manager" && plant.ActiveSupervisor().Name != who.Name)
        {
            Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"{verb}\",\"caller\":\"{who.Kind}:{who.Name}\",\"refused\":\"not-supervisor\"}}");
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "not-supervisor", reason = "this credential is not the active supervisor: register with subscribe --own --supervisor first", exit = 30 }), "application/json", Encoding.UTF8, 403);
        }
        if (ownerMode && who.Kind == "manager" && plant.SupervisorStep(false) is { Ok: false } noStep)
        {
            Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"{verb}\",\"caller\":\"{who.Kind}:{who.Name}\",\"refused\":\"step-budget\"}}");
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "step-budget", reason = "the supervisor's step budget of this hour is spent; keep waiting for events and do not work around it", retryAfterSeconds = noStep.RetryAfter, exit = 20 }), "application/json", Encoding.UTF8, 429);
        }
        // the plant manager's flow verb (docs/design/plant-manager.md 10e, Manager.cs): one action per call, by the active supervisor or the operator token
        // (Auth.cs refuses every other credential). Checked here, before anything changes: the mode (advise turns every action off), the action, the policy
        // code, the reason, and the limit per hour over every item. A name action (drain, resume, restart-session) is the plant's and is answered here; an
        // item action runs as the rule verb, which checks the owner's hold and the limit per item on the item itself.
        object? mgrTicket = null;
        if (verb == "manager-action")
        {
            if (!ownerMode) return Fail(403, "not-owner-mode", "the gateway is not running in owner mode", 30);
            var mAction = authorized["action"]?.ToString() ?? ""; var mPolicy = authorized["policy"]?.ToString() ?? ""; var mWorker = authorized["worker"]?.ToString() ?? "";
            var mBy = who.Kind == "manager" ? who.Name : "operator"; authorized["by"] = mBy;
            // the Copilot session of the on-demand manager (ManagerCopilot.cs) goes into the journal line it causes
            var mSession = who.Kind == "manager" ? services.GetService<ManagerCopilot>()?.SessionUrl(who.Name) ?? "" : "";
            if (mSession != "") authorized["session"] = mSession; else authorized.Remove("session");
            // one line, no HTML comment markers (the journal's machine-read marker), no mention: as the rule verb cleans it, for the name actions too
            var mReason = authorized["reason"] is JsonValue mrv && mrv.TryGetValue<string>(out var mrs) ? System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(mrs.Replace("://", " "), @"[<>@`\[\]!]", ""), @"\s+", " ").Trim() : "";
            IResult MgrNo(int status, string code, string reason, int exit, int retry = 0)
            {
                Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["managerAction"] = Auth.ValidTag(mAction) ? mAction : "", ["by"] = mBy, ["refused"] = code }.ToJsonString());
                return Results.Text(retry > 0 ? JsonSerializer.Serialize(new { ok = false, verb, code, reason, retryAfterSeconds = retry, exit }) : JsonSerializer.Serialize(new { ok = false, verb, code, reason, exit }), "application/json", Encoding.UTF8, status);
            }
            if (!Auth.ManagerItemActions.Contains(mAction) && !Auth.ManagerNameActions.Contains(mAction))
                return MgrNo(400, "usage", $"action is one of {string.Join(", ", Auth.ManagerItemActions.Concat(Auth.ManagerNameActions))}", 2);
            if (!plant.MgrActions.Policies.Contains(mPolicy)) return MgrNo(400, "unknown-policy", $"the policy code is one of manager.actions.policies: {string.Join(", ", plant.MgrActions.Policies)}", 2);
            if (mReason.Length > 300 || (authorized["reason"] is { } rn && rn.GetValueKind() != JsonValueKind.String)) return MgrNo(400, "usage", "reason is text of at most 300 characters", 2);
            if (mReason != "" && messenger.LooksSecret(mReason)) return MgrNo(409, "secret-in-message", "the reason looks like a credential or token; it goes into a journal comment and never carries a secret", 10);
            if (plant.ManagerMode() == "advise") return MgrNo(409, "advise-mode", "the plant manager is in advise mode (manager.mode, set by the owner): it changes nothing; report to the operator instead", 10);
            if (Auth.ManagerNameActions.Contains(mAction))
            {
                if (!Auth.ValidName(mWorker) || messaging.Reserved.Contains(mWorker)) return MgrNo(400, "usage", "worker is the name of a worker session", 2);
                if (mAction is "restart-session" or "drain" && plant.ManagerRuntimeOwned(0, mWorker)) return MgrNo(409, "runtime-owner", "that session is a runtime task that is still running; the runtime manager ends it past its deadline", 10);
                var (nt, nra, ncd) = plant.ManagerActionBegin(mAction, 0, mWorker, mPolicy, mBy);
                if (nt == null && ncd == "name-action-limit") return MgrNo(409, "name-action-limit", $"the plant manager drained or restarted {mWorker} {plant.MgrActions.PerItemPerDay} times in 24 hours: leave it to the operator", 10, nra);
                if (nt == null) return MgrNo(429, "action-limit", $"the plant manager's limit of {plant.MgrActions.PerHour} actions an hour is reached; leave the rest to the operator", 20, nra);
                (int Exit, string Code, string Reason, List<int> Items) nr;
                if (mAction == "restart-session") nr = plant.ManagerRestart(mWorker, mPolicy);
                else { var dr = plant.ManagerDrain(mWorker, mAction == "drain"); nr = (dr.Exit, dr.Code, dr.Reason, new List<int>()); }
                var (nx, ncode, nreason, items) = nr;
                plant.ManagerActionDone(nt, nx == 0, ncode, mWorker);
                Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["managerAction"] = mAction, ["worker"] = mWorker, ["policy"] = mPolicy, ["by"] = mBy, ["exit"] = nx }.ToJsonString());
                if (nx != 0) return Results.Text(nx == 20 ? JsonSerializer.Serialize(new { ok = false, verb, action = mAction, code = ncode, reason = nreason, retryAfterSeconds = 60, exit = nx }) : JsonSerializer.Serialize(new { ok = false, verb, action = mAction, code = ncode, reason = nreason, exit = nx }), "application/json", Encoding.UTF8, nx == 12 ? 404 : nx == 20 ? 503 : 409);
                if (mAction == "restart-session")
                    foreach (var oi in items)
                        plant.JournalManagerAction(oi, new JsonObject { ["action"] = mAction, ["policy"] = mPolicy, ["by"] = mBy, ["reason"] = mReason, ["session"] = mSession, ["detail"] = $"restarted the session of **{mWorker}**, which owns this item; its next session resumes it" }, $"restart|{mWorker}|{DateTime.UtcNow.Ticks}");
                hub.RequestSweep();
                var mstate = plant.IsDraining(mWorker);
                return Results.Text(JsonSerializer.Serialize(new { ok = true, verb, action = mAction, worker = mWorker, policy = mPolicy, draining = mstate, items, exit = 0 }), "application/json");
            }
            if (!(authorized["item"] is JsonValue miv && miv.TryGetValue<int>(out var mItem) && mItem > 0)) return MgrNo(400, "usage", "item is a work item number", 2);
            // a pin names a session this gateway knows (registered since its start): a typo would hold the item for nobody
            if (mAction == "pin" && !(Auth.ValidName(mWorker) && plant.KnowsWorker(mWorker))) return MgrNo(404, "no-such-worker", "pin names a worker session that has registered with this gateway", 12);
            authorized["reason"] = mReason;
            // a runtime task (Copilot, Cursor, CAE job) that still runs is neither stalled nor disconnected: the manager leaves its owner alone
            if (mAction is "release-owner" or "requeue" && plant.ManagerRuntimeOwned(mItem, "")) return MgrNo(409, "runtime-owner", "the owner of this item is a runtime task that is still running; the runtime manager ends it past its deadline", 10);
            var (it0, ira, _) = plant.ManagerActionBegin(mAction, mItem, mWorker, mPolicy, mBy);
            if (it0 == null) return MgrNo(429, "action-limit", $"the plant manager's limit of {plant.MgrActions.PerHour} actions an hour is reached; leave the rest to the operator", 20, ira);
            mgrTicket = it0;
        }
        // plant data for the digest (worker-messaging.md 3.9): a verb called with an item token is that session's activity on that item
        if (ownerMode && messaging.On && who.Kind == "item") plant.NoteActivity(who.Name, who.Item, true);
        // text that a session writes into an item because of a message is marked by the gateway, not by the model (worker-messaging.md 3.8)
        if (ownerMode && messaging.On && verb is "append-body" or "comment" && DerivedText(verb, authorized, who) is { } derivedRefusal) return derivedRefusal;
        if (ownerMode && QcHub.Handled.Contains(verb))
        {   // the verbs that touch the in-memory half of a QC environment (and Start, Reset, Teardown, which place jobs)
            var (qs, qj) = await qc.HandleVerb(verb, authorized, who);
            Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"{verb}\",\"item\":\"{authorized["item"]}\",\"holder\":\"{authorized["holder"]}\",\"caller\":\"{who.Kind}:{who.Role}\",\"status\":{qs}}}");
            return Results.Text(qj, "application/json", Encoding.UTF8, qs);
        }
        // below the reserve of the GitHub budget only the essential verbs run (GitHubBudget.cs, issue 92): the rest wait for the reset
        if (!GitHubBudget.EssentialVerbs.Contains(verb) && githubBudget.Low() is { Low: true } gl)
        {
            githubBudget.CountVerbRefusal();
            // the owner is at work and is told to wait: that is no quiet item (gateway/Lease.cs)
            if (ownerMode && who.Kind == "item" && who.Role == "owner") { try { plant.NoteItemCall(who.Name, who.Item, ""); } catch { } }
            Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"{verb}\",\"caller\":\"{who.Kind}:{who.Name}\",\"refused\":\"github-budget\",\"resource\":\"{gl.Resource}\"}}");
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = "github-budget", reason = $"the gateway's GitHub budget ({gl.Resource}) is below its reserve; only accept, claim-item, release and my-items run until the reset", retryAfterSeconds = Math.Max(1, (int)(gl.ResetAt - DateTime.UtcNow).TotalSeconds), resetAt = gl.ResetAt.ToString("o"), exit = 20 }), "application/json", Encoding.UTF8, 503);
        }
        var timeout = TimeSpan.FromSeconds(verb is "wait-for-checks" ? 140 : 580);
        // issue 30: only the offer that stands for this session now can be accepted (a late accept of a withdrawn offer is refused, exit 11)
        var accepting = false;
        if (ownerMode && verb == "accept" && who.Kind == "item")
        {
            var (ax, acode, areason) = plant.BeginAccept(who);
            if (ax != 0)
            {
                Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"accept\",\"item\":\"{who.Item}\",\"caller\":\"{who.Kind}:{who.Role}\",\"refused\":\"{acode}\"}}");
                return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code = acode, reason = areason, exit = ax }), "application/json", Encoding.UTF8, 409);
            }
            accepting = true;
            // a runtime worker's acceptance carries its execution's page (a Copilot task's session), for the item's journal
            if (!(authorized["url"] is JsonValue) && plant.RuntimeUrlOf(who.Name) is { Length: > 0 } ru) authorized["url"] = ru;
        }
        if (ownerMode && who.Role == "qc" && verb is "accept" or "qc-progress" or "qc-fail" or "qc-stopped") qc.Arrived(who);
        // the AI session that calls (a worker, role, manager or keeper credential): the rule scripts end every comment they write for it with a link
        // to that session (Board.ps1 Add-Comment). Never a token: the page of the gateway, or the runtime's own page of the agent or task.
        authorized.Remove("_session");
        if (ctx.Caller().SessionLinkOf(who) is { } sl) authorized["_session"] = sl;
        var (code, output) = await runner.RunAsync(verb, authorized.ToJsonString(), timeout);
        if (mgrTicket != null)
        {   // the plant's part of an item action of the plant manager, after its rule script changed the item (Manager.cs)
            JsonObject? mo = null; try { mo = JsonNode.Parse(output) as JsonObject; } catch { }
            plant.ManagerActionDone(mgrTicket, code == 0, code == 0 ? "" : mo?["code"]?.ToString() ?? $"exit-{code}");
            // the journal line of an action can be dropped (the item's journal is full, or GitHub refused it): the gateway's log says so
            if (code == 0 && mo?["journaled"] is JsonValue jv && jv.TryGetValue<bool>(out var jb) && !jb)
                Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["managerAction"] = authorized["action"]?.ToString(), ["item"] = authorized["item"]?.DeepClone(), ["journaled"] = false }.ToJsonString());
            if (code == 0 && mo != null && int.TryParse(authorized["item"]?.ToString(), out var mi))
            {
                try
                {
                    switch (authorized["action"]?.ToString())
                    {
                        case "unpark":
                            if (mo["reoffer"] is JsonValue rov && rov.TryGetValue<bool>(out var rob) && rob) plant.OnVerb("release", new JsonObject { ["item"] = mi }, who, new JsonObject());
                            plant.ClearParks(new[] { mi });
                            break;
                        case "release-owner": if (mo["holder"]?.ToString() is { } mh && Auth.ValidName(mh)) plant.ManagerReleased(mi, mh); break;
                        case "requeue": plant.ManagerRequeued(mi); break;
                    }
                }
                catch (Exception e) { Console.Error.WriteLine($"plant hook manager-action: {e.Message}"); }
                hub.RequestSweep();
            }
        }
        var item = authorized["item"]?.ToString() ?? ""; var holder = authorized["holder"]?.ToString() ?? "";
        Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"{verb}\",\"item\":\"{item}\",\"holder\":\"{holder}\",\"caller\":\"{who.Kind}:{who.Role}\",\"exit\":{code}}}");
        if (string.IsNullOrWhiteSpace(output)) output = JsonSerializer.Serialize(new { ok = false, code = "no-output", reason = "the rule script produced no output", exit = code == 0 ? 20 : code });
        var status = code switch { 0 => 200, 21 => 202, 2 => 400, 12 => 404, 10 or 11 or 40 => 409, 20 => 503, _ => 500 };
        // a gate refusal of one code on one item, for the digest's refusals ring (worker-messaging.md 3.9)
        if (ownerMode && messaging.On && code is 10 or 11 && who.Kind == "item") { try { plant.NoteRefusal(who.Name, who.Item, JsonNode.Parse(output)?["code"]?.ToString() ?? ""); } catch { } }
        // a verb that failed for an item (the gateway or GitHub could not serve it, or the rule script crashed), for the digest's verb-errors (3.9)
        if (ownerMode && messaging.On && status is 503 or 500 && who.Kind == "item") { string failed = ""; try { failed = JsonNode.Parse(output)?["code"]?.ToString() ?? ""; } catch { } plant.NoteVerbError(who.Name, who.Item, failed, code); }
        if (ownerMode && keeper.Enabled && verb == "complete" && code == 0 && int.TryParse(authorized["item"]?.ToString(), out var kpi)) { try { if (JsonNode.Parse(output)?["parked"] is JsonValue kpv && kpv.TryGetValue<bool>(out var kpb) && kpb) plant.KeeperPark(kpi, "worker-failure", authorized["column"]?.ToString() ?? ""); } catch { } }
        if (ownerMode && keeper.Enabled && who.Kind == "keeper" && code == 0) { try { var ko = JsonNode.Parse(output); plant.KeeperCreated((ko?["created"] as JsonArray)?.Count ?? (ko?["follow_up"] is JsonValue fv && fv.TryGetValue<int>(out _) ? 1 : 0)); } catch { } }
        if (ownerMode && messaging.OperatorEnabled) { try { plant.OperatorVerbEvent(verb, code, who, authorized, output); } catch { } }
        if (ownerMode && code == 0) { try { plant.OnVerb(verb, authorized, who, (JsonNode.Parse(output) as JsonObject) ?? new JsonObject()); } catch (Exception e) { Console.Error.WriteLine($"plant hook {verb}: {e.Message}"); } }
        if (accepting) plant.EndAccept(who.Assignment);
        // issue 31: a session that calls a verb successfully is answering: its count of lapsed offers starts again. Issue 100: not the accept its stream
        // client makes by itself (accepted_by client): a blocked session's client accepts every offer, and that is no sign that the session works
        if (ownerMode && code == 0 && who.Kind is "item" or "worker" && !(verb == "accept" && authorized["accepted_by"]?.ToString() == "client")) plant.NoteAnswered(who.Name);
        // any call of the owner for its item is its activity and renews the lease when due (gateway/Lease.cs); after OnVerb, which records an accept
        if (ownerMode && who.Kind == "item" && who.Role == "owner") { try { JsonObject? vr = null; try { vr = JsonNode.Parse(output) as JsonObject; } catch { } plant.NoteItemCall(who.Name, who.Item, verb, authorized, vr, code == 0); } catch (Exception e) { Console.Error.WriteLine($"lease hook {verb}: {e.Message}"); } }
        if (ownerMode && code == 0 && (verb is "qc-progress" or "qc-fail" or "qc-stopped" || (verb == "accept" && who.Role == "qc"))) { try { await qc.AfterVerb(verb, authorized, who, (JsonNode.Parse(output) as JsonObject) ?? new JsonObject()); } catch (Exception e) { Console.Error.WriteLine($"qc hook {verb}: {e.Message}"); } }
        if ((code == 0 || code == 21) && verb is "complete" or "validation-verdict" or "claim-item" or "accept" or "settle" or "merge" or "approve-pr") hub.RequestSweep(int.TryParse(item, out var swItem) ? swItem : 0);
        return Results.Text(output, "application/json", Encoding.UTF8, status);
    }

    // from_message on append-body and comment (worker-messaging.md 3.8): the id must be a message the caller sent or received; the text loses backticks
    // and HTML comment markers, is capped at 500 characters and becomes one marked, quoted block before the rule script runs. Without from_message the
    // literal marker is refused, so a session can neither forge a block nor close one early. Returns the refusal, or null when the call goes on.
    IResult? DerivedText(string verb, JsonObject body, Principal who)
    {
        var field = verb == "append-body" ? "content" : "body";
        IResult No(int status, string code, string reason, int exit)
        {
            Console.WriteLine($"{{\"at\":\"{DateTime.UtcNow:o}\",\"verb\":\"{verb}\",\"caller\":\"{who.Kind}:{who.Name}\",\"refused\":\"{code}\"}}");
            return Results.Text(JsonSerializer.Serialize(new { ok = false, verb, code, reason, exit }), "application/json", Encoding.UTF8, status);
        }
        var text = body[field] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : null;
        if (!body.ContainsKey("from_message") || body["from_message"] is null)
            return text != null && text.Contains("aisf:assumption", StringComparison.OrdinalIgnoreCase)
                ? No(409, "reserved-marker", "the text carries the marker aisf:assumption, which only the gateway writes: text that comes from a message goes in with --from-message <id>", 10) : null;
        var mid = body["from_message"]!.ToString();
        if (who.Kind != "item" || text == null || !System.Text.RegularExpressions.Regex.IsMatch(mid, @"^m-[0-9a-f]{8}-\d{4,}\z"))
            return No(400, "usage", "from_message is the id of a message this session sent or received, given with the session's item token and the text", 2);
        var (known, source, from, inReplyTo) = plant.MessageParty(mid, who.Name);
        if (!known) return No(404, "no-such-message", "from_message names no message that this session sent or received in this run of the gateway", 12);
        var clean = text.Replace("`", "").Replace("\r", "");
        while (clean.Contains("<!--") || clean.Contains("-->")) clean = clean.Replace("<!--", "").Replace("-->", "");
        var capped = new StringBuilder(); var count = 0;
        foreach (var rune in clean.Trim().EnumerateRunes()) { if (count++ >= 500) break; capped.Append(rune.ToString()); }
        clean = capped.ToString().Trim();
        if (clean.Length == 0) return No(400, "usage", "there is no text left to write: send the assumption or the decision in your own words", 2);
        // the block names the message whose text it carries and that message's sender; for an answer also the question it answers
        body[field] = $"<!-- aisf:assumption source={source} from={from}{(inReplyTo == "" ? "" : " in_reply_to=" + inReplyTo)} -->\n" + string.Join("\n", clean.Split('\n').Select(l => "> " + l)) + "\n<!-- /aisf:assumption -->";
        body.Remove("from_message");
        return null;
    }
}
