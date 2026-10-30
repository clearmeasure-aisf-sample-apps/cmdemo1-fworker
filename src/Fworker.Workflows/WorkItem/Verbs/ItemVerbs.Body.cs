using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.WorkItem.Verbs;

// The verbs that write on the item's body and comments, and the review record (Verbs.Item.ps1: Verb_append_body, Verb_comment, Verb_record_review, Verb_move_next).

public sealed partial class ItemVerbs
{
    // Verb_append_body
    async Task<JsonObject> AppendBody(ItemCall c)
    {
        var n = c.Item; var content = c.NeedText("content");
        if (content.Trim() == "") throw new ItemRefusal(2, "nothing-to-append", "There is nothing to append. Send non-blank markdown content.");
        var info = await board.GetItemInfo(n, c.Ct); AssertOpen(info); AssertColumnMatches(info, c);
        // The docs-only decision is a fact of the design step, kept in the item state: the marker counts only when it arrives with the design section, appended in the design column.
        var hasMarker = content.Contains(Cfg.DocsOnlyMarker, StringComparison.Ordinal);
        var section = Cfg.SectionOf(info.ColumnText);
        var designCol = section is not null;
        var isDesign = designCol && content.IndexOf(section!, StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasMarker && !isDesign)
            throw new ItemRefusal(10, "docs-only-marker-not-allowed", $"the marker {Cfg.DocsOnlyMarker} is accepted only inside the design section, appended while the item is in its design column; remove it from this text");
        var next = info.Body.TrimEnd() + "\n\n" + content.Trim() + "\n";
        await gh.Json("PATCH", $"{gh.RepoPath}/issues/{n}", new JsonObject { ["body"] = next }, c.Ct);
        var check = await board.GetItemInfo(n, c.Ct);
        var probe = content.Trim(); if (probe.Length > 60) probe = probe[..60];
        if (!check.Body.Contains(probe, StringComparison.Ordinal)) throw new ItemRefusal(20, "append-not-visible", "the append did not show in a re-read of the body", new JsonObject { ["retryAfterSeconds"] = 2 });
        var st = await store.Read(n, c.Ct); var s = st.State;
        if (!Ps.Has(s, "last_append")) s["last_append"] = new JsonObject();
        Ps.CiSet((JsonObject)s["last_append"]!, info.ColumnText, Ps.Iso(Now));
        if (isDesign) s["design"] = new JsonObject { ["docs_only"] = hasMarker, ["at"] = Ps.Iso(Now) };   // the latest design section decides
        await store.Save(n, st, c.Ct);
        return new JsonObject { ["ok"] = true, ["id"] = n, ["appended"] = true, ["bodyLength"] = check.Body.Length };
    }

    // Verb_comment
    async Task<JsonObject> Comment(ItemCall c)
    {
        var n = c.Item; var text = c.NeedText("body");
        if (text.Trim() == "") throw new ItemRefusal(2, "empty-comment", "The comment body is empty.");
        var info = await board.GetItemInfo(n, c.Ct);
        var last = await board.LastColumn(c.Ct);
        if (Ps.Eq(info.ColumnText, last) && info.Column is not null) throw new ItemRefusal(10, "finished-column", $"item is in '{info.ColumnText}', the board's last column: it is finished and takes no more comments from an agent");
        await AddComment(n, text, c);
        return new JsonObject { ["ok"] = true, ["id"] = n, ["commented"] = true };
    }

