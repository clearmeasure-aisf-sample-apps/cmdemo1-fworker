using System.Net;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Xunit;

namespace Fworker.Integrations.Tests;

public class ErrorClassifierTests
{
    static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    static Classification C(int status, string body = "", bool idem = false, bool refreshed = false, TimeSpan? ra = null, DateTimeOffset? reset = null, int? rem = null) =>
        ErrorClassifier.Classify(new HttpOutcome(status, body, ra, reset, rem), idem, refreshed, Now);

    [Theory]
    [InlineData(500)] [InlineData(502)] [InlineData(503)] [InlineData(408)]
    public void ServerErrorsAreTransient(int s) { var c = C(s); Assert.Equal(ErrorAction.Retry, c.Action); Assert.Equal(FailureKind.Transient, c.Kind); }

    [Fact] public void TimeoutIsTransient() => Assert.Equal(FailureKind.Transient, ErrorClassifier.Classify(new HttpOutcome(0, TimedOut: true), false).Kind);
    [Fact] public void SuccessIsSuccess() => Assert.Equal(ErrorAction.Success, C(201).Action);

    [Fact] public void First401RefreshesSecondIsPermanent()
    {
        Assert.Equal(FailureKind.Transient, C(401).Kind);
        Assert.Equal(FailureKind.Permanent, C(401, refreshed: true).Kind);
    }

    [Fact] public void RetryAfterReschedules()
    {
        var c = C(429, ra: TimeSpan.FromSeconds(42));
        Assert.Equal(ErrorAction.Reschedule, c.Action); Assert.Equal(FailureKind.RateLimited, c.Kind); Assert.Equal(TimeSpan.FromSeconds(42), c.RetryAfter);
    }

    [Fact] public void ExhaustedPrimaryLimit403WaitsForTheReset()
    {
        var c = C(403, reset: Now.AddMinutes(5), rem: 0);
        Assert.Equal(FailureKind.RateLimited, c.Kind); Assert.Equal(TimeSpan.FromMinutes(5), c.RetryAfter);
    }

    [Fact] public void SecondaryLimit403IsRateLimited() => Assert.Equal(FailureKind.RateLimited, C(403, "You have exceeded a secondary rate limit").Kind);
    [Fact] public void PlainForbiddenIsPermanent() => Assert.Equal(FailureKind.Permanent, C(403, "Resource not accessible by integration").Kind);
    [Fact] public void Bare429StillWaits() => Assert.Equal(FailureKind.RateLimited, C(429).Kind);

    [Fact] public void AlreadyExistsIsSuccessOnlyForIdempotentCommands()
    {
        Assert.Equal(ErrorAction.Success, C(422, "{\"message\":\"Reference already exists\"}", idem: true).Action);
        Assert.Equal(ErrorAction.Success, C(409, "already queued", idem: true).Action);
        Assert.Equal(FailureKind.Permanent, C(422, "Reference already exists", idem: false).Kind);
        Assert.Equal(FailureKind.Permanent, C(422, "Validation failed", idem: true).Kind);
    }

    [Theory] [InlineData(400)] [InlineData(404)] [InlineData(410)]
    public void OtherClientErrorsArePermanent(int s) { var c = C(s); Assert.Equal(ErrorAction.Fail, c.Action); Assert.Equal(FailureKind.Permanent, c.Kind); }

    [Fact] public void ExceptionsClassify()
    {
        Assert.Equal(FailureKind.Transient, ErrorClassifier.Classify(new HttpRequestException("x")).Kind);
        Assert.Equal(FailureKind.Transient, ErrorClassifier.Classify(new TaskCanceledException()).Kind);
        Assert.Equal(FailureKind.Permanent, ErrorClassifier.Classify(new InvalidOperationException()).Kind);
    }
}

public class IdempotencyKeyTests
{
    [Fact] public void SameFactsSameKey() => Assert.Equal(IdempotencyKeys.For("AppendJournalLine", "o/r", 5, "line"), IdempotencyKeys.For("AppendJournalLine", "o/r", 5, "line"));
    [Fact] public void PartsAreNotAmbiguous() => Assert.NotEqual(IdempotencyKeys.For("X", "a", "bc"), IdempotencyKeys.For("X", "ab", "c"));
    [Fact] public void CommandNameIsPartOfTheKey() => Assert.NotEqual(IdempotencyKeys.For("A", 1), IdempotencyKeys.For("B", 1));
    [Fact] public void NumbersUseTheInvariantCulture() => Assert.Equal("X|3:1.5", IdempotencyKeys.For("X", 1.5));
    [Fact] public void ShortIsStableAndSixteenHex()
    {
        var s = IdempotencyKeys.Short("k");
        Assert.Equal(16, s.Length); Assert.Equal(s, IdempotencyKeys.Short("k")); Assert.NotEqual(s, IdempotencyKeys.Short("k2"));
    }
}

public class CommandHandlerTests
{
    sealed class H(IIdempotencyLedger l, Func<Task<CommandResult>> run) : IntegrationCommandHandler<MoveBoardCard>(l)
    {
        public int Calls;
        protected override Task<CommandResult> Execute(MoveBoardCard c, CancellationToken ct) { Calls++; return run(); }
    }
    static readonly MoveBoardCard Cmd = new("k1", 5, "Dev");

    [Fact] public async Task ReplayOfASettledKeyDoesNotRunAgain()
    {
        var h = new H(new InMemoryIdempotencyLedger(), () => Task.FromResult(CommandResult.Success()));
        Assert.True((await h.Handle(Cmd)).Ok); Assert.True((await h.Handle(Cmd)).Ok);
        Assert.Equal(1, h.Calls);
    }

