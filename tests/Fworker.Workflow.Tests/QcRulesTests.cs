using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Workflows.Qc;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.1: the pure rules of the QC gate, pinned to plugin/scripts/lib/Qc.ps1: the eligibility reasons in the script's words, the human approval of Find-HumanApproval, the text hygiene of ConvertTo-QcText, the
/// state machine's moves, the link token, and the offer comment, which is pinned to the golden write of the Tier 1 suites (length 416, sha256 0c4ca506 of its normalized text).
/// </summary>
public sealed class QcRulesTests
{
    static QcConfig Cfg(string mergePolicy = "auto", string qc = "{}", Func<string, string?>? env = null) =>
        QcConfig.From(JsonNode.Parse(QcHarness.ConfigJson(mergePolicy, qc)), env ?? (n => n switch { "FACTORY_PUBLIC_URL" => "https://gateway.example.test", "FACTORY_TOKEN_SECRET" => QcHarness.Secret, "FACTORY_OWNER_MODE" => "1", _ => null }));

    static JsonObject State(int pr = 9, bool passed = true, bool merge = false)
    {
        var s = new JsonObject { ["verdict"] = new JsonObject { ["passed"] = passed, ["pr"] = pr } };
        if (merge) s["merge"] = new JsonObject { ["sha"] = "abc" };
        return s;
    }

    static JsonObject Pr(string headRef = "factory-worker/5-development", string repo = "o/r", bool draft = false, string state = "open") =>
        FakeQcWorld.Pr(9, FakeQcWorld.Sha1, headRef, "alice", draft, state, repo);

    static readonly string[] Labels = ["factory worker"];

