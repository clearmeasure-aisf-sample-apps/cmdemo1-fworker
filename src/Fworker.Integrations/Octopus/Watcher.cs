using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Seams;

namespace Fworker.Integrations.Octopus;

/// <summary>Where the watcher reads: octopus.space and octopus.project of the configuration (the server is the reader's).</summary>
public sealed record OctopusWatchSettings(string Space, string Project, int ReleaseWindow = 30, int DeploymentWindow = 30, int MaxCallsPerPoll = 60, TimeSpan? Lookback = null)
{
    /// <summary>Releases and deployments older than this at the watcher's first poll are only noted, not published (a restart does not replay the history).</summary>
    public TimeSpan LookbackOrDefault => Lookback ?? TimeSpan.FromMinutes(15);
}

/// <summary>
/// W4.2: the C# Octopus watcher. One <see cref="Poll"/> reads what the PowerShell promotion pass reads (the same endpoints: project, environments, the project's releases and
/// deployments, a deployment's task, a release by id, the interruptions of a running task) and publishes ReleaseCreated, ReleaseCommitResolved, the phases of a
/// deployment and SignOffRequested. It never writes: every call is a GET. Budget rules as Invoke-Octo's: the project is read once, a release once, the task of a
/// deployment only until it is terminal, and a poll makes at most MaxCallsPerPoll calls (the rest waits for the next poll). Nothing is published twice: what was
/// published is remembered by event key. The memory is the process's and bounded: a key is kept for as long as its release or deployment is in the window the watcher reads
/// (ReleaseWindow, DeploymentWindow), and forgotten when the item has left it (the window is the newest items, so an item that left does not come back; if it did, the train's
/// inputs are idempotent and it would be replayed once). A restart replays what is inside the lookback, and the train deduplicates by the same keys. A release whose commit was not
/// known when it was first seen is looked at again on every poll (the list carries the build information and the notes) and publishes ReleaseCommitResolved when the commit appears.
/// Not thread safe: one caller polls.
/// </summary>
public sealed class OctopusWatcher(IOctopusReader reader, Action<IngressEvent> publish, OctopusWatchSettings settings, Func<DateTimeOffset>? clock = null)
{
    static readonly Regex Sha = new(@"\b[0-9a-fA-F]{40}\b", RegexOptions.Compiled);
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Dictionary<string, HashSet<string>> published = [];   // event keys already published, by the release ("R:id") or deployment ("D:id") they belong to, so that they are forgotten with it
    readonly HashSet<string> finished = [];   // deployment ids whose terminal phase is published (or that ended before the watcher began): their task is not read again
    readonly Dictionary<string, Rel> releases = [];
    DateTimeOffset? since;
    bool replayBegan, replayDone;   // the first poll(s) replay the history; the delivery train decides nothing until the replay is complete
    string? projectId;
    int calls;

    sealed record Rel(string Id, string Version, string? Commit);

    /// <summary>The calls the last poll made.</summary>
    public int LastCalls => calls;

    /// <summary>How many things the watcher keeps in memory (published keys, finished deployments, known releases): bounded by the windows, never by the age of the process.</summary>
    public int Remembered => published.Values.Sum(k => k.Count) + finished.Count + releases.Count;

