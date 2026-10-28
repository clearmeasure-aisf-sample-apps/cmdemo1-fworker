using System.Globalization;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Domain;

namespace Fworker.Workflows.Delivery;

// W5.1: DeliveryTrainSaga, one per deployable and environment (docs/design/wolverine-factory.md 3.2, delivery-speed.md 12, system-gateway.md H3, H5, H9).
//
// A train keeps what it has seen (merged items, releases, Octopus deployments, Sign-offs) in a row of the DeliveryTrains table and decides on every input: per target environment it looks
// only at the newest release that succeeded in the previous one and judges it with the rules of D2 (EvidenceComposite) and D3 (ContentGate, ReleaseChoice), falling back to the release
// before it when the newer is blocked, as the content gate decides today. Where v1 promotes (tdd), v1's decision stays the acting one: the train decides, publishes TrainDecided and, only
// when its policy lets it act (DeliveryPolicy.Acts) and the engine acts, asks for the deployment with PromoteRelease and answers a Sign-off with AnswerSignOff. Nothing here calls Octopus: both
// are integration commands that a host without Engine.V2Acts does not execute.
//
// It is a saga in the sense of the design (state per key, saved with a revision, timers by Wolverine scheduled messages), not Wolverine's Saga base class: that one needs generated handler
// code per saga type, and the state here is the one JSON document the shadow run compares. Delivery is at least once: every input is applied twice with the same result.

/// <summary>Whether a train has this deployment's items delivered: the delivered-to table answers the slot dependencies-delivered(env).</summary>
public interface IDeliveredLookup
{
    Task<IReadOnlyList<ItemId>> NotYetDelivered(IEnumerable<ItemId> dependencies, string environment, CancellationToken ct = default);
}

/// <summary>What one input made the train do: the messages to publish (decisions, delivered-to events, commands) and when to wake it.</summary>
public sealed record TrainResult(IReadOnlyList<object> Messages, DateTimeOffset? WakeAt)
{
    public static readonly TrainResult None = new([], null);
}

/// <summary>Two writers moved the same train at once more often than the retries allow; the message is retried by Wolverine.</summary>
public sealed class TrainConflictException(string key) : Exception($"the delivery train {key} kept changing under the handler");

