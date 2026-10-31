using Fworker.Contracts.Messaging;
using Xunit;
using static Fworker.Workflow.Tests.TrainFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// The premise of "a delivered item stays in the release queue until v1's AutoPromoter reaches prod": the prod train, in shadow (v1's AutoPromoter makes the deployments), still publishes
/// DeliveredTo(item, prod, release) when a prod deployment succeeds, and the saga holds its item for that fact. A failed deployment and an older release say nothing.
/// </summary>
public class DeliveryTrainProdShadowTests
{
    static async Task<TrainFixture> ProdTrain()
    {
        var f = new TrainFixture(env: "prod", shadow: true);
        await f.Baseline();
        f.Range('d', 'a');
        await f.Merge(7, 'a');
        return f;
    }

    [Fact]
    public async Task ASucceededProdDeploymentByV1_PublishesDeliveredToProdForTheItemsOfTheRelease()
    {
        var f = await ProdTrain();
        await f.Release("1.0.2", 'a');
        await f.Deploy("1.0.2", "Prod", "Started"); await f.Deploy("1.0.2", "Prod", "Succeeded");
        var d = Assert.Single(f.Of<DeliveredTo>());
        Assert.Equal(("acme/orders#7", "prod", "1.0.2"), (d.Item.ToString(), d.Environment, d.Release));
    }

    [Fact]
    public async Task ANewerReleaseThatCarriesTheItemThroughTheCompare_PublishesItAsDeliveredInThatRelease()
    {
        var f = await ProdTrain();
        f.Range('d', 'a', 'b');
        await f.Release("1.0.2", 'a');   // (never deployed to prod)
        await f.Release("1.0.3", 'b');
        await f.Deploy("1.0.3", "Prod", "Started"); await f.Deploy("1.0.3", "Prod", "Succeeded");
        var d = Assert.Single(f.Of<DeliveredTo>());
        Assert.Equal(("acme/orders#7", "prod", "1.0.3"), (d.Item.ToString(), d.Environment, d.Release));
    }

    [Fact]
    public async Task AFailedProdDeployment_PublishesNothing()
    {
        var f = await ProdTrain();
        await f.Release("1.0.2", 'a');
        await f.Deploy("1.0.2", "Prod", "Started"); await f.Deploy("1.0.2", "Prod", "Failed");
        Assert.Empty(f.Of<DeliveredTo>());
    }

    [Fact]
    public async Task AnOlderReleaseDeployedAfterANewerOne_PublishesNothingAgain()
    {
        var f = await ProdTrain();
        f.Range('d', 'a', 'b'); f.Range('b', 'a');
        await f.Release("1.0.2", 'a'); await f.Release("1.0.3", 'b');
        await f.Deploy("1.0.3", "Prod", "Started"); await f.Deploy("1.0.3", "Prod", "Succeeded");
        Assert.Single(f.Of<DeliveredTo>());
        f.All.Clear();
        f.Now += TimeSpan.FromMinutes(5);
        await f.Deploy("1.0.2", "Prod", "Started"); await f.Deploy("1.0.2", "Prod", "Succeeded");
        Assert.Empty(f.Of<DeliveredTo>());
    }
}
