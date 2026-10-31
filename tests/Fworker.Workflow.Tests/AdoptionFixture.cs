using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Domain;
using Fworker.Workflows.Adoption;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflow.Tests;

/// <summary>W9.10m: the snapshots and the settings the adoption tests map. The state comments are written as v1 writes them (Board.ps1: ConvertTo-Json of the state).</summary>
static class AdoptionFixture
{
    public static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    public static readonly ItemId Item = ItemId.Parse("acme/orders#7");
    public static readonly string Sha = new('a', 40);
    public static readonly string Head = new('b', 40);

    public const string Design = "Technical Design", Develop = "Development", Verify = "Functional Testing", Release = "Release Queue", Done = "Done";

    public static string Iso(TimeSpan ago) => (Now - ago).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    public static string Ahead(TimeSpan by) => (Now + by).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public static AdoptionSettings Settings(Func<WorkItemPolicy, WorkItemPolicy>? policy = null, DateTimeOffset? legacyBefore = null, string runtime = "cloud")
    {
        var p = WorkItemPolicy.Default with
        {
            FinalEnvironment = "tdd", FeatureRules = new FeaturePlanRules(true), Sweep = new SweepRules(TimeSpan.FromHours(24), TimeSpan.FromMinutes(30), 3, TimeSpan.FromMinutes(60)),
        };
        var docs = new DocsOnlySettings([@"^docs/.*\.md$", @"^README\.md$"], "<!-- aisf:docs-only -->", legacyBefore, [Design], "video", ["Integration Build (SQL container)"]);
        return new AdoptionSettings(policy?.Invoke(p) ?? p, new AdoptionColumns(Design, Develop, Verify, Done, [Release]), docs, new ItemGateConfig("Auto Merge", "video", ["hold:owner"]), runtime);
    }

    public static AdoptionSnapshot Snapshot(string? column, JsonObject? state, Func<AdoptionSnapshot, AdoptionSnapshot>? with = null, bool open = true)
    {
        var s = new AdoptionSnapshot(Item, open, column, ["factory worker", "Auto Merge"], "The body.", Now.AddDays(-2), state);
        return with?.Invoke(s) ?? s;
    }

    /// <summary>A state comment: the revision, the fields given, as v1 would have them.</summary>
    public static JsonObject State(int rev = 5, string? extra = null)
    {
        var json = $$"""{ "schema": 1, "item": 7, "rev": {{rev}}, "updated": "{{Iso(TimeSpan.FromMinutes(5))}}"{{(extra is null ? "" : ", " + extra)}} }""";
        return (JsonObject)JsonNode.Parse(json)!;
    }

    public static string Claim(string column, bool owner, TimeSpan leaseLeft, int launches = 1, string? more = null) =>
        $$"""
        "claim": { "holder": "w1", "column": "{{column}}", "started": "{{Iso(TimeSpan.FromMinutes(20))}}", "heartbeat": "{{Iso(TimeSpan.FromMinutes(1))}}", "lease_until": "{{(leaseLeft >= TimeSpan.Zero ? Ahead(leaseLeft) : Iso(-leaseLeft))}}", "launches": {{launches}}{{(owner ? ", \"owner\": true, \"worker\": \"w1\", \"assignment\": \"a1\"" : "")}}{{(more is null ? "" : ", " + more)}} }
        """;

    public static string Pass(string? head = null, bool processed = false) =>
        $$"""
        "verdict": { "passed": true, "pr": 11, "head_sha": "{{head ?? Head}}", "summary": "all criteria met", "at": "{{Iso(TimeSpan.FromMinutes(10))}}", "round": 0, "processed": {{(processed ? "true" : "false")}} }
        """;

    public static string Park(string reason, TimeSpan? left = null, string? more = null) =>
        $$"""
        "park": { "reason": "{{reason}}", "at": "{{Iso(TimeSpan.FromMinutes(30))}}", "until": "{{Ahead(left ?? TimeSpan.FromHours(23))}}"{{(more is null ? "" : ", " + more)}} }
        """;

    public static string Merged(string awaiting, string? more = null) =>
        $$"""
        "merge": { "sha": "{{Sha}}", "awaiting": "{{awaiting}}", "merged_at": "{{Iso(TimeSpan.FromMinutes(10))}}", "pr": 11{{(more is null ? "" : ", " + more)}} }
        """;

    public static string Join(params string?[] parts) => string.Join(", ", parts.Where(p => !string.IsNullOrEmpty(p)));

    public static AdoptionMapping Map(string? column, JsonObject? state, AdoptionSettings? settings = null, Func<AdoptionSnapshot, AdoptionSnapshot>? with = null) =>
        AdoptionMapper.Map(Snapshot(column, state, with), settings ?? Settings(), Now);

    /// <summary>The state of an item mapped and expected to be the saga's.</summary>
    public static WorkItemState Mapped(string? column, JsonObject? state, AdoptionSettings? settings = null, Func<AdoptionSnapshot, AdoptionSnapshot>? with = null)
    {
        var m = Map(column, state, settings, with);
        if (m.Skipped) throw new InvalidOperationException($"the item was skipped: {m.SkipReason}");
        return m.State!;
    }
}