public sealed class DeliveryTrainService(ITrainStore store, IDeliveryPolicies policies, ITrainReads reads, IDeliveredLookup delivered, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>How long a command's effect may stay unseen in Octopus before the train sends it again (the key makes the second send harmless).</summary>
    public static readonly TimeSpan CommandRetry = TimeSpan.FromMinutes(5);
    /// <summary>A request that shows no deployment in Octopus for this long is dropped and the train judges the releases again.</summary>
    public static readonly TimeSpan RequestExpiry = TimeSpan.FromMinutes(30);
    /// <summary>A replay that has not ended after this long stops holding the train back (the end marker was lost).</summary>
    public static readonly TimeSpan ReplayLimit = TimeSpan.FromMinutes(30);
    /// <summary>When a read of GitHub or of the app is refused or fails, the train looks again after this (the input is already saved).</summary>
    public static readonly TimeSpan TransientRetry = TimeSpan.FromMinutes(1);
    /// <summary>A train that waits (a release not promoted, a deployment followed) looks again at least this often, whatever else wakes it.</summary>
    public static readonly TimeSpan SafetyWake = TimeSpan.FromMinutes(10);
    const int Attempts = 5;

    /// <summary>
    /// Two phases, so that no input is lost to a read that fails. First the input is applied and saved. Then the state is judged (reads of GitHub and of the app, which can throw): a read that fails
    /// leaves the saved state as it is and schedules a new look (<see cref="TransientRetry"/>) instead of failing the message. While a replay runs (<see cref="TrainReplay"/>) there is no second phase.
    /// </summary>
    public async Task<TrainResult> Handle(TrainInput input, CancellationToken ct = default)
    {
        var (deployable, environment) = TrainKey.Split(input.Key);
        var policy = policies.For(deployable, environment);
        if (policy is null) return TrainResult.None;   // the deployable left the catalog
        var replayEnded = input is TrainReplay { Active: false };
        if (input is not TrainWake)
        {
            var saved = false;
            for (var attempt = 0; attempt < Attempts && !saved; attempt++)
            {
                var rec = await store.Load(input.Key, ct);
                var state = rec?.State ?? TrainState.New(input.Key);
                Apply(state, input, policy, clock());
                state.Sequence++;
                saved = await store.Save(input.Key, state, rec?.Revision ?? 0, ct);
            }
            if (!saved) throw new TrainConflictException(input.Key);
        }
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var rec = await store.Load(input.Key, ct);
            if (rec is null) return TrainResult.None;
            var state = rec.State;
            var now = clock();
            var stale = false;
            if (input is TrainWake { Due: { } due }) { if (due == state.WakeDue) state.WakeDue = null; else stale = true; }   // (a timer that is not the pending one only looks again)
            var gated = state.ReplayingSince is { } since && now - since < ReplayLimit;
            TrainRun? run = null; DateTimeOffset? wake = null;
            if (gated) wake = state.ReplayingSince + DeliveryTrainService.ReplayLimit;   // if the end marker is lost, the train looks again when the hold runs out
            if (!gated)
            {
                run = new TrainRun(state, policy, reads, delivered, now, replayEnded);
                try { await run.Evaluate(ct); }
                catch (Exception e) when (e is HttpRequestException or TimeoutException || (e is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    return new TrainResult([], now + TransientRetry);   // nothing of this look is kept; the input is saved
                }
                wake = run.NextWake(stale);
                if (wake is { } next && (state.WakeDue is not { } cur || cur <= now || next < cur)) state.WakeDue = next;
            }
            state.Sequence++;
            if (await store.Save(input.Key, state, rec.Revision, ct)) return new TrainResult(run?.Out ?? [], wake);
        }
        throw new TrainConflictException(input.Key);
    }

    /// <summary>Applies the input to the state.</summary>
    static void Apply(TrainState s, TrainInput input, DeliveryPolicy p, DateTimeOffset now)
    {
        switch (input)
        {
            case TrainCandidateAdded a:
                var m = a.Merged;
                // a redelivery keeps the candidate (and its changed gates) and never brings back an item already delivered
                if (s.Candidates.ContainsKey(m.Item.ToString()) || s.DeliveredItems.Contains($"{m.Item}|{m.Commit.ToLowerInvariant()}")) break;
                s.Candidates[m.Item.ToString()] = new TrainCandidate { Item = m.Item.ToString(), Commit = m.Commit.ToLowerInvariant(), PullRequest = m.PullRequest, Gates = m.Gates, Dependencies = m.Dependencies.Select(d => d.ToString()).ToList(), MergedAt = now };
                break;
            case TrainGatesChanged g:
                if (s.Candidates.TryGetValue(g.Item, out var c)) c.Gates = g.Gates;
                break;
            case TrainReleaseSeen r:
                UpsertRelease(s, r.ReleaseId, r.Version, r.Commit, r.At);
                break;
            case TrainDeploymentSeen d:
                var key = p.KeyOf(d.Environment);
                UpsertRelease(s, d.ReleaseId, d.Version, d.Commit, d.At);
                if (key is null) break;
                var have = s.DeploymentOf(d.Version, key);
                if (have is null || have.DeploymentId != d.DeploymentId)
                {
                    if (!s.Deployments.TryGetValue(d.Version, out var byEnv)) s.Deployments[d.Version] = byEnv = [];
                    byEnv[key] = have = new TrainDeployment { DeploymentId = d.DeploymentId, TaskId = d.TaskId, Phase = "Started", StartedAt = d.At };
                    // a newer deployment of the release to the environment (a redeploy after a failure) starts the settling over
                    if (key == p.Environment) s.Settled.Remove(d.Version);
                }
                if (d.Phase != "Started" && !have.Completed) { have.Phase = d.Phase; have.FinishedAt = d.At; }
                if (have.TaskId.Length == 0) have.TaskId = d.TaskId;
                if (key == p.Environment && have.Completed) s.SignOffs.RemoveAll(x => x.Version == d.Version);   // the Sign-off ended with the deployment
                break;
            case TrainSignOffAsked so:
                UpsertRelease(s, so.ReleaseId, so.Version, so.Commit, so.At);
                if (p.KeyOf(so.Environment) != p.Environment) break;
                if (s.SignOffs.All(x => x.InterruptionId != so.InterruptionId))
                    s.SignOffs.Add(new TrainSignOff { InterruptionId = so.InterruptionId, DeploymentId = so.DeploymentId, TaskId = so.TaskId, Version = so.Version, Since = now });
                break;
            case TrainHold h:
                s.Held = h.Held; s.HeldReason = h.Held ? h.Reason : "";
                break;
            case TrainReplay r:
                s.ReplayingSince = r.Active ? now : null;
                break;
        }
    }

    static void UpsertRelease(TrainState s, string releaseId, string version, string? commit, DateTimeOffset at)
    {
        if (!s.Releases.TryGetValue(version, out var r)) s.Releases[version] = r = new TrainRelease { Version = version, ReleaseId = releaseId, Assembled = at };
        if (string.IsNullOrEmpty(r.ReleaseId)) r.ReleaseId = releaseId;
        if (!string.IsNullOrEmpty(commit)) r.Commit = commit.ToLowerInvariant();
    }
}

