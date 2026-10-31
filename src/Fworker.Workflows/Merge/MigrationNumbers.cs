using System.Text.RegularExpressions;

namespace Fworker.Workflows.Merge;

/// <summary>A DbUp script of a pull request whose number another script of the base branch already has (<c>Get-MigrationCollision</c>): the script of the pull request, the one on the base branch, and the next free number.</summary>
public sealed record MigrationCollision(string Script, string TakenBy, string Next);

/// <summary>
/// W9.10q: the migration-number rule of Prs.ps1 (<c>Get-MigrationCollision</c>), as a pure function. Defect of 2026-10-23: two parallel pull requests each added DbUp script 040 and both merged. The DbUp scripts of the pull request
/// (<c>src/Database/scripts/Update/NNN_*.sql</c>, added or renamed into place) whose NNN is already the number of a DIFFERENT script on the base branch. The merge verb and the completion of Development both ask it.
/// </summary>
public static class MigrationNumbers
{
    /// <summary>The directory of the scripts, as the rule script has it (the repository of the work items, not this one).</summary>
    public const string Directory = "src/Database/scripts/Update";

    static readonly Regex InPullRequest = new(@"^src/Database/scripts/Update/(\d+)_[^/]*\.sql$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex OnBase = new(@"^(\d+)_[^/]*\.sql$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Number = new(@"/(\d+)_[^/]*$", RegexOptions.CultureInvariant);

    /// <summary>Whether the pull request adds or moves a script into the directory at all (without one the script reads nothing from the base branch).</summary>
    public static bool Touches(IEnumerable<(string File, string Status)> prFiles) => prFiles.Any(f => !f.Status.Equals("removed", StringComparison.OrdinalIgnoreCase) && InPullRequest.IsMatch(f.File));

    /// <summary>The names of the contents listing of the directory on the base branch that are scripts, as paths (<c>dir/name</c>).</summary>
    public static IEnumerable<string> ScriptsOnBase(IEnumerable<string> names) => names.Where(n => OnBase.IsMatch(n)).Select(n => $"{Directory}/{n}");

    /// <summary>The first colliding pair and the next free number (one above the highest of the base branch and the pull request, padded as the script's number), or null when there is no collision.</summary>
    public static MigrationCollision? Find(IEnumerable<(string File, string Status)> prFiles, IEnumerable<string> onBase)
    {
        var mine = prFiles.Where(f => !f.Status.Equals("removed", StringComparison.OrdinalIgnoreCase) && InPullRequest.IsMatch(f.File)).Select(f => f.File).ToList();
        if (mine.Count == 0) return null;
        var master = onBase.ToList();
        static long Num(string f) => long.Parse(Number.Match(f).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var max = master.Concat(mine).Select(Num).Max();
        foreach (var f in mine)
        {
            var hit = master.Where(m => !string.Equals(m, f, StringComparison.OrdinalIgnoreCase) && Num(m) == Num(f) && !mine.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
            if (hit.Count == 0) continue;
            var width = Regex.Match(f, @"/(\d+)_").Groups[1].Value.Length;
            return new MigrationCollision(f, hit[0], (max + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width, '0'));
        }
        return null;
    }

    /// <summary>The text of the refusal of the merge verb (a session's call).</summary>
    public static string MergeReason(int pullRequest, MigrationCollision c, string baseBranch) =>
        $"merge refused: PR #{pullRequest} adds {c.Script}, but {c.TakenBy} on {baseBranch} already has that number; the branch's script must be renumbered to {c.Next} (never the one on {baseBranch}: DbUp runs a renamed script again)";

    /// <summary>The text of the refusal of the completion of Development.</summary>
    public static string CompleteReason(MigrationCollision c, string baseBranch) =>
        $"the pull request adds {c.Script}, but {c.TakenBy} on {baseBranch} already has that number. Renumber this branch's script to {c.Next} (never rename the script on {baseBranch}: DbUp runs a renamed script again), push, then call complete again.";
}
