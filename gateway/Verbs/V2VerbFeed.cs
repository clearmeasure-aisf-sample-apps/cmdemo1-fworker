using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Workflows.WorkItem;

// W6.6 (#335), W7.3 (#340): the decision verbs of an item the WorkItemSaga owns, in the hybrid composition (seam S1, docs/design/fworker-wolverine-refactor.md 4). v1's rule script still does what the verb does (the comment, the
// state comment, the card, the claim) and answers the session; this router then tells the saga what happened, as the inputs of its docs plan (ReviewRecorded, VerdictRecorded, StepCompleted, ...), which the saga publishes decisions,
// timers and the merge call for. So the verb's answer is v1's and unchanged (the golden writes of a verb are the script's), the saga is never ahead of the script, and v1 can take the item back at any time with its own state intact
// (FACTORY_V2_OFF, or the saga handing the item back). What the saga decides on top of the script, it decides alone: the merge, the demotions and parks of D5's rows are the same numbers, so they agree with the script's.
// An event that cannot be handed to the saga (the host is down) is tried again and then dropped with a log line: the saga's lease runs out and its failure budget parks the item, which is the way a lost session ends too.
// Only where the engine acts; an item without a saga costs one look at the ownership.
// W7.3 adds the feature plan: its design carries a `Files:` line (the footprint of D6), the pull request of its development completion brings its changed files, and a development completion of a feature item is asked to
// continue with Functional Testing while CI runs (`ftWithPendingChecks` in the call, which the script reads; delivery-speed.md 5.1), because the saga watches the checks beside the session.

/// <summary>The saga's inputs for a verb call, as pure functions of the call, the script's answer and the saga's state.</summary>
static class DocsVerbs
{
    public static readonly HashSet<string> Handled = ["append-body", "record-review", "complete", "validation-verdict"];

    /// <summary>The calls of a session working on its item that are a sign of life for the step's lease (v1 renews the claim's lease on the owner's calls, Lease.cs), besides the verbs above.</summary>
    public static readonly HashSet<string> Activity = ["comment", "claim-item", "open-pr", "update-pr", "publish-demo", "private-build", "wait-for-checks"];

    /// <summary>The pull request whose head the inputs need, or 0: the head the verdict was given for (when the call does not name it) and the head of the pull request a development completes with.</summary>
    /// <summary>Whether the saga has the item on a plan this feed tells it about: the docs plan (W6.6), or the feature plan (W7.3) where the engine carries its steps (a feature item of an engine that does not is v1's to decide).</summary>
    public static bool Fed(WorkItemState state, DocsPlanSettings? settings) => state.Plan == WorkPlans.Docs || (WorkPlans.IsCode(state.Plan) && settings?.CarriesFeature == true);   // (the hotfix plan of an incident, W8.1, has the feature plan's steps)

    public static int PullRequestToRead(string verb, JsonObject body, JsonObject answer, WorkItemState state, DocsPlanSettings? settings = null)
    {
        if (!state.IsOpen || !Fed(state, settings)) return 0;
        if (verb == "validation-verdict" && state.Step == StepKind.Verify && Bool(answer, "recorded") && Text(body, "head_sha").Length == 0) return Int(body, "pr");
        if (verb == "complete" && state.Step == StepKind.Develop && Bool(answer, "completed")) return Int(answer, "prNumber");
        return 0;
    }

