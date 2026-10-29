using Xunit;

namespace Fworker.Domain.Tests;

/// <summary>
/// D6: specification of the file-overlap hold (delivery-speed.md section 7): the hold at Technical Design's end, the release (holder left, expiry), the
/// ranked release of items held on the same files, and the absence of deadlock. There is no v1 code; these tests are the definition.
/// </summary>
public sealed class FileOverlapRuleTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
    static readonly FileOverlapSettings On = new(true, 120, Array.Empty<string>());

    static Footprint Fp(params string[] entries) => new(entries, FootprintSource.Design);
    static OverlapPeer Peer(int n, params string[] entries) => new(n, Fp(entries));
    static OverlapWaiter Waiter(int n, Footprint fp, PlanRank? rank = null, DateTimeOffset? age = null, OverlapHold? hold = null)
        => new(n, fp, rank ?? new PlanRank(false, null, false), age ?? T0, hold);

    // ---- the hold -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnIntersectingItemInFlight_HoldsTheItem_WithTheWaitingCode()
    {
        var d = FileOverlapRule.Check(7, Fp("ui/site.css"), new[] { Peer(5, "ui/*.css") }, On, T0);
        Assert.True(d.Held);
        Assert.Equal(OverlapReason.Overlap, d.Reason);
        Assert.Equal("file-overlap #5", d.WaitingCode);
        Assert.Equal(new OverlapHold(5, T0, T0.AddMinutes(120)), d.Hold);
    }

    [Fact]
    public void NoSharedFile_AdmitsTheItem()
    {
        var d = FileOverlapRule.Check(7, Fp("src/a.cs"), new[] { Peer(5, "src/b.cs", "ui/*.css") }, On, T0);
        Assert.False(d.Held);
        Assert.Equal(OverlapReason.NoOverlap, d.Reason);
        Assert.Null(d.WaitingCode);
        Assert.Null(d.Hold);
    }

    [Fact]
    public void NothingInFlight_AdmitsTheItem()
        => Assert.Equal(OverlapReason.NoOverlap, FileOverlapRule.Check(7, Fp("a.cs"), Array.Empty<OverlapPeer>(), On, T0).Reason);

    [Fact]
    public void TheRuleIsOffByDefault()
    {
        Assert.False(FileOverlapSettings.Default.Enabled);
        Assert.Equal(120, FileOverlapSettings.Default.MaxHoldMinutes);
        Assert.Empty(FileOverlapSettings.Default.Ignore);
        var d = FileOverlapRule.Check(7, Fp("a.cs"), new[] { Peer(5, "a.cs") }, FileOverlapSettings.Default, T0);
        Assert.Equal(OverlapReason.Disabled, d.Reason);
        Assert.False(d.Held);
    }

    [Fact]
    public void AnItemWithoutAFootprint_IsNeverHeld()
    {
        var d = FileOverlapRule.Check(7, Footprint.Empty, new[] { Peer(5, "**") }, On, T0);
        Assert.Equal(OverlapReason.NoFootprint, d.Reason);
    }

    [Fact]
    public void AnInFlightItemWithoutAFootprint_HoldsNoOne()
    {
        var d = FileOverlapRule.Check(7, Fp("a.cs"), new[] { new OverlapPeer(5, Footprint.Empty) }, On, T0);
        Assert.Equal(OverlapReason.NoOverlap, d.Reason);
    }

    [Fact]
    public void TheItemIsNotHeldByItself()
    {
        var d = FileOverlapRule.Check(7, Fp("a.cs"), new[] { Peer(7, "a.cs") }, On, T0);
        Assert.False(d.Held);
    }

    [Fact]
    public void OfSeveralHolders_TheLowestNumberHolds()
    {
        var d = FileOverlapRule.Check(9, Fp("a.cs"), new[] { Peer(8, "a.cs"), Peer(3, "a.cs"), Peer(5, "a.cs") }, On, T0);
        Assert.Equal(3, d.Hold!.By);
    }

    [Fact]
    public void AGlobAgainstAGlob_Holds()
    {
        var d = FileOverlapRule.Check(7, Fp("tests/scenarios/*.ps1"), new[] { Peer(5, "tests/**") }, On, T0);
        Assert.True(d.Held);
    }

    // ---- ignore -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnIgnoredSharedFile_DoesNotHold()
    {
        var settings = On with { Ignore = new[] { "package-lock.json" } };
        var d = FileOverlapRule.Check(7, Fp("src/a.cs", "ui/package-lock.json"), new[] { Peer(5, "src/b.cs", "ui/package-lock.json") }, settings, T0);
        Assert.False(d.Held);
        Assert.Equal(OverlapReason.NoOverlap, d.Reason);
    }

    [Fact]
    public void AnIgnoredFile_StillLetsTheOtherSharedFilesHold()
    {
        var settings = On with { Ignore = new[] { "docs/**" } };
        var d = FileOverlapRule.Check(7, Fp("docs/a.md", "src/a.cs"), new[] { Peer(5, "docs/b.md", "src/a.cs") }, settings, T0);
        Assert.Equal(5, d.Hold!.By);
    }

    [Fact]
    public void AnItemWhoseEveryFileIsIgnored_HasNoFootprint()
    {
        var settings = On with { Ignore = new[] { "*.md" } };
        var d = FileOverlapRule.Check(7, Fp("docs/a.md"), new[] { Peer(5, "docs/a.md") }, settings, T0);
        Assert.Equal(OverlapReason.NoFootprint, d.Reason);
    }

    [Fact]
    public void TheIgnorePatternsApplyToTheHoldersFootprintToo()
    {
        var settings = On with { Ignore = new[] { "ui/generated/" } };
        var d = FileOverlapRule.Check(7, Fp("ui/generated/x.js"), new[] { Peer(5, "ui/generated/x.js", "ui/site.css") }, settings, T0);
        Assert.Equal(OverlapReason.NoFootprint, d.Reason);
        var d2 = FileOverlapRule.Check(7, Fp("ui/site.css", "ui/generated/x.js"), new[] { Peer(5, "ui/generated/x.js") }, settings, T0);
        Assert.Equal(OverlapReason.NoOverlap, d2.Reason);
    }

    // ---- the pull request's files replace the design -------------------------------------------------------------------------------------------

    [Fact]
    public void AfterThePush_TheHolderIsJudgedByItsChangedFiles()
    {
        var design = Fp("ui/site.css", "ui/app.js");
        var beforePush = Footprint.Resolve(design, null);
        var afterPush = Footprint.Resolve(design, new[] { "ui/app.js" });          // site.css was not touched after all

        Assert.True(FileOverlapRule.Check(7, Fp("ui/site.css"), new[] { new OverlapPeer(5, beforePush) }, On, T0).Held);
        Assert.False(FileOverlapRule.Check(7, Fp("ui/site.css"), new[] { new OverlapPeer(5, afterPush) }, On, T0).Held);
        Assert.True(FileOverlapRule.Check(7, Fp("ui/app.js"), new[] { new OverlapPeer(5, afterPush) }, On, T0).Held);
    }

    [Fact]
    public void AfterThePush_AFileThePullRequestAddedHolds()
    {
        var holder = new OverlapPeer(5, Footprint.Resolve(Fp("src/a.cs"), new[] { "src/a.cs", "src/extra.cs" }));
        Assert.True(FileOverlapRule.Check(7, Fp("src/extra.cs"), new[] { holder }, On, T0).Held);
    }

    // ---- release ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhenTheHolderLeavesTheRange_TheHoldEnds()
    {
        var hold = new OverlapHold(5, T0, T0.AddMinutes(120));
        var stillThere = FileOverlapRule.Check(7, Fp("a.cs"), new[] { Peer(5, "a.cs") }, On, T0.AddMinutes(30), hold);
        Assert.True(stillThere.Held);
        Assert.Equal(hold, stillThere.Hold);                                         // same by, since, until

        // merged, parked, closed or moved back: the caller no longer passes it as in flight
        var gone = FileOverlapRule.Check(7, Fp("a.cs"), Array.Empty<OverlapPeer>(), On, T0.AddMinutes(31), hold);
        Assert.False(gone.Held);
        Assert.Equal(OverlapReason.NoOverlap, gone.Reason);
    }

    [Fact]
    public void AfterMaxHoldMinutes_TheItemGoesOn_EvenWhileTheHolderIsStillThere()
    {
        var hold = new OverlapHold(5, T0, T0.AddMinutes(120));
        var holders = new[] { Peer(5, "a.cs") };

        Assert.True(FileOverlapRule.Check(7, Fp("a.cs"), holders, On, T0.AddMinutes(119), hold).Held);
        var atLimit = FileOverlapRule.Check(7, Fp("a.cs"), holders, On, T0.AddMinutes(120), hold);
        Assert.False(atLimit.Held);
        Assert.Equal(OverlapReason.Expired, atLimit.Reason);
        Assert.Equal(OverlapReason.Expired, FileOverlapRule.Check(7, Fp("a.cs"), holders, On, T0.AddHours(5), hold).Reason);   // and it stays released
    }

    [Fact]
    public void TheLimitIsTheSettings_NotAFixedTwoHours()
    {
        var settings = On with { MaxHoldMinutes = 15 };
        var first = FileOverlapRule.Check(7, Fp("a.cs"), new[] { Peer(5, "a.cs") }, settings, T0);
        Assert.Equal(T0.AddMinutes(15), first.Hold!.Until);
        Assert.Equal(OverlapReason.Expired, FileOverlapRule.Check(7, Fp("a.cs"), new[] { Peer(5, "a.cs") }, settings, T0.AddMinutes(15), first.Hold).Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ANonPositiveLimit_NeverHolds(int minutes)
    {
        var d = FileOverlapRule.Check(7, Fp("a.cs"), new[] { Peer(5, "a.cs") }, On with { MaxHoldMinutes = minutes }, T0);
        Assert.False(d.Held);
    }

    [Fact]
    public void WhenTheHolderChanges_TheWaitKeepsItsOriginalLimit()
    {
        var hold = new OverlapHold(5, T0, T0.AddMinutes(120));
        // #5 merged, #6 now works on the same file
        var d = FileOverlapRule.Check(7, Fp("a.cs"), new[] { Peer(6, "a.cs") }, On, T0.AddMinutes(60), hold);
        Assert.True(d.Held);
        Assert.Equal(new OverlapHold(6, T0, T0.AddMinutes(120)), d.Hold);
        Assert.Equal("file-overlap #6", d.WaitingCode);
    }

    [Fact]
    public void TheCurrentHolder_IsKeptWhileItStillShares_EvenWhenALowerNumberAppears()
    {
        var hold = new OverlapHold(8, T0, T0.AddMinutes(120));
        var d = FileOverlapRule.Check(9, Fp("a.cs"), new[] { Peer(3, "a.cs"), Peer(8, "a.cs") }, On, T0.AddMinutes(10), hold);
        Assert.Equal(8, d.Hold!.By);
    }

    // ---- ranked release -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ReleaseOrder_FollowsTheRank_ThenAge_ThenNumber()
    {
        var fp = Fp("a.cs");
        var waiters = new[]
        {
            Waiter(10, fp, new PlanRank(false, null, false), T0.AddMinutes(5)),
            Waiter(11, fp, new PlanRank(false, "high", false), T0.AddMinutes(9)),
            Waiter(12, fp, new PlanRank(false, "top", true), T0.AddMinutes(9)),   // top beats everything but a pin
            Waiter(13, fp, new PlanRank(true, null, true), T0.AddMinutes(9)),     // pinned first
            Waiter(14, fp, new PlanRank(false, null, false), T0.AddMinutes(1)),   // older than 10
            Waiter(9, fp, new PlanRank(false, null, false), T0.AddMinutes(5)),    // same age as 10, lower number
        };
        Assert.Equal(new[] { 13, 12, 11, 14, 9, 10 }, FileOverlapRule.ReleaseOrder(waiters).Select(w => w.Number).ToArray());
    }

    [Fact]
    public void ItemsHeldOnTheSameFiles_AreReleasedOneByOne_ByRank()
    {
        var fp = Fp("ui/site.css");
        var waiters = new[]
        {
            Waiter(20, fp, new PlanRank(false, null, false), T0),
            Waiter(21, fp, new PlanRank(false, "top", false), T0.AddMinutes(10)),
            Waiter(22, fp, new PlanRank(false, "high", false), T0.AddMinutes(5)),
        };
        // the holder #5 is gone: the first by rank goes, the others wait for it
        var pass = FileOverlapRule.Release(waiters, Array.Empty<OverlapPeer>(), On, T0.AddMinutes(30));
        Assert.Equal(new[] { 21, 22, 20 }, pass.Select(r => r.Number).ToArray());
        Assert.False(pass[0].Decision.Held);
        Assert.True(pass[1].Decision.Held);
        Assert.Equal(21, pass[1].Decision.Hold!.By);
        Assert.True(pass[2].Decision.Held);
        Assert.Equal(21, pass[2].Decision.Hold!.By);
    }

    [Fact]
    public void ItemsOnDifferentFiles_AreAllReleasedTogether()
    {
        var waiters = new[] { Waiter(20, Fp("a.cs")), Waiter(21, Fp("b.cs")), Waiter(22, Fp("ui/*.css")) };
        var pass = FileOverlapRule.Release(waiters, Array.Empty<OverlapPeer>(), On, T0);
        Assert.All(pass, r => Assert.False(r.Decision.Held));
    }

    [Fact]
    public void ARankedReleaseAlsoRespectsWhatIsAlreadyInFlight()
    {
        var waiters = new[] { Waiter(20, Fp("a.cs"), new PlanRank(false, "top", false)), Waiter(21, Fp("b.cs")) };
        var pass = FileOverlapRule.Release(waiters, new[] { Peer(5, "a.cs") }, On, T0);
        Assert.True(pass.Single(r => r.Number == 20).Decision.Held);
        Assert.Equal(5, pass.Single(r => r.Number == 20).Decision.Hold!.By);
        Assert.False(pass.Single(r => r.Number == 21).Decision.Held);
    }

    [Fact]
    public void AnExpiredWaiter_IsReleasedAndThenHoldsTheLaterOnes()
    {
        var expired = new OverlapHold(5, T0, T0.AddMinutes(120));
        var waiters = new[]
        {
            Waiter(20, Fp("a.cs"), hold: expired),
            Waiter(21, Fp("a.cs"), new PlanRank(false, null, false), T0.AddMinutes(1)),
        };
        var pass = FileOverlapRule.Release(waiters, new[] { Peer(5, "a.cs") }, On, T0.AddMinutes(121));
        Assert.Equal(OverlapReason.Expired, pass[0].Decision.Reason);
        Assert.True(pass[1].Decision.Held);                     // #21 has no hold yet: it waits for #5 (lowest number) and now also shares with #20

        // #5 merges: what #21 waits for is #20, which went on
        var after = FileOverlapRule.Release(new[] { Waiter(21, Fp("a.cs")) }, new[] { Peer(20, "a.cs") }, On, T0.AddMinutes(122));
        Assert.Equal(20, after[0].Decision.Hold!.By);
    }

    [Fact]
    public void ARankedPassKeepsEachWaitersOwnHoldLimit()
    {
        var held = new OverlapHold(5, T0, T0.AddMinutes(120));
        var pass = FileOverlapRule.Release(new[] { Waiter(20, Fp("a.cs"), hold: held) }, new[] { Peer(5, "a.cs") }, On, T0.AddMinutes(10));
        Assert.Equal(held, pass[0].Decision.Hold);
    }

    // ---- no deadlock --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AHoldingItemIsNeverHeld_TwoItemsOnTheSameFilesCannotWaitForEachOther()
    {
        // #20 and #21 share a file and both reach the end of Technical Design in the same pass: only one of them goes on
        var pass = FileOverlapRule.Release(new[] { Waiter(20, Fp("a.cs")), Waiter(21, Fp("a.cs")) }, Array.Empty<OverlapPeer>(), On, T0);
        Assert.Equal(1, pass.Count(r => !r.Decision.Held));
        Assert.False(pass[0].Decision.Held);                    // the one released holds the other
        Assert.Equal(pass[0].Number, pass[1].Decision.Hold!.By);
    }

    [Fact]
    public void ACrowdOnOneFile_DrainsInRankOrder_AndNeverGetsStuck()
    {
        var all = Enumerable.Range(30, 8).ToList();
        var waiting = all.Select(n => Waiter(n, Fp("ui/site.css"), age: T0.AddMinutes(n))).ToList();
        var inFlight = new List<OverlapPeer>();
        var released = new List<int>();
        var now = T0;

        for (var round = 0; round < all.Count && waiting.Count > 0; round++)
        {
            var pass = FileOverlapRule.Release(waiting, inFlight, On, now);
            var go = pass.Where(r => !r.Decision.Held).Select(r => r.Number).ToList();
            Assert.Single(go);                                   // progress: exactly one item is released per round
            released.AddRange(go);
            waiting.RemoveAll(w => go.Contains(w.Number));
            // the released item merges before the next round
            inFlight.Clear();
            now = now.AddMinutes(20);
        }

        Assert.Empty(waiting);
        Assert.Equal(all, released);                             // oldest first: the order of the ages
    }

    [Fact]
    public void ARunOfHoldsAlwaysEnds_ByTheLimitAtTheLatest()
    {
        // a holder that never leaves cannot hold anyone beyond maxHoldMinutes
        var holders = new[] { Peer(5, "a.cs") };
        var d = FileOverlapRule.Check(7, Fp("a.cs"), holders, On, T0);
        var hold = d.Hold;
        var at = T0;
        for (var i = 0; i < 100 && d.Held; i++)
        {
            at = at.AddMinutes(5);
            d = FileOverlapRule.Check(7, Fp("a.cs"), holders, On, at, hold);
            hold = d.Hold ?? hold;
        }
        Assert.False(d.Held);
        Assert.Equal(T0.AddMinutes(120), at);
    }
}
