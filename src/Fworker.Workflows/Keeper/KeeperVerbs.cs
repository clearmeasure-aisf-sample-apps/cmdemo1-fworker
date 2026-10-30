using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;

namespace Fworker.Workflows.Keeper;

/// <summary>
/// W9.6 (#358): the seven keeper and manager verbs as the engine answers them: <c>factory.ps1 --server &lt;verb&gt;</c> of v1 in C#. A call is the JSON the script read on its stdin; the answer is the exit code and the one
/// line of JSON the script printed (the fields of the verb, <c>verb</c>, <c>ok</c> and <c>exit</c>; a refusal is <c>ok</c> false with its <c>code</c>, <c>reason</c> and extra fields). Like the script it takes the
/// session's link out of the call (<c>_session</c>, the footer of the comments), ignores the lease fence (<c>_lease</c>: v2's leadership is Wolverine's) and the test cause, and holds the item's lock for every verb that
/// names an item except the read-only backlog.
/// </summary>
public sealed class KeeperVerbs(VerbContext cx)
{
    /// <summary>The verbs this class answers, in the order of the inventory.</summary>
    public static readonly IReadOnlyList<string> Verbs = ["backlog", "amend-criteria", "split-item", "create-item", "set-priority", "link-dependency", "manager-action"];

    /// <summary>The verbs that do not take the item's lock (<c>$NoLockVerbs</c> of factory.ps1).</summary>
    public static readonly IReadOnlyList<string> NoLock = ["backlog"];

    // written as the rule script wrote it: no escaping of <, >, & or of letters outside ASCII (journal text, comments)
    static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly KeeperCommands keeper = new(cx);
    readonly ManagerCommands manager = new(cx);

    public async Task<VerbAnswer> Run(string verb, string stdin, TimeSpan timeout)
    {
        var work = Execute(verb, stdin);
        try { return await work.WaitAsync(timeout); }
        catch (TimeoutException)
        {
            // the script was killed at its time; here the work is left to finish, and nothing waits for it
            _ = work.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return new VerbAnswer(20, new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = "timeout", ["reason"] = "the rule script did not finish in time", ["retryAfterSeconds"] = 30, ["exit"] = 20 }.ToJsonString(Relaxed));
        }
    }

    async Task<VerbAnswer> Execute(string verb, string stdin)
    {
        try
        {
            if (!Verbs.Contains(verb)) throw VerbRejection.Usage($"unknown verb '{verb}'");
            var body = string.IsNullOrWhiteSpace(stdin) ? new JsonObject() : JsonNode.Parse(stdin) as JsonObject ?? new JsonObject();
            // the AI session the gateway runs this verb for: the footer of every comment written for it; the fence and the test cause are not this engine's
            var session = body["_session"] as JsonObject;
            foreach (var k in body.Select(p => p.Key).Where(k => k is "_session" or "_cause" or "_lease").ToList()) body.Remove(k);
            var a = new VerbArgs(body);
            JsonObject result;
            if (a.Has("item") && !NoLock.Contains(verb))
            {
                await using (await cx.Locks.Item(a.ItemNumber())) result = await Dispatch(verb, a, session);
            }
            else result = await Dispatch(verb, a, session);
            result["verb"] = verb;
            if (!result.ContainsKey("ok")) result["ok"] = true;
            result["exit"] = 0;
            return new VerbAnswer(0, result.ToJsonString(Relaxed));
        }
        catch (VerbRejection e)
        {
            var o = new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = e.Code, ["reason"] = e.Message };
            foreach (var kv in e.Extra) o[kv.Key] = JsonSerializer.SerializeToNode(kv.Value);
            o["exit"] = e.Exit;
            return new VerbAnswer(e.Exit, o.ToJsonString(Relaxed));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"internal error in {verb} : {e.GetType().Name}: {e.Message}");
            return new VerbAnswer(20, new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = "internal-error", ["reason"] = e.Message, ["retryAfterSeconds"] = 30, ["exit"] = 20 }.ToJsonString(Relaxed));
        }
    }

    Task<JsonObject> Dispatch(string verb, VerbArgs a, JsonObject? session) => verb switch
    {
        "backlog" => keeper.Backlog(),
        "amend-criteria" => keeper.AmendCriteria(a),
        "split-item" => keeper.SplitItem(a, session),
        "create-item" => keeper.CreateItem(a),
        "set-priority" => keeper.SetPriority(a),
        "link-dependency" => keeper.LinkDependency(a),
        "manager-action" => manager.Action(a),
        _ => throw VerbRejection.Usage($"unknown verb '{verb}'"),
    };
}
