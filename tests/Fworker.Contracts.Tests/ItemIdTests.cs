using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Xunit;

namespace Fworker.Contracts.Tests;

public class ItemIdTests
{
    static readonly RepoId Home = new("Org", "Home-Repo");

    [Theory]
    [InlineData("org/repo#1", "org/repo#1")]
    [InlineData("Clear-Measure/CMDemo1_Fworker#299", "clear-measure/cmdemo1_fworker#299")]
    [InlineData("  org/repo.name#1234567  ", "org/repo.name#1234567")]
    public void ParseThenFormatRoundTripsLowercase(string text, string expected)
    {
        var id = ItemId.Parse(text);
        Assert.Equal(expected, id.ToString());
        Assert.Equal(id, ItemId.Parse(id.ToString()));
    }

    [Fact]
    public void CaseDoesNotMakeIdsDiffer()
    {
        var a = ItemId.Parse("Org/Repo#5");
        var b = new ItemId("ORG", "rEPO", 5);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal("org", a.Repository.Owner);
        Assert.Equal("repo", a.Repository.Repo);
        Assert.Equal(5, a.Number);
    }

    [Fact]
    public void DifferentRepositoriesWithTheSameNumberDoNotCollide() =>
        Assert.NotEqual(ItemId.Parse("org/a#1"), ItemId.Parse("org/b#1"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#5")]
    [InlineData("5")]
    [InlineData("org#5")]
    [InlineData("org/repo")]
    [InlineData("org/repo#")]
    [InlineData("org/repo#0")]
    [InlineData("org/repo#007")]
    [InlineData("org/repo#-1")]
    [InlineData("org/repo#+1")]
    [InlineData("org/repo#1a")]
    [InlineData("org/repo#12345678")]
    [InlineData("org/repo#99999999999")]
    [InlineData("org/repo#1#2")]
    [InlineData("org/repo!1")]
    [InlineData("org//repo#1")]
    [InlineData("a/b/c#1")]
    [InlineData("/repo#1")]
    [InlineData("org/#1")]
    [InlineData("or g/repo#1")]
    [InlineData("org/re po#1")]
    public void InvalidTextIsRefused(string text)
    {
        Assert.False(ItemId.TryParse(text, out var id));
        Assert.Equal(default, id);
        Assert.Throws<FormatException>(() => ItemId.Parse(text));
    }

    [Fact]
    public void NullIsRefused() => Assert.False(ItemId.TryParse(null, out _));

    [Fact]
    public void BareHashMeansHomeOnlyWhenAHomeIsGiven()
    {
        Assert.False(ItemId.TryParse("#12", out _));
        Assert.True(ItemId.TryParse("#12", Home, out var id));
        Assert.Equal("org/home-repo#12", id.ToString());
        Assert.Equal(ItemId.Parse("other/x#3"), ItemId.Parse("Other/X#3", Home));
        Assert.Throws<FormatException>(() => ItemId.Parse("12", Home));
        Assert.False(ItemId.TryParse("#0", Home, out _));
    }

    [Fact]
    public void PullRequestFormUsesABang()
    {
        var id = ItemId.Parse("Org/Repo#7");
        Assert.Equal("org/repo!7", id.ToPullRequestString());
        Assert.True(ItemId.TryParsePullRequest("ORG/REPO!7", out var pr));
        Assert.Equal(id, pr);
        Assert.False(ItemId.TryParsePullRequest("org/repo#7", out _));
    }

    [Fact]
    public void ConstructorRefusesBadValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ItemId("org", "repo", 0));
        Assert.Throws<ArgumentException>(() => new ItemId("org/x", "repo", 1));
        Assert.Throws<ArgumentException>(() => new ItemId("org", "", 1));
        Assert.Throws<ArgumentException>(() => new ItemId(default(RepoId), 1));
    }

    [Fact]
    public void RepoIdParsesAndLowercases()
    {
        Assert.Equal("org/repo", RepoId.Parse("Org/Repo").ToString());
        Assert.False(RepoId.TryParse("org", out _));
        Assert.False(RepoId.TryParse("org/repo/x", out _));
        Assert.False(RepoId.TryParse(null, out _));
    }

    // W6.1: the ids survive a message or a state that is saved as JSON (a durable inbox after a restart); the default serializer gave back the default id
    [Fact]
    public void IdsRoundTripThroughJsonAsTheTextPeopleRead()
    {
        var id = ItemId.Parse("Org/Repo#42");
        Assert.Equal("\"org/repo#42\"", System.Text.Json.JsonSerializer.Serialize(id));
        Assert.Equal(id, System.Text.Json.JsonSerializer.Deserialize<ItemId>("\"ORG/repo#42\""));
        Assert.Equal("\"org/repo\"", System.Text.Json.JsonSerializer.Serialize(id.Repository));
        Assert.Equal(id.Repository, System.Text.Json.JsonSerializer.Deserialize<RepoId>("\"Org/Repo\""));
        var message = new ItemOpened(id, "t");
        var back = System.Text.Json.JsonSerializer.Deserialize<ItemOpened>(System.Text.Json.JsonSerializer.Serialize(message))!;
        Assert.Equal(message.Item, back.Item);
    }

    [Fact]
    public void TheObjectFormTheDefaultSerializerWroteIsStillRead()
    {
        Assert.Equal(ItemId.Parse("org/repo#7"), System.Text.Json.JsonSerializer.Deserialize<ItemId>("""{"Repository":{"Owner":"org","Repo":"repo"},"Number":7}"""));
        Assert.Equal(ItemId.Parse("org/repo#7"), System.Text.Json.JsonSerializer.Deserialize<ItemId>("""{"repository":"org/repo","number":7}"""));
        Assert.Equal(RepoId.Parse("org/repo"), System.Text.Json.JsonSerializer.Deserialize<RepoId>("""{"Owner":"org","Repo":"repo"}"""));
    }

    [Theory]
    [InlineData("\"org/repo\"")]
    [InlineData("\"#5\"")]
    [InlineData("5")]
    [InlineData("""{"Number":7}""")]
    [InlineData("""{"Repository":{"Owner":"org","Repo":"repo"},"Number":0}""")]
    public void AnItemThatIsNotOneIsAJsonError(string json) => Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<ItemId>(json));

    [MessageIdentity("test.item-opened")]
    sealed record ItemOpened(ItemId Item, string Title) : ItemEvent(Item);

    sealed record Unnamed(ItemId Item) : ItemCommand(Item);

    [Fact]
    public void MessageBaseTypesCarryTheItemAndAStableName()
    {
        var id = ItemId.Parse("org/repo#1");
        IItemMessage e = new ItemOpened(id, "t");
        Assert.Equal(id, e.Item);
        Assert.IsAssignableFrom<IEvent>(e);
        Assert.IsAssignableFrom<ICommand>(new Unnamed(id));
        Assert.Equal("test.item-opened", MessageIdentity.NameOf(typeof(ItemOpened)));
        Assert.Null(MessageIdentity.NameOf(typeof(Unnamed)));
    }
}
