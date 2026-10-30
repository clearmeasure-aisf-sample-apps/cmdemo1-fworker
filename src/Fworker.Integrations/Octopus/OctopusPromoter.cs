using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Commands;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.Octopus;

// W9.4 (#356): the executor of the delivery train's two commands for the environments where the engine acts (the cutover, W9.9, moves tdd to it): PromoteRelease creates the deployment of a release to an environment
// and AnswerSignOff answers the Sign-off step (a manual intervention) of a deployment the gateway created. Ported from Verbs.Promote.ps1 (Invoke-OctoPost, Get-ReleaseDeployments, the create call of Invoke-AutoPromoteInner,
// Resolve-SignOff) with the same Octopus calls: the Executions API route for an untenanted project, `responsible` by PUT (POST answers 405) and `submit` by POST. A deployment is created only when Octopus lists
// none of that release to that environment (read just before), so a replay of the command, a restart or a second gateway never promotes twice; an interruption that is no longer pending is answered already. The key is the runbook service
// account's (OCTOPUS_RUNBOOK_API_KEY), else the reader key.

/// <summary>What the executor needs of the configuration: the Octopus space, the Octopus name of an environment key (uat, prod) and the key that may write.</summary>
public sealed record OctopusPromoteSettings(string Space, IReadOnlyDictionary<string, string> EnvironmentNames, Func<string?> ApiKey, Func<bool>? MayAct = null)
{
    public string NameOf(string environment) => EnvironmentNames.TryGetValue(environment, out var n) && n.Length > 0 ? n : environment;
}

public sealed class OctopusPromoter(HttpClient http, OctopusPromoteSettings settings)
{
    static string PermissionOf(JsonNode? body)
    {
        var m = Regex.Match(body?["ErrorMessage"]?.ToString() ?? "", "Missing permission: ([A-Za-z]{1,60})");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Invoke-OctoPost / Invoke-Octo: the status (0 when the call did not get an answer) and the body.</summary>
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

    /// <summary>PromoteRelease: the deployment of the release to the environment, unless Octopus already lists one.</summary>
    public async Task<CommandResult> Promote(PromoteRelease c, CancellationToken ct)
    {
        var key = settings.ApiKey();
        if (string.IsNullOrEmpty(key)) return CommandResult.Failed(FailureKind.Permanent, "neither OCTOPUS_RUNBOOK_API_KEY nor OCTOPUS_API_KEY is set on the gateway");
        var space = settings.Space; var envName = settings.NameOf(c.Environment);
        // read just before: a deployment of this release to the target that is there (another sweep, a person, an earlier run of this command) is never doubled
        var (ps, proj) = await Call(HttpMethod.Get, $"api/{space}/projects/{c.Project}", null, key, ct);
        if (ps == 404) return CommandResult.Failed(FailureKind.Permanent, $"project '{c.Project}' not found in {space}");
        if (ps is < 200 or >= 300) return Refused($"GET /api/{space}/projects/{c.Project}", ps, proj);
        var (rs, rel) = await Call(HttpMethod.Get, $"api/{space}/projects/{proj?["Id"]}/releases/{c.Version}", null, key, ct);
        if (rs == 404) return CommandResult.Failed(FailureKind.Permanent, $"release {c.Version} of {c.Project} is not in Octopus");
        if (rs is < 200 or >= 300) return Refused($"GET release {c.Version}", rs, rel);
        var (es, envs) = await Call(HttpMethod.Get, $"api/{space}/environments?take=1000", null, key, ct);
        if (es is < 200 or >= 300) return Refused("GET environments", es, envs);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in envs?["Items"] as JsonArray ?? []) names[e?["Id"]?.ToString() ?? ""] = e?["Name"]?.ToString() ?? "";
        var (ds, deps) = await Call(HttpMethod.Get, $"api/{space}/releases/{rel?["Id"]}/deployments?take=100", null, key, ct);
        if (ds is < 200 or >= 300) return Refused($"GET deployments of release {c.Version}", ds, deps);
        foreach (var d in deps?["Items"] as JsonArray ?? [])
            if (names.TryGetValue(d?["EnvironmentId"]?.ToString() ?? "", out var n) && string.Equals(n, envName, StringComparison.OrdinalIgnoreCase))
                return CommandResult.Success(d?["TaskId"]?.ToString() ?? "");
        if (settings.MayAct is { } may && !may()) return CommandResult.Failed(FailureKind.Transient, "this gateway no longer holds the leader lease; the deployment was not created");
        // the Executions API route this server advertises for an untenanted project (ExecutionsCreateApiDeploymentCreateV1); deployments/create/v1 is 404
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
        if (cs == 403) return Refused("to create the deployment", cs, created);
        return CommandResult.Failed(cs == 429 ? FailureKind.RateLimited : FailureKind.Transient, $"Octopus answered {cs} to POST /{createPath}{(em.Length > 0 ? ": " + em : "")}; the gateway tries again");
    }

    /// <summary>AnswerSignOff: takes responsibility for the interruption (PUT) and submits Proceed or Abort; an interruption that is not pending any more is answered already.</summary>
    public async Task<CommandResult> Answer(AnswerSignOff c, CancellationToken ct)
    {
        var key = settings.ApiKey();
        if (string.IsNullOrEmpty(key)) return CommandResult.Failed(FailureKind.Permanent, "neither OCTOPUS_RUNBOOK_API_KEY nor OCTOPUS_API_KEY is set on the gateway");
        var space = settings.Space;
        var (ls, list) = await Call(HttpMethod.Get, $"api/{space}/interruptions?regarding={c.TaskId}&pendingOnly=true&take=20", null, key, ct);
        if (ls is < 200 or >= 300) return Refused("GET interruptions", ls, list);
        var pending = (list?["Items"] as JsonArray ?? []).Where(i => i is not null && string.Equals(i["Id"]?.ToString(), c.InterruptionId, StringComparison.OrdinalIgnoreCase) && i["IsPending"]?.GetValue<bool>() == true).ToList();
        if (pending.Count == 0) return CommandResult.Success();
        if (settings.MayAct is { } may && !may()) return CommandResult.Failed(FailureKind.Transient, "this gateway no longer holds the leader lease; the Sign-off was not answered");
        var id = c.InterruptionId;
        var (rs, r) = await Call(HttpMethod.Put, $"api/{space}/interruptions/{id}/responsible", null, key, ct);   // Octopus takes responsibility by PUT only (POST answers 405)
        if (rs is not (200 or 201)) return Refused($"to take responsibility for the Sign-off of task {c.TaskId}", rs, r);
        var (ss, s) = await Call(HttpMethod.Post, $"api/{space}/interruptions/{id}/submit",
            new JsonObject { ["Result"] = c.Proceed ? "Proceed" : "Abort", ["Notes"] = c.Notes, ["Instructions"] = null }, key, ct);
        if (ss is not (200 or 201)) return Refused($"to answer the Sign-off of task {c.TaskId}", ss, s);
        return CommandResult.Success();
    }
}

public sealed class PromoteReleaseHandler(IIdempotencyLedger ledger, OctopusPromoter promoter) : IntegrationCommandHandler<PromoteRelease>(ledger)
{
    protected override Task<CommandResult> Execute(PromoteRelease command, CancellationToken ct) => promoter.Promote(command, ct);
}

public sealed class AnswerSignOffHandler(IIdempotencyLedger ledger, OctopusPromoter promoter) : IntegrationCommandHandler<AnswerSignOff>(ledger)
{
    protected override Task<CommandResult> Execute(AnswerSignOff command, CancellationToken ct) => promoter.Answer(command, ct);
}
