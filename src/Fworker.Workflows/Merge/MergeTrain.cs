using Fworker.Contracts.Ids;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Merge;

// W7.2: the rules of the merge train, a pure function of the saved state and one input (MergeTrainService saves what it changed and publishes what it answered). Two strategies:
//  - direct (the default, today's behaviour): every request is let through at once. The line only holds the requests whose answer is outstanding, so a replay of a request is answered the same way.
//  - train: one item at a time. The item first in line gets the turn, the others are answered "blocked: merge-train" and ask again (the saga's own retry). A head that waits for something about its own pull request
//    (checks of the updated head, GitHub computing mergeability, an asynchronous merge) keeps the first place; a head blocked for a reason of its own (no Auto Merge label, a hold, a draft, a fault of the call) goes to the
//    back so it cannot hold the others; a head that holds the first place longer than the patience goes to the back too. An item that stops asking (parked, merged by a person) loses its place after the expiry.
// What the train never does: turn a transient answer into a lasting one. A blocked answer, and a failure the port calls transient, leave the item in the line (or send it to the back of it); only a merge, a head
// that moved and a refusal GitHub gave for good take it out. The merge queue strategy is not here (deferred).

public static class MergeTrain
{
    /// <summary>The reason of the answer an item gets while it is not first in line: a wait, never a failure.</summary>
    public const string WaitReason = "merge-train";

    // the waits of Fworker.Integrations.GitHub.MergeDirect that are about this pull request's own head and master: waiting for them is what the first place is for
    static readonly string[] HoldsTheLine = ["checks-pending", "mergeability-unknown", "branch-updated", "merge-async", "merge-queue"];

    /// <summary>Whether a blocked answer with this reason leaves the item first in line (the others wait for it) or sends it to the back.</summary>
    public static bool KeepsTheLine(string reason) => HoldsTheLine.Contains(reason, StringComparer.Ordinal);

    /// <summary>The business key of the saga's answer to one attempt (the saga applies a key once, so a repeated answer does nothing).</summary>
    public static string AnswerKey(MergeRequested request) => $"merge-attempt|{request.ArmId}|{request.Attempt}";

    public static MergeAttempted Attempted(MergeRequested request, MergeResult result) =>
        new(request.Item, request.Generation, request.ArmId, AnswerKey(request), request.Attempt, result);

    /// <summary>Applies the input to the state and returns what to publish: MergeGranted, MergeAttempted and MergeTrainDecided.</summary>
    public static IReadOnlyList<object> Apply(MergeTrainState s, MergeTrainInput input, MergeTrainSettings cfg, DateTimeOffset now)
    {
        var o = new List<object>();
        switch (input)
        {
            case ReadyToMerge r: Ready(s, r.Request, cfg, now, o); break;
            case MergeTrainAnswered a: Answered(s, a.Request, a.Result, cfg, now, o); break;
            case MergeTrainLeft l: Left(s, l, now, o); break;
        }
        return o;
    }

    static void Ready(MergeTrainState s, MergeRequested req, MergeTrainSettings cfg, DateTimeOffset now, List<object> o)
    {
        var item = req.Item.ToString();
        var train = cfg.Strategy == MergeStrategy.Train;
        var e = s.Line.Find(x => x.Item == item);
        // a new arm (the item was validated again) is a new request: the last place of the line
        if (e is not null && (e.Generation != req.Generation || e.ArmId != req.ArmId)) { s.Line.Remove(e); e = null; }
        if (e is not null && req.Attempt < e.Attempt) return;   // a request of an earlier attempt, delivered late: the later one has been seen
        var repeat = e is not null && e.Attempt == req.Attempt;
        if (e is null)
        {
            e = new MergeTrainEntry { Item = item, Order = s.NextOrder++, EnqueuedAt = now };
            s.Line.Add(e);
            if (train) Decide(s, o, MergeTrainActions.Enqueued, item, req.Generation, now, $"PR #{req.PullRequest} at {Short(req.Head)}");
        }
        e.Generation = req.Generation; e.ArmId = req.ArmId; e.PullRequest = req.PullRequest; e.Head = req.Head; e.UpdatesSoFar = req.UpdatesSoFar; e.UpdatedFrom = req.UpdatedFrom;
        if (!repeat) { e.Attempt = req.Attempt; e.Granted = false; }   // (a later attempt means the answer of the earlier one was received)
        e.LastSeen = now;
        Expire(s, cfg, now, o, except: item);

        // the first place is not for ever (patience): the head that holds it with others waiting goes to the back
        if (train && ReferenceEquals(s.First, e) && e.HeadSince is { } since && now - since >= cfg.Patience && s.Line.Count > 1)
        {
            Rotate(s, e);
            Decide(s, o, MergeTrainActions.Rotated, item, e.Generation, now, $"held the first place for {(int)(now - since).TotalMinutes} minutes without a merge");
        }

        var first = s.First!;
        if (train && !ReferenceEquals(first, e))
        {
            var ahead = s.Line.Count(x => x.Order < e.Order);
            o.Add(Attempted(req, new MergeResult(MergeResultKind.Blocked, WaitReason, $"{ahead} ahead in the merge train of {s.Key}; {ShortItem(first.Item)} is first", Transient: true, RetryAfter: cfg.WaitRetry)));
            return;
        }
        if (!(repeat && e.Granted))
        {
            e.Granted = true; e.Turns++; e.HeadSince ??= train ? now : null;
            if (train) Decide(s, o, MergeTrainActions.Granted, item, e.Generation, now, $"turn {e.Turns}");
        }
        // direct: the update of a branch that is behind is asked once per verdict, on the first attempt; train: on the first turn the item gets
        o.Add(new MergeGranted(s.Key, req, train ? e.Turns == 1 : req.Attempt == 1));
    }