    /// <summary>The commit a release was built from: the first 40 hex digit VcsCommitNumber of its build information, else the first 40 hex digit word of its notes; lower case; null when none (Get-ReleaseCommit).</summary>
    public static string? ReleaseCommit(JsonNode? release)
    {
        if (release?["BuildInformation"] is JsonArray bi)
            foreach (var b in bi) if (b?["VcsCommitNumber"]?.ToString() is { Length: 40 } v && Sha.IsMatch(v)) return v.ToLowerInvariant();
        var m = Sha.Match(release?["ReleaseNotes"]?.ToString() ?? "");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>The phase a task state means: Queued, Executing, Cancelling are Started; Success is Succeeded; Failed, TimedOut, Canceled are Failed. Null for a state this does not know.</summary>
    public static OctopusDeploymentPhase? PhaseOf(string state) => state switch
    {
        "Queued" or "Executing" or "Cancelling" => OctopusDeploymentPhase.Started,
        "Success" => OctopusDeploymentPhase.Succeeded,
        "Failed" or "TimedOut" or "Canceled" => OctopusDeploymentPhase.Failed,
        _ => null,
    };

    async Task<JsonNode?> Read(string path, CancellationToken ct)
    {
        if (calls >= settings.MaxCallsPerPoll) throw new BudgetSpent();
        calls++;
        return await reader.Get(path, ct);
    }

    sealed class BudgetSpent : Exception;

    bool Has(string owner, string key) => published.TryGetValue(owner, out var k) && k.Contains(key);

    bool Mark(string owner, string key)
    {
        if (!published.TryGetValue(owner, out var k)) published[owner] = k = [];
        return k.Add(key);
    }

    void Unmark(string owner, string key)
    {
        if (published.TryGetValue(owner, out var k) && k.Remove(key) && k.Count == 0) published.Remove(owner);
    }

    // a publish that throws is forgotten, so the next poll publishes the event again
    void Emit(string owner, string key, IngressEvent ev) { if (!Mark(owner, key)) return; try { publish(ev); } catch { Unmark(owner, key); throw; } }

    /// <summary>Forgets what belongs to a release or a deployment that is no longer in the window just read. Called only with a window that was read and parsed: an empty answer is not "everything left".</summary>
    void Forget(string prefix, HashSet<string> inWindow)
    {
        foreach (var owner in published.Keys.Where(o => o.StartsWith(prefix, StringComparison.Ordinal) && !inWindow.Contains(o[prefix.Length..])).ToList()) published.Remove(owner);
    }

    /// <summary>The first of these Octopus times that parses (Octopus writes ISO 8601 with an offset); null when none does, and the delivery train then stamps the event with its own clock (#496).</summary>
    public static DateTimeOffset? TimeOf(params JsonNode?[] values)
    {
        foreach (var v in values)
            if (v is JsonValue j && j.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)) return t;
        return null;
    }

    bool Old(JsonNode? o) => since is { } s && DateTimeOffset.TryParse(o?["Created"]?.ToString() ?? o?["Assembled"]?.ToString(), out var t) && t < s - settings.LookbackOrDefault;

    Rel? Known(JsonNode? r)
    {
        var id = $"{r?["Id"]}"; if (id == "") return null;
        if (releases.TryGetValue(id, out var k)) return k;
        return releases[id] = new Rel(id, $"{r?["Version"]}", ReleaseCommit(r));
    }