/// <summary>One evaluation of one train on its state: the settling of finished deployments, the Sign-offs, the promotion. Short-lived; <see cref="Out"/> is what it decided to publish.</summary>
sealed class TrainRun(TrainState s, DeliveryPolicy p, ITrainReads reads, IDeliveredLookup delivered, DateTimeOffset now, bool replay = false)
{
    public List<object> Out { get; } = [];
    readonly List<DateTimeOffset> wakes = [];
    readonly string env = p.Environment;
    string TargetName => p.NameOf(env);
    string? Previous => ReleaseChoice.PreviousEnvironment(env);

    // ---- the evaluation ------------------------------------------------------------------------------------------------------------------------------------

    public async Task Evaluate(CancellationToken ct)
    {
        await Settle(ct);
        if (!p.Acts) return;   // tdd is deployed by the release's lifecycle and an external promoter creates its own deployments: the train observes
        if (s.Held) { Decide("held", "", [], [string.IsNullOrEmpty(s.HeldReason) ? "an incident holds the train" : s.HeldReason]); return; }
        if (!await SignOffs(ct)) await Promote(ct);
        // a train that waits looks again at least every SafetyWake, whatever else wakes it (an input lost between the save and the outbox, a gap no event lifts)
        if (s.Decisions.Values.Any(fp => fp.StartsWith("waiting|") || fp.StartsWith("following|") || fp.StartsWith("signoff-waiting|"))) wakes.Add(now.Add(DeliveryTrainService.SafetyWake));
    }

    /// <summary>The time of the next timer, or null: none needed, or the pending one still stands. <paramref name="stale"/>: this input was a timer other than the pending one.</summary>
    public DateTimeOffset? NextWake(bool stale)
    {
        if (wakes.Count == 0) return null;
        var due = wakes.Min();
        if (stale && s.WakeDue is { } pending && pending > now) return null;   // the pending timer will look again; a stale one starts no second chain
        // (a pending timer is not trusted to exist: it may have been lost between the save and the outbox, so the due time goes out again; a duplicate only looks again)
        return due;
    }

    // ---- settling: a finished deployment to this environment delivers its items -------------------------------------------------------------------------------