    static void Answered(MergeTrainState s, MergeRequested req, MergeResult result, MergeTrainSettings cfg, DateTimeOffset now, List<object> o)
    {
        var item = req.Item.ToString();
        var train = cfg.Strategy == MergeStrategy.Train;
        o.Add(Attempted(req, result));   // always: the saga applies a repeated key once, and an answer that came late is still the saga's to know
        var e = s.Line.Find(x => x.Item == item);
        var current = e is not null && e.Generation == req.Generation && e.ArmId == req.ArmId && e.Attempt == req.Attempt;
        switch (result.Kind)
        {
            case MergeResultKind.Merged:
                // a merge is a fact about the item, whichever request the entry holds
                if (Remember(s, item, result.Commit, now)) Decide(s, o, MergeTrainActions.Merged, item, req.Generation, now, result.Commit.Length > 0 ? $"commit {Short(result.Commit)}" : result.Reason);
                s.Line.RemoveAll(x => x.Item == item);
                break;
            case MergeResultKind.HeadMoved:
                if (current) { s.Line.Remove(e!); Decide(s, o, MergeTrainActions.Dequeued, item, req.Generation, now, $"head moved: {result.Detail}"); }
                break;
            case MergeResultKind.Failed when !result.Transient:
                if (current) { s.Line.Remove(e!); Decide(s, o, MergeTrainActions.Dequeued, item, req.Generation, now, $"{result.Reason}: {result.Detail}"); }
                break;
            default:
                // blocked, or a failure the port calls transient: nothing is decided for good; the turn is over and the saga asks again
                if (!current) break;
                e!.Granted = false;
                if (!train) s.Line.Remove(e);
                else if (!KeepsTheLine(result.Reason)) { Rotate(s, e); Decide(s, o, MergeTrainActions.Rotated, item, req.Generation, now, result.Reason); }
                break;
        }
        Expire(s, cfg, now, o, except: null);
    }

    static void Left(MergeTrainState s, MergeTrainLeft l, DateTimeOffset now, List<object> o)
    {
        var item = l.Item.ToString();
        var e = s.Line.Find(x => x.Item == item);
        // W9.10n: a leave of an earlier instance of the item (re-adopted or reopened since) does not take the new instance's place in the line (0: any, as for a merge seen elsewhere)
        if (e is not null && l.Generation != 0 && e.Generation != 0 && e.Generation != l.Generation) return;
        if (e is not null) { s.Line.Remove(e); Decide(s, o, MergeTrainActions.Dequeued, item, e.Generation, now, l.Reason); }
    }

    static void Rotate(MergeTrainState s, MergeTrainEntry e) { e.Order = s.NextOrder++; e.Granted = false; e.HeadSince = null; }

    static void Expire(MergeTrainState s, MergeTrainSettings cfg, DateTimeOffset now, List<object> o, string? except)
    {
        foreach (var e in s.Line.Where(x => x.Item != except && now - x.LastSeen > cfg.Expiry).ToList())
        {
            s.Line.Remove(e);
            Decide(s, o, MergeTrainActions.Dequeued, e.Item, e.Generation, now, $"no merge request for {(int)(now - e.LastSeen).TotalMinutes} minutes");
        }
    }

    /// <summary>Keeps the merge for a while; false when it was already kept (the answer arrived twice).</summary>
    static bool Remember(MergeTrainState s, string item, string commit, DateTimeOffset now)
    {
        if (s.Merged.Any(m => m.Item == item && string.Equals(m.Commit, commit, StringComparison.OrdinalIgnoreCase))) return false;
        s.Merged.Add(new MergeTrainMerged { Item = item, Commit = commit.ToLowerInvariant(), At = now });
        if (s.Merged.Count > MergeTrainState.KeepMerged) s.Merged.RemoveRange(0, s.Merged.Count - MergeTrainState.KeepMerged);
        return true;
    }

    static void Decide(MergeTrainState s, List<object> o, string action, string item, int generation, DateTimeOffset now, params string[] reasons)
    {
        s.Sequence++;
        o.Add(new MergeTrainDecided(s.Key, s.Sequence, action, ItemId.Parse(item), generation, reasons, now));
    }

    static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
    static string ShortItem(string item) => item[(item.LastIndexOf('#'))..];
}
