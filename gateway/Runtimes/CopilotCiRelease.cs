using System.Text.Json.Nodes;

// The CI release (docs/design/copilot-cloud-agent.md, "CI release"). GitHub puts the workflow runs that a push of the Copilot cloud agent
// triggers into action_required, and POST /actions/runs/{id}/approve refuses them (they are not from a fork); a re-run by an identity with write
// access starts them. On each pass (at most every api.copilot.ciReleaseSeconds, by the acting gateway only) the rule verb ci-release lists the
// action_required runs of copilot/* branches and re-runs those whose pull request is the open, in-scope item's, with that pull request's head
// (Verbs.Pr.ps1). No file guard (the owner's decision of 2026-10-20). The credential is the gateway's own GitHub App; a 403 is reported here and
// on the dashboard as an owner action, never retried with another credential. A run id is re-run once: released ids are passed back as skip.
sealed class CopilotCiRelease
{
    public const string OwnerAction = "grant factory-worker-author App Actions: Read & write (org settings, GitHub Apps, factory-worker-author, Permissions, Actions: Read & write, then accept on the installation)";
    readonly object gate = new();
    readonly LinkedList<string> handled = new();   // run ids released (bounded), passed to the verb as skip
    readonly HashSet<string> handledSet = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTime> refusedAt = new(StringComparer.Ordinal);
    readonly HashSet<string> refusedIds = new(StringComparer.Ordinal);   // distinct runs refused (the refused count)   // run id -> last 403: tried again after RefusedRetry
    readonly HashSet<string> journaledRefusals = new();
    int released, refused, passes, failedPasses, pending;
    string lastError = "", lastPass = "", lastRelease = "";
    bool permissionMissing;
    public TimeSpan RefusedRetry = TimeSpan.FromMinutes(10);

    // One pass. run: the rule verb runner; journal: one line on the item (item, fields, key).
    DateTime probedAt = DateTime.MinValue; string appActions = "";
    public TimeSpan ProbeEvery = TimeSpan.FromMinutes(30);

    public async Task Pass(IReadOnlyCollection<int> items, bool routeAll, Func<string, string, Task<(int Code, string Output)>> run, Func<int, JsonObject, string, Task> journal)
    {
        string[] skip;
        lock (gate)
        {
            var now = DateTime.UtcNow;
            foreach (var k in refusedAt.Where(kv => now - kv.Value >= RefusedRetry).Select(kv => kv.Key).ToList()) refusedAt.Remove(k);
            skip = handledSet.Concat(refusedAt.Keys).ToArray();
            passes++; lastPass = now.ToString("o");
        }
        var probe = DateTime.UtcNow - probedAt >= ProbeEvery;
        var (code, output) = await run("ci-release", new JsonObject { ["items"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray()), ["skip"] = new JsonArray(skip.Select(s => (JsonNode)s).ToArray()), ["routeAll"] = routeAll, ["probe"] = probe }.ToJsonString());
        JsonNode? j = null; try { j = JsonNode.Parse(output); } catch { }
        if (code != 0 || j?["ok"] is not JsonValue okv || !okv.TryGetValue<bool>(out var ok) || !ok)
        {
            lock (gate) { failedPasses++; lastError = $"pass: exit {code} {j?["code"]}".Trim(); }
            return;
        }
        var toJournal = new List<(int Item, JsonObject F, string Key)>();
        lock (gate)
        {
            pending = j["pending"]?.GetValue<int>() ?? 0;
            if (probe) probedAt = DateTime.UtcNow;
            // the installation's Actions permission (read with the App's JWT): anything but write is reported before a run is refused
            if (probe && j["actions"] is JsonValue av && av.TryGetValue<string>(out var act) && act != "")
            {
                appActions = act;
                if (act != "write") { permissionMissing = true; lastError = $"the App installation has Actions: {act}; a re-run needs write"; }
                else if (refusedAt.Count == 0) permissionMissing = false;
            }
            foreach (var r in (j["runs"] as JsonArray) ?? new JsonArray())
            {
                var id = r?["run"]?.ToString() ?? ""; var result = r?["result"]?.ToString() ?? "";
                if (id == "") continue;
                if (result == "released")
                {
                    if (!handledSet.Add(id)) continue;
                    handled.AddLast(id); if (handled.Count > 1000) { handledSet.Remove(handled.First!.Value); handled.RemoveFirst(); }
                    released++; permissionMissing = false; lastRelease = DateTime.UtcNow.ToString("o");
                    var item = r!["item"]?.GetValue<int>() ?? 0; var sha = r["sha"]?.ToString() ?? "";
                    if (item > 0) toJournal.Add((item, new JsonObject { ["run"] = id, ["pr"] = r["pr"]?.GetValue<int>() ?? 0, ["sha7"] = sha.Length > 7 ? sha[..7] : sha, ["branch"] = r["branch"]?.ToString() }, id));
                    Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "copilot-ci-release", ["result"] = "released", ["run"] = id, ["item"] = item, ["pr"] = r["pr"]?.DeepClone() }.ToJsonString());
                }
                else if (result == "refused")
                {
                    if (refusedIds.Add(id)) refused++;
                    refusedAt[id] = DateTime.UtcNow; permissionMissing = true;
                    var ritem = r!["item"]?.GetValue<int>() ?? 0;
                    if (ritem > 0 && journaledRefusals.Add(id)) toJournal.Add((ritem, new JsonObject { ["run"] = id, ["pr"] = r["pr"]?.GetValue<int>() ?? 0, ["status"] = r["status"]?.ToString() ?? "403", ["refused"] = true }, id));
                    lastError = $"run {id}: 403 {r!["message"]}".Trim();
                    Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = "copilot-ci-release", ["result"] = "refused", ["run"] = id, ["status"] = r["status"]?.DeepClone(), ["message"] = r["message"]?.DeepClone() }.ToJsonString());
                }
                else if (result == "failed") lastError = $"run {id}: {r!["status"]} {r["message"]}".Trim();
            }
        }
        foreach (var (item, f, key) in toJournal) { try { await journal(item, f, key); } catch (Exception e) { Console.Error.WriteLine($"ci-release journal #{item}: {e.GetType().Name}"); } }
    }

    public JsonObject Status(bool enabled, double seconds)
    {
        lock (gate)
        {
            var o = new JsonObject { ["enabled"] = enabled, ["intervalSeconds"] = seconds, ["released"] = released, ["refused"] = refused, ["lastError"] = lastError, ["passes"] = passes, ["failedPasses"] = failedPasses,
                ["pending"] = pending, ["lastPass"] = lastPass, ["lastRelease"] = lastRelease, ["permissionMissing"] = permissionMissing, ["appActions"] = appActions };
            if (permissionMissing) o["ownerAction"] = OwnerAction;
            return o;
        }
    }
}
