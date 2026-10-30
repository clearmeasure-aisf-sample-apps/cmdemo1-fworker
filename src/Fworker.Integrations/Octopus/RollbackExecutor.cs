using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Commands;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.Octopus;

// W8.3: the executor of the delivery train's rollback in tdd. It is the only place where the gateway creates a deployment in tdd, by the owner's decision of 2026-10-29: the incident rollback of the app in tdd,
// automatic, tdd only. Nothing else about it is generous:
//   - the environment is checked here as well as in the train: every environment but tdd is refused for good (uat and prod are not configured), whatever the command says;
//   - it runs on the W3.0 command base, so a replay of a settled key answers with the recorded result and the deployment happens once;
//   - it reads Octopus just before it writes: a deployment is created only when the newest deployment of the project to the environment is not already of that release (a replay after a crash between the
//     create and the ledger, a restart, a second gateway), so a replay never deploys twice, and the newest deployment being of the release is a success;
//   - only the leader writes (MayAct), as the promoter does.
// The Octopus calls are those of Verbs.Promote.ps1's deployment create (the Executions API route for an untenanted project). The key is the runbook service account's (OCTOPUS_RUNBOOK_API_KEY), else the reader key.

/// <summary>What the executor needs of the configuration: the Octopus space, the Octopus name of an environment key and the key that may write.</summary>
public sealed record RollbackSettings(string Space, IReadOnlyDictionary<string, string> EnvironmentNames, Func<string?> ApiKey, Func<bool>? MayAct = null)
{
    public string NameOf(string environment) => EnvironmentNames.TryGetValue(environment, out var n) && n.Length > 0 ? n : environment;
}

public sealed class RollbackExecutor(HttpClient http, RollbackSettings settings)
{
    /// <summary>The one environment the executor deploys to.</summary>
    public const string AutomaticEnvironment = "tdd";

    static string PermissionOf(JsonNode? body)
    {
        var m = Regex.Match(body?["ErrorMessage"]?.ToString() ?? "", "Missing permission: ([A-Za-z]{1,60})");
        return m.Success ? m.Groups[1].Value : "";
    }

    async Task<(int Status, JsonNode? Body)> Call(HttpMethod method, string path, JsonNode? body, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path.TrimStart('/'));
        req.Headers.TryAddWithoutValidation("X-Octopus-ApiKey", key);
        req.Headers.UserAgent.ParseAdd("factory-worker-gateway");
        if (method != HttpMethod.Get) req.Content = new StringContent(body is null ? "{}" : body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var resp = await http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            JsonNode? parsed = null;
            try { parsed = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { }
            return ((int)resp.StatusCode, parsed);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return (0, null); }
    }

    static CommandResult Refused(string what, int status, JsonNode? body)
    {
        var perm = PermissionOf(body);
        if (status == 403) return CommandResult.Failed(FailureKind.Permanent, $"Octopus refused {what} (403{(perm.Length > 0 ? ", missing permission " + perm : "")}); the owner grants it to the gateway's Octopus account");
        if (status == 429) return CommandResult.Failed(FailureKind.RateLimited, $"Octopus answered 429 to {what}");
        if (status is 400 or 401 or 404 or 405) return CommandResult.Failed(FailureKind.Permanent, $"Octopus answered {status} to {what}");
        return CommandResult.Failed(FailureKind.Transient, $"Octopus answered {status} to {what}");   // the gateway tries again
    }

