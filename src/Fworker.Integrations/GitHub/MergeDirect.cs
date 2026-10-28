using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Commands;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.GitHub;

/// <summary>
/// W6.2: the direct merge of Verb_merge (plugin/scripts/lib/Verbs.Merge.ps1) under the gateway, one step per call. The steps, in the script's order:
/// the pull request is read (merged already: done; not open: refused; another head than the PASS head: head moved); the merge queue limit; the mergeability wait
/// (<c>mergeable</c> null is read again with a growing pause, up to timing.mergeabilityWaitSeconds); a branch that is behind is brought up to date by GitHub's update-branch when the
/// caller asks; then <c>PUT pulls/{n}/merge</c> with <c>merge_method squash</c> and <c>sha</c> the PASS head. A refusal that says out of date or behind is answered with the update of the branch
/// (counted, bounded); the head GitHub's update made is verified (two parents, the first the PASS head, GitHub's own signature, the second reachable from the base) before
/// it is offered as the new head; any other head is "head moved". A required check that has not passed is "blocked: checks-pending", a pull request GitHub says it cannot merge is "failed: not-mergeable".
/// It never sleeps without the injected <c>delay</c>, so a test runs in no time; the waits add up in the pauses it asks for, not by the clock.
/// </summary>
public sealed class MergeDirect
{
    static readonly Regex OutOfDate = new("out of date|out-of-date|Head branch was modified|behind", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex StatusCheck = new("status check", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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

        // merged before (an earlier call, the sweep, a person): nothing to do, whatever the queue holds
        if (pr["merged"]?.GetValue<bool>() == true) return MergeOutcome.MergedAs(Lower(pr["merge_commit_sha"]?.ToString()), "already-merged");
        if (!string.Equals(pr["state"]?.ToString(), "open", StringComparison.OrdinalIgnoreCase)) return MergeOutcome.Failed("pr-not-open", $"PR #{n} is {pr["state"]}, not open; refusing to merge");

        // under mergePolicy auto the PASS covers the head it was given for and no other
        if (!SameSha(Head(pr), cmd.PassHeadSha)) return await HeadDiffers(client, cmd, pr, ct);

        // the Release workflow keeps only one queued run: merge only while fewer than N other merged items still wait for master CI or their release candidate; a documentation-only change never waits
        if (cmd.MergeQueueDepth > 0 && !cmd.DocsOnly && cmd.QueueBusy >= cmd.MergeQueueDepth)
            return MergeOutcome.Blocked("merge-queue", $"the merge queue is full ({cmd.QueueBusy} of {cmd.MergeQueueDepth}): an earlier merge still waits for its release candidate");

        // mergeable is null while GitHub computes it: read again with a growing pause
        var waited = TimeSpan.Zero; var pause = _timing.MergeabilityPoll;
        while (pr["mergeable"] is null && waited < _timing.MergeabilityWait)
        {
            var d = pause < _timing.MergeabilityWait - waited ? pause : _timing.MergeabilityWait - waited;
            await _delay(d, ct); waited += d; pause = Min(pause * 2, TimeSpan.FromSeconds(30));
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
            var update = await UpdateBranch(client, cmd, n, cmd.PassHeadSha, ct);
            if (update.Accepted) return await AfterUpdate(client, cmd, update, ct);
            // GitHub could not update it (the head moved, a conflict it saw only now): merged as it is, as before; a conflict is refused by the merge itself
        }

        return await Merge(client, cmd, ct);
    }

    async Task<MergeOutcome> Merge(GitHubRepoClient client, MergePullRequest cmd, CancellationToken ct)
    {
        var n = cmd.PullRequest;
        var body = new JsonObject { ["merge_method"] = "squash", ["sha"] = cmd.PassHeadSha };
        var r = await Send(client, HttpMethod.Put, $"pulls/{n}/merge", body, ct);

        // a pull request that is (or was) part of a stack merges only through the asynchronous endpoint
        if (r.Status == 403 && StackMerge.IsMatch(r.Message))
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
        if (r.Status == 403) return MergeOutcome.Failed("github-forbidden", $"GitHub refused the merge of PR #{n} (403): {r.Message}");
        if (r.Status is 405 or 409 or 422)
        {
            var msg = r.Message;
            if (OutOfDate.IsMatch(msg)) return await OutOfDateAfterRefusal(client, cmd, msg, ct);
            if (r.Status == 405 && StatusCheck.IsMatch(msg)) return MergeOutcome.Blocked("checks-pending", msg);
            return MergeOutcome.Failed("merge-refused", $"HTTP {r.Status}: {msg}");
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
        var update = await UpdateBranch(client, cmd, n, cmd.PassHeadSha, ct);
        if (!update.Accepted) return MergeOutcome.Failed("update-refused", $"could not update PR #{n}: {update.Message}");
        return await AfterUpdate(client, cmd, update, ct);
    }

    sealed record Update(bool Accepted, string From, string Expected, string Message);

    async Task<Update> UpdateBranch(GitHubRepoClient client, MergePullRequest cmd, int n, string from, CancellationToken ct)
    {
        Answer u;
        try { u = await Send(client, HttpMethod.Put, $"pulls/{n}/update-branch", new JsonObject { ["expected_head_sha"] = from }, ct); }
        catch (HttpRequestException e) { return new(false, from, "", e.Message); }
        // GitHub answers 202 (accepted; the merge commit is made in the background), 409 (the head moved) or 422 (a conflict, or up to date already)
        if (u.Status != 202) return new(false, from, "", u.Message != "" ? u.Message : $"HTTP {u.Status}");
        return new(true, from, Lower(u.Json?["sha"]?.ToString()), "");
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
        static string? H(HttpResponseMessage r, string h) => r.Headers.TryGetValues(h, out var v) ? v.FirstOrDefault() : null;
        var reset = long.TryParse(H(resp, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var rs) ? DateTimeOffset.FromUnixTimeSeconds(rs) : (DateTimeOffset?)null;
        int? remaining = int.TryParse(H(resp, "x-ratelimit-remaining"), NumberStyles.None, CultureInfo.InvariantCulture, out var rem) ? rem : null;
        return new Answer((int)resp.StatusCode, json, text, new HttpOutcome((int)resp.StatusCode, text, resp.Headers.RetryAfter?.Delta, reset, remaining));
    }

    static string Head(JsonNode pr) => pr["head"]?["sha"]?.ToString() ?? "";
    static bool SameSha(string? a, string? b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    static string Lower(string? s) => (s ?? "").ToLowerInvariant();
    static string Short(string sha) => sha.Length <= 7 ? sha : sha[..7];
    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