    async Task Settle(CancellationToken ct)
    {
        var done = s.Deployments.Where(kv => kv.Value.TryGetValue(env, out var d) && d.Succeeded && !s.Settled.Contains(kv.Key))
            .OrderBy(kv => kv.Value[env].FinishedAt ?? kv.Value[env].StartedAt).Select(kv => kv.Key).ToList();
        foreach (var version in done)
        {
            var release = s.Releases[version]; var dep = s.Deployments[version][env];
            var before = RunningBefore(dep);
            var carried = new List<TrainCandidate>();
            if (!string.IsNullOrEmpty(release.Commit))
            {
                carried.AddRange(s.Candidates.Values.Where(c => c.Commit == release.Commit));
                // everything between what ran before and this release: the items of releases that were never deployed here ride along
                if (before is { } b && b.Commit != release.Commit)
                {
                    var cmp = await reads.Compare(p.Repository, b.Commit, release.Commit, ct);
                    var j = ContentGate.Judge(release.Commit, TargetName, b.Commit, b.Version, cmp, QueuedBySha(), _ => new CommitItemLookup(0, ""));
                    foreach (var n in j.Items) carried.AddRange(CandidatesOf(n));
                }
            }
            carried = carried.DistinctBy(c => c.Item).ToList();
            var items = new List<string>();
            foreach (var c in carried)
            {
                Out.Add(new DeliveredTo(ItemId.Parse(c.Item), env, version));
                s.Candidates.Remove(c.Item);
                s.DeliveredItems.Add($"{c.Item}|{c.Commit}");
                items.Add(c.Item);
            }
            s.Settled.Add(version);
            if (s.Current is null || !s.Releases.TryGetValue(s.Current, out var cur) || (s.DeploymentOf(s.Current, env)?.FinishedAt ?? DateTimeOffset.MinValue) <= (dep.FinishedAt ?? dep.StartedAt)) s.Current = version;
            // older releases that never reached the environment ride in this one: "delivered in release X, superseding Y"
            var superseded = s.Releases.Values.Where(r => r.Version != version && r.Assembled <= release.Assembled && s.DeploymentOf(r.Version, env) is not { Succeeded: true } && !s.Superseded.ContainsKey(r.Version))
                .OrderBy(r => r.Assembled).Select(r => r.Version).ToList();
            foreach (var v in superseded) s.Superseded[v] = version;
            var reasons = new List<string> { superseded.Count == 0 ? $"delivered in release {version}" : $"delivered in release {version}, superseding {string.Join(", ", superseded)}" };
            Decide("delivered", version, items, reasons, always: true, superseded: superseded);
            s.Requested.Remove(version); s.FirstRequested.Remove(version);
        }
    }

    /// <summary>The release that ran in this environment before the deployment: the newest successful deployment that finished earlier and whose release names a commit.</summary>
    (string Version, string Commit)? RunningBefore(TrainDeployment dep)
    {
        var at = dep.FinishedAt ?? dep.StartedAt;
        var prior = s.Deployments.Where(kv => kv.Value.TryGetValue(env, out var d) && d.Succeeded && d != dep && (d.FinishedAt ?? d.StartedAt) < at)
            .OrderByDescending(kv => kv.Value[env].FinishedAt ?? kv.Value[env].StartedAt).Select(kv => kv.Key).FirstOrDefault();
        return prior is not null && s.Releases.TryGetValue(prior, out var r) && !string.IsNullOrEmpty(r.Commit) ? (prior, r.Commit!) : null;
    }

    Dictionary<string, int> QueuedBySha()
    {
        var by = new Dictionary<string, int>();
        foreach (var c in s.Candidates.Values) if (ItemId.TryParse(c.Item, out var id)) by.TryAdd(c.Commit, id.Number);
        return by;
    }

    /// <summary>The candidate of an item number, and the others merged by the same commit (a pull request that closes several items).</summary>
    IEnumerable<TrainCandidate> CandidatesOf(int number)
    {
        var first = s.Candidates.Values.FirstOrDefault(c => ItemId.TryParse(c.Item, out var id) && id.Number == number);
        return first is null ? [] : s.Candidates.Values.Where(c => c.Commit == first.Commit);
    }

    // ---- judging a release for this environment ---------------------------------------------------------------------------------------------------------------

    sealed record Judged(string Version, ContentJudgment Content, IReadOnlyList<string> Items, IReadOnlyList<string> ItemGaps);

