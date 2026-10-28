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
/// published is remembered by event key. The memory is the process's; a restart replays only what is inside the lookback, and the ingress deduplicates by the same
/// keys. Not thread safe: one caller polls.
/// </summary>
public sealed class OctopusWatcher(IOctopusReader reader, Action<IngressEvent> publish, OctopusWatchSettings settings, Func<DateTimeOffset>? clock = null)
{
    static readonly Regex Sha = new(@"\b[0-9a-fA-F]{40}\b", RegexOptions.Compiled);
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly HashSet<string> published = [];
    readonly HashSet<string> finished = [];   // deployment ids whose terminal phase is published (or that ended before the watcher began): their task is not read again
    readonly Dictionary<string, Rel> releases = [];
    DateTimeOffset? since;
    string? projectId;
    int calls;

    sealed record Rel(string Id, string Version, string? Commit);

    /// <summary>The calls the last poll made.</summary>
    public int LastCalls => calls;

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

    // a publish that throws is forgotten, so the next poll publishes the event again
    void Emit(string key, IngressEvent ev) { if (!published.Add(key)) return; try { publish(ev); } catch { published.Remove(key); throw; } }

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
            projectId ??= (await Read($"api/{sp}/projects/{settings.Project}", ct))?["Id"]?.ToString();
            if (projectId == null) return true;   // no such project: nothing to watch
            var envs = new Dictionary<string, string>();
            foreach (var e in (await Read($"api/{sp}/environments?take=1000", ct))?["Items"] as JsonArray ?? []) envs[$"{e?["Id"]}"] = $"{e?["Name"]}".ToLowerInvariant();

            foreach (var r in ((await Read($"api/{sp}/projects/{projectId}/releases?take={settings.ReleaseWindow}", ct))?["Items"] as JsonArray ?? []).OrderBy(x => $"{x?["Assembled"]}"))
            {
                var rel = Known(r); if (rel == null) continue;
                var key = $"octopus:release:{rel.Id}";
                if (published.Contains(key)) continue;
                if (Old(r)) { published.Add(key); published.Add($"octopus:commit:{rel.Id}"); continue; }
                Emit(key, new OctopusReleaseCreated(rel.Id, rel.Version, rel.Commit));
                if (rel.Commit != null) Emit($"octopus:commit:{rel.Id}", new ReleaseCommitResolved(rel.Id, rel.Version, rel.Commit));
            }

            foreach (var d in ((await Read($"api/{sp}/deployments?projects={projectId}&take={settings.DeploymentWindow}", ct))?["Items"] as JsonArray ?? []).OrderBy(x => $"{x?["Created"]}"))
            {
                var depId = $"{d?["Id"]}"; var taskId = $"{d?["TaskId"]}";
                if (depId == "" || taskId == "" || finished.Contains(depId)) continue;
                var task = await Read($"api/{sp}/tasks/{taskId}", ct); if (task == null) continue;   // (gone since the list was read)
                var state = $"{task["State"]}"; if (PhaseOf(state) is not { } phase) continue;
                var startedKey = $"octopus:deployment:{depId}:{OctopusDeploymentPhase.Started}";
                if (phase != OctopusDeploymentPhase.Started && !published.Contains(startedKey) && Old(d)) { finished.Add(depId); continue; }   // ended before the watcher began
                var relId = $"{d?["ReleaseId"]}";
                if (!releases.TryGetValue(relId, out var rel)) rel = Known(await Read($"api/{sp}/releases/{relId}", ct));
                if (rel == null) continue;
                var env = envs.GetValueOrDefault($"{d?["EnvironmentId"]}", $"{d?["EnvironmentId"]}");
                Emit(startedKey, new OctopusDeploymentChanged(depId, taskId, rel.Id, rel.Version, env, OctopusDeploymentPhase.Started, rel.Commit));
                if (phase != OctopusDeploymentPhase.Started)
                {
                    Emit($"octopus:deployment:{depId}:{phase}", new OctopusDeploymentChanged(depId, taskId, rel.Id, rel.Version, env, phase, rel.Commit));
                    finished.Add(depId);
                }
                else if (state == "Executing")
                {
                    foreach (var i in (await Read($"api/{sp}/interruptions?regarding={taskId}&pendingOnly=true&take=20", ct))?["Items"] as JsonArray ?? [])
                        if (i?["IsPending"]?.GetValue<bool>() != false && $"{i?["Id"]}" is { Length: > 0 } iid)
                            Emit($"octopus:signoff:{iid}", new SignOffRequested(iid, depId, taskId, rel.Id, rel.Version, env, rel.Commit));
                }
            }
            return true;
        }
        catch (BudgetSpent) { return false; }
    }
}
