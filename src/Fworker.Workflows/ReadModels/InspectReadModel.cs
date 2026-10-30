using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.ReadModels;

/// <summary>The facts the inspector judges on and the reasons it must not approve (<c>Get-InspectFacts</c>).</summary>
public sealed record InspectResult(JsonObject Facts, JsonArray Mandatory);

/// <summary>
/// W9.5: the inspector's read model, <c>inspect-facts</c> (Verbs.Inspect.ps1): the facts and the mandatory escalations that are computed by rule and not by the model, over the pull request of the item's recorded PASS verdict. The reasons are a
/// floor: protected paths, package changes, migrations, size, binaries, secret-like text, deleted tests, a docs-only marker that disagrees with the files, repeated demotions, a branch that moved after the verdict, checks that are not green, a pull
/// request that is not the item's, and text addressed to an AI reviewer. <c>approve-pr</c> (W9.3) reads the same facts through <see cref="Facts"/>.
/// </summary>
public sealed class InspectReadModel(ItemReads reads, ChecksReadModel checks)
{
    readonly ReadModelSettings settings = reads.Settings;

    /// <summary><c>Get-InspectFacts</c>.</summary>
    public async Task<InspectResult> Facts(int n, ItemInfo info, JsonObject state, JsonNode pr, CancellationToken ct)
    {
        var i = settings.Inspector;
        var files = (await reads.PrFiles(Ps.ToInt(Ps.Prop(pr, "number")), ct)).ToList();
        var lines = 0; foreach (var f in files) lines += Count(f, "additions") + Count(f, "deletions");
        var docs = reads.DocsOnlyFiles(files);
        var marker = reads.ItemDocsOnly(state, info);
        var headSha = Ps.Str(Ps.Prop(pr, "head"), "sha");
        var chk = await checks.ChecksFor(headSha, null, ct);
        var mand = new JsonArray();
        void Add(string code, string reason, IEnumerable<string> evidence) => mand.Add(new JsonObject { ["code"] = code, ["reason"] = reason, ["evidence"] = ChecksFacts.Strings(evidence) });
        static bool Any(IReadOnlyList<string> patterns, string name) => patterns.Any(p => Ps.Match(name, p));
        string Name(JsonNode f) => Ps.Str(f, "filename");

        var e = files.Where(f => Any(i.ProtectedPaths, Name(f))).Select(Name).ToList();
        if (e.Count > 0) Add("protected-path", "the change touches pipeline, build, deployment or repository-control files", e);
        e = files.Where(f => Any(i.PackagePaths, Name(f))).Select(Name).ToList();
        if (e.Count > 0) Add("package-change", "project or package files changed; packages and SDK versions need explicit approval", e);
        e = files.Where(f => Any(i.MigrationPaths, Name(f))).Select(Name).ToList();
        if (e.Count > 0) Add("database-migration", "a database migration script changed", e);
        var maxFiles = docs ? i.DocsMaxFiles : i.MaxFiles;
        if (files.Count > maxFiles || lines > i.MaxLines) Add("too-large", $"too large to inspect reliably ({files.Count} files, {lines} lines; limits {maxFiles} files, {i.MaxLines} lines)", [$"{files.Count} files", $"{lines} lines"]);
        e = files.Where(f => Path.GetExtension(Name(f)).TrimStart('.').ToLowerInvariant() is { Length: > 0 } ext && i.BinaryExtensions.Any(b => b.Equals(ext, StringComparison.OrdinalIgnoreCase))).Select(Name).ToList();
        if (e.Count > 0) Add("binary-file", "binary files cannot be inspected", e);
        e = [];
        foreach (var f in files)
        {
            var patch = Ps.Str(f, "patch");
            foreach (var sp in i.SecretPatterns) if (Ps.Match(patch, sp)) { e.Add($"{Name(f)} (pattern {IndexOf(i.SecretPatterns, sp)})"); break; }
        }
        if (e.Count > 0) Add("secret-like", "a secret-like string appears in the diff", e);
        e = files.Where(f => Ps.Ieq(Ps.Str(f, "status"), "removed") && Ps.Match(Name(f), "test")).Select(Name).ToList();
        if (e.Count > 0) Add("deleted-tests", "test files were deleted", e);
        if ((marker && !docs) || (docs && !marker)) Add("docs-marker-mismatch", "the docs-only marker and the files changed disagree", [$"marker={Bool(marker)}", $"docsOnlyFiles={Bool(docs)}"]);
        var dem = Ps.Prop(state, "demotions") is { } dn ? Ps.ToInt(dn) : 0;
        if (dem >= 2) Add("repeated-demotions", $"this item has failed validation or merge {dem} times", [$"demotions={dem}"]);
        var vsha = Ps.At(state, "verdict", "head_sha") is { } vh ? Ps.Text(vh) : "";
        if (vsha.Length > 0 && !Ps.Ieq(vsha, headSha)) Add("stale-head", "the branch changed after the validation verdict", [$"verdict={vsha.Substring(0, 7)}", $"head={headSha.Substring(0, 7)}"]);
        if (chk.Status != "success") Add("checks-not-green", "required checks are not all successful", new[] { $"status={chk.Status}" }.Concat(chk.FailedChecks).Concat(chk.PendingChecks));
        if (!await reads.PrBelongs(pr, n, ct)) Add("pr-not-item", "the pull request does not belong to this work item", [Ps.Str(Ps.Prop(pr, "head"), "ref")]);
        var texts = new[] { info.Title, info.Body, Ps.Str(pr, "title"), Ps.Str(pr, "body") };
        e = [];
        foreach (var t in texts) foreach (var p in i.AiDirectedText) if (Ps.Match(t, p)) e.Add($"matches {IndexOf(i.AiDirectedText, p)}");
        if (e.Count > 0) Add("ai-directed-text", "item or pull request text is addressed to an AI reviewer", e.Distinct());

        var shown = new JsonArray();
        foreach (var f in files.Take(50)) shown.Add(new JsonObject { ["name"] = Name(f), ["additions"] = Count(f, "additions"), ["deletions"] = Count(f, "deletions"), ["status"] = Ps.Str(f, "status") });
        var facts = new JsonObject
        {
            ["head_sha"] = headSha, ["branch"] = Ps.Str(Ps.Prop(pr, "head"), "ref"), ["author"] = Ps.Str(Ps.Prop(pr, "user"), "login"), ["file_count"] = files.Count, ["line_count"] = lines,
            ["docs_only_files"] = docs, ["docs_marker"] = marker, ["checks"] = chk.Public(), ["demotions"] = dem, ["files"] = shown,
        };
        return new InspectResult(facts, mand);
    }

