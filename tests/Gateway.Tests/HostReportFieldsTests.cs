using System.Text.Json;
using System.Text.Json.Nodes;
using Fworker.Domain;
using Fworker.Workflows.Capacity;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W7.4: the host report's new fields (deploy/octopus/scripts/reconcile-hosts.sh) as the gateway reads them, and the scheduler input made of them. A host with an older script sends none of the fields; none is required.
/// </summary>
public sealed class HostReportFieldsTests
{
    static JsonObject Body(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void AFullReport_IsReadField_ByField()
    {
        var f = HostReportFields.Parse(Body("""{"host":"h","containers":[],"memTotalMb":16000,"memAvailableMb":8000,"cpus":4,"load1":0.52,"diskFreeMb":50000}"""));
        Assert.Equal(new HostFacts(16000, 8000, 4, 0.52, 50000), f);
        Assert.True(f.Any);
        Assert.Equal(new HostMetrics(8000, 4, 0.52, 50000), f.ToMetrics());
    }

    [Fact]
    public void AReportOfAnOlderScript_HasNoFacts_AndFailsClosedForABurstSlot()
    {
        var f = HostReportFields.Parse(Body("""{"host":"h","containers":[{"name":"fw-a","item":1,"state":"running","startedAt":"2026-10-22T09:00:00Z"}]}"""));
        Assert.Equal(HostFacts.None, f);
        Assert.False(f.Any);
        var m = f.ToMetrics();
        // the scheduler's own rule refuses a burst on these (and never tests a guaranteed slot against them)
        Assert.Equal(BurstVerdict.Memory, BurstAdmission.Check(m, 1000, 2));
        Assert.Equal(BurstVerdict.Cpu, BurstAdmission.Check(new HostMetrics(100000, 8, m.Load1, 100000), 1000, 2));
        // and the ledger is JSON: the unknown load must survive a round trip (JSON has no NaN)
        Assert.Equal(m, JsonSerializer.Deserialize<HostMetrics>(JsonSerializer.Serialize(m)));
    }

    [Fact]
    public void APartialReport_KeepsWhatWasReadable()
    {
        var f = HostReportFields.Parse(Body("""{"host":"h","containers":[],"memTotalMb":16000,"memAvailableMb":8000,"diskFreeMb":1234}"""));
        Assert.Equal(new HostFacts(16000, 8000, null, null, 1234), f);
    }

    [Theory]
    [InlineData("""{"memAvailableMb":-5,"load1":-1,"cpus":-2}""")]
    [InlineData("""{"memAvailableMb":"lots","load1":"high","cpus":true}""")]
    [InlineData("""{"memAvailableMb":1e300,"load1":1e300,"cpus":99999999999}""")]
    [InlineData("""{"memAvailableMb":null,"load1":[],"cpus":{}}""")]
    public void ValuesThatAreNotPlainNonNegativeNumbers_AreNoValues_AndNothingThrows(string json)
    {
        var f = HostReportFields.Parse(Body(json));
        Assert.Null(f.MemAvailableMb); Assert.Null(f.Load1); Assert.Null(f.Cpus);
    }

    [Fact]
    public void NumbersAsText_AndFractions_AreTolerated()
    {
        var f = HostReportFields.Parse(Body("""{"memAvailableMb":"8000","load1":"0.5","cpus":4.9}"""));
        Assert.Equal(8000, f.MemAvailableMb); Assert.Equal(0.5, f.Load1); Assert.Equal(4, f.Cpus);
    }

    [Fact]
    public void AContainersMemPeak_IsReadWhenItIsThere()
    {
        Assert.Equal(1536, HostReportFields.MemPeakMb(JsonNode.Parse("""{"name":"fw-a","memPeakMb":1536}""")));
        Assert.Null(HostReportFields.MemPeakMb(JsonNode.Parse("""{"name":"fw-a"}""")));
        Assert.Null(HostReportFields.MemPeakMb(JsonNode.Parse("""{"name":"fw-a","memPeakMb":"x"}""")));
        Assert.Null(HostReportFields.MemPeakMb(null));
    }

    [Fact]
    public void TheSchedulersInput_CarriesTheHostTheCountAndTheMetrics_WithAKeyPerReport()
    {
        var at = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var e = new HostReportEvent("Machines-641", "omarchy", 2, new HostFacts(16000, 8000, 4, 0.5, 50000), ["dotnet"], at);
        var m = CapacityWiring.ToMessage(e);
        Assert.Equal("omarchy", m.Host); Assert.Equal(RuntimeKinds.Octopus, m.Runtime); Assert.Equal(2, m.Running); Assert.Equal(at, m.At);
        Assert.Equal(new HostMetrics(8000, 4, 0.5, 50000), m.Metrics);
        Assert.NotEqual(m.Key, CapacityWiring.ToMessage(e with { At = at.AddMinutes(5) }).Key);
        Assert.Equal(m.Key, CapacityWiring.ToMessage(e).Key);
    }

    [Fact]
    public void CapabilityLabels_AreTheTagsWithThePrefix_WithoutIt()
    {
        var roles = (JsonArray)JsonNode.Parse("""["fworker-host","fworker-cap-dotnet","FWORKER-CAP-Browser","fworker-cap-","fworker-cap-dotnet","other"]""")!;
        Assert.Equal(["browser", "dotnet"], OctopusRunbookRuntime.CapabilitiesOf(roles, "fworker-cap-"));
        Assert.Empty(OctopusRunbookRuntime.CapabilitiesOf(roles, ""));
        Assert.Empty(OctopusRunbookRuntime.CapabilitiesOf((JsonArray)JsonNode.Parse("""["fworker-host"]""")!, "fworker-cap-"));
    }

    [Fact]
    public void TheCapabilityPrefix_IsASetting_WithADefault()
    {
        Assert.Equal("fworker-cap-", OctopusRunbookSettings.From(null, _ => null).CapabilityTagPrefix);
        Assert.Equal("cap:", OctopusRunbookSettings.From(JsonNode.Parse("""{"capabilityTagPrefix":"cap:"}"""), _ => null).CapabilityTagPrefix);
    }
}