    /// <summary>One read of Octopus. False when the poll ended early on its call budget; an Octopus failure throws <see cref="OctopusReadException"/> after the events already published.</summary>
    public async Task<bool> Poll(CancellationToken ct = default)
    {
        calls = 0; since ??= now();
        var sp = settings.Space;
        try
        {
            if (!replayBegan) { publish(new OctopusReplayBegan()); replayBegan = true; }   // (a publish that throws is tried again by the next poll)
            projectId ??= (await Read($"api/{sp}/projects/{settings.Project}", ct))?["Id"]?.ToString();
            if (projectId == null) return true;   // no such project: nothing to watch
            var envs = new Dictionary<string, string>();
            foreach (var e in (await Read($"api/{sp}/environments?take=1000", ct))?["Items"] as JsonArray ?? []) envs[$"{e?["Id"]}"] = $"{e?["Name"]}".ToLowerInvariant();

            var releaseItems = (await Read($"api/{sp}/projects/{projectId}/releases?take={settings.ReleaseWindow}", ct))?["Items"] as JsonArray;
            var windowReleases = releaseItems == null ? null : new HashSet<string>();
            foreach (var r in (releaseItems ?? []).OrderBy(x => $"{x?["Assembled"]}"))
            {
                var rel = Known(r); if (rel == null) continue;
                windowReleases?.Add(rel.Id);
                // the build information or the notes can come after the release is first seen; the list carries both on every poll, so an unresolved commit is looked at again at no cost
                if (rel.Commit == null && ReleaseCommit(r) is { } late) releases[rel.Id] = rel = rel with { Commit = late };
                var owner = $"R:{rel.Id}"; var key = $"octopus:release:{rel.Id}"; var commitKey = $"octopus:commit:{rel.Id}";
                if (!Has(owner, key))
                {
                    if (Old(r)) { Mark(owner, key); Mark(owner, commitKey); continue; }
                    Emit(owner, key, new OctopusReleaseCreated(rel.Id, rel.Version, rel.Commit, TimeOf(r?["Assembled"])));
                }
                if (rel.Commit != null) Emit(owner, commitKey, new ReleaseCommitResolved(rel.Id, rel.Version, rel.Commit));
            }
            if (windowReleases != null) Forget("R:", windowReleases);

            var deploymentItems = (await Read($"api/{sp}/deployments?projects={projectId}&take={settings.DeploymentWindow}", ct))?["Items"] as JsonArray;
            if (deploymentItems != null)
            {
                // what left the windows is forgotten: the keys, the finished deployments and the releases read for them (a release stays while it is in the release window or a deployment of the window names it)
                var windowDeployments = deploymentItems.Select(d => $"{d?["Id"]}").ToHashSet();
                Forget("D:", windowDeployments); finished.RemoveWhere(id => !windowDeployments.Contains(id));
                if (windowReleases != null)
                {
                    var keep = deploymentItems.Select(d => $"{d?["ReleaseId"]}").Concat(windowReleases).ToHashSet();
                    foreach (var id in releases.Keys.Where(id => !keep.Contains(id)).ToList()) releases.Remove(id);
                }
            }
            var refreshed = new HashSet<string>();
            foreach (var d in (deploymentItems ?? []).OrderBy(x => $"{x?["Created"]}"))
            {
                var depId = $"{d?["Id"]}"; var taskId = $"{d?["TaskId"]}";
                if (depId == "" || taskId == "" || finished.Contains(depId)) continue;
                var task = await Read($"api/{sp}/tasks/{taskId}", ct); if (task == null) continue;   // (gone since the list was read)
                var state = $"{task["State"]}"; if (PhaseOf(state) is not { } phase) continue;
                var owner = $"D:{depId}";
                var startedKey = $"octopus:deployment:{depId}:{OctopusDeploymentPhase.Started}";
                if (phase != OctopusDeploymentPhase.Started && !Has(owner, startedKey) && Old(d)) { finished.Add(depId); continue; }   // ended before the watcher began
                var relId = $"{d?["ReleaseId"]}";
                if (!releases.TryGetValue(relId, out var rel)) rel = Known(await Read($"api/{sp}/releases/{relId}", ct));
                else if (rel.Commit == null && windowReleases?.Contains(relId) != true && refreshed.Add(relId))
                {
                    // a release outside the release window is not refreshed by the list: its commit is looked for again here, once per poll, while a deployment of it is unfinished
                    if (ReleaseCommit(await Read($"api/{sp}/releases/{relId}", ct)) is { } late) releases[relId] = rel = rel with { Commit = late };
                }
                if (rel == null) continue;
                var env = envs.GetValueOrDefault($"{d?["EnvironmentId"]}", $"{d?["EnvironmentId"]}");
                // #496: the times are Octopus's own, so a replay after a restart does not make an old deployment look as if it happened now (the deployment's Created is the last resort)
                var startedAt = TimeOf(task["StartTime"], task["QueueTime"], d?["Created"]);
                Emit(owner, startedKey, new OctopusDeploymentChanged(depId, taskId, rel.Id, rel.Version, env, OctopusDeploymentPhase.Started, rel.Commit, startedAt));
                if (phase != OctopusDeploymentPhase.Started)
                {
                    Emit(owner, $"octopus:deployment:{depId}:{phase}", new OctopusDeploymentChanged(depId, taskId, rel.Id, rel.Version, env, phase, rel.Commit, TimeOf(task["CompletedTime"], task["StartTime"], task["QueueTime"], d?["Created"])));
                    finished.Add(depId);
                }
                else if (state == "Executing")
                {
                    foreach (var i in (await Read($"api/{sp}/interruptions?regarding={taskId}&pendingOnly=true&take=20", ct))?["Items"] as JsonArray ?? [])
                        if (i?["IsPending"]?.GetValue<bool>() != false && $"{i?["Id"]}" is { Length: > 0 } iid)
                            Emit(owner, $"octopus:signoff:{iid}", new SignOffRequested(iid, depId, taskId, rel.Id, rel.Version, env, rel.Commit, TimeOf(i?["Created"])));
                }
            }
            if (!replayDone) { publish(new OctopusReplayComplete()); replayDone = true; }   // every window was read in full: the replay is applied
            return true;
        }
        catch (BudgetSpent) { return false; }
    }
}
