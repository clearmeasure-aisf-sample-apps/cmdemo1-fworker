using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The verb inventory (task W1.10, docs/design/verb-inventory.md and .json): the one list of every server verb with its owner and destination. It
/// fails when a verb is added to or removed from $ServerVerbs of plugin/scripts/factory.ps1 without an entry in the inventory, or the reverse, when
/// the table of the document and the JSON differ, and when the callers, the no-lock flag or the use-endpoint flag of an entry differ from the code
/// (VerbPolicy, $NoLockVerbs). Documentation and data only: no product behaviour is read or changed.
/// </summary>
public class VerbInventoryTests
{
    static readonly string Root = RepoRoot();

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gateway", "Program.cs")) && File.Exists(Path.Combine(d.FullName, "plugin", "scripts", "factory.ps1"))) return d.FullName;
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    record Entry(string Verb, string Script, string Owner, string Destination, string[] Callers, bool NoLock, bool UseEndpoint);

    static readonly string FactoryPs1 = File.ReadAllText(Path.Combine(Root, "plugin", "scripts", "factory.ps1"));

    /// <summary>The quoted names of a "$Name = @( ... )" list of factory.ps1.</summary>
    static List<string> ScriptList(string name)
    {
        var m = Regex.Match(FactoryPs1, @"\$" + name + @"\s*=\s*@\((?<body>[^)]*)\)");
        Assert.True(m.Success, $"${name} was not found in plugin/scripts/factory.ps1");
        return Regex.Matches(m.Groups["body"].Value, @"'([a-z0-9-]+)'").Select(x => x.Groups[1].Value).ToList();
    }

