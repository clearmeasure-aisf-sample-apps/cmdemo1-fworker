using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// Credentials. Three kinds, all checked here before a rule script ever runs:
//   admin     the gateway's own token (the sweep, tests, the operator)
//   worker    "w1.<name>.<hmac>"  one per worker, issued once: may register, receive assignments, ask my-items. It cannot act on an item.
//             "w2.<name>.<runtime>.<expiresUnix>.<hmac>" the same rights for a worker that a runtime adapter started (a Container Apps job
//             execution, docs/design/cloud-worker-runtimes.md 2.6): bound to its runtime and an expiry, delivered only by redeeming a one-time nonce
//             (RuntimeNonces, POST /api/v1/runtime/redeem). The runtime a registration claims is trusted only from a w2; a w1 is always "local".
//   item      "i1.<payload>.<hmac>" minted at an assignment for ONE item and ONE role: the only credential that acts on an item.
//   manager   "m1.<name>.<generation>.<hmac>" the supervisor: three read verbs (worker-messaging.md 4.6) and the plant manager's flow verb
//             manager-action (docs/design/plant-manager.md 10e), which never merges, approves, records a verdict or clears a hold.
//   operator  "o1.<name>.<generation>.<hmac>" the factory operator SESSION (Operator.cs, docs/design/operator-session.md): not the admin token. It
//             calls no verb, mints and revokes nothing, and is admitted only at the endpoints that name it (gateway/Endpoints/, the Admitted and OperatorActive guards):
//             messaging, the questions, the supervisor's budget, its release checks, a worker's capacity, drain and resume, and the read-only metrics.
// The secret never leaves the gateway host. Design: docs/design/plant-manager.md section 5.
// Revocation (operator, POST /workers/{name}/revoke): tokens are stateless, so each name has a generation, 0 until its first revocation. Every
// token of a name is bound to the generation it was minted at (generation 0 gives exactly the formats above); a revocation raises the generation,
// so every credential minted before it (worker, supervisor, item) fails here, and one minted afterwards works. The generations are kept in
// GitHub by the rule scripts (refs/factory/<instance>/revoked/<name>/<generation>) and read back at start, like owners after a restart.

sealed record Principal(string Kind, string Name = "", int Item = 0, string Role = "", string Assignment = "", long Expires = 0, int Pr = 0, string Sha = "", string Ip = "", string Runtime = "",
    string Phase = "", string Mode = "", string Agent = "", string Env = "");

// The claims of a role token "r1.<payload>.<hmac>" (docs/design/agents-roles-launch.md 4): one session, one role, and for item roles one item,
// assignment and phase. Minted only by the gateway (a runtime adapter, a test seam); the MAC is over "role:" + payload, so no other kind validates as it.
sealed record RoleClaims(string Name, string Role, string Mode, string Agent, string Env, long Expires, int Item = 0, string Assignment = "", string Phase = "", int PromptVersion = 0);

// One-time nonces of runtime workers (cloud-worker-runtimes.md 2.6). The adapter puts only a nonce into the execution's environment (anyone with
// read on the job's executions sees it); the execution redeems it once for its w2. Kept as SHA-256 hashes in memory: a gateway restart loses the
// unredeemed ones, and that execution fails its redemption and is replaced like any failed execution. A spent hash is remembered for a day, so
// a second redemption is told apart from an unknown nonce in the log (the caller gets the same refusal for both).
sealed class RuntimeNonces
{
    sealed record Entry(string Name, string Runtime, long TokenExpires, DateTime NonceExpires);
    readonly Dictionary<string, Entry> live = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTime> spent = new(StringComparer.Ordinal);
    readonly object gate = new();
    static string Hash(string nonce) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant();

    // tokenExpires: the w2's expiry (execution timeout plus 15 minutes); ttl: how long the nonce may wait for its redemption
    public string Mint(string name, string runtime, long tokenExpires, TimeSpan ttl)
    {
        if (!Auth.ValidName(name) || !Auth.ValidRuntime(runtime)) throw new ArgumentException("invalid worker name or runtime");
        var nonce = "n1." + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        lock (gate) { Prune(); live[Hash(nonce)] = new Entry(name, runtime, tokenExpires, DateTime.UtcNow.Add(ttl)); }
        return nonce;
    }