    /// <summary>Get-ReleaseContent and the item gates of one release: what it carries beyond what runs in this environment, and what its items lack.</summary>
    async Task<Judged> Judge(TrainRelease release, CancellationToken ct)
    {
        var running = s.RunningIn(env);
        CompareRange? cmp = null;
        if (!string.IsNullOrEmpty(release.Commit) && running is { } r && !string.Equals(r.Commit, release.Commit, StringComparison.OrdinalIgnoreCase))
            cmp = await reads.Compare(p.Repository, r.Commit, release.Commit, ct);
        var by = QueuedBySha();
        // the content gate takes a synchronous lookup: the first pass notes the commits that are no candidate's, the second has their answers
        var missing = new List<RangeCommit>(); var answers = new Dictionary<string, CommitItemLookup>();
        var j = ContentGate.Judge(release.Commit, TargetName, running?.Commit, running?.Version, cmp, by, c => { missing.Add(c); return new CommitItemLookup(0, ""); });
        if (missing.Count > 0)
        {
            foreach (var c in missing) answers[c.Sha] = await reads.LookupCommit(p.Repository, c, ct);
            j = ContentGate.Judge(release.Commit, TargetName, running?.Commit, running?.Version, cmp, by, c => answers.TryGetValue(c.Sha, out var a) ? a : new CommitItemLookup(0, ""));
        }
        var items = new List<string>(); var itemGaps = new List<string>();
        foreach (var n in j.Items)
        {
            var cands = CandidatesOf(n).ToList();
            if (cands.Count > 0)
                foreach (var c in cands) { items.Add(c.Item); itemGaps.AddRange(c.Gates.Gaps); if (p.DependenciesDelivered) itemGaps.AddRange(await DependencyGaps(c, j.Items, ct)); }
            else
            {
                // an item found by its commit that is no candidate (a docs-only item went straight to Done): v1 reads its gates too
                var id = new ItemId(p.Repository, n); items.Add(id.ToString());
                if (await reads.ReadGates(id, ct) is { } g) itemGaps.AddRange(g.Gaps);
            }
        }
        return new Judged(release.Version, j, items.Distinct().ToList(), itemGaps);
    }

    /// <summary>The slot dependencies-delivered(env), off unless the policy turns it on: a dependency of an item that is not delivered here (and not in the same release) holds it.</summary>
    async Task<IEnumerable<string>> DependencyGaps(TrainCandidate c, IReadOnlyList<int> releaseItems, CancellationToken ct)
    {
        var deps = c.Dependencies.Where(d => ItemId.TryParse(d, out var id) && !(id.Repository == p.Repository && releaseItems.Contains(id.Number))).Select(ItemId.Parse).ToList();
        if (deps.Count == 0) return [];
        var missing = await delivered.NotYetDelivered(deps, env, ct);
        return missing.Select(m => $"{c.Item} depends on {m}, which is not yet delivered to {TargetName}");
    }

    // ---- the evidence of the previous environment ------------------------------------------------------------------------------------------------------------

    /// <summary>Test-EnvHealth for a release in the previous environment: '' when the evidence is complete. One read per call, spaced by the policy's read gap.</summary>
    async Task<string> Evidence(TrainRelease release, TrainDeployment prev, CancellationToken ct)
    {
        var pk = Previous!;
        var ek = $"{release.Version}|{pk}";
        s.Evidence.TryGetValue(ek, out var ev);
        if (ev?.LastReadAt is { } last && now - last < TimeSpan.FromSeconds(p.MinReadGapSeconds)) return TimeBound(ev.LastGap);
        var url = p.UrlOf(pk);
        var finished = prev.FinishedAt ?? now;
        var probe = url is null ? new EnvProbe(null, null, "", now) : await reads.Probe(p.Deployable, pk, ct);
        var stored = (ev?.Reads ?? []).Select(r => new HealthRead(r.At, r.Status)).ToList();
        var fresh = new HealthRead(probe.At, probe.Status);
        var verdict = EvidenceComposite.Evaluate(pk, url, release.Version, probe.RunningVersion, p.HealthPath, stored, finished, fresh, p.Window);
        // the reads are kept only once the release runs there (v1 stores them after the version check)
        if (url is not null && EvidenceComposite.VersionGap(pk, release.Version, probe.RunningVersion) is null)
        {
            foreach (var other in s.Evidence.Keys.Where(k => k.EndsWith("|" + pk) && k != ek).ToList()) s.Evidence.Remove(other);
            var kept = EvidenceComposite.Keep(EvidenceComposite.Collect(stored, finished, fresh));
            s.Evidence[ek] = new TrainEvidence { Reads = kept.Select(r => new TrainRead { At = r.At, Status = r.Status }).ToList(), LastReadAt = now, LastGap = verdict.Gap };
        }
        else if (ev is not null) { ev.LastReadAt = now; ev.LastGap = verdict.Gap; }
        return TimeBound(verdict.Gap);
    }

