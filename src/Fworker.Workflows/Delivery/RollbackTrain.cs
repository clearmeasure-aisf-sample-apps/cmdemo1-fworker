using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;

namespace Fworker.Workflows.Delivery;

// W8.3: the rollback, the part of one evaluation of a train (TrainRun) that carries it out. The incident asks (TrainRollback, W8.1) and the train answers it (RollbackAnswered); the train
//   1. refuses unless it is the tdd train (RollbackRules.AutomaticEnvironment): any other environment is escalated to a person, nothing is deployed;
//   2. waits for a deployment that is still running, then takes the last good release (the one named, or the one that ran before the release that runs now);
//   3. checks the migration precondition: DbUp's migrations are forward-only, so going back is a rollback only when no migration was added since the target. It reads the files changed between the
//      target's commit and the commit that runs now, and anything it cannot prove is a refusal: the release is not rolled back automatically, the incident is told (refused, code "migration" when one was added, so that it fixes forward);
//   4. asks for the deployment with RollbackRelease (an integration command; the executor creates it in Octopus, tdd only, once), answers "started" and follows the deployment Octopus shows;
//   5. when it succeeded, tells the items the rolled-back releases delivered that they are no longer in tdd (Undelivered, which returns them to their Deliver step) and makes them candidates again, so
//      the release that fixes forward delivers them; the answer "done" (or "failed" when the deployment did not succeed) closes the request.
// A hold does not stop it: the rollback is the incident's own deployment. Nothing here calls Octopus: the command is executed only by a host whose engine acts.

sealed partial class TrainRun
{
    async Task RollbackStep(CancellationToken ct)
    {
        for (var guard = 0; guard < 8; guard++)   // (a request that ends in this look lets the next one begin in it)
        {
            var r = s.ActiveRollback;
            if (r is null) { if (!NextRollback()) return; continue; }
            if (r.Phase == RollbackPhase.Requested) { await PlanRollback(r, ct); if (r.Phase == RollbackPhase.Requested) return; }   // (still waiting for a deployment to end)
            if (r.Phase == RollbackPhase.Deploying) FollowRollback(r);
            if (!r.Terminal) return;
            if (!r.Published) FinishRollback(r);
            if (!NextRollback()) return;
        }
    }

    bool NextRollback()
    {
        if (s.RollbackQueue.Count == 0) return false;
        s.ActiveRollback = RollbackRules.Start(s.RollbackQueue[0]);
        s.RollbackQueue.RemoveAt(0);
        return true;
    }

    // ---- the plan: where, to which release, and the migration precondition ----------------------------------------------------------------------------------------