    // Returns the claims once; afterwards (or for an unknown, malformed or expired nonce) null with "nonce-spent", "nonce-expired" or "nonce-unknown".
    public (string Name, string Runtime, long TokenExpires, string? Error) Redeem(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce) || nonce.Length > 100 || !nonce.StartsWith("n1.")) return ("", "", 0, "nonce-unknown");
        var h = Hash(nonce);
        lock (gate)
        {
            Prune();
            if (spent.ContainsKey(h)) return ("", "", 0, "nonce-spent");
            if (!live.Remove(h, out var e)) return ("", "", 0, "nonce-unknown");
            spent[h] = DateTime.UtcNow;
            if (e.NonceExpires < DateTime.UtcNow || e.TokenExpires < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return ("", "", 0, "nonce-expired");
            return (e.Name, e.Runtime, e.TokenExpires, null);
        }
    }

    // a nonce of an execution that was stopped before it redeemed: never redeemable afterwards
    public void Forget(string name) { lock (gate) foreach (var k in live.Where(kv => kv.Value.Name == name).Select(kv => kv.Key).ToList()) { live.Remove(k); spent[k] = DateTime.UtcNow; } }
    public int Live { get { lock (gate) { Prune(); return live.Count; } } }

    void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var k in live.Where(kv => kv.Value.NonceExpires < now).Select(kv => kv.Key).ToList()) { live.Remove(k); spent[k] = now; }
        foreach (var k in spent.Where(kv => (now - kv.Value).TotalHours > 24).Select(kv => kv.Key).ToList()) spent.Remove(k);
    }
}

// The claims of a "q1." QC link: a capability for ONE pull request at ONE head, carrying no identity.
sealed record QcClaims(int Pr, string Sha, int Item, string Nonce, long Expires);

sealed class Revocations
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> gen = new(StringComparer.Ordinal);
    public int Of(string name) => gen.GetValueOrDefault(name);
    // a generation only rises: a late or repeated write never brings an older credential back
    public int Raise(string name, int to) => gen.AddOrUpdate(name, to, (_, old) => Math.Max(old, to));
}

// The token formats and their checks that need only a secret. What depends on the gateway's configuration is CredentialPolicy (the environment,
// auth.legacyKinds and the keeper, once statics here), and the verb tables and AuthorizeVerb are VerbPolicy (gateway/Policies/, task C8).
static class Auth
{
    public static string KeeperToken(string secret, string name, int generation, int rev = 0) => rev == 0
        ? $"k1.{name}.{generation}.{Mac(secret, $"keeper:{name}:{generation}")}" : $"k1.{name}.{generation}-{rev}.{Mac(secret, $"keeper:{name}:{generation}:{rev}")}";
    public static readonly HashSet<string> Roles = new(StringComparer.Ordinal) { "seat", "worker", "inspector", "qc", "manager", "operator", "keeper", "owner" };
    public static readonly HashSet<string> Modes = new(StringComparer.Ordinal) { "manual", "hosted", "api" };

    public static string RoleToken(string secret, RoleClaims c, int rev = 0)
    {
        var o = new JsonObject { ["v"] = 1, ["n"] = c.Name, ["r"] = c.Role, ["m"] = c.Mode, ["ag"] = c.Agent, ["e"] = c.Env, ["x"] = c.Expires };
        if (c.Phase != "") o["p"] = c.Phase;
        if (c.Item != 0) o["i"] = c.Item;
        if (c.Assignment != "") o["a"] = c.Assignment;
        if (rev != 0) o["g"] = rev;
        if (c.PromptVersion != 0) o["pv"] = c.PromptVersion;
        var p = B64(Encoding.UTF8.GetBytes(o.ToJsonString()));
        return $"r1.{p}.{Mac(secret, "role:" + p)}";
    }

    // The role principal of any credential (agents-roles-launch.md 4): the one place the legacy kinds are mapped. "" when the kind plays no role.
    public static (string Role, string Mode) RoleOf(Principal p) => p.Kind switch
    {
        "admin" => ("owner", "manual"),
        "role" => (p.Role, p.Mode),
        "worker" => ("seat", p.Runtime == "" ? "manual" : "hosted"),
        "item" => (p.Role switch { "owner" => "worker", "inspector" => "inspector", "qc" => "qc", _ => "" }, p.Runtime == "" ? "manual" : "hosted"),
        "manager" => ("manager", "manual"),
        "operator" => ("operator", "manual"),
        "keeper" => ("keeper", "manual"),
        _ => ("", "")
    };

