using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Xunit;

namespace Gateway.Tests;

/// <summary>W5.0: the gateway side of ItemMerged: the history record of the message.</summary>
public sealed class ItemMergedPortTests
{
    [Fact]
    public void TheHistoryRecord_NamesTheItem_AndCarriesCommitGatesAndDependencies()
    {
        var home = new RepoId("acme", "orders");
        var gates = new ItemMergedGates(true, "", true, false, false, []);
        var r = ItemMergedHistoryHandler.Handle(new ItemMerged(new ItemId(home, 7), new string('a', 40), 9, gates, [new ItemId("other", "repo", 12)], "webhook"));
        Assert.Equal(("acme/orders#7", "delivery.item-merged"), (r.ItemKey, r.Type));
        var d = JsonNode.Parse(r.Detail!)!;
        Assert.Equal(new string('a', 40), d["Commit"]!.ToString());
        Assert.Equal("other/repo#12", d["Dependencies"]![0]!.ToString());
        Assert.True(d["Gates"]!["AutoMerge"]!.GetValue<bool>());
    }
}
