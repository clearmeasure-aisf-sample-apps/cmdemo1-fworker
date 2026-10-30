using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.Delivery.Verbs;

/// <summary>What Get-Delivery found for a commit: the release candidate that carries it (null when none), its deployments by environment name, and whether prod has it.</summary>
public sealed class DeliveryOfCommit
{
    public JsonObject? Candidate { get; set; }
    public JsonObject Deployments { get; } = new();
    public bool ProdSuccess { get; set; }

    public JsonObject ToJson() => J.Obj(("candidate", Candidate), ("deployments", Deployments), ("prod_success", ProdSuccess));
}

// W9.4: the Octopus reads of the delivery verbs (Verbs.Delivery.ps1: Get-ReleaseCommit, Get-Delivery, Get-ProdContaining), GET only. The release that carries a commit is the one built from it, else the newest release
// when the commit is an ancestor of it (releases are cumulative on master).
public static class OctopusDelivery
{
    static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static DateTimeOffset Time(JsonNode? n) => DateTimeOffset.TryParse(J.S(n), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : DateTimeOffset.MinValue;

    /// <summary>Get-ReleaseCommit: the build information's commit first, then a sha in the release notes; lower case, null when there is none.</summary>
    public static string? ReleaseCommit(JsonNode? release)
    {
        if (release is JsonObject r && r["BuildInformation"] is JsonArray bi && bi.Count > 0)
            foreach (var b in bi)
                if (Regex.IsMatch(J.S(b, "VcsCommitNumber"), "^[0-9a-fA-F]{40}$")) return J.S(b, "VcsCommitNumber").ToLowerInvariant();
        var m = Regex.Match(J.S(release, "ReleaseNotes"), @"\b[0-9a-fA-F]{40}\b");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    static async Task<bool> IsAncestorOf(RuleKit kit, string sha, string later, CancellationToken ct)
    {
        var cmp = await GhApi.Get(kit.Gh, $"{kit.RepoPath}/compare/{sha}...{later}", ct, 404, 422);
        return cmp.Ok && J.S(cmp.Body, "status") is "ahead" or "identical";
    }

    /// <summary>Get-Delivery.</summary>
    public static async Task<DeliveryOfCommit> Get(RuleKit kit, string sha, CancellationToken ct)
    {
        var o = J.Obj(kit.Cfg, "octopus"); var sp = J.S(o, "space");
        var proj = await kit.Gh.OctopusGet($"api/{sp}/projects/{J.S(o, "project")}", ct)
            ?? throw new VerbReject(12, "octopus-project-not-found", $"project '{J.S(o, "project")}' not found in {sp}");
        var rels = J.Items((await kit.Gh.OctopusGet($"api/{sp}/projects/{J.S(proj, "Id")}/releases?take=30", ct))?["Items"]).Where(x => x is not null).Select(x => x!).ToList();
        var lower = sha.ToLowerInvariant();
        JsonNode? cand = rels.FirstOrDefault(r => ReleaseCommit(r) == lower);
        if (cand is null && rels.Count > 0)
        {
            var newest = rels.OrderByDescending(r => Time(r["Assembled"])).First();
            if (ReleaseCommit(newest) is { } nc && await IsAncestorOf(kit, sha, nc, ct)) cand = newest;
        }
        var res = new DeliveryOfCommit();
        if (cand is null) return res;
        res.Candidate = J.Obj(("version", J.S(cand, "Version")), ("release_id", J.S(cand, "Id")), ("commit", ReleaseCommit(cand)), ("assembled", J.S(cand, "Assembled")));
        var envs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in J.Items((await kit.Gh.OctopusGet($"api/{sp}/environments?take=1000", ct))?["Items"])) envs[J.S(e, "Id")] = J.S(e, "Name");
        var deps = J.Items((await kit.Gh.OctopusGet($"api/{sp}/deployments?projects={J.S(proj, "Id")}&take=30", ct))?["Items"])
            .Where(d => d is not null && Eq(J.S(d, "ReleaseId"), J.S(cand, "Id"))).OrderByDescending(d => Time(d!["Created"])).ToList();
        foreach (var d in deps)
        {
            if (!envs.TryGetValue(J.S(d, "EnvironmentId"), out var name) || name.Length == 0 || res.Deployments.ContainsKey(name)) continue;
            var task = await kit.Gh.OctopusGet($"api/{sp}/tasks/{J.S(d, "TaskId")}", ct);
            res.Deployments[name] = J.Obj(("state", J.S(task, "State")), ("completed", J.B(task, "IsCompleted")), ("at", J.S(d, "Created")), ("deployment", J.S(d, "Id")));
        }
        var prodName = J.S(J.Obj(o, "environments"), "prod");
        foreach (var kv in res.Deployments) if (Eq(kv.Key, prodName) && J.S(kv.Value, "state") == "Success") res.ProdSuccess = true;
        return res;
    }

    /// <summary>Get-ProdContaining: the newest release to prod that contains the change (releases are cumulative on master), or null.</summary>
    public static async Task<string?> ProdContaining(RuleKit kit, string sha, CancellationToken ct)
    {
        var o = J.Obj(kit.Cfg, "octopus"); var sp = J.S(o, "space");
        var proj = await kit.Gh.OctopusGet($"api/{sp}/projects/{J.S(o, "project")}", ct);
        if (proj is null) return null;
        var envs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in J.Items((await kit.Gh.OctopusGet($"api/{sp}/environments?take=1000", ct))?["Items"])) envs[J.S(e, "Id")] = J.S(e, "Name");
        var prodName = J.S(J.Obj(o, "environments"), "prod");
        foreach (var d in J.Items((await kit.Gh.OctopusGet($"api/{sp}/deployments?projects={J.S(proj, "Id")}&take=30", ct))?["Items"]).ToList())
        {
            if (!envs.TryGetValue(J.S(d, "EnvironmentId"), out var env) || env.Length == 0 || !Eq(env, prodName)) continue;
            var task = await kit.Gh.OctopusGet($"api/{sp}/tasks/{J.S(d, "TaskId")}", ct);
            if (task is null || J.S(task, "State") != "Success") continue;
            var rel = await kit.Gh.OctopusGet($"api/{sp}/releases/{J.S(d, "ReleaseId")}", ct);
            if (rel is null) continue;
            var c = ReleaseCommit(rel);
            if (c is null) continue;
            if (c == sha.ToLowerInvariant()) return J.S(rel, "Version");
            if (await IsAncestorOf(kit, sha, c, ct)) return J.S(rel, "Version");
        }
        return null;
    }
}
