using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.GitHub;

/// <summary>
/// The handler of <see cref="CloseWorkItemIssue"/> on the W3.0 base: the issue is closed as completed, as v1's settle does it (PATCH state closed, state_reason completed, no comment), by the identity of the
/// gateway's client. An issue that is closed already is a no-op (read first, so nothing is written). A transport fault is transient (the caller retries the key); a refusal of GitHub is permanent and recorded
/// by the ledger. An earlier handling that was cut off is looked up in the issue's state.
/// </summary>
public sealed class CloseIssueHandler : IntegrationCommandHandler<CloseWorkItemIssue>
{
    readonly GitHubClientFactory clients;

    public CloseIssueHandler(IIdempotencyLedger ledger, GitHubClientFactory clients) : base(ledger, async (c, ct) =>
    {
        var read = await Read(await clients.For(c.Item.Repository, ct), c, ct);
        return read.Closed ? EffectLookup.Found(CommandResult.Success("already-closed")) : read.Ok ? EffectLookup.Absent : EffectLookup.Unknown($"read of the issue: {read.Status}");
    }) { this.clients = clients; }

    protected override async Task<CommandResult> Execute(CloseWorkItemIssue c, CancellationToken ct)
    {
        var client = await clients.For(c.Item.Repository, ct);
        var read = await Read(client, c, ct);
        if (read.Closed) return CommandResult.Success("already-closed");
        if (!read.Ok) return Refused(read.Status, read.Outcome);
        var body = new JsonObject { ["state"] = "closed", ["state_reason"] = "completed" };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await client.Send(HttpMethod.Patch, $"issues/{c.Item.Number}", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        return resp.IsSuccessStatusCode ? CommandResult.Success("closed") : Refused((int)resp.StatusCode, GitHubResponses.ToOutcome(resp, text));
    }

    static CommandResult Refused(int status, HttpOutcome outcome)
    {
        var c = ErrorClassifier.Classify(outcome, idempotentCommand: false);
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
