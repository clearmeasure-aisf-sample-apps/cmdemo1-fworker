using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// gateway/Identity/RevocationService.cs on its own (C4a, a protected area): when there is nothing to read, the read of the generations at start
/// and its retries, the status block, and the revocation. The rule scripts are a fake call; the plant's stream close is a delegate.
/// </summary>
public sealed class RevocationServiceTests
{
    const string Secret = "revocation-service-tests-secret";

    sealed class Scripts
    {
        public readonly ConcurrentQueue<(string Verb, string Input, TimeSpan Limit)> Calls = new();
        public Func<string, string, (int, string)> Answer = (_, _) => (0, "{\"ok\":true}");
        public TaskCompletionSource? Hold;   // a revoke-worker call waits on it
        public async Task<(int Code, string Output)> Run(string verb, string input, TimeSpan limit)
        {
            Calls.Enqueue((verb, input, limit));
            if (verb == "revoke-worker" && Hold is { } h) await h.Task;
            return Answer(verb, input);
        }
    }

    static RevocationOptions Options(bool skip = false, bool github = true, double attempt = 45, double alarm = 180) =>
        new() { SkipRevocations = skip, GitHubConfigured = github, AttemptSeconds = attempt, AlarmSeconds = alarm };

    static (RevocationService Service, Revocations Generations, Scripts Scripts, List<string> Closed) Make(RevocationOptions? options = null, bool ownerMode = true,
        string secret = Secret, bool testHooks = true, bool sessionsIssued = true, double firstPause = 5.0)
    {
        var gens = new Revocations(); var scripts = new Scripts(); var closed = new List<string>();
        var s = new RevocationService(options ?? Options(), ownerMode, secret, testHooks, sessionsIssued, gens, scripts.Run, new RuntimeNonces(), n => { lock (closed) closed.Add(n); return n == "streaming"; })
            { FirstPauseSeconds = firstPause };
        return (s, gens, scripts, closed);
    }

