using System.Text.Json.Nodes;

namespace Fworker.Integrations.Octopus;

/// <summary>A deployment of the project waits at a manual intervention (the Sign-off step): the environment it deploys to (the Octopus name, lower case), the interruption and the release.</summary>
public sealed record PendingInterruption(string Environment, string InterruptionId, string DeploymentId, string TaskId, string ReleaseId, string Version, DateTimeOffset? Created);

/// <summary>
/// W9.9b: the Sign-offs Octopus shows now, for the adoption at the start of the engine. It reads what the watcher reads for them (the project, the environments, the project's newest deployments, the task of each and, for a task
/// that is executing, its pending interruptions and its release) and nothing else, and only with GET. A read that Octopus refuses throws <see cref="OctopusReadException"/> (the adoption is tried again); a deployment whose task
/// is gone is left out. At most <c>DeploymentWindow</c> deployments are looked at, as the watcher's window is.
/// </summary>
public sealed class PendingSignOffReader(IOctopusReader reader, string space, string project, int deploymentWindow = 30)
{
    public async Task<IReadOnlyList<PendingInterruption>> Read(CancellationToken ct = default)
    {
        var found = new List<PendingInterruption>();
        var projectId = (await reader.Get($"api/{space}/projects/{project}", ct))?["Id"]?.ToString();
        if (string.IsNullOrEmpty(projectId)) return found;
        var envs = new Dictionary<string, string>();
        foreach (var e in (await reader.Get($"api/{space}/environments?take=1000", ct))?["Items"] as JsonArray ?? []) envs[$"{e?["Id"]}"] = $"{e?["Name"]}".ToLowerInvariant();
        var versions = new Dictionary<string, string>();
        foreach (var d in (await reader.Get($"api/{space}/deployments?projects={projectId}&take={deploymentWindow}", ct))?["Items"] as JsonArray ?? [])
        {
            var depId = $"{d?["Id"]}"; var taskId = $"{d?["TaskId"]}";
            if (depId == "" || taskId == "") continue;
            var task = await reader.Get($"api/{space}/tasks/{taskId}", ct);
            if ($"{task?["State"]}" != "Executing") continue;   // (a Sign-off is pending only while the deployment executes)
            var interruptions = (await reader.Get($"api/{space}/interruptions?regarding={taskId}&pendingOnly=true&take=20", ct))?["Items"] as JsonArray ?? [];
            var pending = interruptions.Where(i => i?["IsPending"]?.GetValue<bool>() != false && $"{i?["Id"]}" is { Length: > 0 }).ToList();
            if (pending.Count == 0) continue;
            var releaseId = $"{d?["ReleaseId"]}";
            if (!versions.TryGetValue(releaseId, out var version)) versions[releaseId] = version = $"{(await reader.Get($"api/{space}/releases/{releaseId}", ct))?["Version"]}";
            var env = envs.GetValueOrDefault($"{d?["EnvironmentId"]}", $"{d?["EnvironmentId"]}");
            foreach (var i in pending) found.Add(new PendingInterruption(env, $"{i!["Id"]}", depId, taskId, releaseId, version, OctopusWatcher.TimeOf(i["Created"])));
        }
        return found;
    }
}
