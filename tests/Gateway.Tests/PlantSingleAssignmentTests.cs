using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// V1.1: the plant keeps ONE own-item assignment per item, whoever made the offer (the key of an assignment is item, kind and role). An offer of v1's sweep that is assigned but not accepted yet, and the saga's offer of
/// the same item after the admission gave it a saga, are the same assignment: the saga's slot offer does not make a second one for another worker.
/// </summary>
public sealed class PlantSingleAssignmentTests
{
    static JsonObject V1Offer(int item) => new() { ["item"] = item, ["column"] = "Development", ["kind"] = "own-item", ["reason"] = "orphan" };
    static JsonObject SagaOffer(int item) => new() { ["item"] = item, ["column"] = "Development", ["kind"] = "own-item", ["reason"] = "handoff", ["v2"] = true, ["arm"] = "1.2" };

    static int AssignmentsOf(Plant plant, int item) => plant.Snapshot()["assignments"]!.AsArray().Count(a => (int)a!["item"]! == item && a["kind"]!.ToString() == "own-item");

    [Fact]
    public void AV1OfferAssignedButNotAccepted_AndTheSagasOfferOfTheSameItem_AreOneAssignment()
    {
        var plant = new Plant("secret", (_, _, _, _) => Task.CompletedTask, 30);
        plant.Register("w1", "claude", Array.Empty<string>(), 1, new[] { "owner" }, 0);
        plant.Register("w2", "claude", Array.Empty<string>(), 1, new[] { "owner" }, 0);
        plant.Plan(new JsonArray(V1Offer(7)), null, null);
        Assert.Equal(1, AssignmentsOf(plant, 7));
        // the admission gave the item a saga: its offer reaches the plant outside a sweep
        plant.PlanSagaOffers(new JsonArray(SagaOffer(7)), [7]);
        Assert.Equal(1, AssignmentsOf(plant, 7));
        plant.PlanSagaOffers(new JsonArray(SagaOffer(7)), [7]);
        plant.Plan(new JsonArray(V1Offer(7)), null, null);
        Assert.Equal(1, AssignmentsOf(plant, 7));
    }
}
