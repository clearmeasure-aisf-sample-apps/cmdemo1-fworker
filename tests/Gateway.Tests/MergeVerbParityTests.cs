using System.Diagnostics;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.3 (#355): the parity of the merge train verbs on v2 with the rule scripts. The same cases run twice: the REAL scripts (Verbs.Merge.ps1, Verbs.Inspect.ps1, BaseUpdate.ps1) under PowerShell with a scripted GitHub and a
/// scripted item state (parity/merge-verbs-parity.ps1), and the C# verbs with the saga state that is the same situation. The answers must be the same JSON: every field, the exit code, and the texts of the refusals. A case
/// is one situation: its `v1` half is what the state comment, the labels and the card say, its `saga` half what the WorkItemSaga holds for it. The test returns at once where there is no pwsh.
/// </summary>
public sealed class MergeVerbParityTests
{
    static readonly RepoId Home = new("acme", "orders");
    const string Name = "factory-worker";

    static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gateway", "Program.cs")) && File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) return d.FullName;
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    // ---- the cases ----------------------------------------------------------------------------------------------------------------------

    static JsonObject J(string json) => (JsonObject)JsonNode.Parse(json)!;

    static JsonObject Pr(int n, string branch, string sha, string mergeable = "true", string state = "open", string mergeableState = "clean") =>
        J($"{{\"number\":{n},\"ref\":\"{branch}\",\"sha\":\"{sha}\",\"mergeable\":{mergeable},\"mergeable_state\":\"{mergeableState}\",\"state\":\"{state}\"}}");

    sealed record Situation(string Column, string[] Labels, string V1State, string Saga, bool Open = true);

    static JsonObject Item(Situation s) => new()
    {
        ["v1"] = new JsonObject { ["open"] = s.Open, ["column"] = s.Column, ["labels"] = new JsonArray(s.Labels.Select(l => (JsonNode)l).ToArray()), ["state"] = JsonNode.Parse(s.V1State) },
        ["saga"] = JsonNode.Parse(s.Saga),
    };

    static JsonObject Case(string name, string verb, string args, JsonObject? github = null, Dictionary<int, Situation>? items = null, JsonObject? qc = null)
    {
        var all = new JsonObject();
        foreach (var (n, s) in items ?? []) all[n.ToString()] = Item(s);
        return new JsonObject { ["name"] = name, ["verb"] = verb, ["args"] = J(args), ["github"] = github ?? J("{\"head\":\"m1\",\"open\":[],\"pulls\":{},\"files\":{},\"behind\":{},\"changed\":null,\"update\":202}"), ["items"] = all, ["qc"] = qc ?? new JsonObject() };
    }

    static JsonObject Gh(string head = "m1", string? changed = null, JsonArray? open = null, string pulls = "{}", string files = "{}", string behind = "{}", int update = 202, string? behindStatus = null)
    {
        var g = new JsonObject { ["head"] = head, ["changed"] = changed is null ? null : JsonNode.Parse(changed), ["open"] = open ?? [], ["pulls"] = J(pulls), ["files"] = J(files), ["behind"] = J(behind), ["update"] = update };
        if (behindStatus is not null) g["behindStatus"] = J(behindStatus);
        return g;
    }

    // a situation of the item on a step, with the same facts in both halves
    static Situation Developing(int pr = 11) => new("Development", [], $"{{\"pr\":{{\"number\":{pr},\"branch\":\"x\"}}}}", $"{{\"step\":\"Develop\",\"pullRequest\":{pr}}}");
    static Situation PassWaiting() => new("Functional Testing", ["Auto Merge"], "{\"verdict\":{\"passed\":true,\"processed\":false,\"pr\":11,\"head_sha\":\"h7\"},\"pr\":{\"number\":11}}", "{\"step\":\"Merge\",\"pullRequest\":11,\"verdictHead\":\"h7\"}");
    static Situation Merged() => new("Done", ["Auto Merge"], "{\"verdict\":{\"passed\":true,\"processed\":true,\"pr\":11,\"head_sha\":\"h7\"},\"merge\":{\"sha\":\"ccc3333\",\"awaiting\":\"master-green\",\"pr\":11},\"pr\":{\"number\":11}}",
        "{\"step\":\"Deliver\",\"pullRequest\":11,\"verdictHead\":\"h7\",\"mergeCommit\":\"ccc3333\"}");

    static List<JsonObject> Cases()
    {
        
        var cases = new List<JsonObject>
        {
            // ---- base-watch ----
            Case("watch: the first look has the head and no rows", "base-watch", "{\"known\":\"\",\"items\":[],\"mergedPrs\":[]}", Gh("m1")),
            Case("watch: master moved, the overlap and the behind count", "base-watch", "{\"known\":\"m1\",\"items\":[7],\"mergedPrs\":[]}",
                Gh("m2", "[\"docs/a.md\",\"src/b.cs\"]", Open7(), files: "{\"11\":[\"src/b.cs\",\"docs/own.md\"]}", behind: $"{{\"{Name}/7-x\":3}}"), new() { [7] = Developing() }),
            Case("watch: master did not move", "base-watch", "{\"known\":\"m1\",\"items\":[7]}", Gh("m1", null, Open7(), behind: $"{{\"{Name}/7-x\":1}}"), new() { [7] = Developing() }),
            Case("watch: a range GitHub does not have", "base-watch", "{\"known\":\"gone\",\"items\":[7]}", Gh("m2", null, Open7()), new() { [7] = Developing() }),
            Case("watch: a PASS waiting for its merge is skipped", "base-watch", "{\"known\":\"m1\",\"items\":[7]}", Gh("m1", null, Open7()), new() { [7] = PassWaiting() }),
            Case("watch: a merge being settled is skipped", "base-watch", "{\"known\":\"m1\",\"items\":[7]}", Gh("m1", null, Open7()), new() { [7] = Merged() }),
            Case("watch: a pull request that is gone and an item without a branch", "base-watch", "{\"known\":\"m1\",\"items\":[8,9]}", Gh("m1"),
                new() { [8] = Developing(99), [9] = new("Development", [], "{}", "{\"step\":\"Develop\"}") }),
            Case("watch: an item in Functional Testing is not moved", "base-watch", "{\"known\":\"m1\",\"items\":[7]}", Gh("m1", null, Open7()),
                new() { [7] = new("Functional Testing", [], "{\"pr\":{\"number\":11}}", "{\"step\":\"Verify\",\"pullRequest\":11}") }),
            Case("watch: a QC run for the head is not moved", "base-watch", "{\"known\":\"m1\",\"items\":[7]}", Gh("m1", null, Open7()),
                new() { [7] = new("Development", [], "{\"pr\":{\"number\":11},\"qc\":{\"11\":{\"sha\":\"h7\",\"status\":\"ready\"}}}", "{\"step\":\"Develop\",\"pullRequest\":11}") },
                qc: J("{\"11\":\"{\\\"sha\\\":\\\"h7\\\",\\\"status\\\":\\\"ready\\\"}\"}")),
            Case("watch: a QC run for another head is no hold", "base-watch", "{\"known\":\"m1\",\"items\":[7]}", Gh("m1", null, Open7(), behind: $"{{\"{Name}/7-x\":2}}"),
                new() { [7] = new("Development", [], "{\"pr\":{\"number\":11},\"qc\":{\"11\":{\"sha\":\"other\"}}}", "{\"step\":\"Develop\",\"pullRequest\":11}") },
                qc: J("{\"11\":\"{\\\"sha\\\":\\\"other\\\"}\"}")),
            Case("watch: a pull request the webhook saw merged is skipped", "base-watch", "{\"known\":\"m1\",\"items\":[7],\"mergedPrs\":[11]}", Gh("m1", null, Open7()), new() { [7] = Developing() }),
            Case("watch: a copilot branch belongs to the item that recorded the pull request", "base-watch", "{\"known\":\"m1\",\"items\":[7]}",
                Gh("m1", null, [Pr(21, "copilot/fix-it", "h21"), Pr(22, "copilot/other", "h22")], behind: "{\"copilot/fix-it\":4}"), new() { [7] = Developing(21) }),
            Case("watch: the conflict modes read each pull request, reuse a known answer and wait for GitHub's computing", "base-watch",
                "{\"known\":\"m1\",\"items\":[7,8,9],\"conflicts\":true,\"conflictKnown\":{\"8\":{\"prHead\":\"h8\",\"head\":\"m1\",\"conflict\":false}}}",
                Gh("m1", null, [Pr(11, $"{Name}/7-x", "h7"), Pr(12, $"{Name}/8-x", "h8"), Pr(13, $"{Name}/9-x", "h9")],
                    pulls: "{\"11\":[{\"number\":11,\"ref\":\"x\",\"sha\":\"h7\",\"mergeable\":false,\"mergeable_state\":\"dirty\",\"state\":\"open\"}]," +
                           "\"12\":[{\"number\":12,\"ref\":\"x\",\"sha\":\"h8\",\"mergeable\":true,\"mergeable_state\":\"clean\",\"state\":\"open\"}]," +
                           "\"13\":[{\"number\":13,\"ref\":\"x\",\"sha\":\"h9\",\"mergeable\":null,\"mergeable_state\":\"unknown\",\"state\":\"open\"},{\"number\":13,\"ref\":\"x\",\"sha\":\"h9\",\"mergeable\":true,\"mergeable_state\":\"clean\",\"state\":\"open\"}]}"),
                new() { [7] = Developing(11), [8] = Developing(12), [9] = Developing(13) }),
            Case("watch: GitHub that cannot tell how far behind", "base-watch", "{\"known\":\"m1\",\"items\":[7]}", Gh("m1", null, Open7(), behindStatus: $"{{\"{Name}/7-x\":422}}"), new() { [7] = Developing() }),

            // ---- base-update-branch ----
            Case("update: a pull request that is closed", "base-update-branch", "{\"item\":7,\"pr\":11,\"expected\":\"h7\"}", Gh(pulls: "{\"11\":[{\"number\":11,\"ref\":\"x\",\"sha\":\"h7\",\"state\":\"closed\",\"mergeable\":true,\"mergeable_state\":\"clean\"}]}"), new() { [7] = Developing() }),
            Case("update: a head that moved", "base-update-branch", "{\"item\":7,\"pr\":11,\"expected\":\"h7\"}", Gh(pulls: "{\"11\":[{\"number\":11,\"ref\":\"x\",\"sha\":\"h8\",\"state\":\"open\",\"mergeable\":true,\"mergeable_state\":\"clean\"}]}"), new() { [7] = Developing() }),
            Case("update: a verdict on record", "base-update-branch", "{\"item\":7,\"pr\":11,\"expected\":\"h7\"}", Gh(pulls: OpenPull("h7")), new() { [7] = PassWaiting() }),
            Case("update: a merge on record", "base-update-branch", "{\"item\":7,\"pr\":11,\"expected\":\"h7\"}", Gh(pulls: OpenPull("h7")), new() { [7] = Merged() }),
            Case("update: in Functional Testing", "base-update-branch", "{\"item\":7,\"pr\":11,\"expected\":\"h7\"}", Gh(pulls: OpenPull("h7")),
                new() { [7] = new("Functional Testing", [], "{\"pr\":{\"number\":11}}", "{\"step\":\"Verify\",\"pullRequest\":11}") }),
            Case("update: a QC run for the head", "base-update-branch", "{\"item\":7,\"pr\":11,\"expected\":\"h7\"}", Gh(pulls: OpenPull("h7")),
                new() { [7] = new("Development", [], "{\"pr\":{\"number\":11},\"qc\":{\"11\":{\"sha\":\"h7\"}}}", "{\"step\":\"Develop\",\"pullRequest\":11}") }, qc: J("{\"11\":\"{\\\"sha\\\":\\\"h7\\\"}\"}")),
            Case("update: a conflict", "base-update-branch", "{\"item\":7,\"pr\":11,\"expected\":\"h7\"}", Gh(pulls: OpenPull("h7", "false", "dirty")), new() { [7] = Developing() }),
            Case("update: mergeability not computed", "base-update-branch", "{\"item\":7,\"pr\":11,\"expected\":\"h7\"}", Gh(pulls: OpenPull("h7", "null", "unknown")), new() { [7] = Developing() }),
            Case("update: computed after a look", "base-update-branch", "{\"item\":7,\"pr\":11,\"expected\":\"h7\"}",
                Gh(pulls: "{\"11\":[{\"number\":11,\"ref\":\"factory-worker/7-x\",\"sha\":\"h7\",\"state\":\"open\",\"mergeable\":null,\"mergeable_state\":\"unknown\"},{\"number\":11,\"ref\":\"factory-worker/7-x\",\"sha\":\"h7\",\"state\":\"open\",\"mergeable\":true,\"mergeable_state\":\"clean\"}]}", behind: $"{{\"{Name}/7-x\":2}}"),
                new() { [7] = Developing() }),
            Case("update: a branch that is current", "base-update-branch", "{\"item\":7,\"pr\":11,\"expected\":\"h7\"}", Gh(pulls: OpenPull("h7")), new() { [7] = Developing() }),
            Case("update: a branch that is behind is updated", "base-update-branch", "{\"item\":7,\"pr\":11,\"expected\":\"h7\"}", Gh(pulls: OpenPull("h7"), behind: $"{{\"{Name}/7-x\":4}}"), new() { [7] = Developing() }),
            Case("update: GitHub refuses with 409", "base-update-branch", "{\"item\":7,\"pr\":11,\"expected\":\"h7\"}", Gh(pulls: OpenPull("h7"), behind: $"{{\"{Name}/7-x\":4}}", update: 409), new() { [7] = Developing() }),
            Case("update: GitHub refuses with 422", "base-update-branch", "{\"item\":7,\"pr\":11,\"expected\":\"h7\"}", Gh(pulls: OpenPull("h7"), behind: $"{{\"{Name}/7-x\":4}}", update: 422), new() { [7] = Developing() }),
            Case("update: the expected head is required", "base-update-branch", "{\"item\":7,\"pr\":11}", Gh(), new() { [7] = Developing() }),
            Case("update: the pull request is required", "base-update-branch", "{\"item\":7,\"expected\":\"h7\"}", Gh(), new() { [7] = Developing() }),

            // ---- merge ----
            Case("merge: the item is required", "merge", "{}"),
            Case("merge: a column that is not the validation column", "merge", "{\"item\":7}", items: new() { [7] = new("Technical Design", ["Auto Merge"], "{}", "{\"step\":\"Design\"}") }),
            Case("merge: no PASS recorded", "merge", "{\"item\":7}", items: new() { [7] = new("Functional Testing", ["Auto Merge"], "{}", "{\"step\":\"Verify\",\"pullRequest\":11}") }),
            Case("merge: a hold label comes before everything else", "merge", "{\"item\":7,\"pr\":99}", items: new() { [7] = PassWaiting() with { Labels = ["hold:owner"] } }),
            Case("merge: the hold label, in any case", "merge", "{\"item\":7}", items: new() { [7] = PassWaiting() with { Labels = ["Auto Merge", "HOLD:OWNER"] } }),
            Case("merge: another pull request than the verdict's", "merge", "{\"item\":7,\"pr\":99}", items: new() { [7] = PassWaiting() }),
            Case("merge: no Auto Merge label", "merge", "{\"item\":7}", items: new() { [7] = PassWaiting() with { Labels = ["docs"] } }),
            Case("merge: already merged", "merge", "{\"item\":7}", items: new() { [7] = Merged() }),
            Case("merge: already merged, naming its pull request", "merge", "{\"item\":7,\"pr\":11}", items: new() { [7] = Merged() }),
            Case("merge: an issue that is closed", "merge", "{\"item\":7}", items: new() { [7] = PassWaiting() with { Open = false, Saga = "{\"step\":\"Merge\",\"pullRequest\":11,\"verdictHead\":\"h7\",\"status\":\"completed\"}" } }),

            // ---- approve-pr ----
            Case("approve-pr: the item is required", "approve-pr", "{\"text\":\"fine\"}"),
            Case("approve-pr: the text is required", "approve-pr", "{\"item\":7}"),
            Case("approve-pr: no inspection under auto", "approve-pr", "{\"item\":7,\"text\":\"fine\\nINSPECTOR: APPROVE\"}", items: new() { [7] = PassWaiting() }),

            // ---- clear-hold ----
            Case("clear-hold: the item is required", "clear-hold", "{}"),
            Case("clear-hold: no hold of the inspector", "clear-hold", "{\"item\":7}", items: new() { [7] = PassWaiting() }),
            Case("clear-hold: a closed issue", "clear-hold", "{\"item\":7}", items: new() { [7] = PassWaiting() with { Open = false, Saga = "{\"step\":\"Merge\",\"pullRequest\":11,\"verdictHead\":\"h7\",\"status\":\"completed\"}" } }),
        };
        return cases;
    }

    static JsonArray Open7() => new() { Pr(11, $"{Name}/7-x", "h7") };

    static string OpenPull(string sha, string mergeable = "true", string mergeableState = "clean") =>
        $"{{\"11\":[{{\"number\":11,\"ref\":\"{Name}/7-x\",\"sha\":\"{sha}\",\"state\":\"open\",\"mergeable\":{mergeable},\"mergeable_state\":\"{mergeableState}\"}}]}}";

    // ---- the C# side --------------------------------------------------------------------------------------------------------------------

    sealed class CaseGitHub(JsonObject g) : IBaseUpdateGitHub
    {
        readonly Dictionary<int, int> reads = [];
        public Task<string> BaseHead(CancellationToken ct) => Task.FromResult(g["head"]!.ToString());
        public Task<IReadOnlyList<string>?> ChangedFiles(string from, string to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>(g["changed"] is JsonArray a ? a.Select(x => x!.ToString()).Take(300).ToList() : null);
        public Task<IReadOnlyList<BasePull>> OpenPullRequests(CancellationToken ct) => Task.FromResult<IReadOnlyList<BasePull>>(((JsonArray)g["open"]!).Select(p => Pull(p!)).ToList());
        public Task<BasePull> PullRequest(int number, CancellationToken ct)
        {
            var seq = g["pulls"]![number.ToString()] is JsonArray a ? a : [g["pulls"]![number.ToString()]!.DeepClone()];
            var i = Math.Min(reads.GetValueOrDefault(number), seq.Count - 1); reads[number] = reads.GetValueOrDefault(number) + 1;
            return Task.FromResult(Pull(seq[i]!));
        }
        public Task<IReadOnlyList<string>> PullRequestFiles(int number, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(g["files"]![number.ToString()] is JsonArray a ? a.Select(x => x!.ToString()).ToList() : []);
        public Task<int> BehindBy(string branch, CancellationToken ct) =>
            Task.FromResult(g["behindStatus"]?[branch] is not null ? -1 : g["behind"]![branch] is JsonValue v ? v.GetValue<int>() : 0);
        public Task<int> UpdateBranch(int pullRequest, string expectedHead, CancellationToken ct) => Task.FromResult(g["update"]!.GetValue<int>());

        static BasePull Pull(JsonNode p) => new(p["number"]!.GetValue<int>(), p["state"]!.ToString(), p["ref"]!.ToString(), p["sha"]!.ToString(), p["mergeable"] is JsonValue m ? m.GetValue<bool>() : null, p["mergeable_state"]!.ToString());
    }

    sealed class CaseItems(InMemoryWorkItemStore store, JsonObject qc) : IBaseUpdateItems
    {
        public Task<BaseItemFacts?> FactsOf(int item, CancellationToken ct) =>
            Task.FromResult(store.States.TryGetValue(new ItemId(Home, item).ToString(), out var s) ? BaseItemFacts.From(s) : null);
        public Task<string?> QcRecord(int pullRequest, CancellationToken ct) => Task.FromResult(qc[pullRequest.ToString()]?.ToString());
    }

    sealed class CaseLabels(JsonObject items) : IItemLabels
    {
        public Task<IReadOnlyList<string>?> LabelsOf(ItemId item, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>(items[item.Number.ToString()]?["v1"]?["labels"] is JsonArray a ? a.Select(x => x!.ToString()).ToList() : null);
    }

    static async Task<(int Exit, JsonObject Answer)> RunV2(JsonObject c)
    {
        var store = new InMemoryWorkItemStore();
        var items = (JsonObject)c["items"]!;
        foreach (var (key, node) in items)
        {
            var saga = (JsonObject)node!["saga"]!;
            var id = new ItemId(Home, int.Parse(key)).ToString();
            var state = new WorkItemState
            {
                Item = id, Generation = 1, Plan = WorkPlans.Docs, ArmId = "1.1",
                Step = Enum.Parse<StepKind>(saga["step"]?.ToString() ?? "Develop"), Status = saga["status"]?.ToString() ?? WorkItemStatus.Open,
                PullRequest = saga["pullRequest"]?.GetValue<int>() ?? 0, VerdictHead = saga["verdictHead"]?.ToString() ?? "", MergeCommit = saga["mergeCommit"]?.ToString() ?? "",
            };
            Assert.True(await store.Save(id, state, 0, default));
        }
        var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "config", "instance.json")));
        var settings = MergeVerbSettingsReader.From(cfg, DocsPlanSettingsReader.From(cfg).ColumnOf);
        var args = (JsonObject)c["args"]!.DeepClone();
        var service = new MergeVerbService(Home, store, new CaseLabels(items), _ => Task.FromResult(true), settings, new MergeVerbWait(TimeSpan.Zero, 0), _ => Task.CompletedTask);
        var update = new BaseUpdateService(new CaseGitHub((JsonObject)c["github"]!), new CaseItems(store, (JsonObject)c["qc"]!), new BackmergeSettings(Name), _ => Task.CompletedTask);
        Fworker.Contracts.Seams.VerbAnswer? answer = c["verb"]!.ToString() switch
        {
            "merge" => await service.Merge(args, default),
            "approve-pr" => service.ApprovePr(args),
            "clear-hold" => await service.ClearHold(args, default),
            "base-watch" => await update.Watch(args, items.Select(i => int.Parse(i.Key)).ToHashSet(), default),
            "base-update-branch" => await update.UpdateBranch(args, default),
            var other => throw new InvalidOperationException(other),
        };
        Assert.NotNull(answer);   // (every case is one the saga owns or a refusal before the state is read)
        return (answer!.Value.Code, (JsonObject)JsonNode.Parse(answer.Value.Output)!);
    }

    // ---- the comparison -------------------------------------------------------------------------------------------------------------------

    static string Canon(JsonNode? n) => n switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"\"{p.Key}\":{Canon(p.Value)}")) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canon)) + "]",
        _ => n.ToJsonString(),
    };

    static async Task<JsonArray?> RunScripts(List<JsonObject> cases, string dir)
    {
        var casesPath = Path.Combine(dir, "cases.json"); var outPath = Path.Combine(dir, "answers.json");
        await File.WriteAllTextAsync(casesPath, new JsonArray(cases.Select(c => (JsonNode)c.DeepClone()).ToArray()).ToJsonString());
        var script = Path.Combine(Root(), "tests", "Gateway.Tests", "parity", "merge-verbs-parity.ps1");
        var psi = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Cases", casesPath, "-Out", outPath }) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi)!; } catch (System.ComponentModel.Win32Exception) { return null; }
        var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(3))) { p.Kill(true); throw new TimeoutException("the rule scripts did not finish: " + await stdout + await stderr); }
        Assert.True(p.ExitCode == 0, $"the parity script failed ({p.ExitCode}): {await stdout}\n{await stderr}");
        return (JsonArray)JsonNode.Parse(await File.ReadAllTextAsync(outPath))!;
    }

    [Fact]
    public async Task EveryCase_IsAnsweredByV2_AsTheRuleScriptAnswersIt()
    {
        var dir = Directory.CreateTempSubdirectory("merge-parity-").FullName;
        try
        {
            var cases = Cases();
            var scripts = await RunScripts(cases, dir);
            if (scripts is null) return;   // (no pwsh here)
            Assert.Equal(cases.Count, scripts.Count);
            var differences = new List<string>();
            for (var i = 0; i < cases.Count; i++)
            {
                var name = cases[i]["name"]!.ToString();
                var (exit, v2) = await RunV2(cases[i]);
                var script = (JsonObject)scripts[i]!;
                Assert.Equal(name, script["name"]!.ToString());
                var v1 = (JsonObject)script["answer"]!;
                var scriptExit = script["exit"]!.GetValue<int>();
                if (exit != scriptExit || Canon(v1) != Canon(v2)) differences.Add($"{name}\n    script (exit {scriptExit}): {Canon(v1)}\n    v2     (exit {exit}): {Canon(v2)}\n    the script asked GitHub: {string.Join("; ", ((JsonArray)script["calls"]!).Select(x => x!.ToString()))}");
            }
            Assert.True(differences.Count == 0, "v2 answers differently from the rule script:\n" + string.Join("\n", differences));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TheCasesCoverEveryVerbAndEveryOutcomeOfTheBackMergeVerbs()
    {
        var cases = Cases();
        Assert.Equal(["approve-pr", "base-update-branch", "base-watch", "clear-hold", "merge"], cases.Select(c => c["verb"]!.ToString()).Distinct().OrderBy(x => x));
        Assert.Equal(cases.Count, cases.Select(c => c["name"]!.ToString()).Distinct().Count());
    }
}
