using Xunit;

namespace Fworker.Domain.Tests;

/// <summary>
/// W1.3: the ranking comparer ported into Domain, pinned against what Plant.Plan sorted inline before W0.9 (the differential of
/// tests/Gateway.Tests/PlanRankingTests.cs, replayed here against the port) and against the order the gateway's own comparer pins.
/// An offer is (item, column); pins and priorities are keyed by the item number as text, like the JSON objects of the gateway.
/// </summary>
public sealed class RankingParityTests
{
    const string Intake = "Technical Design";

    sealed record Offer(int Item, string? Column);

    static PlanRank RankOf(Offer o, IReadOnlyDictionary<string, string?>? pins, IReadOnlyDictionary<string, string?>? prios)
    {
        string? pin = null, prio = null;
        pins?.TryGetValue(o.Item.ToString(), out pin);
        prios?.TryGetValue(o.Item.ToString(), out prio);
        return PlanRank.Of(pin, prio, o.Column, Intake);
    }

    static int[] OrderOf(IEnumerable<Offer> offers, IReadOnlyDictionary<string, string?>? pins = null, IReadOnlyDictionary<string, string?>? prios = null)
        => PlanRankComparer.Order(offers, o => RankOf(o, pins, prios)).Select(o => o.Item).ToArray();

    static Offer[] Offers(params (int Item, string? Column)[] rows) => rows.Select(r => new Offer(r.Item, r.Column)).ToArray();

    // the expression Plant.Plan used before W0.9, verbatim in shape (the same keys, the same ThenBy chain)
    static int[] LegacyOrder(Offer[] offers, Dictionary<string, string?>? pins, Dictionary<string, string?>? priorities)
    {
        static int Rank(string? p) => p == "top" ? 0 : p == "high" ? 1 : 2;
        string? Get(Dictionary<string, string?>? d, int item) => d is not null && d.TryGetValue(item.ToString(), out var v) ? v : null;
        return offers.OrderBy(x => string.IsNullOrEmpty(Get(pins, x.Item)) ? 1 : 0).ThenBy(x => Rank(Get(priorities, x.Item)))
            .ThenBy(x => string.Equals(x.Column ?? "", Intake, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .Select(o => o.Item).ToArray();
    }

    [Fact]
    public void NoPinsNoPriorities_KeepsTheSweepsOrder_WithinTheColumnRank()
    {
        var o = Offers((1, Intake), (2, "Development"), (3, Intake), (4, "Functional Testing"), (5, "Development"));
        Assert.Equal(new[] { 2, 4, 5, 1, 3 }, OrderOf(o));
    }

    [Fact]
    public void PinnedFirst_ThenPriority_ThenStartedBeforeNew()
    {
        var o = Offers((1, Intake), (2, "Development"), (3, Intake), (4, Intake), (5, "Development"), (6, Intake));
        var pins = new Dictionary<string, string?> { ["6"] = "worker-a" };
        var prios = new Dictionary<string, string?> { ["3"] = "top", ["4"] = "high", ["5"] = "high" };
        Assert.Equal(new[] { 6, 3, 5, 4, 2, 1 }, OrderOf(o, pins, prios));
    }

    [Fact]
    public void PinnedBeatsTopPriority_AndAnEmptyPinIsNoPin()
    {
        var o = Offers((1, "Development"), (2, "Development"), (3, "Development"));
        var pins = new Dictionary<string, string?> { ["2"] = "w", ["3"] = "" };
        var prios = new Dictionary<string, string?> { ["1"] = "top", ["3"] = "top" };
        Assert.Equal(new[] { 2, 1, 3 }, OrderOf(o, pins, prios));
    }

    [Fact]
    public void IntakeColumn_IsComparedWithoutCase_AndAnUnknownPriorityIsNone()
    {
        Assert.Equal(new[] { 2, 1 }, OrderOf(Offers((1, "technical design"), (2, "Development"))));
        Assert.Equal(new[] { 1, 2 }, OrderOf(Offers((1, Intake), (2, Intake)), null, new Dictionary<string, string?> { ["2"] = "urgent" }));
    }

    [Fact]
    public void PriorityLabelIsComparedAsWritten_NotWithoutCase()
    {
        // the gateway compares "top" and "high" ordinally: "TOP" is no priority
        Assert.Equal(new[] { 1, 2 }, OrderOf(Offers((1, "Development"), (2, "Development")), null, new Dictionary<string, string?> { ["2"] = "TOP" }));
        Assert.Equal(0, PlanRank.PriorityRank("top"));
        Assert.Equal(1, PlanRank.PriorityRank("high"));
        Assert.Equal(2, PlanRank.PriorityRank("TOP"));
        Assert.Equal(2, PlanRank.PriorityRank(null));
    }

    [Fact]
    public void ColumnMissing_CountsAsPastIntake()
    {
        Assert.Equal(new[] { 1, 2 }, OrderOf(Offers((1, null), (2, Intake))));
        Assert.Equal(new[] { 2, 1 }, OrderOf(Offers((1, Intake), (2, null))));
    }

    [Fact]
    public void TheComparerIsAnOrder_Antisymmetric_AndEqualRanksCompareZero()
    {
        var ranks = new List<PlanRank>();
        foreach (var pinned in new[] { false, true })
            foreach (var prio in new string?[] { null, "top", "high", "low" })
                foreach (var intake in new[] { false, true })
                    ranks.Add(new PlanRank(pinned, prio, intake));
        foreach (var a in ranks)
            foreach (var b in ranks)
                Assert.Equal(-Math.Sign(PlanRankComparer.Instance.Compare(b, a)), Math.Sign(PlanRankComparer.Instance.Compare(a, b)));
        Assert.Equal(0, PlanRankComparer.Instance.Compare(new PlanRank(false, null, true), new PlanRank(false, "low", true)));
    }

    [Fact]
    public void TheSortIsStable_ForOneRank()
    {
        var o = Offers((9, "Development"), (3, "Development"), (7, "Development"), (1, "Development"));
        Assert.Equal(new[] { 9, 3, 7, 1 }, OrderOf(o));
    }

    [Fact]
    public void SameOrderAsTheOldInlineSort_ForManyGeneratedSweeps()
    {
        var rnd = new Random(297);
        string?[] cols = { Intake, "Development", "Functional Testing", "Todo", null, "technical design" };
        string?[] prios = { null, "top", "high", "low", "", "TOP" };
        for (var n = 0; n < 2000; n++)
        {
            var count = rnd.Next(0, 25);
            var offers = Enumerable.Range(1, count).Select(i => new Offer(i, cols[rnd.Next(cols.Length)])).ToArray();
            var pins = new Dictionary<string, string?>(); var pr = new Dictionary<string, string?>();
            for (var i = 1; i <= count; i++)
            {
                if (rnd.Next(4) == 0) pins[i.ToString()] = rnd.Next(2) == 0 ? "w" + i : "";
                if (prios[rnd.Next(prios.Length)] is { } p) pr[i.ToString()] = p;
            }
            // a shuffled sweep: the order of equal ranks is the sweep's, whatever it is
            offers = offers.OrderBy(_ => rnd.Next()).ToArray();
            Assert.Equal(LegacyOrder(offers, pins, pr), OrderOf(offers, pins, pr));
            Assert.Equal(LegacyOrder(offers, null, null), OrderOf(offers));
        }
    }
}
