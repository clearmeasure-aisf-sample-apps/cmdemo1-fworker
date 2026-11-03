using Fworker.Contracts.Commands;
using Fworker.Contracts.Messaging;
using Fworker.Workflows.Delivery;
using Xunit;
using static Fworker.Workflow.Tests.TrainFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// V2.1: the train at parity with v1's pass under the limits of its reads: a release of more items than one minute's reads allow, the health read that is one try per look, and the Sign-off that is the train's own.
/// </summary>
public class DeliveryTrainParityTests
{
    const int Items = 100;
    const int CapPerMinute = 40;   // HttpTrainReads.MaxCallsPerMinute, shared by every train and the shadow

    /// <summary>A release of <see cref="Items"/> items (each its own commit, a character that has no case), the baseline 1.0.1 under it, in tdd and healthy.</summary>
    static async Task<TrainFixture> BigRelease(int capFromStart)
    {
        var f = new TrainFixture(); await f.Baseline();
        var chars = Enumerable.Range(0, Items).Select(i => (char)(0x4E00 + i)).ToArray();
        f.Range('d', chars);
        f.Reads.GateReadsPerMinute = capFromStart;
        for (var i = 0; i < Items; i++) await f.Merge(100 + i, chars[i]);
        f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", chars[^1]);
        return f;
    }

    /// <summary>The timer fires (or, when none is due, a quarter of a minute passes) until <paramref name="done"/>; the number of looks it took, or -1.</summary>
    static async Task<int> Drive(TrainFixture f, Func<bool> done, TimeSpan step, int max)
    {
        for (var i = 0; i < max; i++)
        {
            if (done()) return i;
            f.Now += step;
            await f.Wake();
        }
        return done() ? max : -1;
    }

    [Fact]
    public async Task ARelease_OfMoreItemsThanAMinutesReadsAllow_IsPromoted_BecauseTheReadsOfTheLooksBuildUp()
    {
        var f = await BigRelease(CapPerMinute);
        var looks = await Drive(f, () => f.Of<PromoteRelease>().Any(), TimeSpan.FromSeconds(15), 400);
        Assert.True(looks >= 0, "the release was never promoted: every look starved");
        Assert.Single(f.Of<PromoteRelease>());
        Assert.True(f.Reads.GateReads >= Items);
    }

    [Fact]
    public async Task ASignOff_OfAReleaseOfMoreItemsThanAMinutesReadsAllow_IsAnsweredProceed_OnGatesReadAfterTheChainBegan()
    {
        var f = await BigRelease(0);
        await f.Mature();
        Assert.Single(f.Of<PromoteRelease>());
        await f.Deploy("1.0.2", "Uat", "Started");
        f.Reads.GateReadsPerMinute = CapPerMinute; f.Reads.GateReadAt.Clear();   // the gates were last read a long time ago: every read at the answer is a new one, and a minute allows 40
        var began = f.Now;
        await f.SignOff("1.0.2", "Uat");
        Assert.Empty(f.Of<AnswerSignOff>());
        Assert.Equal("signoff-waiting", f.LastDecision!.Action);
        Assert.Contains("could not be read", f.LastDecision.Reasons[0]);
        Assert.False(f.State.SignOffs.Single().Answered);
        var looks = await Drive(f, () => f.Of<AnswerSignOff>().Any(), TimeSpan.FromSeconds(15), 100);
        Assert.True(looks >= 0, "the Sign-off was never answered: every look starved");
        Assert.True(Assert.Single(f.Of<AnswerSignOff>()).Proceed);
        Assert.True(f.Now - began < DeliveryTrainService.FreshChainMax, $"took {f.Now - began}");
        Assert.Equal(Items, f.Reads.GateReadAt.Count);
        Assert.All(f.Reads.GateReadAt.Values, t => Assert.True(t >= began));   // every answer is one read after the Sign-off asked, the first look's included
    }

    [Fact]
    public async Task ASignOff_WhoseGatesCannotBeRead_WaitsAndIsAbortedAtTheLimit_NotBlockedForEver()
    {
        var f = await BigRelease(0);
        await f.Mature();
        await f.Deploy("1.0.2", "Uat", "Started");
        f.Reads.GateReadsPerMinute = 2; f.Reads.GateReadAt.Clear();   // 100 items, 2 reads a minute: never enough inside the cache
        await f.SignOff("1.0.2", "Uat");
        Assert.Equal("signoff-waiting", f.LastDecision!.Action);
        Assert.Empty(f.Of<AnswerSignOff>());
        var looks = await Drive(f, () => f.Of<AnswerSignOff>().Any(), TimeSpan.FromMinutes(2), 80);
        Assert.True(looks >= 0, "a Sign-off that cannot read its gates must reach its limit");
        var a = Assert.Single(f.Of<AnswerSignOff>());
        Assert.False(a.Proceed);
        Assert.Contains("h at the Sign-off (limit 2 h): the gates of its items could not be read", a.Notes);
        Assert.Equal("signoff-abort", f.LastDecision!.Action);
    }