    static async Task Until(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("the condition did not hold in time"); await Task.Delay(20); }
    }

    [Theory]
    [InlineData(false, true, true, "")]
    [InlineData(true, false, true, "owner-sessions-off")]
    [InlineData(true, false, false, "owner-sessions-off")]
    [InlineData(true, true, false, "github-not-configured")]
    [InlineData(true, true, true, "")]
    public void SkipReason_Precedence(bool ownerMode, bool sessionsIssued, bool github, string reason) =>
        Assert.Equal(reason, RevocationService.SkipReasonOf(ownerMode, sessionsIssued, github));

    [Fact]
    public async Task NotOwnerMode_LoadedAtOnce_NothingRead()
    {
        var (s, _, scripts, _) = Make(ownerMode: false);
        Assert.True(s.IsLoaded);
        Assert.Equal("", s.SkipReason);
        await s.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.Empty(scripts.Calls);
        // owner mode is the revoke route's check (not-owner-mode), never the revocation's: as before the move, RevokeAsync does not repeat it
        Assert.Equal((true, 1, false), await s.RevokeAsync("alice", "operator"));
    }

    [Fact]
    public async Task NoTokenSecret_LoadedAtOnce_NothingRead()
    {
        var (s, _, scripts, _) = Make(secret: "");
        Assert.True(s.IsLoaded);
        await s.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.Empty(scripts.Calls);
    }

    [Theory]
    [InlineData(false, true, "owner-sessions-off")]
    [InlineData(true, false, "github-not-configured")]
    public async Task Skipped_LoadedAtOnce_StatusNamesReason_RevokeRefused(bool sessionsIssued, bool github, string reason)
    {
        var (s, _, scripts, _) = Make(Options(github: github), sessionsIssued: sessionsIssued);
        Assert.True(s.IsLoaded);
        Assert.Equal(reason, s.SkipReason);
        Assert.Equal(new JsonObject { ["state"] = "skipped", ["reason"] = reason }.ToJsonString(), s.Status().ToJsonString());
        await s.StartAsync(CancellationToken.None);
        Assert.Equal((false, 0, false), await s.RevokeAsync("alice", "operator"));
        await Task.Delay(100);
        Assert.Empty(scripts.Calls);
    }

    [Fact]
    public void TestSwitch_NeedsTestHooks()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Make(Options(skip: true), testHooks: false));
        Assert.Equal("FACTORY_TEST_SKIP_REVOCATIONS is for test gateways only: it needs messaging.testHooks in the configuration", e.Message);
    }

    [Fact]
    public async Task TestSwitch_Off_LoadedAtOnce_RevokeRefused_SkipReasonWinsInStatus()
    {
        var (s, _, scripts, _) = Make(Options(skip: true));
        Assert.True(s.Off);
        Assert.True(s.IsLoaded);
        Assert.Equal(new JsonObject { ["state"] = "off", ["reason"] = "test-switch" }.ToJsonString(), s.Status().ToJsonString());
        Assert.Equal((false, 0, false), await s.RevokeAsync("alice", "operator"));
        await s.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.Empty(scripts.Calls);
        var (s2, _, _, _) = Make(Options(skip: true, github: false));
        Assert.True(s2.Off);
        Assert.Equal(new JsonObject { ["state"] = "skipped", ["reason"] = "github-not-configured" }.ToJsonString(), s2.Status().ToJsonString());
    }

    [Fact]
    public async Task Read_StartsWithTheHost_RaisesValidPositiveGenerations()
    {
        var (s, gens, scripts, _) = Make();
        scripts.Answer = (verb, _) => verb == "revocations" ? (0, "{\"ok\":true,\"revoked\":{\"mallory\":3,\"bad name!\":4,\"zed\":0,\"neg\":-2,\"text\":\"x\",\"frac\":1.5}}") : (0, "{}");
        Assert.False(s.IsLoaded);
        Assert.Equal(new JsonObject { ["state"] = "loading", ["failedAttempts"] = 0 }.ToJsonString(), s.Status().ToJsonString());
        Assert.Empty(scripts.Calls);   // nothing is read before the host starts the service
        await s.StartAsync(CancellationToken.None);
        await s.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        var call = Assert.Single(scripts.Calls);
        Assert.Equal(("revocations", "{}", TimeSpan.FromSeconds(45)), call);
        Assert.Equal(3, gens.Of("mallory"));
        Assert.Equal(0, gens.Of("bad name!"));
        Assert.Equal(0, gens.Of("zed"));
        Assert.Equal(0, gens.Of("neg"));
        Assert.Equal(0, gens.Of("text"));
        Assert.Equal(0, gens.Of("frac"));
        Assert.Equal(0, s.FailedAttempts);
        Assert.Equal(new JsonObject { ["state"] = "loaded", ["failedAttempts"] = 0 }.ToJsonString(), s.Status().ToJsonString());
        // a second start reads nothing more
        await s.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.Single(scripts.Calls);
        await s.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Read_FailsThenSucceeds_RetriesWithGrowingLimit_CountsAttempts()
    {
        var (s, gens, scripts, _) = Make(Options(attempt: 45), firstPause: 0.05);
        var answers = new Queue<(int, string)>(new[] { (1, "{\"ok\":false}"), (1, "{\"ok\":false,\"code\":\"timeout\"}"), (0, "not json"), (0, "{\"ok\":true}"), (0, "{\"ok\":true,\"revoked\":{\"alice\":2}}") });
        scripts.Answer = (_, _) => answers.Dequeue();
        await s.StartAsync(CancellationToken.None);
        await s.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, s.FailedAttempts);
        Assert.Equal(2, gens.Of("alice"));
        // the limit of each attempt: 45 s, then twice, then three times capped at 120 s
        Assert.Equal(new[] { 45.0, 90, 120, 120, 120 }, scripts.Calls.Select(c => c.Limit.TotalSeconds).ToArray());
        Assert.All(scripts.Calls, c => Assert.Equal(("revocations", "{}"), (c.Verb, c.Input)));
        Assert.Equal(new JsonObject { ["state"] = "loaded", ["failedAttempts"] = 4 }.ToJsonString(), s.Status().ToJsonString());
        Assert.True(s.Since <= DateTime.UtcNow && s.Since > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Read_Failing_StaysLoading_AndRevokeRefused()
    {
        var (s, _, scripts, _) = Make(firstPause: 0.05);
        scripts.Answer = (verb, _) => verb == "revocations" ? (1, "{\"ok\":false}") : (0, "{}");
        await s.StartAsync(CancellationToken.None);
        await Until(() => s.FailedAttempts >= 2);
        Assert.False(s.IsLoaded);
        Assert.Equal("loading", (string?)s.Status()["state"]);
        Assert.Equal((false, 0, false), await s.RevokeAsync("alice", "operator"));
        Assert.DoesNotContain(scripts.Calls, c => c.Verb == "revoke-worker");
    }

    [Fact]
    public async Task Read_Throwing_IsCountedAndRetried()
    {
        var (s, _, scripts, _) = Make(firstPause: 0.05);
        var n = 0;
        scripts.Answer = (_, _) => ++n == 1 ? throw new IOException("pwsh gone") : (0, "{\"ok\":true,\"revoked\":{}}");
        await s.StartAsync(CancellationToken.None);
        await s.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, s.FailedAttempts);
    }

    [Fact]
    public async Task Revoke_WritesNextGeneration_ThenRaises_ForgetsNonce_ClosesStream()
    {
        var gens = new Revocations(); var scripts = new Scripts(); var nonces = new RuntimeNonces(); var closed = new List<string>();
        scripts.Answer = (verb, _) => verb == "revocations" ? (0, "{\"ok\":true,\"revoked\":{\"mallory\":3}}") : (0, "{\"ok\":true}");
        var s = new RevocationService(Options(), true, Secret, true, true, gens, scripts.Run, nonces, n => { closed.Add(n); return n == "streaming"; });
        await s.StartAsync(CancellationToken.None);
        await s.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        var nonce = nonces.Mint("alice", "cae-job", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), TimeSpan.FromMinutes(10));

        Assert.Equal((true, 1, false), await s.RevokeAsync("alice", "operator"));
        Assert.Equal(1, gens.Of("alice"));
        Assert.Equal(new[] { "alice" }, closed);
        Assert.NotNull(nonces.Redeem(nonce).Error);   // the nonce not yet redeemed is spent with the name
        Assert.Equal((true, 4, false), await s.RevokeAsync("mallory", "runtime"));
        Assert.Equal((true, 1, true), await s.RevokeAsync("streaming", "operator"));
        var writes = scripts.Calls.Where(c => c.Verb == "revoke-worker").ToList();
        Assert.Equal(new[] { "{\"name\":\"alice\",\"generation\":1}", "{\"name\":\"mallory\",\"generation\":4}", "{\"name\":\"streaming\",\"generation\":1}" }, writes.Select(c => c.Input).ToArray());
        Assert.All(writes, c => Assert.Equal(TimeSpan.FromSeconds(120), c.Limit));
    }

    [Fact]
    public async Task Revoke_WriteFails_NothingRevoked()
    {
        var (s, gens, scripts, closed) = Make();
        scripts.Answer = (verb, _) => verb == "revocations" ? (0, "{\"ok\":true,\"revoked\":{}}") : (1, "{\"ok\":false}");
        await s.StartAsync(CancellationToken.None);
        await s.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((false, 0, false), await s.RevokeAsync("alice", "operator"));
        Assert.Equal(0, gens.Of("alice"));
        Assert.Empty(closed);
    }

    [Fact]
    public async Task Revoke_OneAtATime_GenerationsInSequence()
    {
        var (s, gens, scripts, _) = Make();
        scripts.Answer = (verb, _) => verb == "revocations" ? (0, "{\"ok\":true,\"revoked\":{}}") : (0, "{\"ok\":true}");
        await s.StartAsync(CancellationToken.None);
        await s.Loaded.WaitAsync(TimeSpan.FromSeconds(10));
        scripts.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = s.RevokeAsync("alice", "operator");
        var second = s.RevokeAsync("alice", "runtime");
        await Until(() => scripts.Calls.Count(c => c.Verb == "revoke-worker") >= 1);
        await Task.Delay(100);
        Assert.Single(scripts.Calls, c => c.Verb == "revoke-worker");   // the second waits for the first
        scripts.Hold.SetResult();
        Assert.Equal((true, 1, false), await first);
        Assert.Equal((true, 2, false), await second);
        Assert.Equal(2, gens.Of("alice"));
    }
}
