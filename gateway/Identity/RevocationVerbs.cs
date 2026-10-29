using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// W9.7 (#359): `revocations` and `revoke-worker` on v2, from plugin/scripts/lib/Owner.ps1 (Get-RevokedRefs, Verb_revocations, Verb_revoke_worker). The generation of a revoked name lives in GitHub as a ref beside the
// claim refs: refs/factory/<instance>/revoked/<name>/<generation> (the durable truth; a restart reads them again before it accepts any session credential: RevocationService). Nothing about who may revoke or when is
// decided here: the endpoint and RevocationService guard that, as in v1, and the verb pipeline refuses both verbs to every caller (VerbPolicy.UseEndpointReason).
static class RevocationVerbs
{
    const RegexOptions Ps = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;   // PowerShell's -match

    static string RefPath(ServiceGitHub gh) => $"factory/{gh.Name}/revoked";

    // the rows of a list answer: @(Invoke-Gh ...)
    internal static List<JsonNode?> Rows(JsonNode? body) => body switch { JsonArray a => a.ToList(), null => [], _ => [body] };

    /// <summary>Get-RevokedRefs: every ref under a prefix of the revoked tree. The read goes on until a page comes back empty, brings nothing new, or after 200 pages: GitHub's matching-refs may ignore the page parameter and
    /// answer the same refs again, and a read that never ended would keep the gateway from starting.</summary>
    static async Task<List<string>> RevokedRefs(ServiceGitHub.CallScope s, string sub)
    {
        var gh = s.Gh; var seen = new HashSet<string>(StringComparer.Ordinal); var found = new List<string>();
        for (var page = 1; page <= 200; page++)
        {
            var rows = Rows((await s.Get($"{gh.RepoPath}/git/matching-refs/{RefPath(gh)}/{sub}?per_page=100&page={page}")).Json);
            if (rows.Count == 0) break;
            var added = 0;
            foreach (var r in rows) { var name = RefOf(r); if (seen.Add(name)) { found.Add(name); added++; } }
            if (added == 0) break;
        }
        return found;
    }

    static string RefOf(JsonNode? row) => row is JsonObject o ? VerbArgs.Text(o["ref"]) : "";

    /// <summary>revocations: every revoked name with its highest generation; read once when the gateway starts.</summary>
    /// <remarks>v1 collected the names in a PowerShell hashtable, which does not tell Alice from alice: two refs of names that differ by case only gave one entry. The gateway's generations are per exact name (Revocations,
    /// ordinal), so v2 keeps both: an answer with more revoked names than v1's can only refuse a credential v1 would have let back in.</remarks>
    public static async Task<JsonObject> Revocations(ServiceGitHub.CallScope s)
    {
        var prefix = $"refs/{RefPath(s.Gh)}/";
        var revoked = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in await RevokedRefs(s, ""))
        {
            if (!r.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var m = Regex.Match(r[prefix.Length..], @"^([A-Za-z0-9_-]{1,40})/(\d{1,9})$", Ps);
            if (!m.Success) continue;
            var n = m.Groups[1].Value; var g = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (!revoked.TryGetValue(n, out var have) || have < g) revoked[n] = g;
        }
        var list = new JsonObject();
        foreach (var kv in revoked) list[kv.Key] = kv.Value;
        return new JsonObject { ["ok"] = true, ["revoked"] = list };
    }

    /// <summary>revoke-worker: records generation G for one name (idempotent) and keeps one ref per name: the new generation is written, read back, and only then are the name's lower generations removed. The reader takes the
    /// highest, so a removal that fails costs nothing but a leftover ref (the next revoke of the name removes it); a lower generation is never removed before the higher one is seen on GitHub.</summary>
    public static async Task<JsonObject> RevokeWorker(VerbArgs a, ServiceGitHub.CallScope s)
    {
        var gh = s.Gh;
        var name = VerbArgs.Text(a.Need("name")); var gen = VerbArgs.Int(a.Need("generation"));
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9_-]{1,40}$", Ps) || gen < 1) throw new VerbReject(2, "usage", "name is a worker name and generation is 1 or more");
        var head = Sha(await s.Get($"{gh.RepoPath}/git/ref/heads/{gh.DefaultBranch}"));
        // only "already exists" is an acceptable 422
        var r = await s.Call(HttpMethod.Post, $"{gh.RepoPath}/git/refs", new JsonObject { ["ref"] = $"refs/{RefPath(gh)}/{name}/{gen}", ["sha"] = head }, allow: [422]);
        if (r.Allowed && !Regex.IsMatch(Message(r), "already exists", Ps))
            throw new VerbReject(20, "github-error", "GitHub refused the revocation ref", new JsonObject { ["detail"] = Message(r), ["retryAfterSeconds"] = 30 });
        var prefix = $"refs/{RefPath(gh)}/{name}/"; var gens = new List<int>();
        foreach (var rf in await RevokedRefs(s, name + "/"))
            if (rf.StartsWith(prefix, StringComparison.Ordinal) && Regex.IsMatch(rf[prefix.Length..], @"^\d{1,9}$", Ps)) gens.Add(int.Parse(rf[prefix.Length..], CultureInfo.InvariantCulture));
        if (!gens.Contains(gen)) throw new VerbReject(20, "not-recorded", $"the revocation ref of generation {gen} is not on GitHub after the write", new JsonObject { ["retryAfterSeconds"] = 15 });
        var removed = new JsonArray(); var left = new JsonArray();
        foreach (var g in gens.Where(x => x < gen).Order())
        {
            try { await s.Call(HttpMethod.Delete, $"{gh.RepoPath}/git/refs/{RefPath(gh)}/{name}/{g}"); removed.Add(g); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                left.Add(g);
                Console.Error.WriteLine($"revoke-worker: generation {g} of '{name}' was not removed: {e.Message}");
            }
        }
        return new JsonObject { ["ok"] = true, ["name"] = name, ["generation"] = gen, ["removed"] = removed, ["left"] = left };
    }

    // (Invoke-Gh ...).object.sha : a missing member is an error of the script, which is an internal error here too
    internal static string Sha(GhResponse r) =>
        (r.Json as JsonObject)?["object"] is JsonObject o && o["sha"] is { } sha ? VerbArgs.Text(sha) : throw new InvalidOperationException("The property 'sha' cannot be found on this object. Verify that the property exists.");

    // "$($r.body.message)" of a refused write
    internal static string Message(GhResponse r) => r.Json is JsonObject o ? VerbArgs.Text(o["message"]) : "";
}