    static (List<Entry> Verbs, List<string> GatewayVerbs, List<string> Kinds) Inventory()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "docs", "design", "verb-inventory.json")));
        var r = doc.RootElement;
        var verbs = r.GetProperty("verbs").EnumerateArray().Select(e => new Entry(
            e.GetProperty("verb").GetString()!, e.GetProperty("script").GetString()!, e.GetProperty("owner").GetString()!,
            e.GetProperty("destination").GetString()!, e.GetProperty("callers").EnumerateArray().Select(c => c.GetString()!).ToArray(),
            e.GetProperty("noLock").GetBoolean(), e.GetProperty("useEndpoint").GetBoolean())).ToList();
        return (verbs, r.GetProperty("gatewayVerbs").EnumerateArray().Select(x => x.GetString()!).ToList(),
            r.GetProperty("destinationKinds").EnumerateArray().Select(x => x.GetString()!).ToList());
    }

    [Fact]
    public void EveryServerVerb_HasOneInventoryEntry_AndNothingElseHasOne()
    {
        var script = ScriptList("ServerVerbs");
        Assert.Equal(script.Count, script.Distinct().Count());
        var inv = Inventory().Verbs.Select(e => e.Verb).ToList();
        Assert.Equal(inv.Count, inv.Distinct().Count());
        Assert.Empty(script.Except(inv).OrderBy(x => x));   // a verb added to factory.ps1 needs an entry in docs/design/verb-inventory.json and a row in the .md
        Assert.Empty(inv.Except(script).OrderBy(x => x));   // an entry whose verb is gone from factory.ps1 is removed
    }

    [Fact]
    public void EveryServerVerb_HasARuleScriptFunction_InTheFileTheInventoryNames()
    {
        var libDir = Path.Combine(Root, "plugin", "scripts", "lib");
        foreach (var e in Inventory().Verbs)
        {
            var file = Path.Combine(libDir, e.Script);
            Assert.True(File.Exists(file), $"{e.Verb}: {e.Script} does not exist");
            Assert.Matches(@"(?m)^function Verb_" + e.Verb.Replace('-', '_') + @"\b", File.ReadAllText(file));
        }
    }

    [Fact]
    public void EveryEntry_HasAnOwner_AndAKnownDestination()
    {
        var (verbs, _, kinds) = Inventory();
        foreach (var e in verbs)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Owner), $"{e.Verb} has no owner");
            Assert.Contains(e.Destination, kinds);
        }
    }

    [Fact]
    public void TheCallersOfEveryEntry_AreTheOnesVerbPolicyAllows()
    {
        var sets = new Dictionary<string, HashSet<string>>
        {
            ["owner"] = VerbPolicy.OwnerVerbs, ["inspector"] = VerbPolicy.InspectorVerbs, ["worker"] = VerbPolicy.WorkerVerbs,
            ["owner-runtime"] = VerbPolicy.GitCredentialVerbs, ["manager"] = VerbPolicy.ManagerVerbs, ["keeper"] = VerbPolicy.KeeperVerbs,
            ["qc"] = VerbPolicy.QcVerbs, ["qc-click"] = VerbPolicy.QcClickVerbs
        };
        var (verbs, gateway, _) = Inventory();
        foreach (var e in verbs)
        {
            Assert.All(e.Callers, c => Assert.True(sets.ContainsKey(c), $"{e.Verb}: unknown caller '{c}'"));
            var expected = sets.Where(s => s.Value.Contains(e.Verb)).Select(s => s.Key).OrderBy(x => x).ToArray();
            Assert.True(expected.SequenceEqual(e.Callers.OrderBy(x => x)), $"{e.Verb}: inventory callers [{string.Join(", ", e.Callers)}], VerbPolicy [{string.Join(", ", expected)}]");
        }
        // a verb of a policy set is a server verb or one of the gateway's own; a new one of either kind is written down
        var server = verbs.Select(e => e.Verb).ToHashSet();
        foreach (var v in sets.Values.SelectMany(s => s).Distinct())
            Assert.True(server.Contains(v) || gateway.Contains(v), $"VerbPolicy names '{v}', which is neither a server verb nor in gatewayVerbs of the inventory");
        Assert.All(gateway, g => Assert.DoesNotContain(g, server));
    }

    [Fact]
    public void TheFlagsOfEveryEntry_AreTheOnesTheCodeHas()
    {
        var noLock = ScriptList("NoLockVerbs").ToHashSet();
        foreach (var e in Inventory().Verbs)
        {
            Assert.True(e.NoLock == noLock.Contains(e.Verb), $"{e.Verb}: noLock {e.NoLock} in the inventory, $NoLockVerbs says {noLock.Contains(e.Verb)}");
            Assert.True(e.UseEndpoint == (VerbPolicy.UseEndpointReason(e.Verb) != null), $"{e.Verb}: useEndpoint {e.UseEndpoint} in the inventory, VerbPolicy.UseEndpointReason says otherwise");
        }
    }

    [Fact]
    public void TheTableOfTheDocument_IsTheJson()
    {
        var rows = File.ReadAllLines(Path.Combine(Root, "docs", "design", "verb-inventory.md"))
            .Select(l => Regex.Match(l, @"^\| `(?<v>[a-z0-9-]+)` \| `(?<s>[^`]+)` \| (?<o>[A-Za-z0-9]+) \| (?<d>[a-z]+) \| (?<c>[^|]+) \|$"))
            .Where(m => m.Success).ToDictionary(m => m.Groups["v"].Value, m => m);
        var (verbs, _, _) = Inventory();
        Assert.Equal(verbs.Count, rows.Count);
        foreach (var e in verbs)
        {
            Assert.True(rows.TryGetValue(e.Verb, out var m), $"{e.Verb} has no row in docs/design/verb-inventory.md");
            Assert.Equal(e.Script, m!.Groups["s"].Value);
            Assert.Equal(e.Owner, m.Groups["o"].Value);
            Assert.Equal(e.Destination, m.Groups["d"].Value);
            var callers = m.Groups["c"].Value.Trim();
            Assert.Equal(e.Callers.Length == 0 ? "admin only" : string.Join(",", e.Callers), callers);
        }
    }
}
