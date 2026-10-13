using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// Credentials. Three kinds, all checked here before a rule script ever runs:
//   admin     the gateway's own token (the sweep, tests, the operator)
//   worker    "w1.<name>.<hmac>"  one per worker, issued once: may register, receive assignments, ask my-items. It cannot act on an item.
//   item      "i1.<payload>.<hmac>" minted at an assignment for ONE item and ONE role: the only credential that acts on an item.
//   manager   "m1.<name>.<generation>.<hmac>" the supervisor: three read verbs and nothing else (worker-messaging.md 4.6).
//   operator  "o1.<name>.<generation>.<hmac>" the factory operator SESSION (Operator.cs, docs/design/operator-session.md): not the admin token. It
//             calls no verb, mints and revokes nothing, and is admitted only at the endpoints that name it (Program.cs, Admitted).
// The secret never leaves the gateway host. Design: docs/design/plant-manager.md section 5.
// Revocation (operator, POST /workers/{name}/revoke): tokens are stateless, so each name has a generation, 0 until its first revocation. Every
// token of a name is bound to the generation it was minted at (generation 0 gives exactly the formats above); a revocation raises the generation,
// so every credential minted before it (worker, supervisor, item) fails here, and one minted afterwards works. The generations are kept in
// GitHub by the rule scripts (refs/factory/<instance>/revoked/<name>/<generation>) and read back at start, like owners after a restart.

sealed record Principal(string Kind, string Name = "", int Item = 0, string Role = "", string Assignment = "", long Expires = 0, int Pr = 0, string Sha = "", string Ip = "");

// The claims of a "q1." QC link: a capability for ONE pull request at ONE head, carrying no identity.
sealed record QcClaims(int Pr, string Sha, int Item, string Nonce, long Expires);

sealed class Revocations
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> gen = new(StringComparer.Ordinal);
    public int Of(string name) => gen.GetValueOrDefault(name);
    // a generation only rises: a late or repeated write never brings an older credential back
    public int Raise(string name, int to) => gen.AddOrUpdate(name, to, (_, old) => Math.Max(old, to));
}

static class Auth
{
    public static readonly HashSet<string> OwnerVerbs = new()
    {
        "get-item", "append-body", "comment", "accept", "release", "claim-item", "record-review", "complete", "open-pr", "pr-checks",
        "wait-for-checks", "validation-verdict", "merge", "model-tier", "get-delivery"
    };
    public static readonly HashSet<string> InspectorVerbs = new() { "get-item", "inspect-facts", "approve-pr", "accept", "pr-checks", "model-tier" };
    public static readonly HashSet<string> WorkerVerbs = new() { "my-items" };
    // The supervisor's verbs. The body keeps only the fields listed for the verb, whatever the caller sent.
    public static readonly HashSet<string> ManagerVerbs = new() { "get-item", "pr-checks", "get-delivery" };
    static readonly Dictionary<string, string[]> ManagerBodyFields = new()
    {
        ["get-item"] = ["item"], ["pr-checks"] = ["item", "pr", "sha"], ["get-delivery"] = ["item", "commit"]
    };
    // Role "qc": the session that runs a PR's environment. No release, no merge, no comment, no approval: only its own job.
    public static readonly HashSet<string> QcVerbs = new() { "get-item", "accept", "qc-job", "qc-progress", "qc-report", "qc-fail", "qc-heartbeat", "qc-stopped" };
    // Principal "qc-click": a person who holds a link. Not a credential: the /qc handlers build it after checking the link, and /api/v1/verbs never yields one.
    public static readonly HashSet<string> QcClickVerbs = new() { "qc-view", "qc-request", "qc-reset", "qc-teardown", "qc-touch" };

