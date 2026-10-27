using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;
using Fworker.Domain.Commands;
using Xunit;

namespace Fworker.Domain.Tests;

public class CommandContractTests
{
    static readonly IntegrationCommand[] All =
    [
        new StartRuntimeSession("k1", "octopus", "w1", 5, "Development", "p", "n"),
        new StopRuntimeSession("k2", "octopus", "w1", "done"),
        new RunRunbook("k3", "proj", "start", "tdd", new Dictionary<string, string> { ["a"] = "b" }),
        new AppendJournalLine("k4", 5, "line"),
        new WriteStateComment("k5", 5, "body"),
        new MoveBoardCard("k6", 5, "Done"),
    ];

    [Fact]
    public void EveryCommandHasAStableNameAndTarget()
    {
        Assert.Equal(["StartRuntimeSession", "StopRuntimeSession", "RunRunbook", "AppendJournalLine", "WriteStateComment", "MoveBoardCard"], All.Select(c => c.Name));
        Assert.Equal([CommandTarget.Runtime, CommandTarget.Runtime, CommandTarget.Octopus, CommandTarget.GitHub, CommandTarget.GitHub, CommandTarget.GitHub], All.Select(c => c.Target));
        Assert.Equal(All.Length, All.Select(c => c.Name).Distinct().Count());
    }

    [Fact]
    public void CommandsCompareByValue()
    {
        Assert.Equal(new MoveBoardCard("k", 1, "Done"), new MoveBoardCard("k", 1, "Done"));
        Assert.NotEqual(new MoveBoardCard("k", 1, "Done"), new MoveBoardCard("k2", 1, "Done"));
    }

    [Fact]
    public void ResultFactoriesSetTheClassification()
    {
        var ok = CommandResult.Success("exec", "http://x");
        Assert.True(ok.Ok); Assert.Equal(FailureKind.None, ok.Failure); Assert.Equal("exec", ok.Execution); Assert.Equal("http://x", ok.Url);
        var bad = CommandResult.Failed(FailureKind.RateLimited, "quota");
        Assert.False(bad.Ok); Assert.Equal(FailureKind.RateLimited, bad.Failure); Assert.Equal("quota", bad.Error);
    }

    [Fact]
    public async Task RecordingSinkCapturesInOrderAndAnswersSuccess()
    {
        var sink = new RecordingCommandSink();
        ICommandPort port = sink;
        foreach (var c in All) Assert.True((await port.SendAsync(c)).Ok);
        Assert.Equal(All, sink.Commands);
        Assert.Equal(["StartRuntimeSession k1", "StopRuntimeSession k2", "RunRunbook k3", "AppendJournalLine k4", "WriteStateComment k5", "MoveBoardCard k6"], sink.Lines());
        Assert.Single(sink.Of("MoveBoardCard"));
    }

    [Fact]
    public async Task RecordingSinkClearsAndRejectsNull()
    {
        var sink = new RecordingCommandSink();
        await sink.SendAsync(All[0]);
        sink.Clear();
        Assert.Empty(sink.Commands);
        await Assert.ThrowsAsync<ArgumentNullException>(() => sink.SendAsync(null!));
    }
}
