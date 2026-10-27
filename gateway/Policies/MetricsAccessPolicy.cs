// Who may read the metrics, and with which queries (task C8, gateway-composition.md 3 F; it was the body of MetricsAccessFilter): the operator, the
// active operator session (as the operator: Admitted), the active supervisor for its daily summary (docs/design/plant-manager.md 10e) and the active
// backlog keeper (the median time in Development, for the oversized-item policy). The supervisor and the keeper read the cached answers of two windows
// only: they never force a recomputation over every item's comments. Pure; the plant's slots come in as functions so they are read only for the
// credential that needs them. tests/Gateway.Tests/MetricsAccessPolicyTests.cs.
static class MetricsAccessPolicy
{
    // kind and name: the admitted credential (CallerView.Admitted); fresh, max, since: the query as sent (strings, "" when absent).
    // Null: the call goes on.
    public static PolicyRefusal? Check(string kind, string name, bool ownerMode, bool managerEnabled, Func<string> activeSupervisor, Func<string> activeKeeper,
        string fresh, string max, string since)
    {
        if (kind == "manager")
        {
            if (!ownerMode || !managerEnabled || activeSupervisor() != name) return new(403, "not-supervisor", "this credential is not the active supervisor: register with subscribe --own --supervisor first", 30);
            if (BeyondTwoWindows(fresh, max, since)) return new(400, "usage", "the supervisor reads since=24h or since=7d (no fresh, no max)", 2);
        }
        else if (kind == "keeper")
        {
            if (!ownerMode || activeKeeper() != name) return new(403, "not-keeper", "this credential is not the active backlog keeper: register with subscribe --own --keeper first", 30);
            if (BeyondTwoWindows(fresh, max, since)) return new(400, "usage", "the backlog keeper reads since=24h or since=7d (no fresh, no max)", 2);
        }
        else if (kind != "admin") return PolicyRefusal.Bare(403);
        return null;
    }

    // true when the query asks for more than the cached answers of since=24h or 7d (or none)
    public static bool BeyondTwoWindows(string fresh, string max, string since) => fresh == "1" || max != "" || since is not ("" or "24h" or "7d");

    // The operator session's query rule (the handler's): since=24h or 7d (or none), no max and no fresh at all (the key present counts). Null when the
    // query is fine; the usage text otherwise.
    public static string? OperatorQueryError(string since, string max, bool freshPresent) =>
        since is not ("" or "24h" or "7d") || max != "" || freshPresent
            ? "the operator session reads the metrics with since=24h or 7d (or none), no max and no fresh: answers come from a 120 s cache and a miss computes them" : null;
}
