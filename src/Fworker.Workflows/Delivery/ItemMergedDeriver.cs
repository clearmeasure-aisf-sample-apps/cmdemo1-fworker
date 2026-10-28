using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Persistence;

namespace Fworker.Workflows.Delivery;

// W5.0: ItemMerged(commit, gates, dependencies), derived and not emitted. v1 emits nothing through S6 (no change to Verbs.Merge.ps1 or Verbs.Delivery.ps1); the facts come
// from two places that see the same merge, and the merged-items table makes them one:
//  - the merge webhook: v1's ingest-event answers "base.merged" (pr, sha = the merge commit) for any pull request merged into the default branch and "pr.merged" for a factory
//    branch; both reach the ingress inbox as source "webhook";
//  - the board reconciler (W4.3): a card whose issue closed ("board.card", open false) reaches it as source "poll"; the merged pull request is found from the issue.
// The pull request maps to its items with the content gate of D3 (ContentGate.CandidateItems); an item counts when it carries the factory's state comment. Active only where
// the engine acts (Engine.V2Acts): the gateway registers nothing of it in shadow.

/// <summary>The outcome of one derivation: the messages to publish and why there are none.</summary>
public sealed record ItemMergedDerivation(IReadOnlyList<ItemMerged> Merged, string Reason);

public sealed partial class ItemMergedDeriver(IMergeFactsReader reader, IMergedItemStore store, RepoId home, Func<string> defaultBranch, ItemGateConfig gates, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha40();

    /// <summary>Whether the event can be a merge at all: the inbox hands every event here and most are not.</summary>
    public static bool Relevant(JsonObject ev) =>
        ev["event"]?.ToString() is "base.merged" or "pr.merged" || (ev["event"]?.ToString() == "board.card" && ev["open"] is JsonValue o && o.TryGetValue<bool>(out var open) && !open);

    /// <summary>Derives the ItemMerged of one inbox event. <paramref name="messageId"/> is the Wolverine message: it claims the rows, so a redelivery derives the same messages again.</summary>
    public async Task<ItemMergedDerivation> Derive(string source, string eventJson, Guid messageId, CancellationToken ct)
    {
        if (JsonNode.Parse(eventJson) is not JsonObject ev || !Relevant(ev)) return new([], "not-a-merge");
        var repo = ev["repository"]?.ToString() is { } rs && RepoId.TryParse(rs, out var r) ? r : home;
        var number = Int(ev["item"]); var pr = Int(ev["pr"]);
        MergedPullRequest? pull;
        switch (ev["event"]!.ToString())
        {
            case "base.merged":
                // the merge commit is in the event: a commit another message took in needs no read
                var sha = (ev["sha"]?.ToString() ?? "").ToLowerInvariant();
                if (pr <= 0 || !Sha40().IsMatch(sha)) return new([], "no-pull-request-or-commit");
                if (await store.CommitTakenByOther(sha, messageId, ct)) return new([], "duplicate-commit");
                pull = await reader.ReadPullRequest(repo, pr, ct);
                break;
            case "pr.merged":
                if (pr <= 0) return new([], "no-pull-request");
                pull = await reader.ReadPullRequest(repo, pr, ct);
                if (pull?.MergeCommit is { } c && await store.CommitTakenByOther(c, messageId, ct)) return new([], "duplicate-commit");
                break;
            default:   // board.card, closed
                if (number <= 0) return new([], "no-item");
                pull = await reader.FindMergedPullRequest(new ItemId(repo, number), ct);
                if (pull?.MergeCommit is { } c2 && await store.CommitTakenByOther(c2, messageId, ct)) return new([], "duplicate-commit");
                break;
        }
        if (pull is null) return new([], "pull-request-not-found");
        return await FromPullRequest(pull, source, messageId, ct);
    }

    async Task<ItemMergedDerivation> FromPullRequest(MergedPullRequest pull, string source, Guid messageId, CancellationToken ct)
    {
        var commit = (pull.MergeCommit ?? "").ToLowerInvariant();
        if (!pull.Merged || !Sha40().IsMatch(commit)) return new([], "not-merged");
        if (!string.Equals(pull.BaseRef, defaultBranch(), StringComparison.OrdinalIgnoreCase)) return new([], "not-the-default-branch");
        var merged = new List<ItemMerged>();
        foreach (var n in ContentGate.CandidateItems(pull.Number, pull.HeadRef, pull.Title, pull.Body))
        {
            var id = new ItemId(pull.Repository, n);
            var facts = await reader.ReadItem(id, ct);
            if (facts is not { IsFactoryItem: true }) continue;
            var message = Build(id, commit, pull.Number, facts, source);
            var row = new MergedItem
            {
                ItemKey = id.ToString(), Commit = commit, PullRequest = pull.Number, Source = source, MergedAt = now(), MessageId = messageId,
                GatesJson = JsonSerializer.Serialize(message.Gates), DependenciesJson = JsonSerializer.Serialize(message.Dependencies.Select(d => d.ToString())),
            };
            if (await store.TryRecord(row, ct)) merged.Add(message);   // false: the other source got there first
        }
        return new(merged, merged.Count > 0 ? "derived" : "no-new-item");
    }

    ItemMerged Build(ItemId id, string commit, int pr, ItemMergeFacts facts, string source)
    {
        var gaps = ItemGates.Gaps(new ItemGateFacts(id.Number, facts.Labels, facts.VerdictPassed, facts.VerdictPr, facts.MergePr, facts.MergeBy, facts.DemoVideoPublished), gates);
        bool Has(string name) => facts.Labels.Any(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase));
        var passMerged = facts.VerdictPassed && facts.VerdictPr is not null && facts.MergePr is > 0 && facts.MergePr == facts.VerdictPr && !string.Equals(facts.MergeBy, "person", StringComparison.OrdinalIgnoreCase);
        var g = new ItemMergedGates(Has(gates.AutoMergeLabel), Has("Hold") ? "Hold" : ItemGates.HoldLabel(facts.Labels, gates.HoldLabels), passMerged, Has(gates.VideoLabel), facts.DemoVideoPublished, gaps);
        return new ItemMerged(id, commit, pr, g, ItemMergedDependencies.Parse(id, facts.Body), source);
    }

    static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
}
