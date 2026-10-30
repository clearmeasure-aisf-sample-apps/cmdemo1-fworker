using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// W9.7 (#359): `operator-releases` and `operator-release` on v2, from plugin/scripts/lib/OperatorRelease.ps1. A self-merged release of the operator session restarts the gateway, which keeps nothing on its own disk, so the
// releases of a day are refs in GitHub beside the claim and revocation refs: refs/factory/<instance>/operator-release/<yyyyMMdd>/<pull request>/<version>. The gateway alone calls them (the read at start,
// OperatorReleaseLoader, and POST /api/v1/operator/record-release, whose guards, daily cap and answers are unchanged); the verb pipeline refuses both to every caller (VerbPolicy.UseEndpointReason).
static class OperatorReleaseVerbs
{
    const RegexOptions Ps = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;   // PowerShell's -match

    static string RefPath(ServiceGitHub gh) => $"factory/{gh.Name}/operator-release";

    /// <summary>operator-releases: the releases recorded for one UTC day, one per pull request.</summary>
    public static async Task<JsonObject> Releases(ServiceVerbArgs a, ServiceGitHub.CallScope s)
    {
        var gh = s.Gh;
        var day = ServiceVerbArgs.Text(a.Need("day"));
        if (!Regex.IsMatch(day, @"^\d{8}$", Ps)) throw new VerbReject(2, "usage", "day is yyyyMMdd");
        var found = new JsonArray(); var seen = new HashSet<int>(); var prefix = $"refs/{RefPath(gh)}/{day}/";
        for (var page = 1; page <= 10; page++)
        {
            var rows = RevocationVerbs.Rows((await s.Get($"{gh.RepoPath}/git/matching-refs/{RefPath(gh)}/{day}/?per_page=100&page={page}")).Json);
            foreach (var row in rows)
            {
                var r = row is JsonObject o ? ServiceVerbArgs.Text(o["ref"]) : "";
                if (!r.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var m = Regex.Match(r[prefix.Length..], @"^(\d{1,9})/(\d{1,6}(\.\d{1,9}){1,3})$", Ps);
                if (!m.Success) continue;
                var pr = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (seen.Add(pr)) found.Add(new JsonObject { ["pr"] = pr, ["version"] = m.Groups[2].Value });
            }
            if (rows.Count < 100) break;
        }
        return new JsonObject { ["ok"] = true, ["day"] = day, ["releases"] = found };
    }

    /// <summary>operator-release: records one self-merged release (idempotent).</summary>
    public static async Task<JsonObject> Record(ServiceVerbArgs a, ServiceGitHub.CallScope s)
    {
        var gh = s.Gh;
        var day = ServiceVerbArgs.Text(a.Need("day")); var pr = ServiceVerbArgs.Int(a.Need("pr")); var version = ServiceVerbArgs.Text(a.Need("version"));
        if (!Regex.IsMatch(day, @"^\d{8}$", Ps) || pr < 1 || !Regex.IsMatch(version, @"^\d{1,6}(\.\d{1,9}){1,3}$", Ps))
            throw new VerbReject(2, "usage", "day is yyyyMMdd, pr a pull request number and version digits and dots");
        var head = RevocationVerbs.Sha(await s.Get($"{gh.RepoPath}/git/ref/heads/{gh.DefaultBranch}"));
        var r = await s.Call(HttpMethod.Post, $"{gh.RepoPath}/git/refs", new JsonObject { ["ref"] = $"refs/{RefPath(gh)}/{day}/{pr}/{version}", ["sha"] = head }, allow: [422]);
        if (r.Allowed && !Regex.IsMatch(RevocationVerbs.Message(r), "already exists", Ps))
            throw new VerbReject(20, "github-error", "GitHub refused the release ref", new JsonObject { ["detail"] = RevocationVerbs.Message(r), ["retryAfterSeconds"] = 30 });
        return new JsonObject { ["ok"] = true, ["day"] = day, ["pr"] = pr, ["version"] = version };
    }
}
