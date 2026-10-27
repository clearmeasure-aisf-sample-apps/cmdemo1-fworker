using System.Text;
using System.Text.Json.Nodes;

// The credential rules that depend on the gateway's configuration (task C8, gateway-composition.md 3 F), moved verbatim out of Auth.cs, where the
// environment, auth.legacyKinds and the keeper's settings were process-wide statics set by Program.cs. They are fields of this object now, made once
// by the composition (GatewayComposition) and injected into the authentication middleware and the endpoints that mint a token. The token formats
// and their MACs stay in Auth.cs (pure functions of a secret); tests/Gateway.Tests/CredentialPolicyTests.cs.
sealed class CredentialPolicy(string environment, bool legacyKinds, KeeperSettings keeper)
{
    // auth.legacyKinds (agents-roles-launch.md 4, 15 step 10): while true (the source default) the per-role kinds w1, w2, m1, o1, k1 and i1 are
    // accepted beside r1; false refuses them at every endpoint. The admin token, q1 links and n1 nonces are not role credentials and stay.
    public bool LegacyKinds { get; } = legacyKinds;
    // the gateway's environment (FACTORY_ENVIRONMENT, trimmed and lower case): an r1 names the environment it was minted for and is refused by any other
    public string Environment { get; } = environment;
    // the backlog keeper's settings (Keeper.cs): k1 tokens are accepted only while keeper.enabled and for its names
    public KeeperSettings Keeper { get; } = keeper;

    // Returns null for an unknown, malformed, forged or expired credential.
    public Principal? Authenticate(string? header, string adminToken, string secret, MessagingSettings msg, Revocations rev)
    {
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ")) return null;
        var t = header[7..];
        if (Auth.Same(t, adminToken)) return new Principal("admin");
        if (secret.Length == 0) return null;
        if (t.StartsWith("r1.")) return VerifyRoleToken(secret, t, msg, rev);
        if (!LegacyKinds) return null;
        if (t.StartsWith("w1."))
        {
            var last = t.LastIndexOf('.'); if (last <= 3) return null;
            var name = t[3..last]; if (!Auth.ValidName(name) || msg.Reserved.Contains(name)) return null;
            return Auth.Same(t, Auth.WorkerToken(secret, name, rev.Of(name))) ? new Principal("worker", name) : null;
        }
        if (t.StartsWith("w2."))
        {
            var parts = t.Split('.'); if (parts.Length != 5) return null;
            var (name, runtime) = (parts[1], parts[2]);
            if (!Auth.ValidName(name) || msg.Reserved.Contains(name) || !Auth.ValidRuntime(runtime) || !long.TryParse(parts[3], out var x) || parts[3] != x.ToString()) return null;
            if (x < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
            return Auth.Same(t, Auth.WorkerToken2(secret, name, runtime, x, rev.Of(name))) ? new Principal("worker", name, Expires: x, Runtime: runtime) : null;
        }
        if (t.StartsWith("m1."))
        {
            var parts = t.Split('.'); if (parts.Length != 4 || !msg.ManagerEnabled || !msg.ManagerNames.Contains(parts[1])) return null;
            return Auth.Same(t, Auth.ManagerToken(secret, parts[1], msg.TokenGeneration, rev.Of(parts[1]))) ? new Principal("manager", parts[1]) : null;
        }
        if (t.StartsWith("o1."))
        {   // only while operator.enabled, only a name of operator.allowedNames, bound to operator.tokenGeneration and the name's revocation generation
            var parts = t.Split('.'); if (parts.Length != 4 || !msg.OperatorEnabled || !msg.OperatorNames.Contains(parts[1])) return null;
            return Auth.Same(t, Auth.OperatorToken(secret, parts[1], msg.OperatorTokenGeneration, rev.Of(parts[1]))) ? new Principal("operator", parts[1]) : null;
        }
        if (t.StartsWith("k1."))
        {   // only while keeper.enabled, only a name of keeper.allowedNames, bound to keeper.tokenGeneration and the name's revocation generation
            var parts = t.Split('.'); if (parts.Length != 4 || !Keeper.Enabled || !Keeper.Names.Contains(parts[1])) return null;
            return Auth.Same(t, Auth.KeeperToken(secret, parts[1], Keeper.TokenGeneration, rev.Of(parts[1]))) ? new Principal("keeper", parts[1]) : null;
        }
        if (t.StartsWith("i1.")) return Auth.VerifyItemToken(secret, t, rev);
        return null;
    }

    // An r1: MAC, expiry, environment, a known role and mode, a valid unreserved name and the name's revocation generation. Null otherwise.
    public Principal? VerifyRoleToken(string secret, string t, MessagingSettings msg, Revocations rev)
    {
        if (t.Length > 4000) return null;
        var parts = t.Split('.'); if (parts.Length != 3 || parts[0] != "r1") return null;
        if (!Auth.Same(parts[2], Auth.Mac(secret, "role:" + parts[1]))) return null;
        try
        {
            var j = JsonNode.Parse(Encoding.UTF8.GetString(Auth.UnB64(parts[1])))!;
            if (j["v"]?.GetValue<int>() != 1) return null;
            var name = j["n"]!.ToString(); var role = j["r"]!.ToString(); var mode = j["m"]!.ToString(); var env = j["e"]!.ToString();
            var x = j["x"]!.GetValue<long>();
            if (x < DateTimeOffset.UtcNow.ToUnixTimeSeconds() || !Auth.ValidName(name) || msg.Reserved.Contains(name) || !Auth.Roles.Contains(role) || !Auth.Modes.Contains(mode)) return null;
            if (!string.Equals(env, Environment, StringComparison.Ordinal)) return null;
            if ((j["g"]?.GetValue<int>() ?? 0) != rev.Of(name)) return null;
            return new Principal("role", name, j["i"]?.GetValue<int>() ?? 0, role, j["a"]?.ToString() ?? "", x, Runtime: mode == "manual" ? "" : mode == "api" ? (j["ag"]?.ToString() == "copilot-cloud-agent" ? "copilot-cloud" : "cursor-cloud") : "cae-job",
                Phase: j["p"]?.ToString() ?? "", Mode: mode, Agent: j["ag"]?.ToString() ?? "", Env: env);
        }
        catch { return null; }
    }
}