    static string Bool(bool b) => b ? "True" : "False";

    // [int](Get-Prop $f 'additions' 0): the number of lines, none when GitHub names none
    static int Count(JsonNode file, string name) => Ps.Prop(file, name) is { } v ? Ps.ToInt(v) : 0;

    // [array]::IndexOf on the pattern list: the first equal element (ordinal)
    static int IndexOf(IReadOnlyList<string> list, string value) { for (var k = 0; k < list.Count; k++) if (list[k] == value) return k; return -1; }

    /// <summary><c>Get-VerdictState</c>: the item, its state and the pull request of the PASS verdict nobody has processed yet, or exit 10 no-pass-verdict.</summary>
    public async Task<(ItemInfo Info, JsonObject State, JsonNode Pr)> VerdictState(int n, CancellationToken ct)
    {
        var info = await reads.Info(n, ct);
        var s = await reads.State(n, ct);
        var verdict = Ps.Prop(s, "verdict");
        if (verdict is null || !Ps.Bool(Ps.Prop(verdict, "passed")) || Ps.Bool(Ps.Prop(verdict, "processed")))
            throw new VerbRefusal(10, "no-pass-verdict", "no unprocessed PASS verdict is recorded for this item");
        return (info, s, await reads.Pr(Ps.ToInt(Ps.Prop(verdict, "pr")), ct));
    }

    static readonly Regex DerivedBlock = new(@"<!-- aisf:assumption source=([A-Za-z0-9-]{1,40}) from=([A-Za-z0-9_-]{1,40})(?: in_reply_to=[A-Za-z0-9-]{1,40})? -->(?:(?!<!-- aisf:assumption ).)*?<!-- /aisf:assumption -->",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));

    /// <summary><c>Remove-DerivedBlocks</c>: the text without the blocks the gateway marked as derived from a message. A marker that is opened and never closed is no block: the text after it stays, so a marker can hide nothing.</summary>
    public string RemoveDerivedBlocks(string text)
    {
        if (!settings.MessagingOn || text.Length == 0 || text.IndexOf("aisf:assumption", StringComparison.Ordinal) < 0) return text;
        return DerivedBlock.Replace(text, "");
    }

    /// <summary><c>Verb_inspect_facts</c>.</summary>
    public async Task<JsonObject> InspectFacts(VerbArgs a, CancellationToken ct)
    {
        var n = a.ItemNumber();
        var v = await VerdictState(n, ct);
        var r = await Facts(n, v.Info, v.State, v.Pr, ct);
        return new JsonObject
        {
            ["ok"] = true, ["id"] = n, ["facts"] = r.Facts, ["mandatory"] = r.Mandatory, ["must_escalate"] = r.Mandatory.Count > 0,
            ["item"] = new JsonObject { ["title"] = v.Info.Title, ["body"] = RemoveDerivedBlocks(v.Info.Body) },
        };
    }
}
