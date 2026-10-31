using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.GitHub;

// W9.10i: what v1's promotion reads of a deployment the gateway made, written where the delivery train acts. Verbs.Promote.ps1 keeps, on every item of a release, `merge.promoted.<environment>` = { task, deployment, at, release }
// (the tasks of the deployments "the gateway created" for a release: a Sign-off at one of them is the gateway's to answer, any other it only follows) and, on the first item of the release, `signoff_wait_since` inside that record
// (since when a Sign-off waits, so that the wait limit survives a restart). The train's executor (PromoteRelease, AnswerSignOff) wrote neither, so after FACTORY_V2_OFF v1 would take the Sign-off of a deployment the train
// made for somebody else's and leave it unanswered until the wait limit. The writes are idempotent (nothing is saved when the record is there already), so the retry of a command that was recorded but not acknowledged is harmless.

/// <summary>The two records, as pure edits of a state comment's state.</summary>
public static class PromotionRecord
{
    /// <summary>
    /// <c>merge.promoted.&lt;environment&gt;</c> for the deployment of <paramref name="release"/> as task <paramref name="task"/>. Only an item with a merge record has it (as v1: its journal hook edits `merge` when there is one).
    /// False when nothing changed: the record names this task and release already.
    /// </summary>
    public static bool SetPromoted(JsonObject state, string environment, string task, string release, DateTime at)
    {
        if (state["merge"] is not JsonObject merge) return false;
        if (merge["promoted"] is not JsonObject promoted) merge["promoted"] = promoted = new JsonObject();
        if (promoted[environment] is JsonObject current && current["task"]?.ToString() == task && current["release"]?.ToString() == release) return false;
        promoted[environment] = new JsonObject { ["task"] = task, ["at"] = JournalJson.Iso(at), ["release"] = release };
        return true;
    }

    /// <summary>
    /// <c>merge.promoted.&lt;environment&gt;.signoff_wait_since</c>, kept only for the task it was recorded with (v1 ignores a value whose record names another task) and set once. False when the item has no record of that task
    /// or the wait is recorded already.
    /// </summary>
    public static bool SetSignOffWait(JsonObject state, string environment, string task, DateTime since)
    {
        if (state["merge"]?["promoted"]?[environment] is not JsonObject record || record["task"]?.ToString() != task) return false;
        if (JournalJson.Has(record, "signoff_wait_since")) return false;
        record["signoff_wait_since"] = JournalJson.Iso(since);
        return true;
    }
}

/// <summary>Writes the records to the state comments of the home repository's items, through the same projection the saga's decisions use (one comment, one revision counter, retried on a conflict).</summary>
public sealed class PromotionStateWriter(StateCommentProjection comments, RepoId home, Action<string>? log = null)
{
    readonly Action<string> log = log ?? (m => Console.Error.WriteLine(m));

    /// <summary>The deployment <paramref name="task"/> the command made (or found) is recorded on every item the release carries. Items of another repository are not this writer's.</summary>
    public Task Promoted(PromoteRelease command, string task, DateTimeOffset at, CancellationToken ct = default) =>
        Each(command.Items, item => comments.Update(item, s => PromotionRecord.SetPromoted(s, command.Environment, task, command.Version, at.UtcDateTime), ct), $"the deployment {task} of {command.Version}");

    /// <summary>The Sign-off of <paramref name="task"/> waits since <paramref name="since"/>: kept on the first item of the release only, as v1 keeps it.</summary>
    public Task SignOffWaiting(IReadOnlyList<string> items, string environment, string task, DateTimeOffset since, CancellationToken ct = default) =>
        Each(items.Take(1).ToList(), item => comments.Update(item, s => PromotionRecord.SetSignOffWait(s, environment, task, since.UtcDateTime), ct), $"the Sign-off of task {task}");

    // every item is tried; the first failure that may pass on a later try is thrown after the others (the command's retry runs this again, and an item that is written is skipped); a refusal for good (the state does not fit) is logged
    async Task Each(IReadOnlyList<string>? items, Func<ItemId, Task<ProjectionOutcome>> write, string what)
    {
        Exception? failure = null;
        foreach (var text in items ?? [])
        {
            if (!ItemId.TryParse(text, out var item) || item.Repository != home) continue;
            try { await write(item); }
            catch (OperationCanceledException) { throw; }
            catch (JournalRefusedException e) { log($"{what} is not recorded on {item}: {e.Message}"); }
            catch (Exception e) { failure ??= e; }
        }
        if (failure is not null) throw failure;
    }
}
