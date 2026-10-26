using System.Text.Json.Nodes;

// The gateway owns the completion of a Copilot (or Cursor) Development run (rule verb copilot-settle): before a launch (an item whose pull request
// already carries the change waits for its checks, no new task) and at every end (the task's branch is taken over; Development is done pending
// checks). Task C5c moved it out of Program.cs (CopilotSettle) unchanged: the same input (head only when there is one), the same log line, and on
// a failed run the same answer (launch before a launch, none at an end). A constructor parameter of the Copilot and the Cursor runtime.
sealed class CopilotSettleClient(Func<string, string, TimeSpan, Task<(int Code, string Output)>> run)
{
    public async Task<string> Settle(int item, string stage, string? head)
    {
        var args = new JsonObject { ["item"] = item, ["stage"] = stage }; if (!string.IsNullOrEmpty(head)) args["head"] = head;
        var (rc, output) = await run("copilot-settle", args.ToJsonString(), TimeSpan.FromSeconds(120));
        JsonNode? j = null; try { j = JsonNode.Parse(output); } catch { }
        var action = rc == 0 ? j?["action"]?.ToString() ?? "" : "";
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["copilot"] = "settle", ["item"] = item, ["stage"] = stage, ["exit"] = rc, ["action"] = action, ["reason"] = j?["reason"]?.ToString() ?? j?["code"]?.ToString(), ["pr"] = j?["pr"]?.DeepClone() }.ToJsonString());
        return rc == 0 ? action : (stage == "launch" ? "launch" : "none");
    }

    // a manager's Ending: a Development run that ended is settled (true: the item waits for its checks, the end is no failure of the phase). The
    // head is the branch the run reported (Copilot's HeadOf; Cursor has none), read only for Development
    public async Task<bool> Ending(RuntimeManager.Exec e, Func<int, string?>? headOf) =>
        e.Phase.Equals("Development", StringComparison.OrdinalIgnoreCase) && await Settle(e.Item, "end", headOf?.Invoke(e.Item)) == "awaiting";
}
