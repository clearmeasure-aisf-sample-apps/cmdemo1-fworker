using Fworker.Contracts.Commands;
using Xunit;

namespace Gateway.Tests;

// W1.5: v1's command port delegates a runtime start and stop to the runtime adapter and nothing else.
public class V1CommandPortTests
{
    sealed class FakeRuntime : IWorkerRuntime
    {
        public string Kind => "fake";
        public int MaxConcurrent => 1;
        public ProvisionResult Result = new(true, "exec-1", "", "http://page");
        public bool Throw;
        public ProvisionRequest? Seen; public (string Name, string Reason)? Stopped;
        public Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct) { if (Throw) throw new InvalidOperationException("x"); Seen = r; return Task.FromResult(Result); }
        public Task Deprovision(string workerName, string reason, CancellationToken ct) { Stopped = (workerName, reason); return Task.CompletedTask; }
        public Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct) => Task.FromResult<IReadOnlyList<RuntimeInstance>>([]);
    }

    static V1CommandPort PortOf(FakeRuntime rt) => new(kind => kind == "fake" ? rt : null);

    [Fact]
    public async Task StartDelegatesToProvisionWithTheSameRequest()
    {
        var rt = new FakeRuntime();
        var r = await PortOf(rt).SendAsync(new StartRuntimeSession("k", "fake", "w", 7, "Development", "prof", "nonce", "role"));
        Assert.True(r.Ok); Assert.Equal("exec-1", r.Execution); Assert.Equal("http://page", r.Url);
        Assert.Equal(new ProvisionRequest("w", 7, "Development", "prof", "nonce", "role"), rt.Seen);
    }

    [Theory]
    [InlineData("no capacity", FailureKind.RateLimited)]
    [InlineData("quota", FailureKind.RateLimited)]
    [InlineData("start-error: x", FailureKind.Transient)]
    public async Task StartFailureIsClassified(string error, FailureKind kind)
    {
        var rt = new FakeRuntime { Result = new ProvisionResult(false, "", error) };
        var r = await PortOf(rt).SendAsync(new StartRuntimeSession("k", "fake", "w", 1, "p", "m", "n"));
        Assert.False(r.Ok); Assert.Equal(kind, r.Failure); Assert.Equal(error, r.Error);
    }

    [Fact]
    public async Task StartThatThrowsIsATransientFailureNotAnException()
    {
        var r = await PortOf(new FakeRuntime { Throw = true }).SendAsync(new StartRuntimeSession("k", "fake", "w", 1, "p", "m", "n"));
        Assert.False(r.Ok); Assert.Equal(FailureKind.Transient, r.Failure);
    }

    [Fact]
    public async Task StopDelegatesToDeprovision()
    {
        var rt = new FakeRuntime();
        var r = await PortOf(rt).SendAsync(new StopRuntimeSession("k", "fake", "w", "done"));
        Assert.True(r.Ok); Assert.Equal(("w", "done"), rt.Stopped);
    }

    [Fact]
    public async Task AnUnknownRuntimeIsAPermanentFailure()
    {
        var r = await PortOf(new FakeRuntime()).SendAsync(new StopRuntimeSession("k", "other", "w", "done"));
        Assert.False(r.Ok); Assert.Equal(FailureKind.Permanent, r.Failure);
    }

    [Fact]
    public async Task CommandsV1PerformsInRuleScriptsAreNotSupported()
    {
        var port = PortOf(new FakeRuntime());
        foreach (IntegrationCommand c in new IntegrationCommand[] { new AppendJournalLine("k", 1, "l"), new WriteStateComment("k", 1, "b"), new MoveBoardCard("k", 1, "Done"), new RunRunbook("k", "p", "r", "e", new Dictionary<string, string>()) })
            Assert.Equal(FailureKind.NotSupported, (await port.SendAsync(c)).Failure);
    }
}