    /// <summary>
    /// A gap of the evidence is lifted by the clock or by the app, never by an event the train hears of: the train wakes itself. The app that does not run the release yet and the reads that are not
    /// old enough look again at the policy's poll; the other kinds (an unhealthy answer, which only a new deployment lifts, and a missing health URL) at the safety interval.
    /// </summary>
    string TimeBound(string gap)
    {
        if (gap.Length == 0) return gap;
        var soon = gap.StartsWith($"the {Previous} app does not run") || gap.Contains("has not been Healthy long enough");
        wakes.Add(soon ? now.AddSeconds(p.PollSeconds) : now.Add(DeliveryTrainService.SafetyWake));
        return gap;
    }

    // ---- the Sign-off ------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Answers or waits at the Sign-offs of deployments the train asked for (a Sign-off of a deployment of anyone else is only followed). True when a Sign-off decided the evaluation.</summary>
    async Task<bool> SignOffs(CancellationToken ct)
    {
        foreach (var so in s.SignOffs.ToList())
        {
            if (so.Answered)
            {
                // the answer's effect is not seen yet: send it again after a while (the key makes a second send harmless)
                if (so.AnsweredAt is { } at && now - at >= DeliveryTrainService.CommandRetry) { so.AnsweredAt = now; Out.Add(AnswerOf(so, so.LastProceed, so.LastNotes)); }
                wakes.Add((so.AnsweredAt ?? now).Add(DeliveryTrainService.CommandRetry));
                continue;
            }
            if (!s.Requested.ContainsKey(so.Version) || !s.Releases.TryGetValue(so.Version, out var release))
            {
                Decide("following", so.Version, [], [$"the Sign-off of task {so.TaskId} belongs to a deployment the gateway did not create"]);
                continue;
            }
            var j = await Judge(release, ct);
            // The Sign-off asks again only what can get worse while it waits, and aborts only for a hard failure (Invoke-AutoPromoteInner): an item gate, a commit of the release that is no
            // factory work item's, the release superseded in the target, the previous environment's app not Healthy now. A waiting reason leaves it pending.
            var hard = j.ItemGaps.Concat(j.Content.Hard).ToList();
            if (j.Content.Skip) hard.Add($"a release at least as new already runs in {TargetName}");
            var common = j.Content.Gaps.ToList();
            var prev = s.DeploymentOf(so.Version, Previous!);
            if (prev is null) common.Add($"it has no deployment to {p.NameOf(Previous!)}");
            else if (!prev.Succeeded) common.Add($"its deployment to {p.NameOf(Previous!)} is {(prev.Completed ? prev.Phase : "still " + prev.Phase)} ({prev.DeploymentId})");
            var soft = common.Where(g => !hard.Contains(g)).ToList();
            if (hard.Count == 0 && soft.Count == 0)
            {
                var now1 = await HealthNow(ct);
                if (now1.Length > 0) hard.Add(now1);
            }
            if (hard.Count == 0 && soft.Count > 0)
            {
                // a waiting Sign-off blocks later deployments of the project to the environment: after signoffWaitHours it is aborted
                var waitedH = (now - so.Since).TotalHours;
                if (waitedH >= p.SignoffWaitHours) hard = [$"waited {Math.Round(waitedH, 1).ToString("R", CultureInfo.InvariantCulture)} h at the Sign-off (limit {p.SignoffWaitHours.ToString("R", CultureInfo.InvariantCulture)} h): {string.Join("; ", soft)}"];
                else
                {
                    wakes.Add(so.Since.AddHours(p.SignoffWaitHours)); wakes.Add(now.AddSeconds(p.PollSeconds));
                    Decide("signoff-waiting", so.Version, j.Items, [$"the Sign-off of task {so.TaskId} waits: {string.Join("; ", soft)}"]);
                    continue;
                }
            }
            var proceed = hard.Count == 0;
            var notes = proceed ? "Signed off by the factory worker gateway: every gate of the release holds (docs/design/auto-promote.md)." : $"Aborted by the factory worker gateway: {string.Join("; ", hard)}";
            so.Answered = true; so.AnsweredAt = now; so.LastProceed = proceed; so.LastNotes = notes;
            Out.Add(AnswerOf(so, proceed, notes));
            wakes.Add(now.Add(DeliveryTrainService.CommandRetry));
            Decide(proceed ? "signoff-proceed" : "signoff-abort", so.Version, j.Items, proceed ? [] : hard);
        }
        return s.SignOffs.Count > 0;   // a deployment waits at its Sign-off: there is nothing to promote beside it
    }