    /// <summary>The inputs, in the order to publish them. <paramref name="head"/> is what <see cref="PullRequestToRead"/> asked for ("" when it was not asked for or could not be read).</summary>
    public static IReadOnlyList<WorkItemInput> Inputs(string verb, JsonObject body, JsonObject answer, WorkItemState state, DocsPlanSettings settings, string head, IReadOnlyList<string>? files = null)
    {
        if (!state.IsOpen || !Fed(state, settings) || state.Parked) return [];
        var feature = WorkPlans.IsCode(state.Plan);
        var item = state.ItemId; var gen = state.Generation; var arm = state.ArmId;
        switch (verb)
        {
            case "append-body":
                // the design section lands in the design column; whether it carries the docs-only marker is what the saga's plan decides on
                var content = Text(body, "content");
                if (state.Step != StepKind.Design || !Bool(answer, "appended") || settings.DesignSection.Length == 0 || !content.Contains(settings.DesignSection, StringComparison.OrdinalIgnoreCase)) return [];
                // the feature plan keeps the design's `Files:` line as the item's footprint (D6)
                return [new DesignRecorded(item, gen, arm, $"design|{arm}|{Hash(content)}", content.Contains(settings.DocsMarker, StringComparison.Ordinal), feature ? Footprint.ParseFilesLine(content) : null)];

            case "record-review":
                var verdict = Text(answer, "verdict");
                if (state.Step is not (StepKind.Design or StepKind.Develop) || verdict is not ("COMPLETE" or "FINDINGS")) return [];
                return [new ReviewRecorded(item, gen, arm, $"review|{arm}|{Int(answer, "round")}|{verdict}", verdict == "COMPLETE")];

            case "complete":
                // the script parked the item (the worker reported a failure)
                if (Bool(answer, "parked")) return [new ParkWorkItem(item, gen, $"park|{arm}|{Text(answer, "reason")}", Text(answer, "reason") is { Length: > 0 } r ? r : "worker-failure")];
                if (!Bool(answer, "completed") || state.Step is not (StepKind.Design or StepKind.Develop)) return [];
                var inputs = new List<WorkItemInput>();
                if (state.Step == StepKind.Develop && Int(answer, "prNumber") is > 0 and var pr)
                    inputs.Add(new PullRequestChanged(item, gen, $"pr|{arm}|{pr}|{head}", pr, head, feature && files is not null ? files : []));
                inputs.Add(new StepCompleted(item, gen, arm, $"done|{arm}"));
                return inputs;

            case "validation-verdict":
                if (state.Step != StepKind.Verify || !Bool(answer, "recorded")) return [];
                var passed = Bool(answer, "passed"); var pull = Int(body, "pr");
                var verdictHead = Text(body, "head_sha") is { Length: > 0 } given ? given : head;
                // a PASS the script parked (no Auto Merge label) is not a PASS the saga merges: it parks too. The operator's unpark resumes both: the admin endpoint and the unpark-item verb each tell the saga (ResumeWorkItem; W9.10h, SagaInputs and ItemAdminFeed)
                if (passed && Text(answer, "next") == "parked") return [new ParkWorkItem(item, gen, $"park|{arm}|pass-without-auto-merge", "pass-without-auto-merge")];
                return [new VerdictRecorded(item, gen, arm, $"verdict|{arm}|{pull}|{verdictHead}|{passed}", passed, pull, verdictHead, Text(body, "summary"))];
        }
        return [];
    }

    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    static string Text(JsonObject o, string name) { try { return o[name]?.ToString() ?? ""; } catch { return ""; } }
    static bool Bool(JsonObject o, string name) { try { return o[name] is JsonValue v && ((v.TryGetValue<bool>(out var b) && b) || (v.TryGetValue<string>(out var s) && string.Equals(s, "true", StringComparison.OrdinalIgnoreCase))); } catch { return false; } }
    static int Int(JsonObject o, string name) { try { return o[name] is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<string>(out var s) && int.TryParse(s, out i))) ? i : 0; } catch { return 0; } }
}

