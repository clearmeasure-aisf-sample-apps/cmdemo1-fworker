using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Integrations.GitHub;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>W9.10k: the board reader asks for the labels of each issue only when it is told to (the reader of the reconciler where the engine acts), and then every card carries them.</summary>
public class BoardReaderLabelsTests
{
    sealed class Stub(string answer) : HttpMessageHandler
    {
        public readonly List<string> Queries = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Queries.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["query"]!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    const string Page = """
        { "data": { "organization": { "projectV2": { "items": { "pageInfo": { "hasNextPage": false, "endCursor": null }, "nodes": [
          { "id": "c1", "fieldValueByName": { "name": "Development" }, "content": { "number": 5, "state": "OPEN", "title": "one", "repository": { "nameWithOwner": "acme/orders" },
            "labels": { "nodes": [ { "name": "factory worker" }, { "name": "worker:alice" }, { "name": "" } ] } } },
          { "id": "c2", "fieldValueByName": null, "content": { "number": 6, "state": "CLOSED", "title": "two", "repository": { "nameWithOwner": "acme/orders" } } }
        ] } } } } }
        """;

    static HttpBoardReader Reader(Stub s, bool labels) => new(new HttpClient(s) { BaseAddress = new Uri("https://api.example/") }, new StaticCredentialSource([], new GitHubCredential("t", "x")), withLabels: labels);

    [Fact]
    public async Task WithLabels_TheQueryAsksForThem_AndEveryCardCarriesTheNamesThatAreNotEmpty()
    {
        var stub = new Stub(Page);
        var cards = await Reader(stub, true).Read(new BoardRef("acme", 1));
        Assert.Contains("labels(first:30)", Assert.Single(stub.Queries));
        Assert.Equal(["factory worker", "worker:alice"], cards.Single(c => c.Number == 5).Labels);
        Assert.Empty(cards.Single(c => c.Number == 6).Labels!);   // (asked for, and the issue has none)
        Assert.Equal(("Development", true), (cards[0].Column, cards[0].Open)); Assert.Null(cards[1].Column);
    }

    [Fact]
    public async Task WithoutLabels_TheQueryIsWhatItWas_AndTheCardsSayNothingOfThem()
    {
        var stub = new Stub(Page);
        var cards = await Reader(stub, false).Read(new BoardRef("acme", 1));
        Assert.DoesNotContain("labels", Assert.Single(stub.Queries));
        Assert.All(cards, c => Assert.Null(c.Labels));
    }
}