    [Fact] public async Task TransientFailureIsNotRecordedSoItRunsAgain()
    {
        var h = new H(new InMemoryIdempotencyLedger(), () => throw new HttpRequestException("down"));
        var r = await h.Handle(Cmd);
        Assert.Equal(FailureKind.Transient, r.Failure);
        await h.Handle(Cmd);
        Assert.Equal(2, h.Calls);
        Assert.Null(IntegrationCommandHandler<MoveBoardCard>.FailureMessage(Cmd, r));
    }

    [Fact] public async Task PermanentFailureIsRecordedAndBecomesIntegrationFailed()
    {
        var h = new H(new InMemoryIdempotencyLedger(), () => Task.FromResult(CommandResult.Failed(FailureKind.Permanent, "no")));
        var r = await h.Handle(Cmd); await h.Handle(Cmd);
        Assert.Equal(1, h.Calls);
        var m = IntegrationCommandHandler<MoveBoardCard>.FailureMessage(Cmd, r)!;
        Assert.Equal("MoveBoardCard", m.CommandName); Assert.Equal("k1", m.IdempotencyKey); Assert.Equal(FailureKind.Permanent, m.Kind);
    }

    [Fact] public async Task EmptyKeyIsRefused()
    {
        var h = new H(new InMemoryIdempotencyLedger(), () => Task.FromResult(CommandResult.Success()));
        Assert.Equal(FailureKind.Permanent, (await h.Handle(Cmd with { IdempotencyKey = " " })).Failure);
        Assert.Equal(0, h.Calls);
    }
}

public class GitHubBudgetTests
{
    sealed class Clock(DateTimeOffset t) : TimeProvider { public DateTimeOffset T = t; public override DateTimeOffset GetUtcNow() => T; }
    static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    static string Reset(int plusSeconds) => (T0.ToUnixTimeSeconds() + plusSeconds).ToString();

    [Fact] public void KeyedByCredentialAndResource()
    {
        var b = new GitHubBudget(300, new Clock(T0));
        b.Observe("acme", "core", "10", Reset(600));
        Assert.True(b.Low("acme", "core").Low);
        Assert.False(b.Low("other", "core").Low);
        Assert.False(b.Low("acme", "graphql").Low);
    }

    [Fact] public void LowerRemainingOfTheSameResetWinsAndOlderResetLoses()
    {
        var b = new GitHubBudget(300, new Clock(T0));
        b.Observe("a", "core", "500", Reset(600)); b.Observe("a", "core", "100", Reset(600)); b.Observe("a", "core", "400", Reset(600));
        Assert.Equal(100, b.Remaining("a", "core"));
        b.Observe("a", "core", "5", Reset(300));
        Assert.Equal(100, b.Remaining("a", "core"));
    }

    [Fact] public void AWindowThatEndedCountsAsFull()
    {
        var clock = new Clock(T0); var b = new GitHubBudget(300, clock);
        b.Observe("a", "core", "1", Reset(60));
        Assert.True(b.Low("a", "core").Low);
        clock.T = T0.AddSeconds(61);
        Assert.False(b.Low("a", "core").Low); Assert.Null(b.Remaining("a", "core"));
    }

    [Fact] public void GarbageHeadersAreIgnored()
    {
        var b = new GitHubBudget(300, new Clock(T0)); b.Observe("a", "core", "x", Reset(60)); b.Observe("a", "core", null, null);
        Assert.Null(b.Remaining("a", "core"));
    }
}

public class GitHubClientFactoryTests
{
    sealed class Recorder : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Seen.Add(r);
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.Add("x-ratelimit-resource", "core"); resp.Headers.Add("x-ratelimit-remaining", "7");
            resp.Headers.Add("x-ratelimit-reset", (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600).ToString());
            return Task.FromResult(resp);
        }
    }
    static (GitHubClientFactory F, Recorder R, GitHubBudget B) Make()
    {
        var rec = new Recorder(); var budget = new GitHubBudget();
        var creds = new StaticCredentialSource([new("Acme", new("acme-app", "tok-acme")), new("zed", new("zed-app", "tok-zed"))]);
        return (new GitHubClientFactory(new HttpClient(rec) { BaseAddress = new Uri("https://api.example/") }, creds, budget), rec, budget);
    }

    [Fact] public async Task InstallationIsResolvedPerOwnerAndCaseInsensitively()
    {
        var (f, rec, _) = Make();
        var a = await f.For(RepoId.Parse("ACME/one")); var b = await f.For(RepoId.Parse("acme/two")); var z = await f.For(RepoId.Parse("zed/x"));
        Assert.Equal("acme-app", a.CredentialLabel); Assert.Equal("acme-app", b.CredentialLabel); Assert.Equal("zed-app", z.CredentialLabel);
        (await z.Send(HttpMethod.Get, "issues/3")).Dispose();
        Assert.Equal("/repos/zed/x/issues/3", rec.Seen[0].RequestUri!.AbsolutePath);
        Assert.Equal("tok-zed", rec.Seen[0].Headers.Authorization!.Parameter);
    }

    [Fact] public async Task UnknownOwnerHasNoInstallation() =>
        await Assert.ThrowsAsync<NoInstallationException>(async () => await Make().F.For(RepoId.Parse("nobody/x")));

    [Fact] public async Task RateLimitHeadersLandUnderTheOwnersCredential()
    {
        var (f, _, budget) = Make();
        (await (await f.For(RepoId.Parse("acme/one"))).Send(HttpMethod.Get, "issues")).Dispose();
        Assert.Equal(7, budget.Remaining("acme-app", "core"));
        Assert.Null(budget.Remaining("zed-app", "core"));
        Assert.True((await f.For(RepoId.Parse("acme/two"))).Low().Low);
    }
}
