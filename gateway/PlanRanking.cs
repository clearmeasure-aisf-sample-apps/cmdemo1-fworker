using System.Text.Json.Nodes;

// The order in which Plant.Plan offers the items of a sweep (delivery-speed.md 3.1), as one comparer. It reproduces what Plan sorted inline before
// W0.9, no more: pinned items first (issue 46), then the priority label (top, high, none), then work already started before new work (issue 97:
// an item in a column past the intake column before one in the intake column). The sort is stable, so items of one rank keep the sweep's order.
// The later ranks of the design (resume, deeper column, age, generated last) join here, each with its own pinned test.
readonly record struct PlanRank(bool Pinned, string? Priority, bool InIntake)
{
    public static int PriorityRank(string? p) => p == "top" ? 0 : p == "high" ? 1 : 2;

    public static PlanRank Of(JsonNode offer, JsonObject? pins, JsonObject? priorities, string intakeColumn)
    {
        var item = offer["item"]!.ToString();
        return new PlanRank(
            !string.IsNullOrEmpty(pins?[item]?.ToString()),
            priorities?[item]?.ToString(),
            string.Equals(offer["column"]?.ToString() ?? "", intakeColumn, StringComparison.OrdinalIgnoreCase));
    }
}

sealed class PlanRankComparer : IComparer<PlanRank>
{
    public static readonly PlanRankComparer Instance = new();

    public int Compare(PlanRank a, PlanRank b)
    {
        if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
        var pc = PlanRank.PriorityRank(a.Priority).CompareTo(PlanRank.PriorityRank(b.Priority));
        if (pc != 0) return pc;
        if (a.InIntake != b.InIntake) return a.InIntake ? 1 : -1;
        return 0;
    }

    // the offers in the order Plan takes them
    public static IEnumerable<JsonNode?> Order(JsonArray offers, JsonObject? pins, JsonObject? priorities, string intakeColumn)
        => offers.OrderBy(o => PlanRank.Of(o!, pins, priorities, intakeColumn), Instance);
}
