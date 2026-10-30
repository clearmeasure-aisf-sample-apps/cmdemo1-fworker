using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.WorkItem.Verbs;

/// <summary>What the item's pull request owes before the checks are the gate (<c>Get-BuildOwed</c>).</summary>
public sealed record BuildOwed(bool Owed, bool DocsOnlyFiles, string Verification, string Reason, IReadOnlyList<string>? NotDocumentation = null);

/// <summary>The pull requests of an item, as Prs.ps1 finds and reads them: the open one of the item, a pull request by number, its files, and the docs-only fast path's build rule.</summary>
public sealed class ItemPulls(ItemGitHub gh, IItemStateStore store)
{
    ItemVerbConfig Cfg => gh.Config;

    /// <summary><c>Find-ItemPr</c>: the open pull request on the item's branch (the first page of open ones, as the script reads it).</summary>
    public async Task<JsonNode?> FindItemPr(int item, CancellationToken ct)
    {
        var openList = (await gh.Get($"{Cfg.RepoPath}/pulls?state=open&per_page=100", ct)) switch { JsonArray a => a.Where(x => x is not null).Select(x => x!).ToList(), null => [], var one => [one] };
        string Ref(JsonNode p) => Ps.Text(Ps.Get(p, "head"), "ref");
        var mine = openList.Where(p => Regex.IsMatch(Ref(p), $"^{Regex.Escape(Cfg.Name)}/{item}-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        if (mine.Count == 0) mine = openList.Where(p => Regex.IsMatch(Ref(p), $"/{item}-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        // a Copilot cloud agent's branch (copilot/<slug>) is the item's when the state records it
        if (mine.Count == 0 && openList.Any(p => Ref(p).StartsWith("copilot/", StringComparison.OrdinalIgnoreCase)))
        {
            JsonObject? sp = null;
            try { sp = (await store.Read(item, ct)).State; } catch (OperationCanceledException) { throw; } catch { }
            if (sp is not null && Ps.Has(sp, "pr") && Ps.Has(sp["pr"], "branch"))
                mine = openList.Where(p => Ps.Eq(Ref(p), Ps.Text(sp["pr"], "branch")) && Ps.Int(p, "number") == Ps.Int(sp["pr"], "number")).ToList();
        }
        return mine.Count == 0 ? null : mine[0];
    }

    /// <summary><c>Get-Pr</c>.</summary>
    public Task<JsonNode?> GetPr(int number, CancellationToken ct) => gh.Get($"{Cfg.RepoPath}/pulls/{number}", ct);

    /// <summary><c>Get-PrFiles</c>: every file of the pull request, page by page.</summary>
    public Task<List<JsonNode>> GetPrFiles(int number, CancellationToken ct) => gh.Pages($"{Cfg.RepoPath}/pulls/{number}/files", ct);

    public static IEnumerable<string> Names(IEnumerable<JsonNode> files) => files.Select(f => Ps.Text(f, "filename"));

    /// <summary><c>Get-BuildOwed</c>: the private build is not owed only when the design recorded the item as docs-only AND every changed file is documentation.</summary>
    public async Task<BuildOwed> GetBuildOwed(ItemInfo info, int prNumber, JsonObject state, CancellationToken ct)
    {
        var marker = ItemRules.ItemDocsOnly(state, info, Cfg);
        List<JsonNode>? files = null;
        try { files = await GetPrFiles(prNumber, ct); } catch (OperationCanceledException) { throw; } catch { }
        if (files is null) return new BuildOwed(true, false, "app", "the files of the pull request could not be read; the private build is owed");
        var docs = ItemRules.DocsOnlyFiles(Names(files), Cfg);
        var other = files.Select(f => Ps.Text(f, "filename")).Where(n => !ItemRules.DocsOnlyFiles([n], Cfg)).Take(5).ToList();
        if (marker && docs)
        {
            var video = info.HasLabel(Cfg.VideoLabel);
            return new BuildOwed(false, true, video ? "app" : "diff", "every changed file is documentation and the technical design recorded the item as docs-only: no private build and no SDK are owed; the checks of the pull request are the gate");
        }
        if (marker)
            return new BuildOwed(true, false, "app", $"the technical design recorded the item as docs-only but the pull request changes files that are not documentation ({string.Join(", ", other)}): the private build is owed before complete", other);
        return new BuildOwed(true, docs, "app", "the technical design did not record the item as docs-only: the private build is owed");
    }

    /// <summary>The fields <c>Get-BuildOwed</c> adds to an answer (<c>build_owed</c>, <c>build_reason</c>).</summary>
    public static void AddTo(JsonObject answer, BuildOwed bo) { answer["build_owed"] = bo.Owed; answer["build_reason"] = bo.Reason; }

    // ---- the push gate after repeated red checks (Verbs.Pr.ps1) ------------------------------------------------------------------------------------

    /// <summary><c>Get-ChecksRedCount</c>.</summary>
    public static int ChecksRedCount(JsonObject s) => Ps.Has(s, "checks_red") && Ps.Has(s["checks_red"], "count") ? Ps.Int(s["checks_red"], "count") : 0;

    /// <summary><c>Assert-PushGate</c>: from timing.redChecksGate red heads on, a push needs the session's attestation that the private build passed on the head; an item that owes no private build is not gated.</summary>
    public async Task AssertPushGate(int item, ItemInfo info, JsonObject s, string headSha, int? prNumber, CancellationToken ct)
    {
        var limit = Cfg.RedChecksGate; var count = ChecksRedCount(s);
        if (limit <= 0 || count < limit) return;
        if (Ps.Has(s, "private_build"))
        {
            var pb = (JsonObject)s["private_build"]!;
            if (headSha.Length > 0 && Ps.Text(pb, "sha") == headSha) return;
            if (headSha.Length == 0 && Ps.FromIso(pb["at"]) is { } at && Ps.FromIso(Ps.Get(s["checks_red"], "last_at")) is { } last && at >= last) return;
        }
        if (prNumber is { } pr && pr != 0) { var bo = await GetBuildOwed(info, pr, s, ct); if (!bo.Owed) return; }
        var shown = headSha.Length > 0 ? headSha[..Math.Min(7, headSha.Length)] : "the head of the branch";
        throw new ItemRefusal(10, "private-build-required",
            $"the checks of this item's pull request went red on {count} heads: run the private build on {shown} until it passes, then record it with private-build --item {item} --attest <full sha of that commit>, and call this again. No push without a passing private build.",
            new JsonObject { ["checks_red"] = count, ["head_sha"] = headSha, ["gate"] = limit });
    }
}
