using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Intake;

// W6.0: the intake rule of the v2 engine (docs/design/system-gateway.md H4, fworker-wolverine-refactor.md R6). It is Invoke-Intake of plugin/scripts/lib/Verbs.Sweep.ps1 (issue 104) as a pure function, with the two
// additions of the hybrid composition: a WIP limit per repository beside the global one (unset by default: a repository with no entry is limited only by the global limit), and the choice of the engine that takes
// the item (intake.v2Plans: the plans whose items the v2 engine owns; every other item stays with v1's sweep). v1's own intake keeps running unchanged for the items it takes; this rule decides which item comes next
// and whose it is. Nothing here reads the board: the caller passes the candidates in board order and the counts in flight.

/// <summary>The settings of the <c>intake</c> block of config/instance.json, with the label names of its root.</summary>
public sealed record IntakeSettings(
    bool Enabled,
    int WipLimit,
    string FromColumn,
    IReadOnlyList<string> HoldLabels,
    IReadOnlyDictionary<string, int> RepositoryWipLimits,
    IReadOnlyList<string> V2Plans,
    string V2Label,
    string FactoryLabel,
    string RivalLabel,
    string DemoLabel,
    string EnvLabel = "")
{
    public const string DefaultV2Label = "engine:v2";

    /// <summary>
    /// Reads the settings from the configuration root (<c>intake</c>, <c>label</c>, <c>rivalLabel</c>, <c>demoLabel</c>) as Get-IntakeSettings does: FACTORY_INTAKE (0 or 1) overrides <c>enabled</c> and FACTORY_INTAKE_WIP
    /// the global limit. A malformed block falls back to the defaults field by field: nothing is on, no repository is limited and no plan is the v2 engine's.
    /// </summary>
    public static IntakeSettings From(JsonNode? root, Func<string, string?>? env = null)
    {
        env ??= _ => null;
        JsonNode? i = null; try { i = root?["intake"]; } catch { }
        var enabled = Bool(i, "enabled");
        if (env("FACTORY_INTAKE") is "0" or "1") enabled = env("FACTORY_INTAKE") == "1";
        var wip = Int(i, "wipLimit") ?? 6;
        if (int.TryParse(env("FACTORY_INTAKE_WIP"), out var envWip) && envWip >= 0) wip = envWip;
        var perRepo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (i?["repositoryWipLimits"] is JsonObject limits)
                foreach (var (name, value) in limits)
                    if (RepoId.TryParse(name, out var repo) && value is JsonValue v && v.TryGetValue<int>(out var n) && n >= 0) perRepo[repo.ToString()] = n;
        }
        catch { }
        return new IntakeSettings(enabled, wip, Text(i, "fromColumn") ?? "Todo", Strings(i, "holdLabels"), perRepo, Strings(i, "v2Plans").Select(p => p.ToLowerInvariant()).ToList(),
            Text(i, "v2Label") ?? DefaultV2Label, Text(root, "label") ?? "", Text(root, "rivalLabel") ?? "", Text(root, "demoLabel") ?? "",
            Fworker.Workflows.Keeper.FactoryRules.EnvLabelOf(root, env));
    }

    static bool Bool(JsonNode? n, string name) { try { return n?[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b; } catch { return false; } }
    static int? Int(JsonNode? n, string name) { try { return n?[name] is JsonValue v && v.TryGetValue<int>(out var x) && x >= 0 ? x : null; } catch { return null; } }
    static string? Text(JsonNode? n, string name) { try { return n?[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null; } catch { return null; } }
    static List<string> Strings(JsonNode? n, string name)
    {
        var l = new List<string>();
        try { if (n?[name] is JsonArray a) foreach (var x in a) if (x is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0) l.Add(s.Trim()); } catch { }
        return l;
    }

    /// <summary>The WIP limit of a repository, or null when it has none of its own.</summary>
    public int? LimitOf(RepoId repository) => RepositoryWipLimits.TryGetValue(repository.ToString(), out var n) ? n : null;

    /// <summary>Whether the v2 engine takes items of this plan (the plan an intake candidate is known to be on: "docs" for an item whose body carries the docs-only marker).</summary>
    public bool IsV2Plan(string? plan) => !string.IsNullOrWhiteSpace(plan) && V2Plans.Contains(plan.Trim().ToLowerInvariant());
}

/// <summary>An item waiting in the intake column, as the caller read it from the board and the issue. <paramref name="Plan"/> is the plan it is known to be on, or null.</summary>
public sealed record IntakeCandidate(ItemId Item, bool Open, bool HasCard, IReadOnlyList<string> Labels, bool DependenciesMet = true, string? Plan = null);

public enum IntakeEngine { V1, V2 }

/// <summary>The item to bring onto the board and the engine that owns it.</summary>
public sealed record IntakePull(ItemId Item, IntakeEngine Engine, string? Plan);

/// <summary>What the rule decided: the item to pull (null for none), why none, and the candidates it passed over with the reason.</summary>
public sealed record IntakeDecision(IntakePull? Pull, string Reason, IReadOnlyList<(ItemId Item, string Reason)> Passed)
{
    public static IntakeDecision None(string reason) => new(null, reason, []);
}

public static class IntakeRule
{
    /// <summary>The reasons the rule gives. A reason with a repository in it is the per-repository limit.</summary>
    public const string Disabled = "disabled", NoFreeSlot = "no-free-slot", DesignWaiting = "design-waiting", WipLimit = "wip-limit", NoCandidate = "no-candidate", Pulled = "pulled";

    /// <summary>
    /// The next item to pull, or none. <paramref name="freeSlots"/> are the sessions that could take a new item now; <paramref name="designWaiting"/> is whether an item already waits in the first AI column for a
    /// session; <paramref name="inFlight"/> are the open items of the AI columns by repository ("owner/repo"; both engines' items, as the board shows them). At most one item, and none while the AI columns hold the
    /// global limit or more. A candidate whose repository is at its own limit is passed over, and the next one of another repository may be pulled instead.
    /// </summary>
    public static IntakeDecision Decide(IReadOnlyList<IntakeCandidate> candidates, IReadOnlyDictionary<string, int> inFlight, int freeSlots, bool designWaiting, IntakeSettings s)
    {
        if (!s.Enabled) return IntakeDecision.None(Disabled);
        if (freeSlots <= 0) return IntakeDecision.None(NoFreeSlot);
        if (designWaiting) return IntakeDecision.None(DesignWaiting);
        if (inFlight.Values.Sum() >= s.WipLimit) return IntakeDecision.None(WipLimit);
        var passed = new List<(ItemId, string)>();
        foreach (var c in candidates)
        {
            if (Ineligible(c, s) is { } why) { passed.Add((c.Item, why)); continue; }
            var repo = c.Item.Repository;
            if (s.LimitOf(repo) is { } limit && inFlight.GetValueOrDefault(repo.ToString()) >= limit) { passed.Add((c.Item, $"repository-wip: {repo} holds {limit} or more")); continue; }
            return new IntakeDecision(new IntakePull(c.Item, s.IsV2Plan(c.Plan) ? IntakeEngine.V2 : IntakeEngine.V1, c.Plan), Pulled, passed);
        }
        return new IntakeDecision(null, NoCandidate, passed);
    }

    // Left in the column: a closed issue, one without the factory label or with the rival or the demo label, a hold label (hold:* or intake.holdLabels), a worker:* pin, an item outside the environment's scope, one without a card, one whose dependencies are not met
    static string? Ineligible(IntakeCandidate c, IntakeSettings s)
    {
        if (!c.Open) return "closed";
        if (s.FactoryLabel.Length > 0 && !Has(c.Labels, s.FactoryLabel)) return "no-factory-label";
        if (s.RivalLabel.Length > 0 && Has(c.Labels, s.RivalLabel)) return "rival-label";
        if (s.DemoLabel.Length > 0 && Has(c.Labels, s.DemoLabel)) return "demo-label";
        if (c.Labels.Any(l => l.StartsWith("hold:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("worker:", StringComparison.OrdinalIgnoreCase) || s.HoldLabels.Contains(l, StringComparer.OrdinalIgnoreCase))) return "held-or-pinned";
        // V1.2: with env scoping on, an item is this gateway's only when it carries exactly one env: label and it is this environment's (Test-ItemInScope); another environment's item stays in the column
        if (s.EnvLabel.Length > 0)
        {
            var envs = c.Labels.Where(l => l.StartsWith("env:", StringComparison.OrdinalIgnoreCase)).ToList();
            if (envs.Count != 1 || !string.Equals(envs[0], s.EnvLabel, StringComparison.OrdinalIgnoreCase)) return "out-of-scope";
        }
        if (!c.HasCard) return "no-card";
        if (!c.DependenciesMet) return "depends-on";
        return null;
    }

    static bool Has(IReadOnlyList<string> labels, string name) => labels.Contains(name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The v2 engine's intake: the rule, then, for an item the v2 engine owns, the start of its saga (the item then carries the engine label, a projection of W6.3). An item that v1 owns is only reported: v1's own intake
/// moves it, so nothing here touches the board. Counts in flight come from the caller (the board) or, without them, from the saga's own table.
/// </summary>
public sealed class WorkItemIntake(IWorkItemStore store, IntakeSettings settings, Func<StartWorkItem, CancellationToken, Task> start)
{
    public IntakeSettings Settings => settings;

    public async Task<IntakeDecision> Run(IReadOnlyList<IntakeCandidate> candidates, int freeSlots, bool designWaiting, IReadOnlyDictionary<string, int>? inFlight = null, CancellationToken ct = default)
    {
        inFlight ??= await store.InFlightByRepository(ct);
        var d = IntakeRule.Decide(candidates, inFlight, freeSlots, designWaiting, settings);
        if (d.Pull is { Engine: IntakeEngine.V2 } pull) await start(new StartWorkItem(pull.Item, "intake", pull.Plan), ct);
        return d;
    }
}
