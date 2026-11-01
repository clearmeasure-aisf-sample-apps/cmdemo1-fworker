using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Commands;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.GitHub;

/// <summary>
/// W6.2: the direct merge of Verb_merge (plugin/scripts/lib/Verbs.Merge.ps1) under the gateway, one step per call. The steps, in the script's order:
/// the pull request is read (merged already: done; not open: refused; another head than the PASS head: head moved); the merge queue limit; the mergeability wait
/// (<c>mergeable</c> null is read again at the fixed poll interval for the short wait of the gateway form, 12 seconds at most); a branch that is behind is brought up to date by GitHub's update-branch when the
/// caller asks; then <c>PUT pulls/{n}/merge</c> with <c>merge_method squash</c> and <c>sha</c> the PASS head. A refusal that says out of date or behind is answered with the update of the branch
/// (counted, bounded); the head GitHub's update made is verified (two parents, the first the PASS head, GitHub's own signature, the second reachable from the base) before
/// it is offered as the new head; any other head is "head moved". A required check that has not passed is "blocked: checks-pending", a pull request GitHub says it cannot merge is "failed: not-mergeable".
/// It never sleeps without the injected <c>delay</c>, so a test runs in no time; the waits add up in the pauses it asks for, not by the clock.
/// </summary>
public sealed class MergeDirect
{
    static readonly Regex OutOfDate = new("out of date|out-of-date|Head branch was modified|behind", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex StatusCheck = new("status check", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Draft = new("\\bdraft\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex StackMerge = new("stack|asynchronous", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    readonly GitHubClientFactory _clients;
    readonly MergeTiming _timing;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public MergeDirect(GitHubClientFactory clients, MergeTiming? timing = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _clients = clients; _timing = timing ?? MergeTiming.Default; _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    sealed record Answer(int Status, JsonNode? Json, string Text, HttpOutcome Outcome)
    {
        public bool Ok => Status is >= 200 and < 300;
        public string Message => Json?["message"]?.ToString() ?? "";
    }

    public async Task<MergeOutcome> Run(MergePullRequest cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.PassHeadSha)) return MergeOutcome.Failed("verdict-head-unknown", "the PASS does not name the head it was given for; it is not merged");
        var client = await _clients.For(cmd.Repository, ct);
        var n = cmd.PullRequest;

        var read = await Get(client, $"pulls/{n}", ct);
        if (read.Status == 404) return MergeOutcome.Failed("pr-not-found", $"PR #{n} does not exist");
        if (!read.Ok) return Unexpected(read, $"read of PR #{n}");
        var pr = read.Json!;

        // the pull request must be the item's (Test-PrBelongs of the script); a caller that names no item has checked it itself
        if (cmd.Item > 0 && !Belongs(pr, cmd)) return MergeOutcome.Failed("pr-mismatch", $"PR #{n} (branch '{pr["head"]?["ref"]}') is not a pull request belonging to work item #{cmd.Item}; refusing to merge");

        // merged before (an earlier call, the sweep, a person): nothing to do, whatever the queue holds
        if (pr["merged"]?.GetValue<bool>() == true) return MergeOutcome.MergedAs(Lower(pr["merge_commit_sha"]?.ToString()), "already-merged");
        if (!string.Equals(pr["state"]?.ToString(), "open", StringComparison.OrdinalIgnoreCase)) return MergeOutcome.Failed("pr-not-open", $"PR #{n} is {pr["state"]}, not open; refusing to merge");

        // under mergePolicy auto the PASS covers the head it was given for and no other
        if (!SameSha(Head(pr), cmd.PassHeadSha)) return await HeadDiffers(client, cmd, pr, ct);

        // GitHub merges no draft; making it ready is the caller's (a command of its own), and a draft is a wait, not a refusal the ledger would keep
        if (pr["draft"]?.GetValue<bool>() == true) return DraftBlock(n);

        // the Release workflow keeps only one queued run: merge only while fewer than N other merged items still wait for master CI or their release candidate; a documentation-only change never waits
        if (cmd.MergeQueueDepth > 0 && !cmd.DocsOnly && cmd.QueueBusy >= cmd.MergeQueueDepth)
            return MergeOutcome.Blocked("merge-queue", $"the merge queue is full ({cmd.QueueBusy} of {cmd.MergeQueueDepth}): an earlier merge still waits for its release candidate");

        // mergeable is null while GitHub computes it: read again at the fixed interval of the script, for the short wait it has under the gateway (never a long one: one step per call)
        var waited = TimeSpan.Zero;
        while (pr["mergeable"] is null && waited < _timing.MergeabilityWait)
        {
            var d = _timing.MergeabilityPoll < _timing.MergeabilityWait - waited ? _timing.MergeabilityPoll : _timing.MergeabilityWait - waited;
            await _delay(d, ct); waited += d;
            read = await Get(client, $"pulls/{n}", ct);
            if (!read.Ok) return Unexpected(read, $"read of PR #{n}");
            pr = read.Json!;
            if (pr["merged"]?.GetValue<bool>() == true) return MergeOutcome.MergedAs(Lower(pr["merge_commit_sha"]?.ToString()), "already-merged");
            if (!SameSha(Head(pr), cmd.PassHeadSha)) return await HeadDiffers(client, cmd, pr, ct);
        }
        if (pr["mergeable"] is null) return MergeOutcome.Blocked("mergeability-unknown", "GitHub has not yet said whether it can be merged");
        if (pr["mergeable"]!.GetValue<bool>() == false) return MergeOutcome.Failed("not-mergeable", $"GitHub reports PR #{n} is NOT mergeable (mergeable_state '{pr["mergeable_state"]}')");

        // the branch is behind its base: GitHub's update branch makes the one head a PASS accepts without a new verdict; the checks of the new head run before the next merge
        if (cmd.UpdateWhenBehind && await BehindBy(client, pr, ct) > 0)
        {
            var update = await UpdateBranch(client, n, cmd.PassHeadSha, ct);
            if (update.Stop is not null) return update.Stop;   // rate limited: asked again later (a 5xx or a dropped connection threw)
            // this update does not count against the limit of updates after refusals, as in the script
            if (update.Accepted) return (await AfterUpdate(client, cmd, update, ct)) with { Updated = true };
            // GitHub could not update it (the head moved, a conflict it saw only now): merged as it is, as before; a conflict is refused by the merge itself
        }

        return await Merge(client, cmd, ct);
    }

    /// <summary>
    /// W9.10c: whether an earlier handling of the same command, cut off before its result was recorded, did the merge: the pull request's merge state. Merged is Found (the result that handling would
    /// have recorded); open is Absent (the merge did not happen, so the call may be made); a read that fails is Unknown (the caller asks again).
    /// </summary>
    public async Task<EffectLookup> LookUpMerge(MergePullRequest cmd, CancellationToken ct = default)
    {
        var client = await _clients.For(cmd.Repository, ct);
        var read = await Get(client, $"pulls/{cmd.PullRequest}", ct);
        if (!read.Ok) return EffectLookup.Unknown($"read of PR #{cmd.PullRequest}: {read.Status}");
        if (read.Json!["merged"]?.GetValue<bool>() == true) return EffectLookup.Found(MergeOutcome.MergedAs(Lower(read.Json["merge_commit_sha"]?.ToString()), "already-merged").ToResult());
        return EffectLookup.Absent;
    }

    async Task<MergeOutcome> Merge(GitHubRepoClient client, MergePullRequest cmd, CancellationToken ct)
    {
        var n = cmd.PullRequest;
        var body = new JsonObject { ["merge_method"] = "squash", ["sha"] = cmd.PassHeadSha };
        var r = await Send(client, HttpMethod.Put, $"pulls/{n}/merge", body, ct);

        // a pull request that is (or was) part of a stack merges only through the asynchronous endpoint
        if (r.Status == 403 && !RateLimited(r) && StackMerge.IsMatch(r.Message))
        {
            var asyncBody = new JsonObject { ["merge_method"] = "squash", ["sha"] = cmd.PassHeadSha, ["merge_action"] = "direct_merge" };
            var ar = await Send(client, HttpMethod.Put, $"pulls/{n}/merge-async", asyncBody, ct);
            // 409: a merge request of it is already pending (an earlier call): still merging, not a refusal
            if (ar.Status == 409) return MergeOutcome.Blocked("merge-async", $"PR #{n} is being merged by GitHub in the background");
            if (!ar.Ok) r = ar;
            else
            {
                var waited = TimeSpan.Zero;
                while (waited < _timing.AsyncMergeWait)
                {
                    var look = await Get(client, $"pulls/{n}", ct);
                    if (look.Ok && look.Json!["merged"]?.GetValue<bool>() == true) return MergeOutcome.MergedAs(Lower(look.Json["merge_commit_sha"]?.ToString()));
                    await _delay(_timing.AsyncMergePoll, ct); waited += _timing.AsyncMergePoll;
                }
                return MergeOutcome.Blocked("merge-async", $"PR #{n} is being merged by GitHub in the background; the merge is picked up when it lands");
            }
        }

        if (r.Ok)
        {
            var sha = Lower(r.Json?["sha"]?.ToString());
            return r.Json?["merged"]?.GetValue<bool>() == true && sha != "" ? MergeOutcome.MergedAs(sha) : MergeOutcome.Failed("merge-no-sha", $"merge of PR #{n} returned no merge SHA");
        }
        // a 403 is a refusal of the credential only when it is not a rate limit (primary, secondary, Retry-After): those are rescheduled by the classifier like any other answer
        if (r.Status == 403)
            return RateLimited(r) ? Unexpected(r, $"merge of PR #{n}") : MergeOutcome.Failed("github-forbidden", $"GitHub refused the merge of PR #{n} (403): {r.Message}");
        if (r.Status is 405 or 409 or 422)
        {
            var msg = r.Message;
            if (OutOfDate.IsMatch(msg)) return await OutOfDateAfterRefusal(client, cmd, msg, ct);
            if (r.Status == 405 && StatusCheck.IsMatch(msg)) return MergeOutcome.Blocked("checks-pending", msg);
            if (Draft.IsMatch(msg)) return DraftBlock(n);
            // Transient on purpose: such a refusal can clear without a new head (approval added, base rules changed, a person's change of the pull request), and the ledger keeps a
            // permanent outcome by its key (pull request, PASS head, updates), which would freeze it. The caller bounds the asking by the verdict timeout, as the script parks a PASS that stays unmerged.
            return MergeOutcome.Failed("merge-refused", $"HTTP {r.Status}: {msg}", FailureKind.Transient);
        }
        return Unexpected(r, $"merge of PR #{n}");
    }

    // The merge was refused for the PASS head. If the branch now carries any other head, nothing validated it; if it still carries the PASS head, GitHub's update brings it up to date.
    async Task<MergeOutcome> OutOfDateAfterRefusal(GitHubRepoClient client, MergePullRequest cmd, string message, CancellationToken ct)
    {
        var n = cmd.PullRequest;
        var look = await Get(client, $"pulls/{n}", ct);
        if (!look.Ok) return Unexpected(look, $"read of PR #{n}");
        if (look.Json!["merged"]?.GetValue<bool>() == true) return MergeOutcome.MergedAs(Lower(look.Json["merge_commit_sha"]?.ToString()), "already-merged");
        if (!SameSha(Head(look.Json), cmd.PassHeadSha)) return await HeadDiffers(client, cmd, look.Json, ct);
        // bounded: the updates of one verdict are counted by the caller, so the calls add up to the same limit as the script's loop
        if (cmd.UpdatesSoFar >= cmd.MaxUpdates) return MergeOutcome.Failed("merge-out-of-date", $"PR #{n} kept falling behind its base after {cmd.MaxUpdates} updates ({message})");
        var update = await UpdateBranch(client, n, cmd.PassHeadSha, ct);
        if (update.Stop is not null) return update.Stop;   // rate limited: nothing was updated, so nothing is counted
        // the script counts the update of a verdict before it asks GitHub, accepted or not: the caller adds one to the count of the verdict either way
        if (!update.Accepted) return MergeOutcome.Failed("update-refused", $"could not update PR #{n}: {update.Message}") with { UpdatesAdded = 1 };
        return (await AfterUpdate(client, cmd, update, ct)) with { Updated = true, UpdatesAdded = 1 };
    }

    sealed record Update(bool Accepted, string From, string Expected, string Message, MergeOutcome? Stop = null);

    // A transport failure or a 5xx throws (the handler classifies it as transient: asked again, never cached as a refusal). Only GitHub's own refusals are one: 409 (the head moved) and 422
    // (a conflict, or up to date already); a rate limit stops the call to be asked again later; any other answer is a refusal the caller reads in the message.
    async Task<Update> UpdateBranch(GitHubRepoClient client, int n, string from, CancellationToken ct)
    {
        var u = await Send(client, HttpMethod.Put, $"pulls/{n}/update-branch", new JsonObject { ["expected_head_sha"] = from }, ct);
        // GitHub answers 202 (accepted; the merge commit is made in the background), 409 (the head moved) or 422 (a conflict, or up to date already)
        if (u.Status == 202) return new(true, from, Lower(u.Json?["sha"]?.ToString()), "");
        if (u.Status is not (409 or 422))
        {
            var other = Unexpected(u, $"update of the branch of PR #{n}");   // throws for a transient one
            if (other.Kind == FailureKind.RateLimited) return new(false, from, "", other.Detail, other);
        }
        return new(false, from, "", u.Message != "" ? u.Message : $"HTTP {u.Status}");
    }

    // The update was accepted: GitHub makes the merge commit in the background. Wait a short while for the head to change; the head it made is the one head a PASS accepts without a new
    // verdict, once verified. The checks of the new head decide the next merge, so this call ends blocked either way.
    async Task<MergeOutcome> AfterUpdate(GitHubRepoClient client, MergePullRequest cmd, Update update, CancellationToken ct)
    {
        var n = cmd.PullRequest;
        JsonNode? pr = null; var waited = TimeSpan.Zero;
        while (true)
        {
            var look = await Get(client, $"pulls/{n}", ct);
            if (!look.Ok) return Unexpected(look, $"read of PR #{n}");
            pr = look.Json!;
            if (!SameSha(Head(pr), update.From) || waited >= _timing.UpdatedHeadWait) break;
            await _delay(_timing.UpdatedHeadPoll, ct); waited += _timing.UpdatedHeadPoll;
        }
        var head = Head(pr);
        // GitHub has not made it yet: the next call finds the head (the caller passes UpdatedFrom)
        if (SameSha(head, update.From)) return MergeOutcome.Blocked("branch-updated", $"PR #{n}: the update of the branch was accepted; its new head is not there yet");
        if (!await VerifyUpdateHead(client, update.From, head, update.Expected, pr["base"]?["ref"]?.ToString() ?? "", ct))
            return MergeOutcome.HeadMoved($"the branch of PR #{n} changed after the validation verdict to a commit that is not the update with the base branch (verdict for {Short(update.From)}, head now {Short(head)})");
        return MergeOutcome.Blocked("branch-updated", $"PR #{n}: the branch was brought up to date; the checks of the new head run now", head);
    }

    // The branch carries another head than the PASS head: only GitHub's own update of the PASS head is accepted (it is offered as the new head), anything else is head moved.
    async Task<MergeOutcome> HeadDiffers(GitHubRepoClient client, MergePullRequest cmd, JsonNode pr, CancellationToken ct)
    {
        var head = Head(pr);
        if (SameSha(cmd.UpdatedFrom, cmd.PassHeadSha) && await VerifyUpdateHead(client, cmd.PassHeadSha, head, cmd.UpdatedTo, pr["base"]?["ref"]?.ToString() ?? "", ct))
            return MergeOutcome.Blocked("branch-updated", $"PR #{cmd.PullRequest}: the branch was brought up to date; the checks of the new head run now", head);
        return MergeOutcome.HeadMoved($"the branch of PR #{cmd.PullRequest} changed after the validation verdict (verdict for {Short(cmd.PassHeadSha)}, head now {Short(head)}); the PASS verdict no longer applies");
    }

    // Is head the merge commit GitHub's update branch made from verdictSha? Exactly two parents, the first the verdict head, the second the base branch head or an ancestor of it
    // (compare says identical or behind), and GitHub's own signature (verified, reason valid, committed by the web-flow identity); when update-branch named the commit, exactly that one.
    async Task<bool> VerifyUpdateHead(GitHubRepoClient client, string verdictSha, string head, string expected, string baseRef, CancellationToken ct)
    {
        if (expected != "" && !SameSha(expected, head)) return false;
        var cm = await Get(client, $"commits/{head}", ct);
        if (!cm.Ok) return false;
        var v = cm.Json!["commit"]?["verification"]; var who = cm.Json["commit"]?["committer"];
        if (v is null || v["verified"]?.GetValue<bool>() != true || v["reason"]?.ToString() != "valid" || who?["name"]?.ToString() != "GitHub" || who["email"]?.ToString() != "noreply@github.com") return false;
        var parents = (cm.Json["parents"] as JsonArray ?? []).Select(p => p?["sha"]?.ToString() ?? "").ToList();
        if (parents.Count != 2 || !SameSha(parents[0], verdictSha) || baseRef == "") return false;
        var cmp = await Get(client, $"compare/{Uri.EscapeDataString(baseRef)}...{parents[1]}", ct);
        return cmp.Ok && cmp.Json!["status"]?.ToString() is "identical" or "behind";
    }

    // how many commits the base has that the head lacks; -1 when it cannot be told (the branch is then merged as it is)
    async Task<int> BehindBy(GitHubRepoClient client, JsonNode pr, CancellationToken ct)
    {
        var baseRef = pr["base"]?["ref"]?.ToString(); if (string.IsNullOrEmpty(baseRef)) return -1;
        var cmp = await Get(client, $"compare/{Uri.EscapeDataString(baseRef)}...{Head(pr)}?per_page=1", ct);
        return cmp.Ok && cmp.Json!["behind_by"] is { } b ? b.GetValue<int>() : -1;
    }

    // An answer this command has no rule for: transient ones throw, so the handler classifies them as the base does; a rate limit is rescheduled; the rest is a refusal.
    static MergeOutcome Unexpected(Answer a, string what)
    {
        var c = ErrorClassifier.Classify(a.Outcome, idempotentCommand: false);
        return c.Action switch
        {
            ErrorAction.Retry => throw new HttpRequestException($"{what}: {c.Reason}"),
            ErrorAction.Reschedule => MergeOutcome.Failed("rate-limited", $"{what}: {c.Reason}", FailureKind.RateLimited, c.RetryAfter),
            // a 403 that is no rate limit is the credential's or the permission's fault on a read too (the outage's first call was the GET of the pull request), and the ledger must not keep it
            _ when a.Status == 403 => MergeOutcome.Failed("github-forbidden", $"GitHub refused {what} (403): {a.Message}"),
            _ => MergeOutcome.Failed("merge-refused", $"{what}: HTTP {a.Status} {a.Message}".TrimEnd()),
        };
    }

    static Task<Answer> Get(GitHubRepoClient client, string relative, CancellationToken ct) => Send(client, HttpMethod.Get, relative, null, ct);

    static async Task<Answer> Send(GitHubRepoClient client, HttpMethod method, string relative, JsonObject? body, CancellationToken ct)
    {
        using var content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await client.Send(method, relative, content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        if (text.Length > 0) { try { json = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { } }
        return new Answer((int)resp.StatusCode, json, text, GitHubResponses.ToOutcome(resp, text));
    }

    // a 403 that is a rate limit (Retry-After, an exhausted primary limit, a secondary limit message): the classifier reschedules it, so it is no refusal
    static bool RateLimited(Answer a) => ErrorClassifier.Classify(a.Outcome, idempotentCommand: false).Action == ErrorAction.Reschedule;

    static MergeOutcome DraftBlock(int n) => MergeOutcome.Blocked("pr-draft", $"PR #{n} is still a draft; GitHub merges no draft until it is marked ready for review");

    // Test-PrBelongs of the script: the branch is the item's (".../<item>-..."), or a copilot/* branch of the pull request the item state records, or the title names the item (#12 is not #123)
    static bool Belongs(JsonNode pr, MergePullRequest cmd)
    {
        var branch = pr["head"]?["ref"]?.ToString() ?? "";
        if (branch.Contains($"/{cmd.Item}-", StringComparison.Ordinal)) return true;
        if (branch.StartsWith("copilot/", StringComparison.Ordinal) && cmd.RecordedPullRequest == cmd.PullRequest) return true;
        return Regex.IsMatch(pr["title"]?.ToString() ?? "", $"(^|[^0-9])#{cmd.Item}([^0-9]|$)", RegexOptions.CultureInvariant);
    }

    static string Head(JsonNode pr) => pr["head"]?["sha"]?.ToString() ?? "";
    static bool SameSha(string? a, string? b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    static string Lower(string? s) => (s ?? "").ToLowerInvariant();
    static string Short(string sha) => sha.Length <= 7 ? sha : sha[..7];
}