    async Task PlanRollback(RollbackState r, CancellationToken ct)
    {
        if (!RollbackRules.Automatic(p))
        {
            Escalate(r, RollbackCodes.NotPermitted, $"automatic rollback is configured for {RollbackRules.AutomaticEnvironment} only{(p.Project.Length == 0 ? " and needs the deployable's Octopus project" : "")}; the rollback of {p.Deployable} in {env} is a person's decision");
            return;
        }
        if (RollbackRules.Newest(s, env) is not { } newest) { Escalate(r, RollbackCodes.Unavailable, $"no release is known to have been deployed to {TargetName}"); return; }
        if (!newest.Deployment.Completed)
        {
            // a rollback during a deployment would race it: wait for it to end (the limit is the request expiry)
            if (now - r.RequestedAt >= DeliveryTrainService.RequestExpiry) { Escalate(r, RollbackCodes.Unavailable, $"the deployment of {newest.Version} to {TargetName} (task {newest.Deployment.TaskId}) is still running after {DeliveryTrainService.RequestExpiry.TotalMinutes:0} minutes"); return; }
            wakes.Add(now.AddSeconds(p.PollSeconds));
            return;
        }
        var from = newest.Version;
        var target = r.RequestedTo ?? RollbackRules.LastGood(s, env, from);
        if (target is null) { Escalate(r, RollbackCodes.NoPreviousRelease, $"no earlier release ran in {TargetName} before {from}, so there is nothing to go back to"); return; }
        if (target == from && newest.Deployment.Succeeded) { Escalate(r, RollbackCodes.NoPreviousRelease, $"{TargetName} already runs {target}"); return; }
        if (!s.Releases.TryGetValue(target, out var to)) { Escalate(r, RollbackCodes.Unavailable, $"the release {target} is not known to the train"); return; }
        if (s.DeploymentOf(target, env) is not { Succeeded: true }) { Escalate(r, RollbackCodes.NoPreviousRelease, $"the release {target} never ran successfully in {TargetName}, so going to it is no rollback"); return; }
        if (s.RolledBack.Contains(target)) { Escalate(r, RollbackCodes.NoPreviousRelease, $"the release {target} was rolled back from earlier"); return; }
        // the release the rollback leaves: the newest deployment's, or when that deployment failed, the one that still runs
        if (string.IsNullOrEmpty(to.Commit) || !s.Releases.TryGetValue(from, out var current) || string.IsNullOrEmpty(current.Commit)) { Escalate(r, RollbackCodes.Unavailable, $"the commit of {(string.IsNullOrEmpty(to.Commit) ? target : from)} is not known, so it cannot be shown that no database migration was added"); return; }
        if (!string.Equals(to.Commit, current.Commit, StringComparison.OrdinalIgnoreCase))
        {
            if (migrations is null) { Escalate(r, RollbackCodes.Unavailable, "this gateway cannot read the files changed between two releases, so it cannot be shown that no database migration was added"); return; }
            var verdict = RollbackRules.Judge(await migrations.ChangedBetween(p.Repository, to.Commit!, current.Commit!, ct), p.Migrations, target, from);
            if (!verdict.Safe) { Escalate(r, verdict.Added ? RollbackCodes.Migration : RollbackCodes.Unavailable, verdict.Reason); return; }
        }
        r.From = from; r.To = target;
        // the items of the releases that came after the target and were delivered here: they are the rollback's undelivered
        var after = s.DeliveredBy.Where(kv => kv.Key != target && s.Releases.TryGetValue(kv.Key, out var rel) && rel.Assembled > to.Assembled).ToList();
        r.RolledBack = after.Select(kv => kv.Key).Append(from).Distinct().ToList();
        r.Items = after.SelectMany(kv => kv.Value).DistinctBy(c => c.Item).ToList();
        r.Phase = RollbackPhase.Deploying;
        r.FirstCommandAt = r.CommandAt = now;
        Out.Add(CommandOf(r));
        Out.Add(Answer(r, "started", "", $"deploying release {target}"));   // (the incident now waits for the deployment)
        wakes.Add(now.Add(DeliveryTrainService.CommandRetry));
        Out.Add(new TrainDecided(s.Key, "rollback", target, r.Items.Select(c => c.Item).ToList(), [$"rolling {TargetName} back from {from} to {target} for {r.Incident}: {r.Reason}"], now, null, replay));
    }

    RollbackRelease CommandOf(RollbackState r) => new(RollbackRules.KeyOf(p.Deployable, r.Incident, r.To, env), p.Deployable, p.Project, r.To, env, r.Incident);

    // ---- following the deployment Octopus shows -----------------------------------------------------------------------------------------------------------------

    void FollowRollback(RollbackState r)
    {
        var dep = s.DeploymentOf(r.To, env);
        if (dep is not { Rollback: true })
        {
            // Octopus shows no deployment of the rollback yet: the command is sent again after a while (its key makes a second send harmless), and given up on after the request expiry
            if (now - r.FirstCommandAt >= DeliveryTrainService.RequestExpiry) { Escalate(r, RollbackCodes.Failed, $"the rollback deployment of {r.To} to {TargetName} was requested and Octopus showed none within {DeliveryTrainService.RequestExpiry.TotalMinutes:0} minutes"); return; }
            if (now - r.CommandAt >= DeliveryTrainService.CommandRetry) { r.CommandAt = now; Out.Add(CommandOf(r)); }
            wakes.Add(r.CommandAt.Add(DeliveryTrainService.CommandRetry));
            return;
        }
        if (!dep.Completed) { wakes.Add(now.AddSeconds(p.PollSeconds)); return; }
        if (!dep.Succeeded) { Escalate(r, RollbackCodes.Failed, $"the rollback deployment of {r.To} to {TargetName} ended {dep.Phase} ({dep.DeploymentId}); a person decides"); return; }
        r.Phase = RollbackPhase.Done;
    }

    // ---- the end -------------------------------------------------------------------------------------------------------------------------------------------------

    void Escalate(RollbackState r, string code, string reason)
    {
        r.Phase = RollbackPhase.Escalated;
        r.Code = code;
        r.Reasons = [reason];
    }

    /// <summary>The answer to the incident (W8.1's contract): the request's identity and generation come back with it.</summary>
    RollbackAnswered Answer(RollbackState r, string outcome, string code, string reason)
        => new(s.Key, r.Incident, r.Generation, outcome, code, r.To.Length > 0 ? r.To : r.RequestedTo, reason, now);

    void FinishRollback(RollbackState r)
    {
        r.Published = true;
        var items = r.Items.Select(c => c.Item).ToList();
        if (r.Phase == RollbackPhase.Done)
        {
            foreach (var c in r.Items)
            {
                Out.Add(new Undelivered(ItemId.Parse(c.Item), env));
                // the item is a candidate again: the release that fixes forward carries its commit and delivers it
                s.DeliveredItems.Remove($"{c.Item}|{c.Commit}");
                s.Candidates.TryAdd(c.Item, new TrainCandidate { Item = c.Item, Commit = c.Commit, PullRequest = c.PullRequest, Gates = c.Gates, Dependencies = c.Dependencies.ToList(), MergedAt = now });
            }
            foreach (var v in r.RolledBack) { s.DeliveredBy.Remove(v); if (!s.RolledBack.Contains(v)) s.RolledBack.Add(v); }
            Out.Add(new TrainDecided(s.Key, "rolled-back", r.To, items, [$"{TargetName} runs {r.To} again"], now, null, replay));
            Out.Add(Answer(r, "done", "", $"{TargetName} runs {r.To} again; {(items.Count == 0 ? "no item was delivered by the releases after it" : $"{items.Count} item(s) are undelivered")}"));
            return;
        }
        Out.Add(new TrainDecided(s.Key, "rollback-escalated", r.To, items, r.Reasons, now, null, replay));
        // refused: nothing was asked of Octopus; failed: the deployment was asked for and did not happen
        Out.Add(Answer(r, r.FirstCommandAt == default ? "refused" : "failed", r.Code, string.Join("; ", r.Reasons)));
    }

    // ---- what a rollback needs to remember of the deliveries ------------------------------------------------------------------------------------------------------

    /// <summary>The items a release delivered, kept for the newest <see cref="RollbackRules.KeptDeliveries"/> releases (the older ones are no rollback's concern).</summary>
    void RememberDelivery(string version, IReadOnlyList<TrainCandidate> carried)
    {
        s.DeliveredBy[version] = carried.Select(c => new TrainCandidate { Item = c.Item, Commit = c.Commit, PullRequest = c.PullRequest, Gates = c.Gates, Dependencies = c.Dependencies.ToList(), MergedAt = c.MergedAt }).ToList();
        foreach (var old in s.DeliveredBy.Keys.OrderByDescending(v => s.Releases.TryGetValue(v, out var rel) ? rel.Assembled : DateTimeOffset.MinValue).Skip(RollbackRules.KeptDeliveries).ToList()) s.DeliveredBy.Remove(old);
    }
}