    AnswerSignOff AnswerOf(TrainSignOff so, bool proceed, string notes)
        => new($"AnswerSignOff|{p.Deployable}|{so.InterruptionId}", p.Deployable, so.InterruptionId, so.TaskId, proceed, notes);

    /// <summary>Test-EnvHealthNow: one read of the previous environment's app, '' when Healthy, else the reason.</summary>
    async Task<string> HealthNow(CancellationToken ct)
    {
        var pk = Previous!;
        if (p.UrlOf(pk) is null) return EvidenceComposite.NoUrlGap(pk);
        var probe = await reads.Probe(p.Deployable, pk, ct);
        return string.Equals(probe.Status, "Healthy", StringComparison.OrdinalIgnoreCase) ? "" : $"the {pk} app answered {probe.Status} on {p.HealthPath}";
    }

    // ---- promotion -----------------------------------------------------------------------------------------------------------------------------------------

    async Task Promote(CancellationToken ct)
    {
        var pk = Previous!;
        var open = s.Deployments.Where(kv => kv.Value.TryGetValue(env, out var d) && !d.Completed).Select(kv => (Version: kv.Key, Dep: kv.Value[env])).FirstOrDefault();
        if (open.Version is not null) { Decide("following", open.Version, [], [$"the deployment of {open.Version} to {TargetName} is still running (task {open.Dep.TaskId})"]); return; }
        foreach (var (version, at) in s.Requested.Where(kv => s.DeploymentOf(kv.Key, env) is null).ToList())
        {
            if (s.FirstRequested.TryGetValue(version, out var first) && now - first >= DeliveryTrainService.RequestExpiry)
            {
                // Octopus never showed the deployment: drop the request so a newer release is not blocked behind it
                s.Requested.Remove(version); s.FirstRequested.Remove(version);
                Decide("waiting", version, [], [$"the deployment of {version} to {TargetName} was requested and Octopus showed none within {DeliveryTrainService.RequestExpiry.TotalMinutes:0} minutes; the request is dropped"]);
                continue;
            }
            // asked for, and Octopus shows nothing yet
            if (now - at >= DeliveryTrainService.CommandRetry && s.Releases.TryGetValue(version, out var rel)) { s.Requested[version] = now; Out.Add(PromoteOf(rel)); }
            wakes.Add(s.Requested[version].Add(DeliveryTrainService.CommandRetry));
            Decide("following", version, [], [$"the deployment of {version} to {TargetName} was requested; Octopus shows none yet"]);
            return;
        }
        // The release is a candidate when it has no successful deployment to this environment. Only a release that succeeded in the previous one can win (the train, delivery-speed.md 12);
        // the newest one that has not says why it waits, as v1 does.
        var open2 = s.Releases.Values.Where(r => s.DeploymentOf(r.Version, env) is not { Succeeded: true } && !s.Superseded.ContainsKey(r.Version)).OrderByDescending(r => r.Assembled).ToList();
        var judged = new List<ReleaseCandidate>(); var notes = new List<(string Version, IReadOnlyList<string> Items, IReadOnlyList<string> Reasons)>();
        Judged? winner = null;
        // v1 judges every queued release in a pass and says what each waits for, so the train describes every open release (the newest one that qualifies is the one promoted; the others
        // are described, never chosen). Behind the release that wins or that waits for the clock, at most MaxDescribedBehind more are read.
        var stop = false; var behind = 0;
        foreach (var r in open2)
        {
            var prev = s.DeploymentOf(r.Version, pk);
            var failedHere = s.DeploymentOf(r.Version, env);
            if (failedHere is not null)
            {
                Note(r.Version, [], [$"its deployment to {TargetName} ended {failedHere.Phase} ({failedHere.DeploymentId}); the operator deploys it again, or the item goes back to Development"]);
                continue;
            }
            if (prev is not { Succeeded: true })
            {
                Note(r.Version, [], [prev is null ? $"it has no deployment to {p.NameOf(pk)}" : $"its deployment to {p.NameOf(pk)} is {(prev.Completed ? prev.Phase : "still " + prev.Phase)} ({prev.DeploymentId})"]);
                continue;
            }
            if (stop && ++behind > MaxDescribedBehind) continue;
            var j = await Judge(r, ct);
            if (j.Content.Skip) continue;   // not newer than what runs here: nothing to promote
            var common = j.Content.Gaps.ToList();
            var evidenceOnly = false;
            if (!stop && j.ItemGaps.Count == 0 && common.Count == 0) { var gap = await Evidence(r, prev, ct); if (gap.Length > 0) { common.Add(gap); evidenceOnly = true; } }
            var reasons = common.Concat(j.ItemGaps).Distinct().ToList();
            if (stop) { if (reasons.Count > 0) Note(r.Version, j.Items, reasons); continue; }
            judged.Add(new ReleaseCandidate(r.Version, r.Assembled, reasons.Count == 0));
            if (reasons.Count == 0) { winner = j; stop = true; continue; }
            Note(r.Version, j.Items, reasons);
            // a release that only waits for the clock or the app is not blocked: the train does not fall back to an older release past it
            if (evidenceOnly) stop = true;
        }
        var pick = ReleaseChoice.NewestQualifying(judged);
        var mentioned = new HashSet<string>();
        foreach (var (version, items, reasons) in notes) { mentioned.Add(version); Decide("waiting", version, items, reasons); }
        if (pick is null || winner is null) { Retract(mentioned); return; }
        mentioned.Add(pick.Version); Retract(mentioned);
        var release = s.Releases[pick.Version];
        s.Requested[pick.Version] = now; s.FirstRequested[pick.Version] = now;
        Out.Add(PromoteOf(release));
        wakes.Add(now.Add(DeliveryTrainService.CommandRetry));
        Decide("promote", pick.Version, winner.Items, []);

        void Note(string version, IReadOnlyList<string> items, IReadOnlyList<string> reasons) => notes.Add((version, items, reasons.ToList()));
    }

