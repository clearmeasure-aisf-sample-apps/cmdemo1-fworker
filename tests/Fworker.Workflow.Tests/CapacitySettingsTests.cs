using System.Text.Json.Nodes;
using Fworker.Domain;
using Fworker.Workflows.Capacity;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>W7.0: the scheduler's settings, read from config/instance.json (the source defaults) and from the configuration of the tdd image (the deploy overrides merged over it). No flag: the defaults are the design's.</summary>
public class CapacitySettingsTests
{
    static string Repo()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "config", "instance.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("config/instance.json not found");
    }

    static JsonObject Source() => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Repo(), "config", "instance.json")))!;

    // config/apply-overrides.ps1: objects merge key by key, a value or an array replaces; the keys of runtimes.environments and api.*.environments are added as written
    static void Merge(JsonObject target, JsonObject source, string where = "")
    {
        foreach (var (key, value) in source.ToList())
        {
            if (where is "runtimes.environments." or "api.cursor.environments." or "api.copilot.environments.") { target[key] = value?.DeepClone(); continue; }
            Assert.True(target.ContainsKey(key), $"the override '{where}{key}' names no setting of the base configuration");
            if (value is JsonObject so) Merge((JsonObject)target[key]!, so, where + key + ".");
            else target[key] = value?.DeepClone();
        }
    }

    static JsonObject Effective()
    {
        var cfg = Source();
        Merge(cfg, (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Repo(), "config", "deploy.overrides.json")))!);
        return cfg;
    }

    [Fact]
    public void TheSourceDefaults_HaveNoRuntimeOn_TheFileOverlapRuleOff_AndTheDesignsNumbers()
    {
        var s = CapacitySettings.From(Source(), "tdd");
        Assert.Empty(s.Runtimes);
        Assert.Equal((false, 120), (s.Overlap.Enabled, s.Overlap.MaxHoldMinutes));
        Assert.Empty(s.Overlap.Ignore);
        Assert.Equal(new HeadroomSettings(2048, 4096, 30, 10), s.Headroom);
        Assert.Equal((2.0, TimeSpan.FromMinutes(240), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10)), (s.ContainerCpus, s.SlotMax, s.HostReportMax, s.SafetyWake));
        Assert.Equal(new AdmissionSettings(120, 30), s.Admission);
    }

    [Fact]
    public void TheSourceDefaults_SaySoInTheFile_ThereIsNoSchedulerFlag()
    {
        var cfg = Source();
        var scheduler = (JsonObject)cfg["scheduler"]!;
        // (W7.3 adds two settings of the feature plan's use of it: featureSlots, a switch of that plan's slots, and the runtime its requests name; neither turns the scheduler on or off)
        Assert.Equal(["slotMaxMinutes", "hostReportMaxMinutes", "safetyWakeMinutes", "toolchains", "featureSlots", "runtime"], scheduler.Select(kv => kv.Key));
        Assert.DoesNotContain(cfg["engine"]!.AsObject().Select(kv => kv.Key), k => k is "scheduler" or "merge");
        Assert.Equal(0, (int)cfg["runtimes"]!["octopus"]!["perHostBurst"]!);   // 0: no burst, the limit is perHostCapacity
        Assert.False((bool)cfg["schedule"]!["fileOverlap"]!["enabled"]!);
    }

    [Fact]
    public void TheTddImage_HasTheTwoRuntimesOfTheEnvironmentWithTheirLimits()
    {
        var s = CapacitySettings.From(Effective(), "tdd");
        // W9.9c, #437 option A: the Copilot and Cursor runtimes are off on tdd for the measured batch (their day counts live in memory); switch them back on in the overrides afterwards
        Assert.Equal(["cae-job", "octopus"], s.Runtimes.Keys.Order());
        var octopus = s.Runtimes["octopus"];
        Assert.Equal((2, 30, 2, 2, true), (octopus.MaxConcurrent, octopus.MaxExecutionsPerDay, octopus.PerHostCapacity, octopus.PerHostBurst, octopus.HasHosts));
        Assert.Equal((2, 30, false), (s.Runtimes["cae-job"].MaxConcurrent, s.Runtimes["cae-job"].MaxExecutionsPerDay, s.Runtimes["cae-job"].HasHosts));
        Assert.All(s.Runtimes.Values, r => Assert.Null(r.Toolchains));   // no toolchain list: unrestricted
    }

    [Fact]
    public void AnEnvironmentWithoutAnEntry_HasNoRuntimesOfTheRuntimesBlock_AndTheApiRuntimesFollowTheirOwnEnvironments()
    {
        var cfg = Effective();
        var qa = CapacitySettings.From(cfg, "qa");   // runtimes.environments has no "qa": every runtime of the block is off; api.*.environments names no "qa" either
        Assert.Empty(qa.Runtimes);
        var uat = CapacitySettings.From(cfg, "uat");
        Assert.DoesNotContain("octopus", uat.Runtimes.Keys);
        Assert.DoesNotContain("copilot-cloud", uat.Runtimes.Keys);   // api.copilot.environments.uat.enabled is false
        // a file with no environments applies as it is
        var plain = (JsonObject)JsonNode.Parse("""{"runtimes":{"octopus":{"enabled":true,"maxConcurrent":4,"perHostCapacity":3,"perHostBurst":5}}}""")!;
        var s = CapacitySettings.From(plain, "anything");
        Assert.Equal((4, 3, 5), (s.Runtimes["octopus"].MaxConcurrent, s.Runtimes["octopus"].PerHostCapacity, s.Runtimes["octopus"].PerHostBurst));
    }

    [Fact]
    public void ARuntimeNeedsToBeEnabled_AWithALimit_AndTheCaeJobAJob()
    {
        var cfg = (JsonObject)JsonNode.Parse("""
            {"runtimes":{
               "octopus":{"enabled":false,"maxConcurrent":2},
               "caeJob":{"enabled":true,"maxConcurrent":2}},
             "api":{"cursor":{"enabled":true,"maxConcurrent":0},"copilot":{"enabled":true,"maxConcurrent":2,"maxExecutionsPerDay":9,"environments":{"tdd":{"gatewayUrl":"x"},"uat":{"enabled":false}}}}}
            """)!;
        var s = CapacitySettings.From(cfg, "tdd");
        Assert.Equal(["copilot-cloud"], s.Runtimes.Keys);   // octopus is disabled, the cae job has no job, cursor has no slot
        Assert.Equal((2, 9), (s.Runtimes["copilot-cloud"].MaxConcurrent, s.Runtimes["copilot-cloud"].MaxExecutionsPerDay));
        Assert.Empty(CapacitySettings.From(cfg, "uat").Runtimes);   // the entry of uat is written ready but off
        Assert.Empty(CapacitySettings.From(cfg, "prod").Runtimes);  // and prod is not named
    }

    [Fact]
    public void ABurstBelowTheCapacity_IsNoBurst_AndTheHeadroomKeysAreOctopus()
    {
        var cfg = (JsonObject)JsonNode.Parse("""
            {"runtimes":{"octopus":{"enabled":true,"maxConcurrent":4,"perHostCapacity":3,"perHostBurst":1,"reserveMb":1000,"minDiskMb":2000,"hostFullSeconds":5,"containerCpus":1.5,"minFootprintSamples":4}},
             "schedule":{"fileOverlap":{"enabled":true,"maxHoldMinutes":45},"overlapIgnore":["*.md"]},
             "scheduler":{"slotMaxMinutes":30,"hostReportMaxMinutes":3,"safetyWakeMinutes":2,"toolchains":{"octopus":["dotnet","node"]}}}
            """)!;
        var s = CapacitySettings.From(cfg, "x");
        Assert.Equal(3, s.Runtimes["octopus"].PerHostBurst);
        Assert.Equal(["dotnet", "node"], s.Runtimes["octopus"].Toolchains);
        Assert.Equal(new HeadroomSettings(1000, 2000, 5, 4), s.Headroom);
        Assert.Equal(1.5, s.ContainerCpus);
        Assert.True(s.Overlap.Enabled);
        Assert.Equal(45, s.Overlap.MaxHoldMinutes);
        Assert.Equal(["*.md"], s.Overlap.Ignore);
        Assert.Equal((TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(2)), (s.SlotMax, s.HostReportMax, s.SafetyWake));
        Assert.Equal(new AdmissionSettings(45, 5), s.Admission);
    }

    [Fact]
    public void AMalformedBlock_FallsBackToTheDefaultsFieldByField_AndNothingIsOn()
    {
        Assert.Empty(CapacitySettings.From(null, "tdd").Runtimes);
        Assert.Empty(CapacitySettings.From(new JsonObject(), "").Runtimes);
        var cfg = (JsonObject)JsonNode.Parse("""
            {"runtimes":"x","api":{"cursor":"x","copilot":[1]},
             "schedule":{"fileOverlap":{"enabled":"yes","maxHoldMinutes":-5},"overlapIgnore":"x"},
             "scheduler":{"slotMaxMinutes":"long","hostReportMaxMinutes":0,"toolchains":[1]}}
            """)!;
        var s = CapacitySettings.From(cfg, "tdd");
        Assert.Empty(s.Runtimes);
        Assert.Equal((false, 120), (s.Overlap.Enabled, s.Overlap.MaxHoldMinutes));
        Assert.Equal(TimeSpan.FromMinutes(240), s.SlotMax);
        Assert.Equal(TimeSpan.FromMinutes(1), s.HostReportMax);   // (at least a minute)
    }

    [Fact]
    public void TheGatewaysManagersDecideWhichRuntimesAreOn()
    {
        var s = CapacitySettings.From(Effective(), "tdd");
        var only = s.Only(["octopus", "cursor-cloud"]);
        Assert.Equal(["octopus"], only.Runtimes.Keys.Order());   // cursor-cloud is off on tdd: Only keeps what is on
        Assert.Equal(["cae-job", "octopus"], s.Only(["octopus", "cae-job"]).Runtimes.Keys.Order());
        Assert.Equal(2, s.Runtimes.Count);   // (the original is untouched)
        Assert.Empty(s.Only([]).Runtimes);
    }
}
