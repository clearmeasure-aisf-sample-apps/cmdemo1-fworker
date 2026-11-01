using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.GitHub;

/// <summary>
/// Whether the item's saga is still on the generation the close was decided for and has completed it. A close that is retried for a long time (GitHub down) must not close an issue that a person reopened since:
/// the reopen starts the next generation. The gateway answers from the saga's row; Integrations does not depend on the workflow store.
/// </summary>
public interface IIssueCloseGuard
{
    Task<bool> StillCompleted(ItemId item, int generation, CancellationToken ct);
}

/// <summary>
/// The handler of <see cref="CloseWorkItemIssue"/> on the W3.0 base: the issue is closed as completed, as v1's settle does it (PATCH state closed, state_reason completed, no comment), by the identity of the
/// gateway's client. An issue that is closed already is a no-op (read first, so nothing is written). A transport fault is transient (the caller retries the key); a refusal of GitHub is permanent and recorded
/// by the ledger, except a 401 or 403 of GitHub, which is written in the form <see cref="AuthFaults"/> knows ("CloseWorkItemIssue: GitHub 403 on PATCH issues/N"), so that the ledger does not keep a credential
/// fault for good (the purge at start drops it). An issue whose saga has moved on to another generation is left open (answer "superseded"). An earlier handling that was cut off is looked up in the issue's state.
/// </summary>
public sealed class CloseIssueHandler : IntegrationCommandHandler<CloseWorkItemIssue>
{
    readonly GitHubClientFactory clients;
    readonly IIssueCloseGuard? guard;

    public CloseIssueHandler(IIdempotencyLedger ledger, GitHubClientFactory clients, IIssueCloseGuard? guard = null) : base(ledger, async (c, ct) =>
    {
        var read = await Read(await clients.For(c.Item.Repository, ct), c, ct);
        return read.Closed ? EffectLookup.Found(CommandResult.Success("already-closed")) : read.Ok ? EffectLookup.Absent : EffectLookup.Unknown($"read of the issue: {read.Status}");
    }) { this.clients = clients; this.guard = guard; }

    protected override async Task<CommandResult> Execute(CloseWorkItemIssue c, CancellationToken ct)
    {
        var client = await clients.For(c.Item.Repository, ct);
        var read = await Read(client, c, ct);
        if (read.Closed) return CommandResult.Success("already-closed");
        if (!read.Ok) return Refused(read.Status, read.Outcome, "GET", c);
        // the issue is open: close it only for the generation that is completed now (a person may have reopened it, which started the next one)
        if (guard is not null && !await guard.StillCompleted(c.Item, c.Generation, ct)) return CommandResult.Success("superseded");
        var body = new JsonObject { ["state"] = "closed", ["state_reason"] = "completed" };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await client.Send(HttpMethod.Patch, $"issues/{c.Item.Number}", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        return resp.IsSuccessStatusCode ? CommandResult.Success("closed") : Refused((int)resp.StatusCode, GitHubResponses.ToOutcome(resp, text), "PATCH", c);
    }

    static CommandResult Refused(int status, HttpOutcome outcome, string method, CloseWorkItemIssue cmd)
    {
        var c = ErrorClassifier.Classify(outcome, idempotentCommand: false);
        // a 401 or 403 is the credential's, not the issue's: the form AuthFaults reads, so that the row is not kept for good
        if (status is 401 or 403) return CommandResult.Failed(c.Kind, $"CloseWorkItemIssue: GitHub {status} on {method} issues/{cmd.Item.Number}");
        return CommandResult.Failed(c.Kind, $"CloseWorkItemIssue: HTTP {status} ({c.Reason})");
    }

    readonly record struct IssueRead(int Status, bool Ok, bool Closed, HttpOutcome Outcome);

    static async Task<IssueRead> Read(GitHubRepoClient client, CloseWorkItemIssue c, CancellationToken ct)
    {
        using var resp = await client.Send(HttpMethod.Get, $"issues/{c.Item.Number}", null, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var closed = false;
        if (resp.IsSuccessStatusCode)
        {
            try { closed = string.Equals(JsonNode.Parse(text)?["state"]?.ToString(), "closed", StringComparison.OrdinalIgnoreCase); } catch (System.Text.Json.JsonException) { }
        }
        return new IssueRead((int)resp.StatusCode, resp.IsSuccessStatusCode, closed, GitHubResponses.ToOutcome(resp, text));
    }

    /// <summary>Runs the command; the result says how it ended.</summary>
    public Task<CommandResult> Process(CloseWorkItemIssue command, CancellationToken ct = default) => Handle(command, ct);
}
