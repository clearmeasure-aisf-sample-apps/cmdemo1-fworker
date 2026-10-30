using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Workflows.Reconcilers;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.8: v2's answers against the rule scripts they replace. The real plugin/scripts/factory.ps1 is run (pwsh, `--server &lt;verb&gt;`, the instance file of the repository, no GitHub credential) on the same inputs as the
/// C# verbs, and the answers are compared whole: every field, the exit code, the events of a webhook (but their time). Only the inputs that need no network are here: the translation of the payloads that read nothing, the
/// refusals of the journal and the transcript verbs, and the read of a card, which in both is the refusal "GH_TOKEN is not set". The tests return at once on a machine without pwsh.
/// </summary>
public sealed class BoardVerbParityTests : IDisposable
{
    static readonly TimeSpan Time = TimeSpan.FromSeconds(30);
    readonly string dir = Directory.CreateTempSubdirectory("board-parity-").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "plugin", "scripts", "factory.ps1")) && File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) return d.FullName;
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    static bool HasPwsh()
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(d, "pwsh"))) return true;
        return false;
    }

    // the rule script, as the Runner starts it, with the repository's instance file and no credential
    async Task<(int Exit, JsonObject Answer)> Script(string verb, string stdin)
    {
        var psi = new ProcessStartInfo("pwsh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(Root(), "plugin", "scripts", "factory.ps1"), "--server", verb }) psi.ArgumentList.Add(a);
        psi.Environment["FACTORY_CONFIG"] = Path.Combine(Root(), "config", "instance.json");
        psi.Environment["FACTORY_STATE_DIR"] = Path.Combine(dir, "state"); psi.Environment["FACTORY_DATA_DIR"] = Path.Combine(dir, "data");
        psi.Environment["GH_TOKEN"] = "unset"; psi.Environment["FACTORY_ENV_SCOPE"] = "0"; psi.Environment["FACTORY_ENVIRONMENT"] = "tdd";
        psi.Environment["FACTORY_AUTHOR_APP"] = "0"; psi.Environment["DOTNET_MultiCoreJitMinNumCpus"] = "65535";
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync(); _ = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await p.WaitForExitAsync(cts.Token);
        var text = (await output).Trim();
        return (p.ExitCode, (JsonObject)JsonNode.Parse(text.Split('\n')[^1])!);
    }

    static BoardVerbs Verbs(IWebhookLookups? lookups = null)
    {
        var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "config", "instance.json")))!;
        string S(string k) => cfg[k]!.ToString();
        return new BoardVerbs(new BoardVerbParts
        {
            Home = new RepoId(S("owner"), S("repo")), Seal = new TranscriptSeal("parity-tests-secret"),
            Translator = new WebhookEventTranslator(new WebhookTranslatorConfig(S("owner"), S("repo"), S("name"), S("defaultBranch"), S("label"), ""), lookups ?? new HttpWebhookLookups(new HttpClient(), null, new RepoId(S("owner"), S("repo")), 679)),
        });
    }

    // the answer with the keys in order, and without the times of the events
    static string Canon(JsonNode? n, bool top = true) => n switch
    {
        JsonObject o => "{" + string.Join(",", o.Where(kv => !(kv.Key == "at" && !top)).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"\"{kv.Key}\":{Canon(kv.Value, false)}")) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(x => Canon(x, false))) + "]",
        null => "null",
        _ => n.ToJsonString(),
    };

    static string Hook(string ev, string payload) => "{\"event\":\"" + ev + "\",\"delivery\":\"d-parity\",\"payload\":" + payload.Replace("%REPOUP%", Repo.ToUpperInvariant()).Replace("%REPO%", Repo) + "}";

    const string Repo = "clearmeasure-aisf-sample-apps/cmdemo1-workorders";

    public static IEnumerable<object[]> IngestCases() =>
    [
        [Hook("workflow_run", """{"action":"completed","workflow_run":{"head_branch":"factory-worker/12-add-thing","head_sha":"abc","conclusion":"success","name":"Build","id":99,"run_attempt":2}}""")],
        [Hook("workflow_run", """{"action":"in_progress","workflow_run":{"head_branch":"Factory-Worker/12-add-thing","head_sha":"abc","conclusion":null,"name":"Build"}}""")],
        [Hook("workflow_run", """{"action":"completed","workflow_run":{"head_branch":"master","head_sha":"def","conclusion":"failure","name":"Build","id":7}}""")],
        [Hook("workflow_run", """{"action":"completed","workflow_run":{"head_branch":"MASTER","head_sha":"def","conclusion":"failure","name":"Build"}}""")],
        [Hook("workflow_run", """{"action":"requested","workflow_run":{"head_branch":"master"}}""")],
        [Hook("workflow_run", """{"action":"completed","workflow_run":{"head_branch":"feature/x","head_sha":"d"}}""")],
        [Hook("pull_request", """{"action":"opened","sender":{"login":"alice"},"pull_request":{"number":40,"head":{"ref":"factory-worker/12-x","sha":"h1","repo":{"full_name":"%REPO%"}},"base":{"ref":"master"}}}""")],
        [Hook("pull_request", """{"action":"synchronize","pull_request":{"number":40,"head":{"ref":"factory-worker/12-x","sha":"h2","repo":{"full_name":"%REPOUP%"}},"base":{"ref":"master"}}}""")],
        [Hook("pull_request", """{"action":"closed","sender":{"login":"bob"},"pull_request":{"number":40,"merged":true,"merge_commit_sha":"m1","head":{"ref":"factory-worker/12-x","sha":"h1","repo":{"full_name":"%REPO%"}},"base":{"ref":"master"}}}""")],
        [Hook("pull_request", """{"action":"closed","pull_request":{"number":41,"merged":false,"head":{"ref":"factory-worker/13-y","sha":"h2","repo":{"full_name":"%REPO%"}},"base":{"ref":"master"}}}""")],
        [Hook("pull_request", """{"action":"converted_to_draft","pull_request":{"number":42,"head":{"ref":"factory-worker/14-z","sha":"h3","repo":{"full_name":"%REPO%"}},"base":{"ref":"master"}}}""")],
        [Hook("pull_request", """{"action":"closed","pull_request":{"number":43,"merged":true,"merge_commit_sha":"m2","head":{"ref":"dependabot/npm/x","sha":"h4"},"base":{"ref":"master"}}}""")],
        [Hook("pull_request", """{"action":"closed","pull_request":{"number":44,"merged":true,"merge_commit_sha":"m3","head":{"ref":"factory-worker/15-w","sha":"h5","repo":{"full_name":"mallory/orders"}},"base":{"ref":"master"}}}""")],
        [Hook("pull_request", """{"action":"opened","pull_request":{"number":45,"head":{"ref":"factory-worker/15-w","sha":"h5"},"base":{"ref":"master"}}}""")],
        [Hook("pull_request", """{"action":"labeled","pull_request":{"number":45,"head":{"ref":"factory-worker/15-w","sha":"h5"}}}""")],
        [Hook("pull_request_review", """{"action":"submitted","review":{"user":{"login":"bob"},"state":"approved","commit_id":"c1"},"pull_request":{"number":40,"head":{"ref":"factory-worker/12-x","sha":"h1"}}}""")],
        [Hook("pull_request_review", """{"action":"dismissed","review":{"user":{"login":"bob"},"state":"dismissed","commit_id":"c1"},"pull_request":{"number":40,"head":{"ref":"factory-worker/12-x","sha":"h1"}}}""")],
        [Hook("pull_request_review", """{"action":"edited","pull_request":{"number":40,"head":{"ref":"factory-worker/12-x"}}}""")],
        [Hook("issues", """{"action":"closed","issue":{"number":3,"labels":[{"name":"Factory Worker"},{"name":"bug"}]}}""")],
        [Hook("issues", """{"action":"reopened","issue":{"number":3,"labels":[{"name":"factory worker"}]}}""")],
        [Hook("issues", """{"action":"closed","issue":{"number":3,"labels":[{"name":"bug"}]}}""")],
        [Hook("issues", """{"action":"edited","issue":{"number":3,"labels":[{"name":"factory worker"}]}}""")],
        [Hook("issues", """{"action":"labeled","issue":{"number":7,"labels":[{"name":"factory worker"}]},"label":{"name":"factory worker"}}""")],   // the column read fails: no column
        [Hook("issues", """{"action":"labeled","issue":{"number":7,"labels":[{"name":"factory worker"}]},"label":{"name":"bug"}}""")],
        [Hook("issues", """{"repository":{"full_name":"acme/other"},"action":"closed","issue":{"number":3,"labels":[{"name":"factory worker"}]}}""")],
        [Hook("issues", """{"repository":{"full_name":"%REPO%"},"action":"closed","issue":{"number":3,"labels":[{"name":"factory worker"}]}}""")],
        [Hook("projects_v2_item", """{"action":"edited","projects_v2_item":{"content_node_id":"I_5"},"changes":{"field_value":{"field_name":"Status","to":{"name":"Development"}}}}""")],   // the card read: no credential
        [Hook("projects_v2_item", """{"action":"created","projects_v2_item":{"content_node_id":"I_5"}}""")],
        [Hook("projects_v2_item", """{"action":"edited","projects_v2_item":{"content_node_id":"I_5"},"changes":{"field_value":{"field_name":"Priority"}}}""")],
        [Hook("push", """{"ref":"refs/heads/master"}""")],
        ["""{"payload":{}}"""],
        ["""{"event":"issues","delivery":"d","payload":{"action":"closed","issue":{"number":3,"labels":[{"name":"factory worker"}]}}}"""],   // no delivery id in the hook of a replay
    ];

    [Theory]
    [MemberData(nameof(IngestCases))]
    public async Task IngestEvent_AnswersWhatTheRuleScriptAnswers(string stdin)
    {
        if (!HasPwsh()) return;
        var (exit, v1) = await Script("ingest-event", stdin);
        var v2 = await Verbs().IngestEvent(stdin, Time);
        Assert.Equal(exit, v2.Code);
        Assert.Equal(Canon(v1), Canon(JsonNode.Parse(v2.Output)));
    }

    public static IEnumerable<object[]> JournalCases() =>
    [
        ["verb", "{}"],
        ["verb", """{"item":7}"""],
        ["verb", """{"item":7,"event":"no-such-event"}"""],
        ["verb", """{"event":"tier"}"""],
    ];

    [Theory]
    [MemberData(nameof(JournalCases))]
    public async Task Journal_RefusesWhatTheRuleScriptRefuses(string _, string stdin)
    {
        if (!HasPwsh()) return;
        var (exit, v1) = await Script("journal", stdin);
        var v2 = await Verbs().Journal(stdin, Time);
        Assert.Equal(exit, v2.Code);
        Assert.Equal(Canon(v1), Canon(JsonNode.Parse(v2.Output)));
    }

    public static IEnumerable<object[]> TranscriptCases() =>
    [
        ["transcript-save", "{}"],
        ["transcript-save", """{"name":"a"}"""],
        ["transcript-save", """{"name":"a","day":"20261029"}"""],
        ["transcript-save", """{"name":"-bad","day":"20261029","data":"fwt1:AAAA"}"""],
        ["transcript-save", """{"name":"a_b.c-d","day":"2026-10-29","data":"fwt1:AAAA"}"""],
        ["transcript-save", """{"name":"a","day":"20261029","data":"plain text"}"""],
        ["transcript-save", "{\"name\":\"a\",\"day\":\"20261029\",\"data\":\"fwt1:" + new string('A', 900000) + "\"}"],
        ["transcript-load", "{}"],
        ["transcript-load", """{"name":"bad name"}"""],
    ];

    [Theory]
    [MemberData(nameof(TranscriptCases))]
    public async Task TheTranscriptVerbs_RefuseWhatTheRuleScriptRefuses(string verb, string stdin)
    {
        if (!HasPwsh()) return;
        var (exit, v1) = await Script(verb, stdin);
        var verbs = Verbs();
        var v2 = verb == "transcript-save" ? await verbs.TranscriptSave(stdin, Time) : await verbs.TranscriptLoad(stdin, Time);
        // the refusal that comes first is the same; a call the script would pass on to GitHub is v2's to refuse for the database (here: none is configured)
        Assert.Equal(exit, v2.Code);
        Assert.Equal(Canon(v1), Canon(JsonNode.Parse(v2.Output)));
    }
}
