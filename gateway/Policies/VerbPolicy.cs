using System.Text.Json.Nodes;

// The verb rules (task C8, gateway-composition.md 3 F), moved verbatim out of Auth.cs: which credential may call which verb, with which fields of the
// body, and what the gateway refuses itself before a rule script runs. Pure: no HTTP, no plant, no clock; tests/Gateway.Tests/VerbPolicyTests.cs.
static class VerbPolicy
{
    public static readonly HashSet<string> OwnerVerbs = new()
    {
        "get-item", "append-body", "comment", "accept", "release", "claim-item", "record-review", "complete", "open-pr", "update-pr", "pr-checks",
        "wait-for-checks", "validation-verdict", "merge", "model-tier", "get-delivery", "private-build", "publish-demo"
    };
    public static readonly HashSet<string> InspectorVerbs = new() { "get-item", "inspect-facts", "approve-pr", "accept", "pr-checks", "model-tier" };
    public static readonly HashSet<string> WorkerVerbs = new() { "my-items" };
    // A verb set of its own (cloud-worker-runtimes.md 2.6): a one-hour GitHub installation token for the work-orders repository, contents write
    // only. Callable only by an item token of role owner minted for a runtime worker (the "rt" claim, from its w2): never by a local worker, an
    // inspector, a QC session, the supervisor or the operator session.
    public static readonly HashSet<string> GitCredentialVerbs = new() { "git-credential" };
    // The supervisor's verbs. The body keeps only the fields listed for the verb, whatever the caller sent.
    // manager-action is the plant manager's flow verb (docs/design/plant-manager.md 10e): one action per call, from ManagerItemActions (a rule script,
    // Verbs.Manager.ps1) or ManagerNameActions (the gateway's own plant). Its `by` is the credential's name, never the body's. The verbs that decide
    // about the work itself are never in this list: merge, approve-pr, validation-verdict, clear-hold, complete, move-next, claim-item, accept.
    public static readonly HashSet<string> ManagerVerbs = new() { "get-item", "pr-checks", "get-delivery", "manager-action" };
    static readonly Dictionary<string, string[]> ManagerBodyFields = new()
    {
        ["get-item"] = ["item"], ["pr-checks"] = ["item", "pr", "sha"], ["get-delivery"] = ["item", "commit"],
        ["manager-action"] = ["item", "action", "policy", "reason", "worker", "level"]
    };
    // The backlog keeper (Keeper.cs, docs/design/backlog-keeper.md): two read verbs, the board read, and its five verbs, which never merge, approve,
    // record a verdict, clear a hold, claim or move an existing card. Its `by` is the credential's name, never the body's.
    public static readonly HashSet<string> KeeperVerbs = new() { "get-item", "pr-checks", "backlog", "amend-criteria", "split-item", "create-item", "set-priority", "link-dependency" };
    static readonly Dictionary<string, string[]> KeeperBodyFields = new()
    {
        ["get-item"] = ["item"], ["pr-checks"] = ["item", "pr", "sha"], ["backlog"] = [],
        ["amend-criteria"] = ["item", "criterion", "replacement", "follow_up", "unpark", "policy", "reason"],
        ["split-item"] = ["item", "children", "chain", "policy", "reason"],
        ["create-item"] = ["title", "body", "labels", "priority", "pin", "depends_on", "policy", "reason"],
        ["set-priority"] = ["item", "level", "policy", "reason"],
        ["link-dependency"] = ["item", "depends_on", "remove", "policy", "reason"]
    };
    public static readonly HashSet<string> ManagerItemActions = new() { "unpark", "release-owner", "pin", "unpin", "requeue", "prioritize", "rerun-failed" };
    public static readonly HashSet<string> ManagerNameActions = new() { "drain", "resume", "restart-session" };
    // Role "qc": the session that runs a PR's environment. No release, no merge, no comment, no approval: only its own job.
    public static readonly HashSet<string> QcVerbs = new() { "get-item", "accept", "qc-job", "qc-progress", "qc-report", "qc-fail", "qc-heartbeat", "qc-stopped" };
    // Principal "qc-click": a person who holds a link. Not a credential: the /qc handlers build it after checking the link, and /api/v1/verbs never yields one.
    public static readonly HashSet<string> QcClickVerbs = new() { "qc-view", "qc-request", "qc-reset", "qc-teardown", "qc-touch" };

