using System.Text.Json.Nodes;

// The item's open pull request (number, head branch), null when it has none: the Copilot runtime starts a task from it (CopilotCloudRuntime.BaseRef).
// Rule verb item-pr. A constructor parameter of CopilotCloudRuntime since task C5b (before, a function Program.cs set after Build()).
interface IItemPrLookup
{
    Task<ItemPr?> FindAsync(int item, CancellationToken ct);
}

sealed class ItemPrLookup(Runner runner) : IItemPrLookup
{
    // the token is not passed on: the rule verb runs to its own 60 s limit, as it did
    public async Task<ItemPr?> FindAsync(int item, CancellationToken ct)
    {
        var (rc, output) = await runner.RunAsync("item-pr", new JsonObject { ["item"] = item }.ToJsonString(), TimeSpan.FromSeconds(60));
        if (rc != 0) return null;
        var j = JsonNode.Parse(output);
        return j?["found"]?.GetValue<bool>() == true && j["branch"]?.ToString() is { Length: > 0 } b && int.TryParse(j["number"]?.ToString(), out var num) ? new ItemPr(num, b) : null;
    }
}
