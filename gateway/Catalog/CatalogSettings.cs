using System.Text.Json.Nodes;

// The settings of the system catalog's wiring (task W1.2, docs/design/system-gateway.md decision 8): where the gateway reads cmdemo1-system's
// system.json and how often. Off by default: a gateway that does not name the catalog answers, composes and starts exactly as before (no hosted
// service, no block on /api/v1/status). The catalog is informational until later waves; the home repository still comes from instance.json.
//
//   instance.json "catalog": { "enabled": false, "repository": "cmdemo1-system" | "owner/repo", "path": "system.json", "ref": "" (the default
//   branch), "refreshSeconds": 300 }
//
//   FACTORY_CATALOG=1|0 overrides "enabled"; the GitHub address is GITHUB_API_URL (the variable the rule scripts read), else api.github.com;
//   the credential is GH_TOKEN (the placeholder "unset" counts as none: the read is then made without one and a private repository answers 404).
sealed class CatalogSettings
{
    public const int DefaultRefreshSeconds = 300, MinRefreshSeconds = 30, MaxRefreshSeconds = 86400;

    public bool Enabled { get; init; }
    public string Owner { get; init; } = "";
    public string Repository { get; init; } = "cmdemo1-system";
    public string Path { get; init; } = "system.json";
    public string Ref { get; init; } = "";
    public int RefreshSeconds { get; init; } = DefaultRefreshSeconds;
    public string ApiUrl { get; init; } = "https://api.github.com";
    public string Token { get; init; } = "";

    public static CatalogSettings From(InstanceConfig cfg, Func<string, string?> env)
    {
        var root = cfg.Root;
        JsonNode? block = null;
        try { block = root?["catalog"]; } catch { }
        bool enabled = false;
        try { enabled = block?["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var b) && b; } catch { }
        var flag = env("FACTORY_CATALOG");
        if (flag == "1") enabled = true; else if (flag == "0") enabled = false;

        var owner = Text(root, "owner");
        var repository = Text(block, "repository");
        if (repository.Contains('/')) { var i = repository.IndexOf('/'); owner = repository[..i]; repository = repository[(i + 1)..]; }
        if (repository.Length == 0) repository = "cmdemo1-system";

        var seconds = DefaultRefreshSeconds;
        try { if (block?["refreshSeconds"] is JsonValue sv && sv.TryGetValue<int>(out var s)) seconds = s; } catch { }
        seconds = Math.Clamp(seconds, MinRefreshSeconds, MaxRefreshSeconds);

        var api = env("GITHUB_API_URL");
        var token = env("GH_TOKEN");
        return new CatalogSettings
        {
            Enabled = enabled, Owner = owner, Repository = repository, RefreshSeconds = seconds,
            Path = Text(block, "path") is { Length: > 0 } p ? p.TrimStart('/') : "system.json",
            Ref = Text(block, "ref"),
            ApiUrl = string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/'),
            Token = RevocationOptions.Unset(token) ? "" : token!.Trim(),
        };
    }

    static string Text(JsonNode? node, string name)
    {
        try { return node?[name] is JsonValue v && v.TryGetValue<string>(out var s) && s != null ? s.Trim() : ""; } catch { return ""; }
    }
}
