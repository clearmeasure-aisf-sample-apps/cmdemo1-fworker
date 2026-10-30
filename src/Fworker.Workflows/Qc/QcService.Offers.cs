using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.Qc;

// The offer comments (Update-QcOffer, Update-QcAssignedComments, Remove-QcOfferText) and the human approval (Update-QcApproved) of Qc.ps1.

public sealed partial class QcService
{
    /// <summary>
    /// Update-QcOffer: posts or edits the offer on the pull request and on the work item; 'posted', 'edited' or 'unchanged'. An offer for this head with more than a day left stands unless <paramref name="force"/>.
    /// A changed offer is a journal line (qc-offered), once per pull request and head.
    /// </summary>
    async Task<string> UpdateOffer(int item, QcItemState st, JsonObject pr, bool force, CancellationToken ct)
    {
        var baseUrl = QcRules.BaseUrl(cfg);
        var login = await github.ServerLogin(ct);
        var prNum = QcJson.Int(pr, "number"); var sha = HeadSha(pr);
        var outcome = "unchanged";
        var prComments = await github.Comments(prNum, ct);
        var targets = new[] { (Number: prNum, Found: QcRules.FindOffer(prComments, prNum, login)), (Number: item, Found: QcRules.FindOffer(st.Comments, prNum, login)) };
        (string Token, long Expires)? minted = null;
        foreach (var t in targets)
        {
            var needs = true;
            if (t.Found is not null && !force)
            {
                var m = Regex.Match(t.Found.Body, "sha=([0-9a-f]{40}) exp=(\\d+)");
                if (m.Success && string.Equals(m.Groups[1].Value, sha, StringComparison.OrdinalIgnoreCase) && long.Parse(m.Groups[2].Value) - new DateTimeOffset(utcNow(), TimeSpan.Zero).ToUnixTimeSeconds() > 86400) needs = false;
            }
            if (!needs) continue;
            minted ??= QcRules.NewToken(cfg, prNum, sha, item, new DateTimeOffset(utcNow(), TimeSpan.Zero), nonce);
            var text = QcRules.OfferText(cfg, prNum, sha, $"{baseUrl}/qc/{prNum}?t={minted.Value.Token}", minted.Value.Expires);
            if (t.Found is not null) { await github.PatchComment(t.Found.Id, text, ct); if (outcome != "posted") outcome = "edited"; }
            else { await github.PostComment(t.Number, text, ct); outcome = "posted"; }
        }
        if (outcome != "unchanged") await JournalLine(item, "qc-offered", new JsonObject { ["pr"] = prNum, ["sha7"] = Sha7(sha) }, $"{prNum}|{sha}", ct);
        return outcome;
    }

    /// <summary>After Start the offer comments name the worker (edited once). A failure is logged and ends nothing.</summary>
    async Task UpdateAssignedComments(int item, QcItemState st, JsonObject rec, CancellationToken ct)
    {
        try
        {
            var login = await github.ServerLogin(ct);
            var prNum = QcJson.Int(rec, "pr"); var sha = QcJson.Str(rec, "sha");
            var lists = new[] { (Number: prNum, List: await github.Comments(prNum, ct)), (Number: item, List: st.Comments) };
            foreach (var t in lists)
            {
                var f = QcRules.FindOffer(t.List, prNum, login);
                if (f is null || f.Body.Contains("was requested and runs on", StringComparison.Ordinal)) continue;
                // keep the link line so a person can still click it
                var link = Regex.Match(f.Body, "\\]\\(([^)]+)\\)").Groups[1].Value;
                var exp = Regex.Match(f.Body, "exp=(\\d+)").Groups[1].Value;
                await github.PatchComment(f.Id, QcRules.AssignedText(prNum, sha, QcJson.Str(rec, "worker"), link, exp), ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { log($"qc: could not edit the offer comments: {e.Message}"); }
    }

    /// <summary>Remove-QcOfferText: the offer on the item and on the pull request says it is no longer offered. Nothing is written when there is none, or it says so already.</summary>
    async Task RemoveOffer(int item, QcItemState st, int prNum, string why, CancellationToken ct)
    {
        var login = await github.ServerLogin(ct);
        var found = QcRules.FindOffer(st.Comments, prNum, login);
        if (found is null) return;
        if (found.Body.Contains("is no longer offered", StringComparison.Ordinal)) return;
        var body = QcRules.WithdrawnText(prNum, why);
        await github.PatchComment(found.Id, body, ct);
        var pc = QcRules.FindOffer(await github.Comments(prNum, ct), prNum, login);
        if (pc is not null) await github.PatchComment(pc.Id, body, ct);
    }

    /// <summary>Update-QcApproved: sets or clears <c>record.approved</c> (a person's approval of the head), saving only on a change. Returns whether it changed.</summary>
    async Task<bool> UpdateApproved(int item, QcItemState st, JsonObject? rec, string head, string? by, CancellationToken ct)
    {
        if (rec is null) return false;
        var had = QcJson.Has(rec, "approved");
        if (!string.IsNullOrEmpty(by))
        {
            var cur = QcJson.Obj(rec, "approved");
            if (had && QcJson.Str(cur, "sha") == head && QcJson.Str(cur, "by") == by) return false;
            rec["approved"] = new JsonObject { ["by"] = Text(by, 64), ["sha"] = head, ["at"] = QcJson.Iso(utcNow()) };
        }
        else if (had) rec.Remove("approved");
        else return false;
        QcRecords.BumpRev(rec);
        await SaveRecord(item, st, rec, ct);
        return true;
    }

    async Task<string?> HumanApproval(JsonObject pr, CancellationToken ct) =>
        cfg.HumanApprovers.Count == 0 ? null : QcRules.FindHumanApproval(pr, cfg.HumanApprovers, await github.Reviews(QcJson.Int(pr, "number"), ct));
}
