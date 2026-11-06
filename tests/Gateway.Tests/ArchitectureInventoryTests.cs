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
    static readonly string[] DeclaredExceptions = ["transcripts"];

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

    // ---- what the removal of v1 took out stays out -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The types and members the removal took out (V2.3 and V3.2 of the plan): v1's AutoPromoter and the delivery train's shadow of it (the comparison with v1's pass, its store, gate, report, handlers and
    /// holder, the ShadowCommand of a shadowed environment, the shadow environments of a policy); the Runner's pool of long-lived pwsh workers and the GitHub-ref lease it put into the scripts. A name here that is in the code of gateway/ or src/ again, generated code included, fails: bringing one back is
    /// a decision, so it is a visible edit of this list. The live parts that kept a shadow name (ShadowHistory, ShadowOctopusPort, OctopusShadowIngress, DeliveryShadowSettings, DeliveryShadowWiring) are not here.
    /// </summary>
    static readonly string[] RetiredNames =
    [
        "AutoPromoter", "ObserveV1", "ObserveV2",
        "DeliveryShadow", "DeliveryShadowHandlers", "DeliveryActingShadowHandlers", "DeliveryShadowService", "DeliveryShadowHolder", "AdminDeliveryShadowEndpoints",
        "ShadowCommand", "ShadowComparer", "ShadowClassifier", "ShadowComparisonResult", "ShadowComparisonRow", "ShadowDecision", "ShadowSide", "ShadowActions", "ShadowOutcomes", "ShadowReasons",
        "ShadowGate", "ShadowLiveRow", "ShadowLiveRelease", "ShadowSummary", "IShadowComparisonStore", "SqlShadowComparisonStore", "InMemoryShadowComparisonStore", "ShadowEnvironments",
        "RunnerPool", "InjectLease", "KillBusy",
    ];

    /// <summary>
    /// Text a string literal could bring back without any of the names: the route of the shadow's report, the identity of its command message, the pool's switch, its setting and its worker host (the gateway
    /// starts no long-lived pwsh; the scenario harness's client host is the harness's own).
    /// </summary>
    static readonly string[] RetiredLiterals = ["\"/api/v1/delivery-shadow\"", "MessageIdentity(\"delivery.train.shadow-command\")", "\"FACTORY_RUNNER_POOL\"", "\"poolMaxCalls\"", "runner-host.ps1",
        "\"leader-lease\"", "\"plant-snapshot-save\"", "\"plant-snapshot-load\"", "\"plant-snapshot-clear\"", "\"plant-snapshot-await\""];

    // ---- the server scripts V3.3 deleted ----------------------------------------------------------------------------------------------------------

    /// <summary>The rule-script files V3.3 deleted: the GitHub-ref lease with the scripts' fencing (Leader.ps1) and the ref snapshot of the plant (PlantSnapshot.ps1; its transcript verbs live in Transcripts.ps1).</summary>
    static readonly string[] DeletedScripts = ["plugin/scripts/lib/Leader.ps1", "plugin/scripts/lib/PlantSnapshot.ps1", "plugin/scripts/lib/Metrics.ps1", "plugin/scripts/lib/Verbs.Manager.ps1", "plugin/scripts/lib/Replenish.ps1", "plugin/scripts/lib/Verbs.Keeper.ps1"];

    /// <summary>The verbs those scripts served: the inventory called them retired, no gateway path called them and the verb route refused them to every caller.</summary>
    static readonly string[] DeletedVerbs = ["leader-lease", "plant-snapshot-save", "plant-snapshot-load", "plant-snapshot-clear", "plant-snapshot-await"];

    /// <summary>Their functions, and the fence state every write of a script once checked: none may be defined or used again anywhere in the plugin.</summary>
    static readonly string[] DeletedFunctions = ["Verb_leader_lease", "Verb_plant_snapshot_save", "Verb_plant_snapshot_load", "Verb_plant_snapshot_clear", "Verb_plant_snapshot_await",
        "Read-LeaderLease", "Write-LeaderLease", "Get-LeaderRefPath", "Get-PlantSnapshotRefPath", "Set-LeaseFence", "Test-LeaseHeld", "Assert-LeaseHeld", "Get-LeaseFenceInfo", "Test-FencedWrite", "LeaseFence"];

    /// <summary>The script's code with its comment lines and the trailing comments of its lines blanked (a word in a comment is no use of it).</summary>
    static string ScriptCode(string text) => string.Join('\n', text.Split('\n').Select(l => Regex.Replace(l.TrimStart().StartsWith('#') ? "" : l, @"\s#\s.*$", "")));

    static List<string> DeletedScriptUses(string path, string code)
    {
        var found = new List<string>();
        foreach (var name in DeletedFunctions)
            if (Regex.IsMatch(code, $@"(?<![\w-]){Regex.Escape(name)}(?![\w-])")) found.Add($"{path}: {name}");
        foreach (var file in DeletedScripts)
            if (code.Contains(Path.GetFileName(file), StringComparison.Ordinal) || code.Contains($"'{Path.GetFileNameWithoutExtension(file)}'", StringComparison.Ordinal)) found.Add($"{path}: {file}");
        return found;
    }

    [Fact]
    public void TheServerScriptsV33Deleted_StayDeleted()
    {
        var problems = new List<string>();
        foreach (var file in DeletedScripts) if (File.Exists(Path.Combine(Root, file))) problems.Add($"{file} exists again");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Root, "plugin"), "*.ps1", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            problems.AddRange(DeletedScriptUses(Rel(f), ScriptCode(File.ReadAllText(f))));
        // and no server verb of factory.ps1 is one of them (the verb inventory follows $ServerVerbs, VerbInventoryTests)
        var factory = File.ReadAllText(Path.Combine(Root, "plugin", "scripts", "factory.ps1"));
        var list = Regex.Match(factory, @"\$ServerVerbs\s*=\s*@\((?<body>[^)]*)\)").Groups["body"].Value;
        foreach (var verb in DeletedVerbs) if (list.Contains($"'{verb}'", StringComparison.Ordinal)) problems.Add($"plugin/scripts/factory.ps1: $ServerVerbs names '{verb}' again");
        Assert.True(problems.Count == 0, "What V3.3 deleted of the server scripts is back (a decision: edit the lists of this test in the same pull request):\n  " + string.Join("\n  ", problems));
    }

    // ---- V3.3, the second part: the client is client only, the gateway's rule scripts are a fixed few ---------------------------------------------

    /// <summary>What plugin/scripts/lib may hold: the client's files (a worker downloads them). A server library here again is a server mode of the client again.</summary>
    static readonly string[] ClientLibs = ["Client.Build.ps1", "Client.Keeper.ps1", "Client.Manager.ps1", "Client.Messages.ps1", "Client.Operator.ps1", "Client.Own.ps1", "Client.Plugin.ps1",
        "Client.Subscribe.ps1", "Config.ps1", "Models.ps1", "Paths.ps1", "Progress.ps1"];

    /// <summary>The gateway's rule-script libraries (plugin/scripts/server/lib), what the verbs of rule.ps1 still reach. Metrics, Verbs.Manager, Verbs.Keeper and Replenish are gone: nothing left reaches them.</summary>
    static readonly string[] ServerLibs = ["Apps.ps1", "BaseUpdate.ps1", "Board.ps1", "Checks.ps1", "Core.ps1", "Journal.ps1", "Models.ps1", "OperatorRelease.ps1", "Owner.ps1", "Prs.ps1", "Qc.ps1",
        "Transcripts.ps1", "Verbs.Delivery.ps1", "Verbs.Inspect.ps1", "Verbs.Item.ps1", "Verbs.Merge.ps1", "Verbs.Pr.ps1", "Verbs.Sweep.ps1"];

    /// <summary>The verbs a rule script still answers: the fallbacks of the routed verbs (the hybrid merge, the merge policy other than auto, the transcript ref store, ingest-event and journal without their parts) and the gateway's housekeeping, which an inert gateway still runs (InertVerbs.Housekeeping).</summary>
    static readonly string[] ScriptVerbs = ["merge", "approve-pr", "clear-hold", "base-watch", "base-update-branch", "transcript-save", "transcript-load", "transcript-prune", "ingest-event", "journal",
        "revocations", "revoke-worker", "operator-releases", "operator-release"];

    static string[] FileNames(params string[] dir) => Directory.EnumerateFiles(Path.Combine([Root, .. dir]), "*.ps1").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void TheClientHasNoServerMode_AndTheGatewaysRuleScriptsAreTheFixedFew()
    {
        var problems = new List<string>();
        // the client
        var factory = ScriptCode(File.ReadAllText(Path.Combine(Root, "plugin", "scripts", "factory.ps1")));
        if (Regex.IsMatch(factory, @"\$ServerVerbs\b|\$NoLockVerbs\b|Use-ItemLock|'Verb_'")) problems.Add("plugin/scripts/factory.ps1 has a server mode again ($ServerVerbs, the verb dispatch or the item lock)");
        if (!factory.Contains("'no-server-mode'", StringComparison.Ordinal)) problems.Add("plugin/scripts/factory.ps1 no longer refuses --server (no-server-mode)");
        foreach (var f in FileNames("plugin", "scripts", "lib").Except(ClientLibs)) problems.Add($"plugin/scripts/lib/{f}: not a client file (the server's libraries are plugin/scripts/server/lib)");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Root, "plugin"), "*.ps1", SearchOption.AllDirectories).Where(f => !Rel(f).StartsWith("plugin/scripts/server/", StringComparison.Ordinal)))
        {
            var code = ScriptCode(File.ReadAllText(f));
            if (Regex.IsMatch(code, @"server/lib|(?m)^function Verb_")) problems.Add($"{Rel(f)}: a client file loads a server library or defines a server verb");
            // a client script that dot-sources a deleted library fails only when that path runs (worker-token loaded Core.ps1)
            foreach (Match m in Regex.Matches(code, @"Join-Path\s+\$PSScriptRoot\s+['""]([^'""$]+\.ps1)['""]"))
                if (!File.Exists(Path.Combine(Path.GetDirectoryName(f)!, m.Groups[1].Value))) problems.Add($"{Rel(f)}: loads {m.Groups[1].Value}, which is not there");
        }
        // the server
        var server = FileNames("plugin", "scripts", "server", "lib");
        if (!server.SequenceEqual(ServerLibs.Order(StringComparer.Ordinal))) problems.Add($"plugin/scripts/server/lib holds [{string.Join(", ", server)}], the test pins [{string.Join(", ", ServerLibs)}]");
        var rule = File.ReadAllText(Path.Combine(Root, "plugin", "scripts", "server", "rule.ps1"));
        var verbs = Regex.Matches(Regex.Match(rule, @"\$ServerVerbs\s*=\s*@\((?<b>[^)]*)\)").Groups["b"].Value, @"'([a-z0-9-]+)'").Select(m => m.Groups[1].Value).ToArray();
        if (!verbs.Order(StringComparer.Ordinal).SequenceEqual(ScriptVerbs.Order(StringComparer.Ordinal))) problems.Add($"server/rule.ps1 answers [{string.Join(", ", verbs)}], the test pins [{string.Join(", ", ScriptVerbs)}]");
        foreach (var f in server)
            foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(Root, "plugin", "scripts", "server", "lib", f)), @"(?m)^function Verb_([a-z0-9_]+)\b"))
                if (!ScriptVerbs.Contains(m.Groups[1].Value.Replace('_', '-'))) problems.Add($"plugin/scripts/server/lib/{f}: Verb_{m.Groups[1].Value} is no verb of rule.ps1 (v2 answers it alone)");
        // the inert gateway's housekeeping is what rule.ps1 answers of it, and the gateway runs rule.ps1, never the client
        foreach (var v in InertVerbs.Housekeeping) if (!ScriptVerbs.Contains(v)) problems.Add($"InertVerbs.Housekeeping names '{v}', which no rule script answers");
        var options = File.ReadAllText(Path.Combine(Root, "gateway", "Configuration", "GatewayOptions.cs"));
        if (!options.Contains("Entry = Path.Combine(scriptsDir, \"server\", \"rule.ps1\")", StringComparison.Ordinal)) problems.Add("gateway/Configuration/GatewayOptions.cs: the Runner's entry is not plugin/scripts/server/rule.ps1");
        // and what a worker downloads leaves the gateway's rule scripts out
        if (PluginArchive.Files(Path.Combine(Root, "plugin")).Any(f => Rel(f).StartsWith("plugin/scripts/server/", StringComparison.Ordinal))) problems.Add("the plugin archive carries plugin/scripts/server/");
        Assert.True(problems.Count == 0, "The client and the gateway's rule scripts (V3.3):\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void TheDeletedScriptsCheck_SeesAViolation_WhenOneIsPlanted()
    {
        Assert.NotEmpty(DeletedScriptUses("x.ps1", ScriptCode("function Verb_leader_lease($a) { }")));
        Assert.NotEmpty(DeletedScriptUses("x.ps1", ScriptCode("    if ($script:LeaseFence -and (Test-FencedWrite $Method $Path $Body)) { Assert-LeaseHeld 'x' }")));
        Assert.NotEmpty(DeletedScriptUses("x.ps1", ScriptCode(". (Join-Path $here 'lib/Leader.ps1')")));
        Assert.NotEmpty(DeletedScriptUses("x.ps1", ScriptCode("foreach ($f in 'Core', 'PlantSnapshot') { . (Join-Path $here \"lib/$f.ps1\") }")));
        // a comment that names them, and a live name that contains one of the words, are no use
        Assert.Empty(DeletedScriptUses("x.ps1", ScriptCode("# Assert-LeaseHeld and lib/Leader.ps1 are gone (V3.3)\n$x = Get-TranscriptRefs   # not Set-LeaseFence")));
    }

    /// <summary>The code with its string literals emptied, so that a log line or a history type that says a retired word is no use of it.</summary>
    static string Identifiers(string code) => Regex.Replace(code, @"""(?:\\.|[^""\\\n])*""", "\"\"");

    static List<string> RetiredUses(string path, string code)
    {
        var found = new List<string>();
        var ids = Identifiers(code);
        foreach (var name in RetiredNames)
            if (Regex.IsMatch(ids, $@"\b{name}\b")) found.Add($"{path}: {name}");
        // DeliveryPolicy.Shadow, read as a member or set as a named argument or a property pattern
        if (Regex.IsMatch(ids, @"\.Shadow\b|[(,{]\s*Shadow\s*:")) found.Add($"{path}: DeliveryPolicy.Shadow");
        foreach (var literal in RetiredLiterals)
            if (code.Contains(literal, StringComparison.Ordinal)) found.Add($"{path}: {literal}");
        return found;
    }

    [Fact]
    public void TheRetiredV1AndShadowNames_AreNotInTheCode()
    {
        var found = Sources("gateway").Concat(Sources("src")).SelectMany(s => RetiredUses(s.Path, s.Code)).ToList();
        Assert.True(found.Count == 0, "What the removal of v1 took out is in the code again (a decision: edit RetiredNames in the same pull request):\n  " + string.Join("\n  ", found));
    }

    [Fact]
    public void TheRetiredNamesCheck_SeesAViolation_WhenOneIsPlanted()
    {
        Assert.NotEmpty(RetiredUses("x.cs", ArchitectureTests.Code("public static void Handle(ShadowCommand m, DeliveryShadow s) { }")));
        Assert.NotEmpty(RetiredUses("x.cs", ArchitectureTests.Code("if (policy is { Shadow: true }) return;")));
        Assert.NotEmpty(RetiredUses("x.cs", ArchitectureTests.Code("var acts = p.Acts && !p.Shadow;")));
        Assert.NotEmpty(RetiredUses("x.cs", ArchitectureTests.Code("routes.MapGet(\"/api/v1/delivery-shadow\", h);")));
        Assert.NotEmpty(RetiredUses("x.cs", ArchitectureTests.Code("readonly RunnerPool? pool = RunnerPool.Create(entry);")));
        Assert.NotEmpty(RetiredUses("x.cs", ArchitectureTests.Code("var size = Environment.GetEnvironmentVariable(\"FACTORY_RUNNER_POOL\");")));
        Assert.NotEmpty(RetiredUses("x.cs", ArchitectureTests.Code("var host = Path.Combine(dir, \"runner-host.ps1\");")));
        Assert.NotEmpty(RetiredUses("x.cs", ArchitectureTests.Code("await runner.RunAsync(\"plant-snapshot-save\", body, timeout);")));
        // the live names that kept a shadow word, a comment and a string are no use
        Assert.Empty(RetiredUses("x.cs", ArchitectureTests.Code("new ShadowHistoryRecord(k, t, d, at); var p = new ShadowOctopusPort(f, true); // the DeliveryShadow is gone\nlog(\"the AutoPromoter is gone\");")));
    }

    /// <summary>The body of a method of a source file, from its signature to the line that closes it at the indentation of the signature (comments blanked).</summary>
    static string MethodBody(string file, string signature)
    {
        var code = ArchitectureTests.Code(File.ReadAllText(Path.Combine(Root, file.Replace('/', Path.DirectorySeparatorChar))));
        var at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{file}: '{signature}' is gone (the test names the method it reads)");
        var indent = at - (code.LastIndexOf('\n', at) + 1);
        var end = code.IndexOf("\n" + new string(' ', indent) + "}", at, StringComparison.Ordinal);
        return code[at..(end < 0 ? code.Length : end)];
    }

    /// <summary>The words of a rule-script call: the Runner's script path, the board verbs' script part, any verb call of the Runner, the verb of v1's sweep.</summary>
    static readonly Regex ScriptCall = new(@"\bRunScriptAsync\b|\bRuleScript\b|\.RunAsync\s*\(|""sync-board""", RegexOptions.Compiled);

    [Fact]
    public void V1sSweepIsNotRun_TheVerbIsTheReconcilersPass_AndThePlantFeedCallsNoScript()
    {
        // V3.2: sync-board answers with the board reconciler's pass; v1's Invoke-SyncBoard would act on the saga's items beside the engine
        var verb = MethodBody("gateway/Verbs/BoardVerbs.cs", "public async Task<VerbAnswer> SyncBoard(");
        Assert.False(Regex.IsMatch(Identifiers(verb), @"\bRunScriptAsync\b|\bRuleScript\b"), "BoardVerbs.SyncBoard runs the rule script again:\n" + verb);
        Assert.Contains("reconciler.Run(", verb);
        // the plant's feed reads the saga's state and the cards' labels only
        var feed = ArchitectureTests.Code(File.ReadAllText(Path.Combine(Root, "gateway", "Sweeper.cs")));
        Assert.False(ScriptCall.IsMatch(feed), "gateway/Sweeper.cs calls a rule script or the verb of v1's sweep: " + ScriptCall.Match(feed).Value);
    }

    [Fact]
    public void TheScriptCallCheck_SeesAViolation_WhenOneIsPlanted()
    {
        Assert.Matches(ScriptCall, "var (code, output) = await p.RuleScript(verb, stdin, timeout);");
        Assert.Matches(ScriptCall, "await runner.RunAsync(\"sync-board\", body, timeout);");
        Assert.Matches(ScriptCall, "RuleScript = runner.RunScriptAsync,");
        Assert.DoesNotMatch(ScriptCall, "var r = await reconciler.Run(cts.Token, only, dry);");
    }

    // ---- the routed verbs' fallbacks V3.3 (slice 6) removed ------------------------------------------------------------------------------------

    /// <summary>The routed verbs that had a rule script behind them (the transcripts' database backend, which keeps a read-only fallback of the ref store until 2026-11-13, is checked for its prune only): the merge train's hybrid composition (an item the saga does not own, a policy other than auto), ingest-event without a translator, journal without a ledger.</summary>
    static readonly string[] RoutedVerbFiles = ["gateway/Verbs/MergeVerbs.cs", "gateway/Verbs/BoardVerbs.cs", "gateway/Ports/BoardPort.cs"];

    static readonly Regex FallbackCall = new(@"\bRunScriptAsync\b|\bRuleScript\b|\bScript\s*\(|GetRequiredService<Runner>|\.RunAsync\s*\(", RegexOptions.Compiled);

    static List<string> FallbackUses(string path, string code)
    {
        var found = FallbackCall.Matches(Identifiers(code)).Select(m => $"{path}: {m.Value}").ToList();
        // the transcripts' database backend may read the ref store (read-only, until 2026-11-13) but prunes the database alone
        if (Regex.Match(code, @"public\s+async\s+Task\s+Prune\s*\(int\s+days\)\s*\{(?<body>[^}]*)\}") is { Success: true } prune && code.Contains("class DatabaseTranscriptBackend", StringComparison.Ordinal)
            && Regex.IsMatch(prune.Groups["body"].Value, @"\blegacy\b"))
            found.Add($"{path}: DatabaseTranscriptBackend prunes the ref store");
        return found;
    }

    [Fact]
    public void TheRoutedVerbs_RunNoRuleScript_AndTheTranscriptsDatabasePrunesNoRefs()
    {
        var found = RoutedVerbFiles.Append("gateway/TranscriptBackends.cs")
            .SelectMany(f => FallbackUses(f, ArchitectureTests.Code(File.ReadAllText(Path.Combine(Root, f.Replace('/', Path.DirectorySeparatorChar)))))).ToList();
        Assert.True(found.Count == 0, "A routed verb hands a call to the rule script again (a decision: V3.3 removed these fallbacks):\n  " + string.Join("\n  ", found));
    }

    [Fact]
    public void TheFallbackCheck_SeesAViolation_WhenOneIsPlanted()
    {
        Assert.NotEmpty(FallbackUses("x.cs", ArchitectureTests.Code("return answer ?? await Script(verb, stdin, timeout);")));
        Assert.NotEmpty(FallbackUses("x.cs", ArchitectureTests.Code("RuleScript = sp.GetRequiredService<Runner>().RunScriptAsync,")));
        Assert.NotEmpty(FallbackUses("x.cs", ArchitectureTests.Code("sealed class DatabaseTranscriptBackend(ITranscriptBackend? legacy) : ITranscriptBackend\n{\n    public async Task Prune(int days)\n    {\n        if (legacy != null) await legacy.Prune(days);\n    }\n}")));
        // a comment and a log line that name the script are no use of it
        Assert.Empty(FallbackUses("x.cs", ArchitectureTests.Code("// the rule script's RunScriptAsync is gone\nlog(\"no Script(...) any more\");\nsealed class DatabaseTranscriptBackend(ITranscriptBackend? legacy) : ITranscriptBackend\n{\n    public async Task Prune(int days)\n    {\n        if (ready()) await repository.Prune(days);\n    }\n}")));
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
