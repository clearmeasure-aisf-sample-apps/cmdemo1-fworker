using System.Text.Json.Nodes;

namespace Fworker.Workflows.ReadModels;

/// <summary>
/// W9.5: <c>check-secrets</c> (Verbs.Sweep.ps1): read-only probes of everything the gateway needs from outside, with names and pass or fail only, never a value: the GitHub token (who it is), access to the repository, the project and its columns, the
/// worker label, the webhook secret and the Octopus key and project. Exit 0 with the list when all pass; exit 30 secrets-check-failed with the list when one fails. Only the gateway's own loops (and the admin) start it.
/// </summary>
public sealed class SecretsReadModel(ItemReads reads, IOctopusApi octopus)
{
    RuleGitHub Gh => reads.Gh;

    const string ProjectQuery = "query($o:String!,$n:Int!){ organization(login:$o){ projectV2(number:$n){ id fields(first:30){ nodes{ ... on ProjectV2SingleSelectField{ id name options{ id name } } } } } } }";

    /// <summary><c>Get-ProjectMeta</c>: the options of the board's Status field, by name.</summary>
    async Task<List<string>> StatusOptions(CancellationToken ct)
    {
        var s = Gh.Settings;
        var d = await Gh.GraphQL(ProjectQuery, new JsonObject { ["o"] = s.Owner, ["n"] = s.Project }, ct);
        var p = Ps.At(d, "organization", "projectV2") ?? throw new InvalidOperationException("the project was not found");
        var f = Ps.List(Ps.Prop(p, "fields"), "nodes").FirstOrDefault(n => Ps.Prop(n, "name") is not null && Ps.Str(n, "name").Equals("Status", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("the project has no Status field");
        return Ps.List(f, "options").Select(o => Ps.Str(o, "name")).ToList();
    }

    /// <summary><c>Verb_check_secrets</c>.</summary>
    public async Task<JsonObject> CheckSecrets(CancellationToken ct)
    {
        var c = Gh.Settings;
        var checks = new List<(string Name, bool Ok, string Detail)>();
        void Add(string name, bool ok, string detail) => checks.Add((name, ok, detail));
        async Task Probe(string name, string failure, Func<Task> body)
        {
            try { await body(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { Add(name, false, failure); }
        }

        await Probe("github-token", "token missing or rejected", async () => Add("github-token", true, $"authenticated as {await reads.ServerLogin(ct)}"));
        await Probe("repo-access", "repository not readable", async () =>
        {
            var perm = Ps.Prop(await Gh.Get(Gh.RepoPath, ct), "permissions") ?? throw new InvalidOperationException("no permissions");
            Add("repo-access", Ps.Bool(Ps.Prop(perm, "push")), $"push={Ps.Text(Ps.Prop(perm, "push"))} admin={Ps.Text(Ps.Prop(perm, "admin"))}");
        });
        await Probe("project-read", "project not readable", async () => { var options = await StatusOptions(ct); Add("project-read", options.Count > 0, $"Status options: {options.Count}"); });
        await Probe("board-columns", "unreadable", async () =>
        {
            var options = await StatusOptions(ct);
            var missing = c.AiColumns.Concat(c.HoldColumns).Concat(c.DoneColumns).Where(col => !Ps.NameIn(col, options)).ToList();
            Add("board-columns", missing.Count == 0, missing.Count > 0 ? $"missing: {string.Join(", ", missing)}" : "all expected columns present");
        });
        await Probe("worker-label", "labels unreadable", async () =>
        {
            var labels = Ps.Items(await Gh.Get($"{Gh.RepoPath}/labels?per_page=100", ct)).Select(l => Ps.Str(l, "name")).ToList();
            var exists = Ps.NameIn(c.WorkerLabel, labels);
            Add("worker-label", exists, $"label '{c.WorkerLabel}' {(exists ? "exists" : "is missing")}");
        });
        var ws = c.Env("FACTORY_WEBHOOK_SECRET");
        Add("webhook-secret", !string.IsNullOrEmpty(ws) && ws.Length >= 32, string.IsNullOrEmpty(ws) ? "not set" : $"set, {ws.Length} chars");
        if (!string.IsNullOrEmpty(c.Env("OCTOPUS_API_KEY")))
        {
            await Probe("octopus-key", "rejected or unreachable", async () =>
            {
                var me = await octopus.Get("api/users/me", ct);
                Add("octopus-key", me is not null, "authenticated");
                var proj = await octopus.Get($"api/{c.Octopus.Space}/projects/{c.Octopus.Project}", ct);
                Add("octopus-project", proj is not null, $"project {c.Octopus.Project}");
            });
        }
        else Add("octopus-key", false, "not set");
        var list = new JsonArray();
        foreach (var k in checks) list.Add(new JsonObject { ["name"] = k.Name, ["ok"] = k.Ok, ["detail"] = k.Detail });
        var bad = checks.Count(k => !k.Ok);
        if (bad > 0) throw new VerbRefusal(30, "secrets-check-failed", $"{bad} check(s) failed", new JsonObject { ["checks"] = list });
        return new JsonObject { ["ok"] = true, ["checks"] = list };
    }
}
