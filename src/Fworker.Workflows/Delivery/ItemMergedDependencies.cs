using System.Text.RegularExpressions;
using Fworker.Contracts.Ids;

namespace Fworker.Workflows.Delivery;

/// <summary>
/// The items an item waits for, from "Depends on" in its body: the port of Get-ItemDependencyRefs (Verbs.Sweep.ps1, issue 65 and W0.4), with the foreign references kept
/// qualified (hook H5 of system-gateway.md: ItemMerged carries them, the delivered-to table answers for them). "#N" is the home repository's item N, "repo#N" takes
/// the home owner, "owner/repo#N" is that item; the item's own number is never a dependency. Any case, a colon allowed, several on one line, no list on later lines.
/// </summary>
public static class ItemMergedDependencies
{
    static readonly Regex Line = new(@"\bdepends[ \t]+on[ \t]*:?[ \t]*([^\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    // the empty alternative: a bare #N after anything ("#1/#2", "#1-#2") is a home ref, as in v1
    static readonly Regex Ref = new(@"(?:(?<![\w.\-/])(?:(?<o>[A-Za-z0-9][A-Za-z0-9\-]*)/(?<r>[A-Za-z0-9_.\-]+)|(?<r>[A-Za-z0-9_.\-]+))|)#(?<n>\d{1,7})\b", RegexOptions.CultureInvariant);

    /// <summary>The dependencies in the order they are written, without repeats. <paramref name="item"/> is the item whose body it is (its repository is the home for "#N").</summary>
    public static IReadOnlyList<ItemId> Parse(ItemId item, string? body)
    {
        var found = new List<ItemId>(); var seen = new HashSet<ItemId>();
        foreach (Match m in Line.Matches(body ?? ""))
            foreach (Match d in Ref.Matches(m.Groups[1].Value))
            {
                var owner = d.Groups["o"].Success ? d.Groups["o"].Value : item.Repository.Owner;
                var repo = d.Groups["r"].Success ? d.Groups["r"].Value : item.Repository.Repo;
                if (!RepoId.TryParse($"{owner}/{repo}", out var rid) || !int.TryParse(d.Groups["n"].Value, out var n) || n < 1) continue;
                var dep = new ItemId(rid, n);
                if (dep == item || !seen.Add(dep)) continue;
                found.Add(dep);
            }
        return found;
    }
}