    [Fact]
    public async Task ASignOff_AdoptedAfterItWaitedLongerThanTheLimit_IsNotAbortedByARefusedRead_AndIsJudgedOnceTheReadsSucceed()
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a'); f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a');
        await f.Mature();
        Assert.Single(f.Of<PromoteRelease>());
        await f.Deploy("1.0.2", "Uat", "Started");
        f.Reads.Fail = new HttpRequestException("the shadow run's reads of this minute are spent");
        await f.SignOff("1.0.2", "Uat");
        await f.AgeSignOff(TimeSpan.FromHours(32.2));   // as adoption does: Since is the interruption's Created time
        await f.Wake();
        Assert.Empty(f.Of<AnswerSignOff>());
        Assert.Equal("signoff-waiting", f.LastDecision!.Action);
        Assert.NotNull(f.State.SignOffs.Single().UnreadSince);
        f.Now += TimeSpan.FromMinutes(5); await f.Wake();
        Assert.Empty(f.Of<AnswerSignOff>());   // five minutes of refusals are not two hours of them
        f.Reads.Fail = null;
        f.Now += TimeSpan.FromMinutes(5); await f.Wake();
        var a = Assert.Single(f.Of<AnswerSignOff>());
        Assert.True(a.Proceed, a.Notes);
        Assert.Null(f.State.SignOffs.Single().UnreadSince);
    }

    [Fact]
    public async Task ASignOff_WhoseGatesHaveBeenUnreadableForTheLimitSinceTheTrainsFirstLook_IsAborted()
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a'); f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a');
        await f.Mature();
        await f.Deploy("1.0.2", "Uat", "Started");
        f.Reads.Fail = new HttpRequestException("the shadow run's reads of this minute are spent");
        await f.SignOff("1.0.2", "Uat");
        await f.AgeSignOff(TimeSpan.FromHours(32.2));
        var looks = await Drive(f, () => f.Of<AnswerSignOff>().Any(), TimeSpan.FromMinutes(10), 20);
        Assert.True(looks >= 0, "an unreadable Sign-off must reach its limit");
        Assert.True(looks >= 12, $"aborted after {looks} looks of 10 minutes: before two hours of refusals");
        var a = Assert.Single(f.Of<AnswerSignOff>());
        Assert.False(a.Proceed);
        Assert.Contains("the gates of its items could not be read", a.Notes);
    }

    [Fact]
    public async Task AHealthReadThatGetsNoAnswer_IsOneTryPerLook_AndOnlyTheThirdInARowIsABadRead()
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a'); f.Reads.Running["tdd"] = "1.0.2";
        f.Reads.Status["tdd"] = "unreachable";
        await f.ReleaseInPrevious("1.0.2", 'a');
        var probes = f.Reads.Probes;
        Assert.Equal(1, f.State.Evidence["1.0.2|tdd"].Unreachable);
        Assert.Empty(f.State.Evidence["1.0.2|tdd"].Reads);   // not a stored bad read
        Assert.Contains("did not answer", f.LastDecision!.Reasons[0]);
        var r = await f.Wake();
        Assert.Equal(probes, f.Reads.Probes);   // the retry is due after retrySeconds (20), not at the next input
        Assert.Equal(f.Now.AddSeconds(20), r.WakeAt);
        await f.After(TimeSpan.FromSeconds(20));
        Assert.Equal(2, f.State.Evidence["1.0.2|tdd"].Unreachable);
        Assert.Empty(f.State.Evidence["1.0.2|tdd"].Reads);
        await f.After(TimeSpan.FromSeconds(20));
        Assert.Equal(0, f.State.Evidence["1.0.2|tdd"].Unreachable);
        Assert.Contains(f.State.Evidence["1.0.2|tdd"].Reads, x => x.Status == "unreachable");   // the third look: a bad read, which only a new deployment lifts
    }

    [Fact]
    public async Task AHealthReadThatGetsNoAnswerOnce_IsForgotten_WhenTheNextLookAnswers()
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a'); f.Reads.Running["tdd"] = "1.0.2";
        f.Reads.Status["tdd"] = "unreachable";
        await f.ReleaseInPrevious("1.0.2", 'a');
        f.Reads.Status["tdd"] = "Healthy";
        await f.After(TimeSpan.FromSeconds(20));
        var ev = f.State.Evidence["1.0.2|tdd"];
        Assert.Equal(0, ev.Unreachable);
        Assert.DoesNotContain(ev.Reads, x => x.Status == "unreachable");
        await f.Mature();
        Assert.Single(f.Of<PromoteRelease>());
    }

    [Fact]
    public async Task ASignOff_ThatFindsThePreviousAppSilent_WaitsTwiceAndAbortsOnTheThirdLook()
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a'); f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a'); await f.Mature();
        Assert.Single(f.Of<PromoteRelease>());
        await f.Deploy("1.0.2", "Uat", "Started");
        f.Reads.Status["tdd"] = "unreachable";
        await f.SignOff("1.0.2", "Uat");
        Assert.Empty(f.Of<AnswerSignOff>());
        Assert.Equal("signoff-waiting", f.LastDecision!.Action);
        await f.After(TimeSpan.FromSeconds(20));
        Assert.Empty(f.Of<AnswerSignOff>());
        await f.After(TimeSpan.FromSeconds(20));
        var a = Assert.Single(f.Of<AnswerSignOff>());
        Assert.False(a.Proceed);
        Assert.Contains("the tdd app answered unreachable", a.Notes);
    }

    [Fact]
    public async Task AnOperatorsRedeployOfAFailedRelease_IsNotTheTrainsOwn_ItsSignOffIsOnlyFollowed()
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a'); f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a'); await f.Mature();
        Assert.Single(f.Of<PromoteRelease>());
        await f.Deploy("1.0.2", "Uat", "Started");
        await f.Deploy("1.0.2", "Uat", "Failed");   // the train's own deployment ended; the version stays in Requested
        await f.Deploy("1.0.2", "Uat", "Started", "D-redeploy");   // the operator deploys it again
        Assert.False(f.State.DeploymentOf("1.0.2", "uat")!.Ours);
        await f.Send(new TrainSignOffAsked(f.Key, "I-redeploy", "D-redeploy", "T-redeploy", "Releases-1.0.2", "1.0.2", "Uat", null, f.Now));
        Assert.Empty(f.Of<AnswerSignOff>());
        Assert.Equal("following", f.LastDecision!.Action);
    }

    [Fact]
    public async Task AnAppThatIsDownCompletely_DoesNotMakeTheDecisionFlapBetweenTwoTexts()
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a'); f.Reads.Running["tdd"] = "1.0.2";
        f.Reads.Running.Remove("tdd"); f.Reads.Status["tdd"] = "unreachable";   // /_build fails too: no running version
        await f.ReleaseInPrevious("1.0.2", 'a');
        for (var i = 0; i < 3; i++) await f.After(TimeSpan.FromSeconds(20));
        var texts = f.Of<TrainDecided>().Where(d => d.Release == "1.0.2").Select(d => string.Join("|", d.Reasons)).ToList();
        var settled = texts.Last();
        await f.After(TimeSpan.FromSeconds(20)); await f.After(TimeSpan.FromSeconds(20)); await f.After(TimeSpan.FromSeconds(20));
        Assert.DoesNotContain(f.Of<TrainDecided>().Where(d => d.Release == "1.0.2").Select(d => string.Join("|", d.Reasons)).Skip(texts.Count), t => t != settled);
    }

    [Fact]
    public async Task ASignOff_OfAnOperatorsOwnDeploymentOfARequestedRelease_IsNotAnswered_OnlyTheExecutorsIs()
    {
        var f = new TrainFixture(); await f.Baseline();
        f.Range('d', 'a'); await f.Merge(7, 'a'); f.Reads.Running["tdd"] = "1.0.2";
        await f.ReleaseInPrevious("1.0.2", 'a'); await f.Mature();
        Assert.Single(f.Of<PromoteRelease>());   // the request stands: the version is in Requested
        await f.Deploy("1.0.2", "Uat", "Started");   // the executor's deployment D-1.0.2-Uat
        Assert.True(f.State.DeploymentOf("1.0.2", "uat")!.Ours);
        await f.Send(new TrainSignOffAsked(f.Key, "I-manual", "D-manual", "T-manual", "Releases-1.0.2", "1.0.2", "Uat", null, f.Now));   // an interruption of some other deployment
        Assert.Empty(f.Of<AnswerSignOff>());
        Assert.Equal("following", f.LastDecision!.Action);
        await f.SignOff("1.0.2", "Uat");
        Assert.Equal("I-1.0.2", Assert.Single(f.Of<AnswerSignOff>()).InterruptionId);
    }
}
