using Fworker.Contracts.Ids;
using Xunit;

namespace Fworker.Domain.Tests;

/// <summary>
/// D1: specification tests of the admission rules (src/Fworker.Domain/AdmissionRules.cs). v1 has no such functions (the runtime limits are inline in
/// RuntimeManager.CanStart, the rest is new design), so each test cites the section of docs/design/delivery-speed.md (DS) or system-gateway.md (SG) it pins.
/// </summary>
public sealed class AdmissionRulesTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    static readonly ItemId Holder = ItemId.Parse("acme/app#12");

    // ---- DS 3.2 rule 3: runtime at maxConcurrent or its day budget (RuntimeManager.CanStart) ----

    [Theory]
    [InlineData(0, 2, 0, 10, true)]
    [InlineData(1, 2, 0, 10, true)]    // n-1 running: a slot is free
    [InlineData(2, 2, 0, 10, false)]   // n running: runtime-at-capacity
    [InlineData(3, 2, 0, 10, false)]   // n+1 (an adopted overcommit) still blocks
    public void RuntimeCapacity_MaxConcurrentBoundary(int running, int max, int today, int budget, bool admitted)
    {
        var d = AdmissionRules.RuntimeCapacity(new RuntimeLoad(running, max, today, budget));
        Assert.Equal(admitted, d.Admitted);
        if (!admitted) Assert.Equal("runtime-at-capacity", d.Code);
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(11, false)]
    public void RuntimeCapacity_DayBudgetBoundary(int today, bool admitted)
    {
        var d = AdmissionRules.RuntimeCapacity(new RuntimeLoad(0, 2, today, 10));
        Assert.Equal(admitted, d.Admitted);
        if (!admitted) Assert.Equal("runtime-budget", d.Code);
    }

    [Fact]
    public void RuntimeCapacity_CapacityIsTestedBeforeTheBudget()
        => Assert.Equal("runtime-at-capacity", AdmissionRules.RuntimeCapacity(new RuntimeLoad(2, 2, 10, 10)).Code);

    [Fact]
    public void RuntimeCapacity_ALimitOfZeroAdmitsNothing()
    {
        Assert.Equal("runtime-at-capacity", AdmissionRules.RuntimeCapacity(new RuntimeLoad(0, 0, 0, 10)).Code);
        Assert.Equal("runtime-budget", AdmissionRules.RuntimeCapacity(new RuntimeLoad(0, 2, 0, 0)).Code);
    }

    // ---- SG H4 and H8 (R7): toolchain and capability admission ----

    [Fact]
    public void Toolchain_NoRequirementAdmits()
    {
        Assert.True(AdmissionRules.Toolchain(null, null).Admitted);
        Assert.True(AdmissionRules.Toolchain(Array.Empty<string>(), new[] { "dotnet" }).Admitted);
    }

    [Fact]
    public void Toolchain_AllRequiredOffered_Admits_WithoutCase()
        => Assert.True(AdmissionRules.Toolchain(new[] { "Node", "pwsh" }, new[] { "node", "PWSH", "dotnet" }).Admitted);

    [Fact]
    public void Toolchain_OneMissing_BlocksAndNamesIt()
    {
        var d = AdmissionRules.Toolchain(new[] { "pwsh", "terraform" }, new[] { "pwsh" });
        Assert.False(d.Admitted);
        Assert.Equal("toolchain", d.Code);
        Assert.Equal("terraform", d.Detail);
        Assert.Equal("toolchain terraform", d.Waiting);
    }

    [Fact]
    public void Toolchain_SeveralMissing_AreListedSortedOnce()
    {
        var d = AdmissionRules.Toolchain(new[] { "az", "Terraform", "az", "node" }, null);
        Assert.Equal("az,node,terraform", d.Detail);
    }

    [Fact]
    public void Toolchain_BlankRequirementsAreIgnored()
        => Assert.True(AdmissionRules.Toolchain(new[] { "", "  " }, Array.Empty<string>()).Admitted);

    // ---- DS 3.1 rank 6 and DS 6: generated items last, and only into a free slot ----

    [Theory]
    [InlineData(true, 0, true)]     // generated, nobody real waits: it may take the free slot
    [InlineData(true, 1, false)]    // generated, one real item waits for a slot: it waits
    [InlineData(true, 5, false)]
    [InlineData(false, 0, true)]
    [InlineData(false, 5, true)]    // a real item is never held by this rule
    public void GeneratedLast(bool generated, int realWaiting, bool admitted)
    {
        var d = AdmissionRules.GeneratedLast(generated, realWaiting);
        Assert.Equal(admitted, d.Admitted);
        if (!admitted) Assert.Equal("generated-last", d.Code);
    }

    // ---- DS 3.2 rule 2 and DS 7: file-overlap hold, released after maxHoldMinutes (default 120) ----

    [Fact]
    public void FileOverlap_NoHold_Admits() => Assert.True(AdmissionRules.FileOverlap(null, T0, 120).Admitted);

    [Fact]
    public void FileOverlap_ActiveHold_BlocksNamingTheHolder()
    {
        var d = AdmissionRules.FileOverlap(new FileOverlapHold(Holder, T0), T0.AddMinutes(10), 120);
        Assert.False(d.Admitted);
        Assert.Equal("file-overlap", d.Code);
        Assert.Equal("file-overlap acme/app#12", d.Waiting);
    }

    [Theory]
    [InlineData(119, false)]   // n-1 minutes: still held
    [InlineData(120, true)]    // n: the hold has lasted maxHoldMinutes and ended
    [InlineData(121, true)]    // n+1
    public void FileOverlap_HoldExpiresAtMaxHoldMinutes(int minutes, bool admitted)
        => Assert.Equal(admitted, AdmissionRules.FileOverlap(new FileOverlapHold(Holder, T0), T0.AddMinutes(minutes), 120).Admitted);

    [Fact]
    public void FileOverlap_ExpiryUsesTheGivenMaximum()
    {
        var hold = new FileOverlapHold(Holder, T0);
        Assert.False(AdmissionRules.FileOverlap(hold, T0.AddMinutes(29), 30).Admitted);
        Assert.True(AdmissionRules.FileOverlap(hold, T0.AddMinutes(30), 30).Admitted);
    }

    [Fact]
    public void FileOverlap_SecondsCount_NotOnlyWholeMinutes()
        => Assert.False(AdmissionRules.FileOverlap(new FileOverlapHold(Holder, T0), T0.AddMinutes(120).AddSeconds(-1), 120).Admitted);

    [Fact]
    public void FileOverlap_AHoldFromTheFuture_StillHolds()
        => Assert.False(AdmissionRules.FileOverlap(new FileOverlapHold(Holder, T0.AddMinutes(5)), T0, 120).Admitted);

    [Fact]
    public void FileOverlap_AMaximumOfZeroOrLess_EndsEveryHoldAtOnce()
    {
        var hold = new FileOverlapHold(Holder, T0);
        Assert.True(AdmissionRules.FileOverlap(hold, T0, 0).Admitted);
        Assert.True(AdmissionRules.FileOverlap(hold, T0, -5).Admitted);
        Assert.True(AdmissionRules.FileOverlap(new FileOverlapHold(Holder, T0.AddMinutes(5)), T0, 0).Admitted);   // even one from the future
    }

    [Fact]
    public void Settings_HoldDefaultsAre120Minutes_And30Seconds()
    {
        var s = new AdmissionSettings();
        Assert.Equal(120, s.MaxHoldMinutes);
        Assert.Equal(30, s.HostFullSeconds);
    }

    // ---- DS 4.2: host-full is a non-failure block of that host for hostFullSeconds ----

    [Fact]
    public void HostFull_NeverReported_Admits() => Assert.True(AdmissionRules.HostFull(null, T0, 30).Admitted);

    [Theory]
    [InlineData(0, false)]
    [InlineData(29, false)]   // n-1 seconds: blocked
    [InlineData(30, true)]    // n: free again
    [InlineData(31, true)]
    public void HostFull_BlocksForHostFullSeconds(int seconds, bool admitted)
    {
        var d = AdmissionRules.HostFull(T0, T0.AddSeconds(seconds), 30);
        Assert.Equal(admitted, d.Admitted);
        if (!admitted) Assert.Equal("host-full", d.Code);
    }

    [Fact]
    public void HostFull_IsNotAFailure_AnythingElseIs()
    {
        Assert.False(AdmissionRules.CountsAsFailure("host-full"));
        Assert.True(AdmissionRules.CountsAsFailure("failed"));
        Assert.True(AdmissionRules.CountsAsFailure(null));
    }
}