sealed class V2VerbFeed(IVerbRouter inner, V2Ownership ownership, IWorkItemStore store, Fworker.Contracts.Ids.RepoId home, DocsPlanSettings settings, Func<WorkItemInput, Task<bool>> publish,
    PullRequestHeads? heads = null, Func<TimeSpan, Task>? delay = null, Func<DateTimeOffset>? clock = null) : IVerbRouter
{
    /// <summary>The flag of a completion that lets Functional Testing start beside pending checks. The gateway's to give; the script reads it by any spelling of its case (a PowerShell hashtable's keys are case-blind).</summary>
    public const string FlagName = "ftWithPendingChecks";

    /// <summary>
    /// W9.10e: the flag of a verdict for an item whose saga merges it: the script then makes no inline merge (Verb_merge -Gateway in validation-verdict); the answer says `next: merge` and the saga's merge train merges. The gateway's to give, like the one above.
    /// </summary>
    public const string SagaMergesFlagName = "sagaMerges";

    /// <summary>Takes the gateway's flags out of a call body whatever the case of their names; true if there was one.</summary>
    public static bool TakeOutFlag(JsonObject body)
    {
        var found = false;
        foreach (var name in body.Select(p => p.Key).Where(k => string.Equals(k, FlagName, StringComparison.OrdinalIgnoreCase) || string.Equals(k, SagaMergesFlagName, StringComparison.OrdinalIgnoreCase)).ToList()) { body.Remove(name); found = true; }
        return found;
    }

    static readonly TimeSpan[] Pauses = [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)];
    /// <summary>A sign of life reaches the saga at most this often for an item: the lease is minutes long, the calls of a working session are seconds apart.</summary>
    public static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(60);
    readonly Func<TimeSpan, Task> wait = delay ?? Task.Delay;
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTimeOffset> renewed = new();

    public async Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout)
    {
        stdin = await AskForFunctionalTestingAlongside(verb, stdin);
        var answer = await inner.RunAsync(verb, stdin, timeout);
        // the script's answer is the caller's, whatever the feed does after it
        if (answer.Code == 0 && (DocsVerbs.Handled.Contains(verb) || DocsVerbs.Activity.Contains(verb)))
        {
            try { await Feed(verb, stdin, answer.Output); }
            catch (Exception e) { Console.Error.WriteLine($"v2 feed {verb}: {e.GetType().Name}"); }
        }
        return answer;
    }

    /// <summary>W9.4: a verb answered by a v2 route (not by the script under this router) is a sign of life of the session as well: it renews the step's lease as the calls of the script do.</summary>
    public async Task Observe(string verb, string stdin, string output)
    {
        if (DocsVerbs.Activity.Contains(verb)) await Feed(verb, stdin, output);
    }

    /// <summary>The least time between two reads of the saga's table that this feed asks for, because an item it was told of is not in the set it has.</summary>
    public static readonly TimeSpan MissReadEvery = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan MissReadLimit = TimeSpan.FromSeconds(3);
    DateTimeOffset lastMissRead;

    /// <summary>
    /// Whether the saga owns the item. The set the ownership holds is read before each sweep, and a saga can start after that read (the intake starts it on its own clock, the sweep reads the set first and the board after): a session
    /// that was offered the item in that gap, and is fast, would send its design, its review and its completion to a feed that does not know the item, and the saga would never hear them (its completion is refused for want of a design,
    /// and the next step's review is recorded in the step it left). So an item that is not in the set is looked for in the table once, at most four times a second for all items; a v1 item costs that read, bounded, and nothing else.
    /// </summary>
    async Task<bool> Owns(int number)
    {
        if (ownership.Owns(number)) return true;
        var t = now();
        if (t - lastMissRead < MissReadEvery) return false;
        lastMissRead = t;
        // a table that does not answer must not hold up the call of an item that is v1's (the read has a limit of 10 s of its own; this one is shorter), and a failure is the old answer: not owned
        try { await ownership.RefreshAsync().WaitAsync(MissReadLimit); }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or System.Data.Common.DbException) { Console.Error.WriteLine($"v2 feed: the saga table was not read for #{number}: {e.GetType().Name}"); return false; }
        return ownership.Owns(number);
    }

    /// <summary>
    /// A development completion of a feature item the saga owns carries `ftWithPendingChecks` (the flag is the gateway's to give: one a caller sent is taken out of every `complete`): pending checks no longer park the item, the script moves it on to Functional Testing and answers `continue` with the next column and
    /// `checks: pending` (delivery-speed.md 5.1), because the saga watches the checks beside the session. A red head is still refused by the script. Any fault here leaves the call as it came.
    /// W9.10e: a verdict for an item the saga owns carries `sagaMerges`: the script records it as always but makes no inline merge (Verb_merge -Gateway); the saga merges through the merge train, and the sweep leaves the item alone, so the item
    /// is merged once, by one writer. An owned item is the saga's whatever its plan; one that is not owned keeps v1's inline merge.
    /// </summary>
    async Task<string> AskForFunctionalTestingAlongside(string verb, string stdin)
    {
        if (verb is not ("complete" or "validation-verdict")) return stdin;
        try
        {
            if (JsonNode.Parse(stdin) is not JsonObject body) return stdin;
            // the flags are the gateway's to give: one a caller sent is taken out, whatever the item
            var sent = TakeOutFlag(body);
            var give = false; var sagaMerges = false;
            if (int.TryParse(body["item"]?.ToString(), out var number) && number > 0 && await Owns(number))
            {
                if (verb == "validation-verdict") sagaMerges = true;
                else if ((await store.Load(new ItemId(home, number).ToString(), CancellationToken.None))?.State is { } state)
                    give = state.IsOpen && !state.Parked && WorkPlans.IsCode(state.Plan) && state.Step == StepKind.Develop && settings.CarriesFeature;
            }
            if (!give && !sagaMerges && !sent) return stdin;
            if (give) body[FlagName] = true;
            if (sagaMerges) body[SagaMergesFlagName] = true;
            return body.ToJsonString();
        }
        catch (Exception e) { Console.Error.WriteLine($"v2 feed {verb}: {e.GetType().Name}"); return stdin; }
    }

    async Task Feed(string verb, string stdin, string output)
    {
        if (JsonNode.Parse(stdin) is not JsonObject body || JsonNode.Parse(output) is not JsonObject answer) return;
        if (!int.TryParse(body["item"]?.ToString(), out var number) || number <= 0 || !await Owns(number)) return;
        var id = new ItemId(home, number);
        if ((await store.Load(id.ToString(), CancellationToken.None))?.State is not { } state) return;
        // the call is the session's sign of life: the lease of the step is renewed (before the inputs that may end the step, so it carries the arm they end)
        if (state.IsOpen && !state.Parked && state.LeaseDue is not null && Due(number)) await Send(new LeaseRenewed(id, state.Generation, state.ArmId, $"renew|{state.ArmId}|{now().UtcTicks}"));
        if (!DocsVerbs.Handled.Contains(verb)) return;
        var head = "";
        IReadOnlyList<string>? files = null;
        if (heads is not null && DocsVerbs.PullRequestToRead(verb, body, answer, state, settings) is > 0 and var pr)
        {
            head = await heads.HeadOf(home, pr, CancellationToken.None);
            // the feature plan's footprint after the first push is the pull request's files (D6); docs items bring none
            if (WorkPlans.IsCode(state.Plan) && verb == "complete") files = await heads.FilesOf(home, pr, CancellationToken.None);
        }
        foreach (var input in DocsVerbs.Inputs(verb, body, answer, state, settings, head, files)) await Send(input);
    }

    // once a minute per item (the first call counts at once)
    bool Due(int number)
    {
        var t = now();
        var last = renewed.GetValueOrDefault(number);
        if (last != default && t - last < RenewEvery) return false;
        renewed[number] = t;
        return true;
    }

    async Task Send(WorkItemInput input)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { if (await publish(input)) return; }
            catch (Exception e) when (attempt < Pauses.Length) { Console.Error.WriteLine($"v2 feed {input.GetType().Name}: {e.GetType().Name}"); }
            if (attempt >= Pauses.Length) { Console.Error.WriteLine($"v2 feed {input.GetType().Name} for {input.Item} dropped: the Wolverine host did not take it"); return; }
            await wait(Pauses[attempt]);
        }
    }
}