    // Verb_record_review
    async Task<JsonObject> RecordReview(ItemCall c)
    {
        var n = c.Item;
        var col = c.NeedText("column"); var role = c.NeedText("role"); var text = c.NeedText("text");
        if (!Ps.Eq(role, "reviewer")) throw new ItemRefusal(10, "not-reviewer", "only the reviewer role records a review");
        var info = await board.GetItemInfo(n, c.Ct); AssertOpen(info); AssertColumnMatches(info, c);
        // COMPLETE must be the last non-blank line. FINDINGS may be followed by its bullets. The last REVIEW: line decides.
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l != "").ToList();
        string? verdict = null;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (Ps.CMatch(lines[i], "^REVIEW: (COMPLETE|FINDINGS)"))
            {
                if (lines[i] == "REVIEW: COMPLETE" && i == lines.Count - 1) verdict = "COMPLETE";
                else if (Ps.CMatch(lines[i], "^REVIEW: FINDINGS")) verdict = "FINDINGS";
                break;
            }
        }
        if (verdict is null) throw new ItemRefusal(10, "no-verdict-line", "the review must end with REVIEW: COMPLETE as its last line, or contain REVIEW: FINDINGS followed by its bullets");
        var st = await store.Read(n, c.Ct); var s = st.State;
        if (!Ps.Has(s, "reviews")) s["reviews"] = new JsonObject();
        var reviews = (JsonObject)s["reviews"]!;
        var rv = Ps.CiGet(reviews, col) as JsonObject ?? new JsonObject { ["round"] = 0 };
        // Docs-only fast path: the design of an item whose body carries the docs-only marker gets one review round, and a second one only for a finding that quotes the item.
        var fast = DocsOnlyDesignReview(s, col); var max = Cfg.MaxReviewRounds;
        if (fast) max = Math.Min(max, Cfg.DocsOnlyMaxReviewRounds);
        if (Ps.Int(rv, "round") >= max)
            throw new ItemRefusal(10, "review-rounds-exhausted", $"the review loop is limited to {max} rounds{(fast ? " for a docs-only design" : "")}; report the failure with succeeded=false");
        (List<JsonObject> Accepted, int Dropped)? grounded = null;
        if (fast && verdict == "FINDINGS")
        {
            grounded = GroundedFindings(info, col, lines);
            if (grounded.Value.Accepted.Count == 0)
                throw new ItemRefusal(10, "finding-without-criterion",
                    "this is a docs-only design: a finding counts only as a line `FINDING: criterion \"<text copied from the item>\" - <what the design fails to do>` or `FINDING: file \"<path named in the design>\" - outside the scope`, with the quoted text found word for word in the item (criterion: above the design section; file: inside it). No such line was found, so there is no finding and no round was used: record the review again with REVIEW: COMPLETE as its last line, or quote the criterion.",
                    new JsonObject { ["dropped"] = grounded.Value.Dropped });
        }
        rv["round"] = Ps.Int(rv, "round") + 1; rv["last"] = verdict; rv["at"] = Ps.Iso(Now);
        rv["ref"] = verdict == "COMPLETE" ? "rv-" + Guid.NewGuid().ToString("N")[..10] : null;
        Ps.CiSet(reviews, col, rv);
        // escalate earlier: two consecutive FINDINGS rounds of this column that cite the same acceptance criterion park the item
        var repeated = verdict == "COMPLETE" ? ItemRules.RepeatedCriteria(s, $"review|{col}", [], clear: true) : ItemRules.RepeatedCriteria(s, $"review|{col}", ItemRules.CitedCriteria(info, text, Cfg));
        if (repeated.Count > 0)
        {
            await ParkRepeatedCriterion(n, st, $"review rounds in **{col}** (rounds {Ps.Int(rv, "round") - 1} and {Ps.Int(rv, "round")})", repeated, c);
            throw new ItemRefusal(10, "same-criterion-repeated", "two consecutive review rounds cite the same acceptance criterion: the item is parked with a question to the owner; stop work on it and report the failure with succeeded=false",
                new JsonObject { ["criteria"] = Ps.Array(repeated), ["round"] = Ps.Int(rv, "round") });
        }
        await store.Save(n, st, c.Ct);
        var o = new JsonObject { ["ok"] = true, ["id"] = n, ["verdict"] = verdict, ["round"] = Ps.Int(rv, "round"), ["review_ref"] = rv["ref"]?.DeepClone(), ["max_rounds"] = max };
        if (grounded is { } g)
        {
            o["findings"] = new JsonArray(g.Accepted.Select(x => (JsonNode)x).ToArray());
            o["dropped"] = g.Dropped;
        }
        return o;
    }

    /// <summary><c>Test-DocsOnlyDesignReview</c>: the docs-only fast path applies to the review of the design column once the body carries the marker.</summary>
    bool DocsOnlyDesignReview(JsonObject s, string column) => Cfg.HasSection(column) && ItemRules.ItemDocsOnly(s, null, Cfg);

    /// <summary><c>Get-GroundedFindings</c>: the findings of a docs-only design review that the item itself backs; the quote is compared with the body, whitespace folded, case kept.</summary>
    (List<JsonObject> Accepted, int Dropped) GroundedFindings(ItemInfo info, string column, IReadOnlyList<string> lines)
    {
        var body = info.Body; var sec = Cfg.SectionOf(column) ?? "";
        var at = body.IndexOf(sec, StringComparison.OrdinalIgnoreCase);
        var own = Ps.Fold(at >= 0 ? body[..at] : body);
        var design = Ps.Fold(at >= 0 ? body[at..] : "");
        var accepted = new List<JsonObject>(); var dropped = 0;
        string[] patterns = ["^[-*\\s]*FINDING:\\s*(criterion|file)\\s+\"(.+?)\"\\s+-\\s+\\S", "^[-*\\s]*FINDING:\\s*(criterion|file)\\s+\"(.+)\"\\s+-\\s+\\S"];
        foreach (var l in lines)
        {
            if (!Ps.CMatch(l, "^[-*\\s]*FINDING:")) continue;
            var ok = false;
            // the shortest quote first (it ends at the first " - ", so a remark that itself holds quotes and a dash is not taken into the quote), then the longest (a criterion that itself contains " - ")
            foreach (var pat in patterns)
            {
                if (ok) continue;
                var m = Regex.Match(l, pat, RegexOptions.CultureInvariant);
                if (!m.Success) continue;
                var kind = m.Groups[1].Value; var q = Ps.Fold(m.Groups[2].Value);
                ok = kind == "criterion" ? q.Length >= 8 && own.Contains(q, StringComparison.Ordinal) : q.Length >= 3 && design.Contains(q, StringComparison.Ordinal);
                if (ok) accepted.Add(new JsonObject { ["kind"] = kind, ["quote"] = q });
            }
            if (!ok) dropped++;
        }
        return (accepted, dropped);
    }

    /// <summary><c>Invoke-ParkRepeatedCriterion</c>: parks the item over a repeated criterion and puts one precise question to the owner on the item.</summary>
    async Task ParkRepeatedCriterion(int n, ItemStateHolder st, string what, IReadOnlyList<string> repeated, ItemCall c)
    {
        var s = st.State;
        ItemRules.SetPark(s, "same-criterion-repeated", Cfg.ParkMinutes, Now);
        ((JsonObject)s["park"]!)["criteria"] = Ps.Array(repeated);
        // forget the record of every source so the first round after the owner's answer (or the park's expiry) gets a fresh attempt
        if (Ps.Has(s, "cited")) s["cited"] = new JsonObject();
        s.Remove("claim"); await store.Save(n, st, c.Ct); await board.RemoveClaimRef(n, c.Ct);
        var list = string.Join("\n", repeated.Select(x => $"- \"{x}\""));
        await AddComment(n, $"**Question for the owner:** two consecutive {what} cite the same acceptance criterion:\n\n{list}\n\nThe factory stopped here instead of trying again, because the same criterion failing twice usually means it is ambiguous, untestable as written, or out of reach of this change. Please answer on this item: should the criterion be reworded (and how), relaxed or dropped, or is the failure a real defect to fix? Then the operator releases the park (`factory.ps1 unpark --item {n} --reason ...`) or moves the card, and the worker resumes.", c);
    }

    /// <summary><c>Move-NextInternal</c>.</summary>
    async Task<string> MoveNextInternal(ItemInfo info, CancellationToken ct)
    {
        var next = await board.NextColumn(info.ColumnText, ct);
        if (next is null) throw new ItemRefusal(10, "final-column", $"'{info.ColumnText}' is the board's last column");
        await board.MoveCard(info, next, ct);
        return next;
    }

    // Verb_move_next
    async Task<JsonObject> MoveNext(ItemCall c)
    {
        var n = c.Item;
        var info = await board.GetItemInfo(n, c.Ct); AssertOpen(info); AssertColumnMatches(info, c);
        if (Ps.NameIn(info.ColumnText, Cfg.ColumnsAi)) throw new ItemRefusal(10, "completion-owns-exit", $"'{info.ColumnText}' is left only by its completion, verdict or settle");
        if (Ps.NameIn(info.ColumnText, Cfg.ColumnsHold)) throw new ItemRefusal(10, "hold-column", $"'{info.ColumnText}' is a hold column");
        if (!info.HasLabel(Cfg.Label)) throw new ItemRefusal(10, "no-worker-label", "not this worker's item");
        var to = await MoveNextInternal(info, c.Ct);
        return new JsonObject { ["ok"] = true, ["id"] = n, ["moved_from"] = info.Column, ["moved_to"] = to };
    }
}
