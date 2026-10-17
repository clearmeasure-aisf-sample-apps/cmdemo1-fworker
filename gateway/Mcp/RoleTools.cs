// The tool set of each role at /mcp (docs/design/agents-roles-launch.md 6.2): the one table that decides what tools/list shows a caller and what
// tools/call admits. A tool outside the caller's role is refused before its handler runs, and every handler then goes through the same
// authorization the /api/v1 endpoint applies (Auth.AuthorizeVerb and the endpoint's own checks): the table narrows, it never widens.
// Code-owner path (.github/CODEOWNERS: /gateway/Mcp/).
static class RoleTools
{
    public static readonly IReadOnlyDictionary<string, string[]> Sets = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        // the owner (environment-test-factories.md 18.3): the read tools; action tools follow in their own step
        ["owner"] = ["factory_status", "workers_and_runs", "item_detail", "metrics"],
    };

    // The role a principal plays at /mcp, or "" when it has none there. The admin token is the owner (agents-roles-launch.md 4).
    public static string RoleOf(Principal p) => p.Kind switch
    {
        "admin" => "owner",
        _ => ""
    };

    public static bool Allows(string role, string tool) => Sets.TryGetValue(role, out var s) && s.Contains(tool);
}