    internal static byte[] Key(string secret) => Encoding.UTF8.GetBytes(secret);
    internal static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    internal static string Mac(string secret, string text) => Hex(HMACSHA256.HashData(Key(secret), Encoding.UTF8.GetBytes(text)));
    internal static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static byte[] UnB64(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4)); }
    internal static bool Same(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    public static bool ValidName(string n) => n.Length is > 0 and <= 40 && n.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
    public static bool ValidRuntime(string s) => s.Length is > 0 and <= 20 && s.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '-') && s != "local";
    public static bool ValidTag(string s) => s.Length is > 0 and <= 60 && s.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '.' or '_' or '-');

    // Compared case-insensitively: "Operator" and "PLANT-MANAGER" name the same thing as the reserved words (worker-messaging.md 4.2).
    static readonly string[] BaseReservedNames = ["operator", "gateway", "all", "supervisor", "plant-manager"];
    public static string OperatorToken(string secret, string name, int generation, int rev = 0) => rev == 0
        ? $"o1.{name}.{generation}.{Mac(secret, $"operator:{name}:{generation}")}" : $"o1.{name}.{generation}-{rev}.{Mac(secret, $"operator:{name}:{generation}:{rev}")}";
    public static HashSet<string> ReservedNames(IEnumerable<string> managerNames) => new(BaseReservedNames.Concat(managerNames), StringComparer.OrdinalIgnoreCase);

    // Worker registration values (worker-messaging.md 2.2): a fixed type, roles from the plant's set, at most 10 capabilities, and model and effort
    // in the tag form when they are sent.
    public static bool ValidRegistration(string type, IEnumerable<string> roles, IReadOnlyCollection<string> caps, string model, string effort) =>
        type is "persistent-local" or "ephemeral-local" or "claude-cloud" or "cursor-cloud" or "copilot-cloud"
        && roles.All(r => r is "owner" or "inspector" or "qc") && caps.Count <= 10 && caps.All(ValidTag)
        && (model == "" || ValidTag(model)) && (effort == "" || ValidTag(effort));

    // The Copilot bootstrap token (docs/design/copilot-cloud-agent.md part 3): the one secret of the work-orders repository's Copilot MCP
    // configuration. It authenticates nothing but POST /api/v1/copilot/exchange (a one-time code of one task for that task's r1): Authenticate
    // never accepts it. Bound to api.copilot.bootstrapGeneration, so raising the generation voids every copy.
    public static string CopilotBootstrapToken(string secret, int generation) => $"b1.{generation}.{Mac(secret, $"copilot-bootstrap:{generation}")}";
    public static bool VerifyCopilotBootstrap(string secret, string? header, int generation) =>
        secret.Length > 0 && header is { Length: < 300 } h && h.StartsWith("Bearer b1.") && Same(h[7..], CopilotBootstrapToken(secret, generation));

    // rev: the name's revocation generation; 0 (never revoked) keeps the original format and MAC input
    public static string WorkerToken(string secret, string name, int rev = 0) => $"w1.{name}.{Mac(secret, rev == 0 ? "worker:" + name : $"worker:{name}:{rev}")}";
    // A runtime worker's credential: name, runtime and expiry are inside the MAC, and the name's revocation generation as for w1.
    public static string WorkerToken2(string secret, string name, string runtime, long expires, int rev = 0) =>
        $"w2.{name}.{runtime}.{expires}.{Mac(secret, rev == 0 ? $"worker2:{name}:{runtime}:{expires}" : $"worker2:{name}:{runtime}:{expires}:{rev}")}";
    public static string ManagerToken(string secret, string name, int generation, int rev = 0) => rev == 0
        ? $"m1.{name}.{generation}.{Mac(secret, $"manager:{name}:{generation}")}" : $"m1.{name}.{generation}-{rev}.{Mac(secret, $"manager:{name}:{generation}:{rev}")}";

    // runtime: the runtime of the worker the assignment is offered to, from its w2 ("" for a local worker); it admits git-credential
    public static string MintItemToken(string secret, int item, string role, string assignment, string worker, TimeSpan ttl, int rev = 0, string runtime = "")
    {
        var o = new JsonObject { ["i"] = item, ["r"] = role, ["a"] = assignment, ["w"] = worker, ["x"] = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds() };
        if (rev != 0) o["g"] = rev;
        if (runtime != "") o["rt"] = runtime;
        var payload = o.ToJsonString();
        var p = B64(Encoding.UTF8.GetBytes(payload));
        return $"i1.{p}.{Mac(secret, p)}";
    }

    // An i1 (an item token): MAC, expiry and the holder's revocation generation. Also used by /mcp for the item token it keeps for a worker
    // session (gateway/Mcp/WorkerSessions.cs): that token is the gateway's own, so auth.legacyKinds does not apply to it there.
    public static Principal? VerifyItemToken(string secret, string t, Revocations rev)
    {
        if (secret.Length == 0 || !t.StartsWith("i1.")) return null;
        var parts = t.Split('.'); if (parts.Length != 3) return null;
        if (!Same(parts[2], Mac(secret, parts[1]))) return null;
        try
        {
            var j = JsonNode.Parse(Encoding.UTF8.GetString(UnB64(parts[1])))!;
            var x = j["x"]!.GetValue<long>(); if (x < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
            // minted before the last revocation of its worker name: refused with everything else of that name
            if ((j["g"]?.GetValue<int>() ?? 0) != rev.Of(j["w"]!.ToString())) return null;
            return new Principal("item", j["w"]!.ToString(), j["i"]!.GetValue<int>(), j["r"]!.ToString(), j["a"]!.ToString(), x, Runtime: j["rt"]?.ToString() ?? "");
        }
        catch { return null; }
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

}
