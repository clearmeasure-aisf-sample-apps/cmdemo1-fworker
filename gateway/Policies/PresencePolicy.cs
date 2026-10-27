using System.Globalization;
using System.Text.Json.Nodes;

// Who may send a heartbeat, and what a heartbeat may carry (task C8, gateway-composition.md 3 F; it was the body of POST /api/v1/presence,
// worker-messaging.md 3.1 and 3.2). Pure: the plant's slots come in as functions. tests/Gateway.Tests/PresencePolicyTests.cs.
static class PresencePolicy
{
    // Online: the caller is the keeper or the operator session, whose heartbeat the request itself stamped (no body, no directory entry): answer
    // "online". Refused: the refusal. Continue: a session with a directory entry; read the body.
    public enum Verdict { Online, Refused, Continue }

    // kind, name, role: the credential; operatorActive: the caller is the active operator session; workerAllowed: messaging.allowedWorkers;
    // activeKeeper and activeSupervisor: the plant's slots.
    public static (Verdict Verdict, PolicyRefusal? Refusal) CheckCaller(string kind, string name, string role, bool operatorActive, Func<string> activeKeeper,
        Func<string> activeSupervisor, Func<string, bool> workerAllowed)
    {
        if (kind == "keeper")
            return activeKeeper() == name ? (Verdict.Online, null) : (Verdict.Refused, new(403, "not-keeper", "this credential is not the active backlog keeper: register with subscribe --own --keeper first", 30));
        if (kind == "operator")
            return operatorActive ? (Verdict.Online, null) : (Verdict.Refused, new(403, "not-operator", "this credential is not the active operator session: register with subscribe --own --operator first", 30));
        if (kind is not ("worker" or "item" or "manager")) return (Verdict.Refused, new(403, "forbidden", "presence is sent with a session's own credential", 30));
        if (kind == "item" && role is not ("owner" or "inspector")) return (Verdict.Refused, new(403, "role-cannot-send", "only the item tokens of an owner or an inspector session have presence (a QC token is pull request code's credential)", 30));
        if (kind != "manager" && !workerAllowed(name)) return (Verdict.Refused, new(403, "not-allowed", "this name is not in messaging.allowedWorkers", 30));
        if (kind == "manager" && activeSupervisor() != name) return (Verdict.Refused, new(403, "not-supervisor", "this credential is not the active supervisor: register with subscribe --own --supervisor first", 30));
        return (Verdict.Continue, null);
    }

    // The fields of the body: the sender comes from the credential, so a body never names it; only summary and progress are known.
    public static PolicyRefusal? CheckFields(JsonObject body)
    {
        foreach (var kv in body)
        {
            if (kv.Key is "from" or "sender" or "holder" or "fromKind" or "role") return new(403, "forged-sender", "the sender comes from the credential; a body never names it", 30);
            if (kv.Key is not ("summary" or "progress")) return new(400, "usage", $"unknown field '{kv.Key}'", 2);
        }
        return null;
    }

    // Local progress of owned items (gateway/Lease.cs rule 3): [{ "item": N, "ageSeconds": S, "source": "commit|files|process|marker|work" }], 20 at most;
    // the age (by the client's clock) and not a time, so a client whose clock is off still reports what is recent. `now` is the gateway's clock.
    public static (List<(int Item, DateTime At, string Source)>? Progress, PolicyRefusal? Refusal) ParseProgress(JsonNode progress, bool isManager, DateTime now)
    {
        var list = new List<(int Item, DateTime At, string Source)>();
        if (isManager) return (null, new(400, "usage", "the supervisor owns no item: progress is sent by an owner's client", 2));
        if (progress is not JsonArray pa || pa.Count > 20) return (null, new(400, "usage", "progress is an array of at most 20 { item, ageSeconds, source }", 2));
        foreach (var e in pa)
        {
            if (e is not JsonObject eo || !int.TryParse(eo["item"]?.ToString(), out var pi) || pi <= 0
                || !double.TryParse(eo["ageSeconds"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var age) || !double.IsFinite(age) || age < 0 || age > 86400
                || eo["source"]?.ToString() is not ("commit" or "files" or "process" or "marker" or "work"))
                return (null, new(400, "usage", "each progress entry is { \"item\": N, \"ageSeconds\": 0 to 86400, \"source\": \"commit|files|process|marker|work\" }", 2));
            list.Add((pi, now.AddSeconds(-age), eo["source"]!.ToString()));
        }
        return (list, null);
    }

    // The summary: text of 140 characters at most (it is shown to every peer, 3.2). Null: fine; the secret check is the messenger's (worker-messaging.md 5.4).
    public static PolicyRefusal? CheckSummary(JsonNode summary, out string text)
    {
        text = "";
        if (summary.GetValueKind() != System.Text.Json.JsonValueKind.String) return new(400, "usage", "summary is text", 2);
        text = summary.GetValue<string>();
        return text.Length > 140 ? new(413, "too-large", "summary is 140 characters at most", 2) : null;
    }
}