    /// <summary>RollbackRelease: the deployment of the last good release to tdd, unless tdd's newest deployment already is of it.</summary>
    public async Task<CommandResult> Rollback(RollbackRelease c, CancellationToken ct)
    {
        if (!string.Equals(c.Environment, AutomaticEnvironment, StringComparison.OrdinalIgnoreCase))
            return CommandResult.Failed(FailureKind.Permanent, $"automatic rollback is configured for {AutomaticEnvironment} only; {c.Environment} is a person's decision");
        var key = settings.ApiKey();
        if (string.IsNullOrEmpty(key)) return CommandResult.Failed(FailureKind.Permanent, "neither OCTOPUS_RUNBOOK_API_KEY nor OCTOPUS_API_KEY is set on the gateway");
        var space = settings.Space; var envName = settings.NameOf(AutomaticEnvironment);
        var (ps, proj) = await Call(HttpMethod.Get, $"api/{space}/projects/{c.Project}", null, key, ct);
        if (ps == 404) return CommandResult.Failed(FailureKind.Permanent, $"project '{c.Project}' not found in {space}");
        if (ps is < 200 or >= 300) return Refused($"GET /api/{space}/projects/{c.Project}", ps, proj);
        var projectId = proj?["Id"]?.ToString() ?? "";
        var (rs, rel) = await Call(HttpMethod.Get, $"api/{space}/projects/{projectId}/releases/{c.Version}", null, key, ct);
        if (rs == 404) return CommandResult.Failed(FailureKind.Permanent, $"release {c.Version} of {c.Project} is not in Octopus");
        if (rs is < 200 or >= 300) return Refused($"GET release {c.Version}", rs, rel);
        var (es, envs) = await Call(HttpMethod.Get, $"api/{space}/environments?take=1000", null, key, ct);
        if (es is < 200 or >= 300) return Refused("GET environments", es, envs);
        string? envId = null;
        foreach (var e in envs?["Items"] as JsonArray ?? [])
            if (string.Equals(e?["Name"]?.ToString(), envName, StringComparison.OrdinalIgnoreCase)) envId = e?["Id"]?.ToString();
        if (string.IsNullOrEmpty(envId)) return CommandResult.Failed(FailureKind.Permanent, $"environment '{envName}' not found in {space}");
        // read just before: the newest deployment of the project to tdd. If it is of this release, the rollback is there already (a replay, a second gateway)
        var (ds, deps) = await Call(HttpMethod.Get, $"api/{space}/deployments?projects={projectId}&environments={envId}&take=1", null, key, ct);
        if (ds is < 200 or >= 300) return Refused("GET deployments", ds, deps);
        if ((deps?["Items"] as JsonArray)?.FirstOrDefault() is { } newest && string.Equals(newest["ReleaseId"]?.ToString(), rel?["Id"]?.ToString(), StringComparison.Ordinal))
            return CommandResult.Success(newest["TaskId"]?.ToString() ?? "");
        if (settings.MayAct is { } may && !may()) return CommandResult.Failed(FailureKind.Transient, "this gateway no longer holds the leader lease; the rollback deployment was not created");
        var createPath = $"api/{space}/deployments/create/untenanted/v1";
        var (cs, created) = await Call(HttpMethod.Post, createPath,
            new JsonObject { ["SpaceIdOrName"] = space, ["ProjectName"] = c.Project, ["ReleaseVersion"] = c.Version, ["EnvironmentNames"] = new JsonArray(envName) }, key, ct);
        if (cs is 200 or 201)
        {
            var task = created?["DeploymentServerTasks"] is JsonArray { Count: > 0 } a ? a[0] : null;
            if (task is null) return CommandResult.Failed(FailureKind.Transient, $"Octopus answered {cs} to POST /{createPath} without a deployment task");
            return CommandResult.Success(task["ServerTaskId"]?.ToString() ?? "");
        }
        var em = created?["ErrorMessage"]?.ToString() ?? "";
        if (em.Length > 200) em = em[..200];
        if (cs == 403) return Refused("to create the rollback deployment", cs, created);
        return CommandResult.Failed(cs == 429 ? FailureKind.RateLimited : cs is 400 or 404 or 422 ? FailureKind.Permanent : FailureKind.Transient,
            $"Octopus answered {cs} to POST /{createPath}{(em.Length > 0 ? ": " + em : "")}");
    }
}

public sealed class RollbackReleaseHandler(IIdempotencyLedger ledger, RollbackExecutor executor) : IntegrationCommandHandler<RollbackRelease>(ledger)
{
    protected override Task<CommandResult> Execute(RollbackRelease command, CancellationToken ct) => executor.Rollback(command, ct);
}
