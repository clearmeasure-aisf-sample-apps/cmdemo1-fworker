using System.Globalization;
using System.Text.Json.Nodes;

// One task, resumed (docs/design/copilot-cloud-agent.md): the owner record of the task (or Cursor agent) that owns an item lives in the item state,
// read and written by the rule verb copilot-owner. Task C5c (gateway-composition.md 3 B) moved it out of Program.cs (OwnerVerb and the LoadOwner,
// SaveOwner and WhyResume functions it set on the runtimes after Build()) unchanged: the same inputs, the same 120 s, an exit other than 0 throws.
// The Copilot and the Cursor runtime take it as a constructor parameter. tests/Gateway.Tests/CloudOwnerGatewayTests.cs pins it.
sealed class CopilotOwnerVerbs(Func<string, string, TimeSpan, Task<(int Code, string Output)>> run)
{
    public async Task<JsonNode?> Run(JsonObject args)
    {
        var (rc, output) = await run("copilot-owner", args.ToJsonString(), TimeSpan.FromSeconds(120));
        if (rc != 0) throw new InvalidOperationException($"copilot-owner {args["action"]} answered exit {rc}");
        return JsonNode.Parse(output);
    }

    // The record of an item, when it is one of this runtime's: the Copilot runtime takes every record but a Cursor agent's (records from before
    // the runtime field), the Cursor runtime only its own (runtime cursor-cloud).
    public async Task<CopilotOwner?> Load(int item, bool cursor)
    {
        var t = (await Run(new JsonObject { ["item"] = item, ["action"] = "get" }))?["task"];
        if (t is not JsonObject || t["id"]?.ToString() is not { Length: > 0 } id || (t["runtime"]?.ToString() == "cursor-cloud") != cursor) return null;
        var o = new CopilotOwner { Item = item, TaskId = id, Url = t["url"]?.ToString() ?? "", Branch = t["branch"]?.ToString() ?? "", Phase = t["phase"]?.ToString() ?? "", Pr = int.TryParse(t["pr"]?.ToString(), out var p) ? p : 0, WaitingOn = "restored" };
        if (DateTime.TryParse(t["since"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var since)) o.Since = since;
        foreach (var x in (t["previous"] as JsonArray) ?? new JsonArray()) if (x?.ToString() is { Length: > 0 } px) o.Previous.Add(px);
        return o;
    }

    public Task<JsonNode?> Save(CopilotOwner o, string runtime) =>
        Run(new JsonObject { ["item"] = o.Item, ["action"] = "set", ["task"] = new JsonObject { ["id"] = o.TaskId, ["url"] = o.Url, ["branch"] = o.Branch, ["pr"] = o.Pr, ["phase"] = o.Phase, ["runtime"] = runtime } });

    // what the item waits on (its checks, the next phase, the merge, master), and the rework a follow-up session is resumed for
    public Task<JsonNode?> Status(int item) => Run(new JsonObject { ["item"] = item, ["action"] = "status" });

    public async Task<string> WhyResume(int item) => (await Status(item))?["rework"]?.ToString() ?? "";

    // the ownership ends: merged and master green (a comment links the task), or the item closed without a merge
    public Task<JsonNode?> Close(int item, bool unmerged = false)
    {
        var args = new JsonObject { ["item"] = item, ["action"] = "close" };
        if (unmerged) args["unmerged"] = true;
        return Run(args);
    }
}