    /// <summary>How many open releases behind the one that wins (or waits for the clock) are still read to say what they wait for.</summary>
    const int MaxDescribedBehind = 10;

    PromoteRelease PromoteOf(TrainRelease r) => new($"PromoteRelease|{p.Deployable}|{r.Version}|{env}", p.Deployable, p.Project, r.Version, env);

    // ---- decisions -----------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Publishes a decision once per change (the same decision again is not news); "delivered" is published every time it happens.</summary>
    void Decide(string action, string release, IReadOnlyList<string> items, IReadOnlyList<string> reasons, bool always = false, IReadOnlyList<string>? superseded = null)
    {
        var fp = $"{action}|{release}|{string.Join(",", items)}|{string.Join("; ", reasons)}";
        if (!always && s.Decisions.TryGetValue(release, out var last) && last == fp) return;
        // one decision is kept per release; a release that is delivered, superseded or no longer judged leaves none behind (the shadow retracts what it held for it)
        if (action is "delivered" or "idle") { s.Decisions.Remove(release); foreach (var v in superseded ?? []) s.Decisions.Remove(v); }
        else s.Decisions[release] = fp;
        Out.Add(new TrainDecided(s.Key, action, release, items, reasons, now, superseded, replay));
    }

    /// <summary>The releases that had a decision and are not judged any more (promoted all the way, not newer than what runs here, gone from the open ones): each is retracted with an "idle" decision.</summary>
    void Retract(HashSet<string> judged)
    {
        foreach (var release in s.Decisions.Keys.Where(k => k.Length > 0 && !judged.Contains(k)).ToList()) Decide("idle", release, [], []);
        s.Decisions.Remove("");
    }
}
