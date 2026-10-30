using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.ReadModels;

/// <summary>
/// The read side of the Octopus API the delivery reads need: GET only, with the server's key. A 404 is null; no key is exit 30 secret-missing; any other failure is exit 20 octopus-error (<c>Invoke-Octo</c>). The gateway implements it.
/// </summary>
public interface IOctopusApi
{
    Task<JsonNode?> Get(string path, CancellationToken ct);
}

/// <summary>
/// W9.5: <c>get-delivery</c> (Verbs.Delivery.ps1): has the change of an item reached the environments? It finds the release candidate of a commit in Octopus (the release built from it, else the newest release when that one contains the commit),
/// and where its deployments stand per environment. Read only: GET against Octopus and GitHub's compare.
/// </summary>
public sealed class DeliveryReadModel(ItemReads reads, IOctopusApi octopus)
{
    RuleGitHub Gh => reads.Gh;

    /// <summary><c>Get-ReleaseCommit</c>: the commit a release was built from, lower case: the build information's VCS commit, else the first 40 hex digits in the release notes.</summary>
    public static string? ReleaseCommit(JsonNode release)
    {
        foreach (var b in Ps.List(release, "BuildInformation"))
            if (Ps.Str(b, "VcsCommitNumber") is { } c && Regex.IsMatch(c, "^[0-9a-fA-F]{40}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return c.ToLowerInvariant();
        var m = Regex.Match(Ps.Str(release, "ReleaseNotes"), @"\b[0-9a-fA-F]{40}\b", RegexOptions.CultureInvariant);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary><c>Get-Delivery</c>.</summary>
    public async Task<JsonObject> Delivery(string sha, CancellationToken ct)
    {
        var o = Gh.Settings.Octopus; var sp = o.Space;
        var proj = await octopus.Get($"api/{sp}/projects/{o.Project}", ct) ?? throw new VerbRefusal(12, "octopus-project-not-found", $"project '{o.Project}' not found in {sp}");
        var projId = Ps.Str(proj, "Id");
        var rels = Ps.List(await octopus.Get($"api/{sp}/projects/{projId}/releases?take=30", ct) ?? throw new NullReferenceException("the releases of the project were not found"), "Items").ToList();
        JsonNode? cand = rels.FirstOrDefault(r => ReleaseCommit(r) == sha.ToLowerInvariant());
        if (cand is null && rels.Count > 0)
        {
            var newest = rels.OrderByDescending(r => Ps.FromIso(Ps.Str(r, "Assembled")) ?? DateTime.MinValue).First();
            var nc = ReleaseCommit(newest);
            if (nc is not null)
            {
                var cmp = await Gh.Allow("GET", $"{Gh.RepoPath}/compare/{sha}...{nc}", [404, 422], null, ct);
                if (!cmp.Allowed && Ps.Str(cmp.Body, "status") is "ahead" or "identical") cand = newest;
            }
        }
        var res = new JsonObject { ["candidate"] = null, ["deployments"] = new JsonObject(), ["prod_success"] = false };
        if (cand is null) return res;
        res["candidate"] = new JsonObject { ["version"] = Ps.Prop(cand, "Version")?.DeepClone(), ["release_id"] = Ps.Prop(cand, "Id")?.DeepClone(), ["commit"] = ReleaseCommit(cand), ["assembled"] = Time(cand, "Assembled") };
        var envs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Ps.List(await octopus.Get($"api/{sp}/environments?take=1000", ct), "Items")) envs[Ps.Str(e, "Id")] = Ps.Str(e, "Name");
        var deps = Ps.List(await octopus.Get($"api/{sp}/deployments?projects={projId}&take=30", ct), "Items")
            .Where(d => Ps.Str(d, "ReleaseId").Equals(Ps.Str(cand, "Id"), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => Ps.FromIso(Ps.Str(d, "Created")) ?? DateTime.MinValue).ToList();
        var deployments = (JsonObject)res["deployments"]!;
        foreach (var d in deps)
        {
            if (!envs.TryGetValue(Ps.Str(d, "EnvironmentId"), out var name) || name.Length == 0 || deployments.Any(kv => Ps.Ieq(kv.Key, name))) continue;
            var task = await octopus.Get($"api/{sp}/tasks/{Ps.Str(d, "TaskId")}", ct);
            deployments[name] = new JsonObject { ["state"] = Ps.Str(task, "State"), ["completed"] = Ps.Bool(Ps.Prop(task, "IsCompleted")), ["at"] = Time(d, "Created"), ["deployment"] = Ps.Prop(d, "Id")?.DeepClone() };
        }
        foreach (var kv in deployments)
            if (Ps.Ieq(kv.Key, o.ProdEnvironment) && Ps.Str(kv.Value, "state").Equals("Success", StringComparison.OrdinalIgnoreCase)) res["prod_success"] = true;
        return res;
    }

    // a time PowerShell read from JSON and writes back as a local time with its offset
    static JsonNode? Time(JsonNode? node, string name) => Ps.Prop(node, name) is { } t ? Ps.JsonTime(Ps.Text(t)) : null;

    /// <summary><c>Verb_get_delivery</c>: of the commit named, or of the merge commit the item's state records.</summary>
    public async Task<JsonObject> GetDelivery(VerbArgs a, CancellationToken ct)
    {
        string sha;
        if (a.Has("commit")) sha = a.Text("commit");
        else
        {
            var state = await reads.State(a.ItemNumber(), ct);
            if (Ps.Prop(state, "merge") is not { } merge) throw new VerbRefusal(10, "not-merged", "the item has no merge commit yet");
            sha = Ps.Str(merge, "sha");
        }
        var d = await Delivery(sha, ct);
        d["ok"] = true; d["commit"] = sha;
        return d;
    }
}
