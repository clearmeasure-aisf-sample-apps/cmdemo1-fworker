using System.Text.Json.Nodes;

// What differs between the owner watches of the two cloud runtimes (task C5c, gateway-composition.md 3 B): the interval (Copilot:
// api.copilot.ciReleaseSeconds, Cursor: api.cursor.ownerWatchSeconds) and the master fixer (Copilot only: the owner task is resumed to fix a red
// master forward). The other differences are wired where the runtimes are made: LoadOwner's runtime test (CopilotOwnerVerbs.Load), Ending's head
// (Copilot's HeadOf, Cursor none) and the credit guard (Copilot's OverBudget, set with the Copilot watch's registration).
sealed class CloudOwnerWatchOptions
{
    public double IntervalSeconds { get; init; } = 120;
    public CopilotCloudRuntime? MasterFixer { get; init; }
}

// The owner watch (gateway-composition.md 3 E, moved out of Program.cs's StartOwnerWatch unchanged): every interval, by the acting gateway, while
// the runtime is on, for each owned item with no session running: what it waits on (its checks, the next phase, the merge, master). Done (merged,
// master green for the merge): the ownership is closed with a comment that links the task. Closed without a merge: closed, unmerged. A red master
// after the merge: the owner task is resumed once per merge commit to fix forward on a new pull request (Copilot only). One status read per owned
// item at most every max(30 s, 3 x the interval): each is a rule-script run, and the runner is shared. It ends at ApplicationStopping, as the
// Task.Run did, or at the host's stopping token, whichever comes first. One per cloud runtime that is on (AddGatewayBackground).
sealed class CloudOwnerWatch : BackgroundService
{
    readonly IItemOwners owners;
    readonly RuntimeManager manager;
    readonly CopilotOwnerVerbs verbs;
    readonly ILeadership leader;
    readonly CancellationToken applicationStopping;
    readonly Dictionary<int, DateTime> ownerChecked = new();
    public CloudOwnerWatchOptions Options { get; }
    public string Kind => owners.Kind;

    public CloudOwnerWatch(IItemOwners owners, RuntimeManager manager, CopilotOwnerVerbs verbs, ILeadership leader, CancellationToken applicationStopping, CloudOwnerWatchOptions options)
    { this.owners = owners; this.manager = manager; this.verbs = verbs; this.leader = leader; this.applicationStopping = applicationStopping; Options = options; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, applicationStopping);
        var ct = stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(Options.IntervalSeconds), ct); } catch (OperationCanceledException) { return; }
            if (!leader.MayAct || !manager.On) continue;
            // an error outside an item's own try ended the Task.Run loop silently; it ends this loop the same way, never the host (a failed
            // BackgroundService stops the host by default)
            try { await Pass(ct); } catch { return; }
        }
    }

    // one pass over the owned items
    public async Task Pass(CancellationToken ct)
    {
        var own = owners.Owners();
        foreach (var key in ownerChecked.Keys.Where(ck => own.All(x => x.Item != ck)).ToList()) ownerChecked.Remove(key);   // items no longer owned
        foreach (var o in own)
        {
            if (owners.Running(o.Item)) continue;
            if (ownerChecked.TryGetValue(o.Item, out var lastCk) && (DateTime.UtcNow - lastCk).TotalSeconds < Math.Max(30, 3 * Options.IntervalSeconds)) continue;
            ownerChecked[o.Item] = DateTime.UtcNow;
            try
            {
                var j = await verbs.Status(o.Item);
                var waiting = j?["waiting"]?.ToString() ?? "";
                owners.Waiting(o.Item, j?["column"]?.ToString() ?? "", waiting);
                if (int.TryParse(j?["pr"]?.ToString(), out var curPr) && curPr > 0) owners.SetPr(o.Item, curPr, j?["branch"]?.ToString() ?? "");
                if (waiting == "done")
                {
                    await verbs.Close(o.Item);
                    await owners.CloseOwner(o.Item);
                    Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = owners.Kind, ["owner"] = "owner-done", ["item"] = o.Item, ["task"] = o.TaskId, ["url"] = o.Url, ["sessions"] = o.Sessions }.ToJsonString());
                }
                else if (waiting == "closed")
                {
                    await verbs.Close(o.Item, unmerged: true);
                    await owners.CloseOwner(o.Item);
                    Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["runtime"] = owners.Kind, ["owner"] = "owner-ended", ["item"] = o.Item, ["task"] = o.TaskId, ["reason"] = "item closed without a merge" }.ToJsonString());
                }
                else if (Options.MasterFixer is { } fixer && waiting == "master-broken" && j?["merge_sha"]?.ToString() is { Length: > 0 } sha && o.MasterFixFor != sha && manager.CanStart().Ok)
                {
                    var prn = o.Pr > 0 ? o.Pr : int.TryParse(j["pr"]?.ToString(), out var spr) ? spr : 0;
                    var failed = string.Join(", ", ((j["failed"] as JsonArray) ?? new JsonArray()).Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)));
                    var text = MasterFixText(o.Item, prn, sha, failed);
                    o.MasterFixFor = sha;
                    if (await fixer.ResumeDirect(o.Item, prn, text, ct)) { manager.CountExternal(); owners.Waiting(o.Item, "", "master-fix"); }
                }
            }
            catch (Exception e) { Console.Error.WriteLine($"{owners.Kind} owner watch #{o.Item}: {e.GetType().Name}"); }
        }
    }

    // the follow-up session's text (Templates/runtime-prompts.txt, section master-fix): the short merge commit, the failed checks when there are any
    public static string MasterFixText(int item, int pr, string sha, string failed)
    {
        var text = RuntimePromptTemplates.Embedded.Render("master-fix", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["item"] = item.ToString(), ["pr"] = pr.ToString(), ["sha"] = sha[..Math.Min(7, sha.Length)], ["failed"] = failed != "" ? $" (failed: {failed})" : "",
        });
        return text.EndsWith('\n') ? text[..^1] : text;   // one line, without the newline that ends it in the file
    }
}
