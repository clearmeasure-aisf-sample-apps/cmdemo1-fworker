using Fworker.Contracts.Ids;

namespace Fworker.Contracts.Commands;

/// <summary>
/// Closes the work item's issue as completed when its saga completes (v1's settle closes it at Done: <c>Close-Issue</c> / <c>RuleKit.CloseIssue</c>, state "closed", state_reason "completed", no comment of its own).
/// An issue that is closed already is left as it is. The key is per item and generation, so a replay closes once and a reopened item (the next generation) that completes again is closed again.
/// </summary>
public sealed record CloseWorkItemIssue(string IdempotencyKey, ItemId Item, int Generation) : IntegrationCommand(IdempotencyKey)
{
    public override string Name => "CloseWorkItemIssue";
    public override CommandTarget Target => CommandTarget.GitHub;

    public static string KeyFor(ItemId item, int generation) => $"CloseWorkItemIssue|{item}|{generation}";
}