    // Authorizes one verb call and returns the (possibly rewritten) body, or a refusal. For non-admin callers the identity fields come from the
    // credential, never from the body: a caller cannot name a different holder, item, assignment or kind.
    public static (JsonObject? Body, string? Refusal) AuthorizeVerb(Principal p, string verb, JsonObject body)
    {
        if (p.Kind == "admin") return (body, null);
        // an r1 calls no verb itself: its tools at /mcp act through the credential the gateway holds for its assignment (gateway/Mcp/)
        if (p.Kind == "role") return (null, $"a role token cannot call '{verb}' directly: use the tools at /mcp");
        if (p.Kind == "manager")
        {
            if (!ManagerVerbs.Contains(verb)) return (null, $"the manager role cannot call '{verb}'");
            var kept = new JsonObject();
            foreach (var f in ManagerBodyFields[verb]) if (body[f] is { } v) kept[f] = v.DeepClone();
            if (verb == "manager-action") kept["by"] = p.Name;
            return (kept, null);
        }
        if (p.Kind == "keeper")
        {
            if (!KeeperVerbs.Contains(verb)) return (null, $"the keeper role cannot call '{verb}'");
            var kept = new JsonObject();
            foreach (var f in KeeperBodyFields[verb]) if (body[f] is { } v) kept[f] = v.DeepClone();
            if (verb is not ("get-item" or "pr-checks" or "backlog")) kept["by"] = p.Name;
            return (kept, null);
        }
        if (p.Kind == "operator") return (null, $"an operator session credential cannot call '{verb}': it calls no verb");
        if (p.Kind == "worker")
        {
            if (!WorkerVerbs.Contains(verb)) return (null, $"a worker credential cannot call '{verb}': it can only register, receive assignments and ask my-items; acting on an item needs the item token delivered with an assignment");
            body["holder"] = p.Name; return (body, null);
        }
        if (p.Kind == "qc-click")
            return QcClickVerbs.Contains(verb) ? (body, null) : (null, $"a QC link cannot call '{verb}'");
        if (GitCredentialVerbs.Contains(verb))
        {
            if (p.Role != "owner" || p.Runtime == "") return (null, $"'{verb}' is only for the owner of an item that a runtime worker holds");
            if (body["item"]?.ToString() != p.Item.ToString()) return (null, $"this token is for item #{p.Item} only");
            return (new JsonObject { ["item"] = p.Item, ["holder"] = p.Name, ["runtime"] = p.Runtime }, null);
        }
        var allowed = p.Role == "inspector" ? InspectorVerbs : p.Role == "owner" ? OwnerVerbs : p.Role == "qc" ? QcVerbs : new HashSet<string>();
        if (!allowed.Contains(verb)) return (null, $"the {p.Role} role cannot call '{verb}'");
        var item = body["item"]?.ToString();
        if (item != p.Item.ToString()) return (null, $"this token is for item #{p.Item} only");
        body["holder"] = p.Name;
        if (p.Role == "qc") { body["assignment"] = p.Assignment; body["role"] = "qc"; if (verb == "accept") body["kind"] = "qc-env"; }
        else if (verb == "accept") { body["assignment"] = p.Assignment; body["kind"] = p.Role == "inspector" ? "bot-job" : "own-item"; body["role"] = p.Role; }
        return (body, null);
    }

    // The verbs the gateway answers with `use-endpoint` (400, exit 2) instead of running the rule script: each has an endpoint or a loop of its own
    // that keeps the stored refs and this process in step. The reason is the answer's text, word for word (it was a chain of ifs in the verb route).
    public static string? UseEndpointReason(string verb) => verb switch
    {
        "revoke-worker" or "revocations" => "use POST /api/v1/workers/{name}/revoke",
        "plant-snapshot-save" or "plant-snapshot-load" or "plant-snapshot-clear" or "plant-snapshot-await" or "leader-lease" or "transcript-save" or "transcript-load" or "transcript-prune"
            => "the plant snapshot is written and read by the gateway itself, at its stop and its start",
        "operator-release" or "operator-releases" => "use POST /api/v1/operator/record-release",
        "ci-release" => "the gateway releases the Copilot agent's held workflow runs by itself",
        "park-item" => "the gateway parks an item runtime-failed by itself",
        "copilot-owner" => "the gateway records a Copilot task's ownership of an item by itself",
        "copilot-settle" => "the gateway settles a Copilot Development task by itself, at its launch and its end",
        "unpark-item" => "use POST /api/v1/items/{n}/unpark (client: factory.ps1 unpark --item N --reason \"...\")",
        "resettle-item" => "use POST /api/v1/items/{n}/resettle",
        "publish-demo" => "send the MP4 as the body of POST /api/v1/demo/{item} (client: factory.ps1 publish-demo --item N --file F)",
        _ => null
    };

    // The backlog keeper's body checks before its verb runs: the flags are JSON booleans and children a JSON array (the rule verb reads exactly what
    // is counted). Returns the usage text of the first field that is wrong, or null.
    public static string? KeeperBodyError(JsonObject body)
    {
        foreach (var bf in new[] { "follow_up", "unpark", "remove", "chain" })
            if (body[bf] is { } bv && bv.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)) return $"{bf} is true or false";
        if (body["children"] is { } cv && cv is not JsonArray) return "children is a JSON array";
        return null;
    }

    // How many new items a keeper verb would create (the day's limit counts them): create-item one, split-item its children (two when not an array),
    // amend-criteria one when it files a follow-up.
    public static int KeeperItemsNeeded(string verb, JsonObject body) => verb switch
    {
        "create-item" => 1,
        "split-item" => body["children"] is JsonArray ka ? ka.Count : 2,
        "amend-criteria" => body["follow_up"] is JsonValue fu && fu.TryGetValue<bool>(out var fb) && fb ? 1 : 0,
        _ => 0
    };

    // The reason of a manager-action as the rule verb cleans it: one line, no HTML comment markers (the journal's machine-read marker), no mention.
    // Not text, or absent: "".
    public static string CleanManagerReason(JsonNode? reason) =>
        reason is JsonValue mrv && mrv.TryGetValue<string>(out var mrs)
            ? System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(mrs.Replace("://", " "), @"[<>@`\[\]!]", ""), @"\s+", " ").Trim() : "";

    // The HTTP status of a rule script's exit code (the verb route's answer)
    public static int StatusOfExit(int code) => code switch { 0 => 200, 21 => 202, 2 => 400, 12 => 404, 10 or 11 or 40 => 409, 20 => 503, _ => 500 };
}