    static byte[] Key(string secret) => Encoding.UTF8.GetBytes(secret);
    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    static string Mac(string secret, string text) => Hex(HMACSHA256.HashData(Key(secret), Encoding.UTF8.GetBytes(text)));
    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static byte[] UnB64(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4)); }
    static bool Same(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    public static bool ValidName(string n) => n.Length is > 0 and <= 40 && n.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
    public static bool ValidTag(string s) => s.Length is > 0 and <= 60 && s.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '.' or '_' or '-');

    // Compared case-insensitively: "Operator" and "PLANT-MANAGER" name the same thing as the reserved words (worker-messaging.md 4.2).
    static readonly string[] BaseReservedNames = ["operator", "gateway", "all", "supervisor", "plant-manager"];
    public static string OperatorToken(string secret, string name, int generation, int rev = 0) => rev == 0
        ? $"o1.{name}.{generation}.{Mac(secret, $"operator:{name}:{generation}")}" : $"o1.{name}.{generation}-{rev}.{Mac(secret, $"operator:{name}:{generation}:{rev}")}";
    public static HashSet<string> ReservedNames(IEnumerable<string> managerNames) => new(BaseReservedNames.Concat(managerNames), StringComparer.OrdinalIgnoreCase);

    // Worker registration values (worker-messaging.md 2.2): a fixed type, roles from the plant's set, at most 10 capabilities, and model and effort
    // in the tag form when they are sent.
    public static bool ValidRegistration(string type, IEnumerable<string> roles, IReadOnlyCollection<string> caps, string model, string effort) =>
        type is "persistent-local" or "ephemeral-local" or "claude-cloud" or "cursor-cloud"
        && roles.All(r => r is "owner" or "inspector" or "qc") && caps.Count <= 10 && caps.All(ValidTag)
        && (model == "" || ValidTag(model)) && (effort == "" || ValidTag(effort));

    // rev: the name's revocation generation; 0 (never revoked) keeps the original format and MAC input
    public static string WorkerToken(string secret, string name, int rev = 0) => $"w1.{name}.{Mac(secret, rev == 0 ? "worker:" + name : $"worker:{name}:{rev}")}";
    public static string ManagerToken(string secret, string name, int generation, int rev = 0) => rev == 0
        ? $"m1.{name}.{generation}.{Mac(secret, $"manager:{name}:{generation}")}" : $"m1.{name}.{generation}-{rev}.{Mac(secret, $"manager:{name}:{generation}:{rev}")}";

    public static string MintItemToken(string secret, int item, string role, string assignment, string worker, TimeSpan ttl, int rev = 0)
    {
        var o = new JsonObject { ["i"] = item, ["r"] = role, ["a"] = assignment, ["w"] = worker, ["x"] = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds() };
        if (rev != 0) o["g"] = rev;
        var payload = o.ToJsonString();
        var p = B64(Encoding.UTF8.GetBytes(payload));
        return $"i1.{p}.{Mac(secret, p)}";
    }

    // Returns null for an unknown, malformed, forged or expired credential.
    public static Principal? Authenticate(string? header, string adminToken, string secret, MessagingSettings msg, Revocations rev)
    {
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ")) return null;
        var t = header[7..];
        if (Same(t, adminToken)) return new Principal("admin");
        if (secret.Length == 0) return null;
        if (t.StartsWith("w1."))
        {
            var last = t.LastIndexOf('.'); if (last <= 3) return null;
            var name = t[3..last]; if (!ValidName(name) || msg.Reserved.Contains(name)) return null;
            return Same(t, WorkerToken(secret, name, rev.Of(name))) ? new Principal("worker", name) : null;
        }
        if (t.StartsWith("m1."))
        {
            var parts = t.Split('.'); if (parts.Length != 4 || !msg.ManagerEnabled || !msg.ManagerNames.Contains(parts[1])) return null;
            return Same(t, ManagerToken(secret, parts[1], msg.TokenGeneration, rev.Of(parts[1]))) ? new Principal("manager", parts[1]) : null;
        }
        if (t.StartsWith("o1."))
        {   // only while operator.enabled, only a name of operator.allowedNames, bound to operator.tokenGeneration and the name's revocation generation
            var parts = t.Split('.'); if (parts.Length != 4 || !msg.OperatorEnabled || !msg.OperatorNames.Contains(parts[1])) return null;
            return Same(t, OperatorToken(secret, parts[1], msg.OperatorTokenGeneration, rev.Of(parts[1]))) ? new Principal("operator", parts[1]) : null;
        }
        if (t.StartsWith("i1."))
        {
            var parts = t.Split('.'); if (parts.Length != 3) return null;
            if (!Same(parts[2], Mac(secret, parts[1]))) return null;
            try
            {
                var j = JsonNode.Parse(Encoding.UTF8.GetString(UnB64(parts[1])))!;
                var x = j["x"]!.GetValue<long>(); if (x < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
                // minted before the last revocation of its worker name: refused with everything else of that name
                if ((j["g"]?.GetValue<int>() ?? 0) != rev.Of(j["w"]!.ToString())) return null;
                return new Principal("item", j["w"]!.ToString(), j["i"]!.GetValue<int>(), j["r"]!.ToString(), j["a"]!.ToString(), x);
            }
            catch { return null; }
        }
        return null;
    }

    // "q1.<payload>.<hmac>", MAC over "qc:" + payload, so it validates as no other token. Error: "bad-link" (forged, malformed, wrong purpose) or "link-expired".
    public static (QcClaims? Claims, string? Error) VerifyQcToken(string secret, string? token)
    {
        if (secret.Length == 0 || string.IsNullOrEmpty(token) || token.Length > 2000 || !token.StartsWith("q1.")) return (null, "bad-link");
        var parts = token.Split('.'); if (parts.Length != 3) return (null, "bad-link");
        if (!Same(parts[2], Mac(secret, "qc:" + parts[1]))) return (null, "bad-link");
        try
        {
            var j = JsonNode.Parse(Encoding.UTF8.GetString(UnB64(parts[1])))!;
            if (j["u"]?.ToString() != "qc-open") return (null, "bad-link");
            var claims = new QcClaims(j["pr"]!.GetValue<int>(), j["sha"]!.ToString(), j["i"]!.GetValue<int>(), j["n"]!.ToString(), j["x"]!.GetValue<long>());
            if (claims.Expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return (null, "link-expired");
            return (claims, null);
        }
        catch { return (null, "bad-link"); }
    }

    // Authorizes one verb call and returns the (possibly rewritten) body, or a refusal. For non-admin callers the identity fields come from the
    // credential, never from the body: a caller cannot name a different holder, item, assignment or kind.
    public static (JsonObject? Body, string? Refusal) AuthorizeVerb(Principal p, string verb, JsonObject body)
    {
        if (p.Kind == "admin") return (body, null);
        if (p.Kind == "manager")
        {
            if (!ManagerVerbs.Contains(verb)) return (null, $"the manager role cannot call '{verb}'");
            var kept = new JsonObject();
            foreach (var f in ManagerBodyFields[verb]) if (body[f] is { } v) kept[f] = v.DeepClone();
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
        var allowed = p.Role == "inspector" ? InspectorVerbs : p.Role == "owner" ? OwnerVerbs : p.Role == "qc" ? QcVerbs : new HashSet<string>();
        if (!allowed.Contains(verb)) return (null, $"the {p.Role} role cannot call '{verb}'");
        var item = body["item"]?.ToString();
        if (item != p.Item.ToString()) return (null, $"this token is for item #{p.Item} only");
        body["holder"] = p.Name;
        if (p.Role == "qc") { body["assignment"] = p.Assignment; body["role"] = "qc"; if (verb == "accept") body["kind"] = "qc-env"; }
        else if (verb == "accept") { body["assignment"] = p.Assignment; body["kind"] = p.Role == "inspector" ? "bot-job" : "own-item"; body["role"] = p.Role; }
        return (body, null);
    }
}
