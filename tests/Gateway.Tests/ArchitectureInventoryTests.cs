using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10l (docs/design/fworker-wolverine-implementation.md, row W9.10l; docs/design/verb-inventory.md, "Internal callers and v1 fallbacks"): the gateway runs rule scripts on its own, outside the verb pipeline, and a few
/// verbs that have a v2 owner still fall back to the script. This is the allow-list of both, read as text like <see cref="ArchitectureTests"/>: a call that is in the code and not in docs/design/verb-inventory.json fails, an
/// entry that is no longer in the code fails (delete it in the pull request that removes the call), an owner or a leaving row that is not a row of the plan fails, and, once the server rule scripts are gone (W9.11b), any
/// entry without a declared exception fails. The same file checks that there is one lock of an item (<c>IItemLock</c>), not one for each port.
/// </summary>
public class ArchitectureInventoryTests
{
    static readonly string Root = RepoRoot();

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gateway", "Program.cs")) && File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) return d.FullName;
        throw new InvalidOperationException("the repository root (gateway/Program.cs) was not found above " + AppContext.BaseDirectory);
    }

    static string Rel(string f) => Path.GetRelativePath(Root, f).Replace('\\', '/');
    static bool Built(string f) => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    static IEnumerable<(string Path, string Code)> Sources(string dir) =>
        Directory.EnumerateFiles(Path.Combine(Root, dir), "*.cs", SearchOption.AllDirectories).Where(Built).OrderBy(f => f, StringComparer.Ordinal).Select(f => (Rel(f), ArchitectureTests.Code(File.ReadAllText(f))));

    // ---- the inventory (docs/design/verb-inventory.json) ---------------------------------------------------------------------------------------

    sealed record Exception_(string Until, string Reason);
    sealed record Caller(string Id, string File, int Sites, string[] Verbs, string Kind, string Owner, string Leaves, Exception_? Exception);
    sealed record Fallback(string Id, string Kind, string File, string? Marker, int Sites, string Owner, string Leaves, Exception_? Exception);

    static readonly JsonObject Inventory = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "docs", "design", "verb-inventory.json")))!;

    static Exception_? Exc(JsonNode? n) => n is JsonObject o ? new Exception_(o["until"]!.ToString(), o["reason"]!.ToString()) : null;

    static readonly List<Caller> Callers = ((JsonArray)Inventory["internalCallers"]!).Select(n => new Caller(n!["id"]!.ToString(), n["file"]!.ToString(), (int)n["sites"]!, ((JsonArray)n["verbs"]!).Select(v => v!.ToString()).ToArray(),
        n["kind"]!.ToString(), n["owner"]!.ToString(), n["leaves"]!.ToString(), Exc(n["exception"]))).ToList();

    static readonly List<Fallback> Fallbacks = ((JsonArray)Inventory["v1Fallbacks"]!).Select(n => new Fallback(n!["id"]!.ToString(), n["kind"]!.ToString(), n["file"]!.ToString(), n["marker"]?.ToString(), (int)n["sites"]!,
        n["owner"]!.ToString(), n["leaves"]!.ToString(), Exc(n["exception"]))).ToList();

    /// <summary>The exceptions that may exist; a new one is a decision, so it is a visible edit of this list.</summary>
    static readonly string[] DeclaredExceptions = ["auto-promote", "leader-lease", "plant-snapshot", "transcripts", "merge-peer-approved", "merge-hybrid-script"];

    static readonly string[] Kinds = ["runner", "router"];

    /// <summary>The rows of the plan (the first cell of its tables).</summary>
    static readonly HashSet<string> PlanRows = File.ReadAllLines(Path.Combine(RepoRoot(), "docs", "design", "fworker-wolverine-implementation.md"))
        .Select(l => Regex.Match(l, @"^\| (W\d+\.\d+[a-z]?) \|")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToHashSet();

    // ---- the calls of the gateway itself ----------------------------------------------------------------------------------------------------------

    /// <summary>Every <c>.RunAsync</c> and <c>.RunScriptAsync</c> of gateway/ outside gateway/Verbs, per file: the literal verbs (<c>RunAsync("verb"</c>) and the count of sites (a method group or a variable verb counts as a site with no literal).
    /// <c>RunScriptAsync</c> is the rule script whatever v2 answers, so a gateway call of it is a script call on every engine; it is counted outside the route side (gateway/Ports and gateway/Identity), where it is a route's own fallback.</summary>
    static Dictionary<string, (int Sites, List<string> Verbs)> FoundCalls()
    {
        var found = new Dictionary<string, (int, List<string>)>();
        foreach (var (path, code) in Sources("gateway"))
        {
            if (path.StartsWith("gateway/Verbs/", StringComparison.Ordinal)) continue;   // the router's own files: the issue lists what is outside it
            var verbs = new List<string>(); var sites = 0;
            var routeSide = path.StartsWith("gateway/Ports/", StringComparison.Ordinal) || path.StartsWith("gateway/Identity/", StringComparison.Ordinal);
            foreach (Match m in Regex.Matches(code, routeSide ? @"\.RunAsync\b" : @"\.Run(?:Script)?Async\b"))
            {
                sites++;
                if (Regex.Match(code[(m.Index + m.Length)..], @"^\(\s*""([a-z0-9-]+)""") is { Success: true } lit) verbs.Add(lit.Groups[1].Value);
            }
            if (sites > 0) found[path] = (sites, verbs);
        }
        return found;
    }

    [Fact]
    public void EveryRunAsyncCallOutsideTheVerbRouter_IsInTheInventory_WithItsVerbs_AndNothingElseIs()
    {
        var found = FoundCalls(); var problems = new List<string>();
        var listed = Callers.GroupBy(c => c.File).ToDictionary(g => g.Key, g => (Sites: g.Sum(c => c.Sites), Verbs: g.SelectMany(c => c.Verbs).OrderBy(v => v, StringComparer.Ordinal).ToList()));
        foreach (var (file, f) in found)
        {
            if (!listed.TryGetValue(file, out var l)) { problems.Add($"{file}: {f.Sites} .RunAsync call(s) [{string.Join(", ", f.Verbs)}] are not in docs/design/verb-inventory.json (internalCallers): add an entry with the plan row that gives the call a v2 owner"); continue; }
            if (l.Sites != f.Sites) problems.Add($"{file}: the code has {f.Sites} .RunAsync call(s), the inventory lists {l.Sites}");
            var fv = f.Verbs.OrderBy(v => v, StringComparer.Ordinal).ToList();
            if (!fv.SequenceEqual(l.Verbs)) problems.Add($"{file}: the code names the verbs [{string.Join(", ", fv)}], the inventory [{string.Join(", ", l.Verbs)}]");
        }
        foreach (var file in listed.Keys.Where(k => !found.ContainsKey(k))) problems.Add($"{file}: the inventory lists calls that are no longer in the code: delete the entries");
        Assert.True(problems.Count == 0, "The gateway's own rule-script calls differ from docs/design/verb-inventory.json:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void TheInventoryIsWellFormed_EveryEntryNamesItsOwnerRowAndTheRowWhereItLeaves()
    {
        var problems = new List<string>();
        var ids = Callers.Select(c => c.Id).Concat(Fallbacks.Select(f => f.Id)).ToList();
        foreach (var dup in ids.GroupBy(i => i).Where(g => g.Count() > 1)) problems.Add($"{dup.Key}: listed twice");
        foreach (var c in Callers)
        {
            if (!Kinds.Contains(c.Kind)) problems.Add($"{c.Id}: kind '{c.Kind}' is none of {string.Join(", ", Kinds)}");
            if (!PlanRows.Contains(c.Owner)) problems.Add($"{c.Id}: owner '{c.Owner}' is not a row of the plan");
            if (c.Kind != "router" && !PlanRows.Contains(c.Leaves)) problems.Add($"{c.Id}: leaves '{c.Leaves}' is not a row of the plan");
            if (c.Kind == "router" && c.Leaves != "") problems.Add($"{c.Id}: a router forward runs no script and never leaves");
            if (c.Sites < 1) problems.Add($"{c.Id}: sites {c.Sites}");
            if (c.Verbs.Length > c.Sites) problems.Add($"{c.Id}: {c.Verbs.Length} verbs on {c.Sites} site(s)");
        }
        foreach (var f in Fallbacks)
        {
            if (!PlanRows.Contains(f.Owner)) problems.Add($"{f.Id}: owner '{f.Owner}' is not a row of the plan");
            if (!PlanRows.Contains(f.Leaves)) problems.Add($"{f.Id}: leaves '{f.Leaves}' is not a row of the plan");
            if (f.Kind is not ("merge-script" or "behavior-gap")) problems.Add($"{f.Id}: kind '{f.Kind}'");
            if ((f.Kind == "behavior-gap") != (f.Marker is null)) problems.Add($"{f.Id}: a behavior-gap has no marker in the code, every other kind has one");
        }
        foreach (var e in Callers.Select(c => (c.Id, c.Exception)).Concat(Fallbacks.Select(f => (f.Id, f.Exception))).Where(x => x.Exception is not null))
        {
            if (e.Exception!.Until is not ("W9.11b" or "W9.12")) problems.Add($"{e.Id}: an exception runs until W9.11b or W9.12, not '{e.Exception.Until}'");
            if (e.Exception.Reason.Length < 40) problems.Add($"{e.Id}: an exception says why");
        }
        Assert.True(problems.Count == 0, "docs/design/verb-inventory.json:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void TheDeclaredExceptions_AreExactlyTheOnesTheTestPins()
    {
        var declared = Callers.Where(c => c.Exception is not null).Select(c => c.Id).Concat(Fallbacks.Where(f => f.Exception is not null).Select(f => f.Id)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(DeclaredExceptions.OrderBy(x => x, StringComparer.Ordinal), declared);
    }

    [Fact]
    public void BeforeW911_EveryEntryWithoutAnException_LeavesAtARowBeforeW911()
    {
        // an entry without an exception is a call or a fallback that a row before the cutover removes; one that leaves at W9.11 or later would be a reason to keep v1
        static bool AtOrAfterW911(string row) => row.StartsWith("W9.11", StringComparison.Ordinal) || row.StartsWith("W9.12", StringComparison.Ordinal) || row.StartsWith("W9.13", StringComparison.Ordinal);
        var late = Callers.Where(c => c.Kind != "router" && c.Exception is null && AtOrAfterW911(c.Leaves)).Select(c => $"{c.Id} leaves at {c.Leaves}")
            .Concat(Fallbacks.Where(f => f.Exception is null && AtOrAfterW911(f.Leaves)).Select(f => $"{f.Id} leaves at {f.Leaves}")).ToList();
        Assert.True(late.Count == 0, "These need a declared exception (a decision of the owner) or an earlier row:\n  " + string.Join("\n  ", late));
    }

    [Fact]
    public void OnceTheServerRuleScriptsAreGone_TheAllowListHoldsOnlyTheDeclaredExceptions()
    {
        // W9.11b removes the server rule scripts; the first of them is the item verbs'. From then on nothing may be left that is not a declared exception (the list "must be empty before W9.11").
        if (File.Exists(Path.Combine(Root, "plugin", "scripts", "lib", "Verbs.Item.ps1"))) return;
        var left = Callers.Where(c => c.Kind != "router" && c.Exception is null).Select(c => c.Id).Concat(Fallbacks.Where(f => f.Exception is null).Select(f => f.Id)).ToList();
        Assert.True(left.Count == 0, "The server rule scripts are gone but these calls and fallbacks are not:\n  " + string.Join("\n  ", left));
    }

    // ---- the v1 fallbacks ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheItemVerbsLeaveNothingToTheRuleScript_AndEveryFallbackMarkerIsStillInTheCode()
    {
        var problems = new List<string>();
        // W9.10q closed the item verbs' fallbacks: nothing hands an item verb to the script (ToV1, IItemV1, ItemDelegated, ItemVerbsV1) any more, and no entry of the inventory is one
        foreach (var (path, code) in Sources("src/Fworker.Workflows").Concat(Sources("gateway")))
            foreach (Match m in Regex.Matches(code, @"\b(ToV1\(|IItemV1\b|ItemDelegated\b|ItemVerbsV1\b)"))
                problems.Add($"{path}: {m.Value.TrimEnd('(')} hands an item verb to the rule script; the item verbs run no script (W9.10q)");
        foreach (var f in Fallbacks.Where(f => f.Kind == "item-v1")) problems.Add($"{f.Id}: an item-v1 fallback is a call the item verbs leave to the script; there are none");
        // every entry's marker is in its file the number of times it says
        foreach (var f in Fallbacks.Where(f => f.Marker is not null))
        {
            var path = Path.Combine(Root, f.File.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { problems.Add($"{f.Id}: {f.File} does not exist"); continue; }
            var n = Regex.Matches(ArchitectureTests.Code(File.ReadAllText(path)), Regex.Escape(f.Marker!)).Count;
            if (n != f.Sites) problems.Add($"{f.Id}: '{f.Marker}' is in {f.File} {n} time(s), the inventory says {f.Sites}" + (n == 0 ? " (the fallback is gone: delete the entry)" : ""));
        }
        Assert.True(problems.Count == 0, "The v1 fallbacks differ from docs/design/verb-inventory.json:\n  " + string.Join("\n  ", problems));
    }

    // ---- the document says what the JSON says --------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDocument_HasARowForEveryEntryOfTheInventory()
    {
        var md = File.ReadAllText(Path.Combine(Root, "docs", "design", "verb-inventory.md"));
        var missing = Callers.Select(c => c.Id).Concat(Fallbacks.Select(f => f.Id)).Where(id => !md.Contains($"| {id} |", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "docs/design/verb-inventory.md has no row for: " + string.Join(", ", missing));
    }

    // ---- one lock of an item -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThereIsOneItemLockInterface_AndTheGatewayHasOneImplementationOfIt()
    {
        var all = Sources("src").Concat(Sources("gateway")).ToList();
        var decl = all.Where(s => Regex.IsMatch(s.Code, @"\binterface\s+IItemLock\b")).Select(s => s.Path).ToList();
        Assert.True(decl.Count == 1 && decl[0] == "src/Fworker.Contracts/Seams/ItemLock.cs", $"IItemLock is declared in: {string.Join(", ", decl)}; there is one, in src/Fworker.Contracts/Seams/ItemLock.cs");
        // classes that implement it: the file lock of the gateway (every port's), and the in-process one a JournalLedger takes when it is given none
        var impls = all.SelectMany(s => Regex.Matches(s.Code, @"\bclass\s+(\w+)\b[^{;]*?:\s*[^{;]*\bIItemLock\b").Select(m => $"{m.Groups[1].Value} ({s.Path})")).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(["FileItemLock (gateway/Ports/ItemLocks.cs)", "InProcessItemLock (src/Fworker.Integrations/Commands/InProcessItemLock.cs)"], impls);
    }

    [Fact]
    public void TheGatewayBuildsNoItemLockOfItsOwn_ItAsksForTheRegisteredOne()
    {
        var problems = new List<string>();
        foreach (var (path, code) in Sources("gateway"))
        {
            // the lock is constructed in ItemLocks.cs (the type and the lookup), by the registration, and as the default of a table built without the gateway's services (ReadModelsPort, for tests)
            if (path is not ("gateway/Ports/ItemLocks.cs" or "gateway/Composition/GatewayServiceCollectionExtensions.cs" or "gateway/Ports/ReadModelsPort.cs" or "gateway/Runtimes/JournalCommandService.cs")
                && Regex.IsMatch(code, @"new\s+(FileItemLock|InProcessItemLock)\b")) problems.Add($"{path}: constructs an item lock; take the registered one (ItemLocks.From, or IItemLock from the services)");
            // a ledger the gateway makes writes its lines under that lock too
            foreach (Match m in Regex.Matches(code, @"new\s+JournalLedger\s*\(([^;]*);"))
                if (!m.Groups[1].Value.Contains("itemLock:", StringComparison.Ordinal)) problems.Add($"{path}: new JournalLedger(...) without itemLock:, so its lines would not exclude a verb or a script on the same item");
            if (Regex.IsMatch(code, @"ConcurrentDictionary<int,\s*SemaphoreSlim>|Dictionary<int,\s*SemaphoreSlim>")) problems.Add($"{path}: a lock for each item of its own");
            // the item-N.lock file is opened in one place; a port that opens it itself is a second lock (the keeper's was, before W9.10l)
            if (path != "gateway/Ports/ItemLocks.cs" && Regex.IsMatch(code, @"item-\{[^}]*\}\.lock")) problems.Add($"{path}: opens the item lock file itself; use the registered IItemLock");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