    // ---- eligibility ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnOpenPullRequestOnTheItemsBranchWithAPassInValidation_IsEligible()
    {
        var el = QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", State(), Pr(), ["src/a.cs"]);
        Assert.True(el.Ok);
        Assert.Empty(el.Reasons);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("fork")]
    [InlineData("branch-prefix")]
    [InlineData("pr-not-open")]
    public void ThePullRequestsOwnFactsRefuse(string reason)
    {
        var pr = reason switch
        {
            "draft" => Pr(draft: true), "fork" => Pr(repo: "evil/r"), "branch-prefix" => Pr(headRef: "other/5-x"), _ => Pr(state: "closed"),
        };
        Assert.Equal([reason], QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", State(), pr, null).Reasons);
    }

    [Fact]
    public void TheBranchPrefixIsTheItemsNumberFollowedByADash_NotAPrefixOfAnotherItem()
    {
        // factory-worker/51-... is item 51's branch, not item 5's (the script's regex is ^name/n-)
        Assert.Contains("branch-prefix", QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", State(), Pr("factory-worker/51-development"), null).Reasons);
        Assert.True(QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", State(), Pr("Factory-Worker/5-Development"), null).Ok);   // (-match is case-insensitive)
    }

    [Fact]
    public void ACopilotBranchCountsOnlyWhenTheStateRecordsThisPullRequest()
    {
        var s = State(); s["pr"] = new JsonObject { ["number"] = 9 };
        Assert.True(QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", s, Pr("copilot/fix-it"), null).Ok);
        s["pr"] = new JsonObject { ["number"] = 10 };
        Assert.Contains("branch-prefix", QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", s, Pr("copilot/fix-it"), null).Reasons);
        Assert.Contains("branch-prefix", QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", State(), Pr("copilot/fix-it"), null).Reasons);
    }

    [Fact]
    public void TheItemsFactsRefuse_TheColumn_TheVerdict_TheMerge_TheAutoMergeLabel()
    {
        Assert.Equal(["not-in-validation"], QcRules.Eligibility(Cfg(), 5, Labels, "Development", State(), Pr(), null).Reasons);
        Assert.Equal(["not-in-validation"], QcRules.Eligibility(Cfg(), 5, Labels, null, State(), Pr(), null).Reasons);
        Assert.True(QcRules.Eligibility(Cfg(), 5, Labels, "functional testing", State(), Pr(), null).Ok);   // (-ine)
        Assert.Equal(["no-pass-verdict"], QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", State(passed: false), Pr(), null).Reasons);
        Assert.Equal(["no-pass-verdict"], QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", State(pr: 10), Pr(), null).Reasons);
        Assert.Equal(["no-pass-verdict"], QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", State(merge: true), Pr(), null).Reasons);
        Assert.Equal(["no-pass-verdict"], QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", new JsonObject(), Pr(), null).Reasons);
        Assert.Equal(["auto-merge"], QcRules.Eligibility(Cfg(), 5, ["factory worker", "auto merge"], "Functional Testing", State(), Pr(), null).Reasons);
    }

    [Fact]
    public void ADocumentationOnlyChangeIsNotEligible_OnlyOnceTheFilesAreKnown_AndOnlyWhenNothingElseRefuses()
    {
        var docs = new[] { "docs/a.md", "README.md" };
        Assert.True(QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", State(), Pr(), null).Ok);
        Assert.Equal(["docs-only"], QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", State(), Pr(), docs).Reasons);
        Assert.Equal(["draft"], QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", State(), Pr(draft: true), docs).Reasons);
        Assert.True(QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", State(), Pr(), ["docs/a.md", "src/a.cs"]).Ok);
        Assert.True(QcRules.Eligibility(Cfg(), 5, Labels, "Functional Testing", State(), Pr(), []).Ok);   // (a pull request with no files proves nothing)
    }

    // ---- the human approval ---------------------------------------------------------------------------------------------------------------------------

    static JsonObject Review(string login, string state, string sha = FakeQcWorld.Sha1) =>
        new() { ["user"] = new JsonObject { ["login"] = login }, ["state"] = state, ["commit_id"] = sha };

    [Fact]
    public void AnApprovalIsThatOfAListedPersonOfTheCurrentHead_TheLatestReviewOfEachReviewer()
    {
        var pr = FakeQcWorld.Pr(9, FakeQcWorld.Sha1, "factory-worker/5-d", author: "bob");
        string[] humans = ["Alice", "carol"];
        Assert.Equal("alice", QcRules.FindHumanApproval(pr, humans, [Review("alice", "APPROVED")]));
        Assert.Null(QcRules.FindHumanApproval(pr, humans, [Review("alice", "APPROVED"), Review("alice", "CHANGES_REQUESTED")]));   // the latest review counts
        Assert.Null(QcRules.FindHumanApproval(pr, humans, [Review("alice", "CHANGES_REQUESTED"), Review("alice", "DISMISSED")]));
        Assert.Equal("alice", QcRules.FindHumanApproval(pr, humans, [Review("alice", "COMMENTED"), Review("alice", "APPROVED")]));
        Assert.Null(QcRules.FindHumanApproval(pr, humans, [Review("alice", "APPROVED", FakeQcWorld.Sha2)]));   // an approval of an older head
        Assert.Null(QcRules.FindHumanApproval(pr, humans, [Review("mallory", "APPROVED")]));                    // not a listed person
        Assert.Null(QcRules.FindHumanApproval(pr, [], [Review("alice", "APPROVED")]));                          // no one is named
        Assert.Equal("carol", QcRules.FindHumanApproval(pr, humans, [Review("mallory", "APPROVED"), Review("carol", "approved")]));   // (-eq: case does not matter)
        var own = FakeQcWorld.Pr(9, FakeQcWorld.Sha1, "factory-worker/5-d", author: "Alice");
        Assert.Null(QcRules.FindHumanApproval(own, humans, [Review("alice", "APPROVED")]));                     // the author never counts
    }

    [Fact]
    public void TheTrustOfARunComesFromTheAuthorAndFromTheInspectorsMandatoryCodes()
    {
        var c = Cfg(qc: "{\"trustedAuthors\":[\"dave\"]}");
        Assert.Empty(QcRules.TrustReasons(c, "alice", "bot", []));      // a human approver
        Assert.Empty(QcRules.TrustReasons(c, "DAVE", "bot", []));       // a trusted author
        Assert.Empty(QcRules.TrustReasons(c, "bot", "bot", []));        // the gateway itself
        Assert.Equal(["author-not-trusted:eve"], QcRules.TrustReasons(c, "eve", "bot", []));
        Assert.Equal(["protected-path", "secret-like"], QcRules.TrustReasons(c, "alice", "bot", ["protected-path", "something-else", "secret-like"]));
        Assert.Equal(["author-not-trusted:eve", "package-change", "database-migration", "ai-directed-text"], QcRules.TrustReasons(c, "eve", "bot", ["package-change", "database-migration", "ai-directed-text"]));
    }

    // ---- text hygiene ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhatAWorkerSendsIsCleanedBeforeItIsPersisted()
    {
        IReadOnlyList<string> none = [];
        Assert.Equal("", QcRecords.Text(null, 300, none));
        Assert.Equal("it's 'quoted'", QcRecords.Text("it's `quoted`", 300, none));                        // a backtick would end the code fence the state is read from
        Assert.Equal("ab", QcRecords.Text("a\u0000\u0007b", 300, none));                                  // control characters go
        Assert.Equal("a\tb\nc", QcRecords.Text("a\tb\nc", 300, none));                                    // (a tab and a line feed stay)
        Assert.Equal("12345", QcRecords.Text("1234567890", 5, none));
        var token = "ghp_" + new string('A', 26);
        Assert.Equal("token [redacted] here", QcRecords.Text($"token {token} here", 300, none));          // the fallback catalogue
        Assert.Equal("x [redacted]", QcRecords.Text("x SECRET-1", 300, ["SECRET-\\d"]));                  // the configuration's catalogue replaces it
        Assert.Equal("mail me@​example", QcRecords.JournalLine("mail me@example", 300, none));       // no @mention can fire from a journal comment
    }

    // ---- the state machine ----------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("requested", "assigned", true)] [InlineData("reset", "assigned", true)] [InlineData("assigned", "assigned", true)] [InlineData("building", "assigned", false)]
    [InlineData("assigned", "building", true)] [InlineData("requested", "building", false)] [InlineData("building", "running", true)] [InlineData("assigned", "running", false)]
    [InlineData("building", "ready", true)] [InlineData("running", "ready", true)] [InlineData("ready", "ready", true)] [InlineData("assigned", "ready", false)]
    [InlineData("requested", "failed", true)] [InlineData("stale", "failed", true)] [InlineData("failed", "failed", false)] [InlineData("torn-down", "failed", false)]
    [InlineData("ready", "stale", true)] [InlineData("requested", "stale", false)] [InlineData("stale", "stale", false)]
    [InlineData("failed", "torn-down", true)] [InlineData("stale", "torn-down", true)] [InlineData("torn-down", "torn-down", false)] [InlineData("ready", "requested", false)]
    public void TheMovesOfARecordAreThoseOfQcMoves(string from, string to, bool allowed) => Assert.Equal(allowed, QcRecords.CanMove(from, to));

    [Fact]
    public void ARecordIsLiveInSevenStates()
    {
        Assert.All(new[] { "requested", "reset", "assigned", "building", "running", "ready", "stale" }, s => Assert.True(QcRecords.IsLive(new JsonObject { ["state"] = s })));
        Assert.All(new[] { "failed", "torn-down", "" }, s => Assert.False(QcRecords.IsLive(new JsonObject { ["state"] = s })));
        Assert.False(QcRecords.IsLive(null));
    }

    [Fact]
    public void ANewRecordCarriesTheScriptsFields_AndEveryMoveRaisesTheRevisionAndKeepsTheLast20States()
    {
        var now = new DateTime(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);
        var pr = FakeQcWorld.Pr(9, FakeQcWorld.Sha1, "factory-worker/5-d", "alice`x");
        var rec = QcRecords.New(5, pr, 2, "reset", now, []);
        Assert.Equal("reset", QcJson.Str(rec, "state"));
        Assert.Equal(1, QcJson.Int(rec, "rev"));
        Assert.Equal("qc-9-1111111-2", QcJson.Str(rec, "env_id"));
        Assert.Equal("alice'x", QcJson.Str(rec, "author"));
        Assert.Equal("2026-10-29T09:00:00.000Z", QcJson.Str(rec, "requested_at"));
        Assert.Equal(0, ((JsonArray)rec["old_assignments"]!).Count);
        Assert.Equal("", QcJson.Str(rec, "assignment"));
        for (var i = 0; i < 25; i++) QcRecords.SetState(rec, i % 2 == 0 ? "assigned" : "building", now.AddMinutes(i));
        Assert.Equal(26, QcJson.Int(rec, "rev"));
        var history = (JsonArray)rec["history"]!;
        Assert.Equal(20, history.Count);
        Assert.Equal("assigned", QcJson.Str((JsonObject)history[^1]!, "state"));
    }

    [Fact]
    public void TheOldAssignmentsKeepTheLastFive()
    {
        var rec = new JsonObject { ["old_assignments"] = new JsonArray("a1", "a2", "a3", "a4", "a5") };
        Assert.Equal(["a2", "a3", "a4", "a5", "a6"], QcRecords.WithOld(rec, "a6").Select(x => x!.ToString()));
        Assert.Equal(["a1", "a2", "a3", "a4", "a5"], QcRecords.WithOld(rec, "").Select(x => x!.ToString()));
    }

    // ---- the link token -------------------------------------------------------------------------------------------------------------------------------

    static string Decode(string b64) => Encoding.UTF8.GetString(Convert.FromBase64String(b64.Replace('-', '+').Replace('_', '/').PadRight((b64.Length + 3) / 4 * 4, '=')));

    [Fact]
    public void TheLinkTokenIsQ1AndThePayloadAndAnHmacOfQcColonPayload()
    {
        var now = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
        var (token, exp) = QcRules.NewToken(Cfg(), 9, FakeQcWorld.Sha1, 5, now, () => "nonce-1");
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal("q1", parts[0]);
        var payload = JsonNode.Parse(Decode(parts[1]))!.AsObject();
        Assert.Equal(9, payload["pr"]!.GetValue<int>());
        Assert.Equal(FakeQcWorld.Sha1, payload["sha"]!.ToString());
        Assert.Equal("qc-open", payload["u"]!.ToString());
        Assert.Equal(now.AddDays(7).ToUnixTimeSeconds(), payload["x"]!.GetValue<long>());
        Assert.Equal(exp, payload["x"]!.GetValue<long>());
        Assert.Equal("nonce-1", payload["n"]!.ToString());
        Assert.Equal(5, payload["i"]!.GetValue<int>());
        var mac = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(QcHarness.Secret), Encoding.UTF8.GetBytes("qc:" + parts[1]))).ToLowerInvariant();
        Assert.Equal(mac, parts[2]);
        Assert.DoesNotContain('=', parts[1]);   // base64url without padding
    }

    [Fact]
    public void WithoutASecretNoTokenIsMinted()
    {
        var c = Cfg(env: n => n == "FACTORY_TOKEN_SECRET" ? "short" : null);
        var e = Assert.Throws<QcReject>(() => QcRules.NewToken(c, 9, FakeQcWorld.Sha1, 5, DateTimeOffset.UtcNow));
        Assert.Equal((30, "no-token-secret"), (e.Exit, e.Code));
    }

    [Theory]
    [InlineData(null, "FACTORY_PUBLIC_URL is not set on the gateway")]
    [InlineData("ftp://x.test", "FACTORY_PUBLIC_URL is not an http(s) address")]
    [InlineData("not a url", "FACTORY_PUBLIC_URL is not an http(s) address")]
    [InlineData("http://127.0.0.1:8080", "FACTORY_PUBLIC_URL points at 127.0.0.1, which nobody else can reach; set it to the address people use")]
    [InlineData("http://localhost", "FACTORY_PUBLIC_URL points at localhost, which nobody else can reach; set it to the address people use")]
    public void TheGatewaysAddressMustBeOneThatPeopleCanReach(string? url, string reason)
    {
        var c = Cfg(env: n => n == "FACTORY_PUBLIC_URL" ? url : null);
        var e = Assert.Throws<QcReject>(() => QcRules.BaseUrl(c));
        Assert.Equal((30, "public-url-not-reachable", reason), (e.Exit, e.Code, e.Message));
    }

    [Fact]
    public void ALoopbackAddressIsAllowedForTestsOnly()
    {
        var c = Cfg(qc: "{\"allowLoopbackForTests\":true}", env: n => n == "FACTORY_PUBLIC_URL" ? "http://127.0.0.1:8080/" : null);
        Assert.Equal("http://127.0.0.1:8080", QcRules.BaseUrl(c));
        c.Validate();
        var bad = Cfg(qc: "{\"allowLoopbackForTests\":true}", env: n => n == "FACTORY_PUBLIC_URL" ? "https://gateway.example.test" : null);
        var e = Assert.Throws<QcReject>(() => bad.Validate());
        Assert.Equal((30, "config-invalid"), (e.Exit, e.Code));
    }

    // ---- the offer comments, pinned to the golden writes ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheOfferCommentIsTheTextOfTheGoldenWrite()
    {
        // tests/scenarios/golden/qc-gate/...: {len:416 sha256:0c4ca506 "QC environment available for PR #{#2} at head {sha}: [Start the QC environment](…"}
        var sha = "0123456789abcdef0123456789abcdef01234567";
        var text = QcRules.OfferText(Cfg(), 12, sha, "http://127.0.0.1:34567/qc/12?t=q1.abcdef.0123", 1793000000);
        var normalized = text.Replace("#12", "#{#2}").Replace("/qc/12?", "/qc/{#2}?").Replace("pr=12", "pr={#2}").Replace(sha, "{sha}").Replace(sha[..7], "{sha}")
            .Replace("127.0.0.1:34567", "127.0.0.1:{port}").Replace("q1.abcdef.0123", "q1.{token}").Replace("exp=1793000000", "exp={n}");
        Assert.Equal(416, normalized.Length);
        Assert.Equal("0c4ca506", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..8].ToLowerInvariant());
    }

    [Fact]
    public void TheCommentAfterStartIsTheLineOfTheGoldenWrite()
    {
        // tests/scenarios/golden/qc-gate/...: = rule PATCH /repos/{repo}/issues/comments/{c} 200 {"body":"QC environment for PR #{#2} at head {sha} runs on **qw1**'s computer ..."}
        var sha = "0123456789abcdef0123456789abcdef01234567";
        var text = QcRules.AssignedText(12, sha, "qw1", "http://127.0.0.1:34567/qc/12?t=q1.abcdef.0123", "1793000000");
        var normalized = text.Replace("#12", "#{#2}").Replace("/qc/12?", "/qc/{#2}?").Replace("pr=12", "pr={#2}").Replace(sha, "{sha}").Replace(sha[..7], "{sha}")
            .Replace("127.0.0.1:34567", "127.0.0.1:{port}").Replace("q1.abcdef.0123", "q1.{token}").Replace("exp=1793000000", "exp={n}");
        Assert.Equal("QC environment for PR #{#2} at head {sha} runs on **qw1**'s computer (reachable only from networks that can reach it): [QC page](http://127.0.0.1:{port}/qc/{#2}?t=q1.{token})\nThis environment was requested and runs on that worker.\n<!-- qc link pr={#2} sha={sha} exp={n} -->", normalized);
    }

    [Fact]
    public void TheCommentsAfterStartAndAfterWithdrawalAreTheTextsOfTheGoldenWrites()
    {
        var sha = "0123456789abcdef0123456789abcdef01234567";
        Assert.Equal(
            $"QC environment for PR #12 at head 0123456 runs on **qw1**'s computer (reachable only from networks that can reach it): [QC page](http://x/qc/12?t=q1.a.b)\nThis environment was requested and runs on that worker.\n<!-- qc link pr=12 sha={sha} exp=77 -->",
            QcRules.AssignedText(12, sha, "qw1", "http://x/qc/12?t=q1.a.b", "77"));
        Assert.Equal("QC environment for PR #12 is no longer offered (auto-merge).\n<!-- qc link pr=12 sha=withdrawn exp=0 -->", QcRules.WithdrawnText(12, "auto-merge"));
    }

    [Fact]
    public void AnOfferIsFoundByTheGatewaysLoginAndThePullRequestsMarker()
    {
        List<QcComment> comments =
        [
            new(1, "someone", "<!-- qc link pr=9 sha=x exp=1 -->"),
            new(2, "factory-bot", "<!-- qc link pr=90 sha=x exp=1 -->"),
            new(3, "Factory-Bot", "text\n<!-- qc link pr=9 sha=x exp=1 -->"),
        ];
        Assert.Equal(3, QcRules.FindOffer(comments, 9, "factory-bot")!.Id);
        Assert.Null(QcRules.FindOffer(comments, 7, "factory-bot"));
        Assert.Null(QcRules.FindOffer(null, 9, "factory-bot"));
    }

    // ---- the configuration ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheLifetimeIsTheMinutes_ElseTheHours_ElseEight()
    {
        QcConfig With(string timing) => QcConfig.From(JsonNode.Parse("{\"timing\":" + timing + "}"), _ => null);
        Assert.Equal(480, With("{}").LifetimeMinutes);
        Assert.Equal(90, With("{\"qcLifetimeHours\":1.5}").LifetimeMinutes);
        Assert.Equal(6, With("{\"qcLifetimeMinutes\":6,\"qcLifetimeHours\":1.5}").LifetimeMinutes);
        Assert.Equal(0.1, QcConfig.From(JsonNode.Parse("{\"timing\":{\"qcLifetimeMinutes\":6}}"), n => n == "FACTORY_TIMING_qcLifetimeMinutes" ? "0.1" : null).LifetimeMinutes);
        Assert.Equal(0, With("{}").TimingOr0("qcGraceMinutes"));
    }

    [Fact]
    public void TheMergeGateIsOnUnderPeerApproved_UnlessTheConfigurationSaysOtherwise()
    {
        Assert.Equal((false, false), Cfg("auto").Gate);
        Assert.Equal((true, true), Cfg("peer-approved").Gate);
        Assert.Equal((false, true), Cfg("peer-approved", "{\"requireRun\":false}").Gate);
        Assert.Equal((true, false), Cfg("auto", "{\"requireRun\":true}").Gate);
        Assert.True(Cfg("peer-approved").GateActive);
        Assert.False(Cfg("auto", "{\"requireRun\":true}").GateActive);   // the gate exists only under peer-approved
        Assert.False(Cfg("peer-approved", "{\"requireRun\":false,\"requireApproval\":false}").GateActive);
        Assert.False(Cfg("peer-approved", env: n => n == "FACTORY_OWNER_MODE" ? "1" : null).GateActive);   // no token secret: no QC
    }

    [Fact]
    public void ALinkTokenThatCouldOutliveItsEnvironmentIsRefused()
    {
        var c = QcConfig.From(JsonNode.Parse("{\"timing\":{\"qcTokenHours\":8,\"qcBuildCapMinutes\":8,\"qcLifetimeMinutes\":480}}"), _ => null);
        var e = Assert.Throws<QcReject>(() => c.Validate());
        Assert.Equal((30, "config-invalid"), (e.Exit, e.Code));
        QcConfig.From(JsonNode.Parse("{\"timing\":{\"qcTokenHours\":9,\"qcBuildCapMinutes\":8,\"qcLifetimeMinutes\":480}}"), _ => null).Validate();
    }

    [Theory]
    [InlineData(null, null, "[]", "")]
    [InlineData(null, null, "[\"tdd\"]", "env:tdd")]
    [InlineData("prod", null, "[\"tdd\"]", "")]
    [InlineData("PROD", null, "[\"prod\"]", "env:prod")]
    [InlineData("prod", "0", "[\"prod\"]", "")]
    [InlineData("prod", "1", "[]", "env:prod")]
    public void TheEnvironmentLabelScopesTheItems(string? environment, string? flag, string scope, string label)
    {
        var c = QcConfig.From(JsonNode.Parse("{\"envScope\":" + scope + "}"), n => n switch { "FACTORY_ENVIRONMENT" => environment, "FACTORY_ENV_SCOPE" => flag, _ => null });
        Assert.Equal(label, c.EnvLabel);
    }
}
