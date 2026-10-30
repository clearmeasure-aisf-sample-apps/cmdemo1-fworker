using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Workflows.WorkItem.Verbs;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.2 (#354): the item verbs answer as the rule scripts do. The cases are the scripts' own branches (Verbs.Item.ps1, Owner.ps1, Verbs.Pr.ps1): the fields of each answer, its exit code and its refusals, the writes to GitHub
/// (in the order and in the form the golden files show them) and the state comment. The GitHub is the in-memory one of ItemWorld.
/// </summary>
public class ItemVerbsTests
{
    const string Marker = "<!-- aisf:docs-only -->";
    const string Design = "## Technical Design";

    static string Sha(string line) => Regex.Replace(line, "[0-9a-f]{40}", "{sha}");

    static ItemWorld World(int item = 1, string column = "Technical Design", string body = "x", params string[] labels)
    {
        var w = new ItemWorld();
        w.AddIssue(item, column, body, labels);
        return w;
    }

    // ---- the golden writes (tests/scenarios/golden/owner/owner-update-pr-...): the caused writes of one item through its design, development and update-pr -----------

    [Fact]
    public async Task TheCausedWritesOfAnItemThroughDesignAndDevelopment_AreTheGoldenFilesLines()
    {
        var w = World(1);
        await w.Call("claim-item", new { item = 1, holder = "alice" });
        w.Now = w.Now.AddSeconds(5);
        w.Writes.Clear();
        await w.Call("append-body", new { item = 1, content = $"{Design}\n\n{Marker}\n- change as described in the item.\n", column = "Technical Design" });
        var rv = await w.Call("record-review", new { item = 1, column = "Technical Design", role = "reviewer", text = "no findings\nREVIEW: COMPLETE" });
        var done = await w.Call("complete", new { item = 1, holder = "alice", comment = "REVIEW: COMPLETE - design done", review_ref = rv["review_ref"]!.ToString(), column = "Technical Design" });
        Assert.Equal("Development", done["moved_to"]!.ToString());
        await w.Call("open-pr", new { item = 1, head = "factory-worker/1-development", title = "Trial 1", body = "change" });
        var golden = new[]
        {
            "PATCH /repos/{repo}/issues/{#1} 200 {\"body\":\"x\\n\\n## Technical Design\\n\\n<!-- aisf:docs-only -->\\n- change as described in the item.\\n\"}",
            "POST /repos/{repo}/issues/{#1}/comments 201 {\"body\":\"\\uD83E\\uDD16 factory-worker: REVIEW: COMPLETE - design done\\n\\n<!-- factory-journal:factory-worker -->\"}",
            "POST /graphql updateProjectV2ItemFieldValue 200 {\"query\":\"mutation($p:ID!,$i:ID!,$f:ID!,$o:String!){ updateProjectV2ItemFieldValue(input:{projectId:$p,itemId:$i,fieldId:$f,value:{singleSelectOptionId:$o}}){ projectV2Item{ id } } }\",\"variables\":{\"f\":\"FIELD_status\",\"i\":\"card_1\",\"o\":\"opt_Development\",\"p\":\"PVT_fake\"}}",
            "POST /repos/{repo}/pulls 201 {\"base\":\"master\",\"body\":\"change\\n\\nRefs #1\",\"draft\":false,\"head\":\"factory-worker/1-development\",\"title\":\"Trial 1 (#1)\"}",
        };
        // the comment's emoji is escaped in the golden file and not in this record: compare the JSON values, not the escapes
        // (the golden file lists the claim ref and the state comment among the "+" lines, the writes of the sweep and of the settled background; the "=" lines are what the calls caused)
        var mine = w.Writes.Where(l => !l.Contains("STATE factory-worker") && !l.StartsWith("DELETE ") && !l.Contains("/refs")).Select(Sha).ToList();
        Assert.Equal(golden.Length, mine.Count);
        for (var i = 0; i < golden.Length; i++) Assert.Equal(Norm(golden[i]), Norm(mine[i]));
        // update-pr: the body of the pull request, with the reference to the item once
        w.Writes.Clear();
        var pr = w.Pulls.Single();
        await w.Call("update-pr", new { item = 1, body = "corrected text" });
        Assert.Equal([$"PATCH /repos/{{repo}}/pulls/{{#{pr.Number}}} 200 {{\"body\":\"corrected text\\n\\nRefs #1\"}}"], w.Writes);
    }

    // the JSON strings of a golden line, escapes undone, so that the line compares by value
    static string Norm(string line) => Regex.Replace(line, @"\\u([0-9A-Fa-f]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()).Replace("🤖", "🤖");

    // ---- the runner: the shape of every answer ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAnswerHasTheVerbOkAndExit_AndAnItemVerbRunsUnderTheItemsLock_GetItemDoesNot()
    {
        var w = World(1);
        var a = await w.Call("comment", new { item = 1, body = "hello" });
        Assert.Equal("comment", a["verb"]!.ToString()); Assert.True((bool)a["ok"]!); Assert.Equal(0, (int)a["exit"]!); Assert.True((bool)a["commented"]!);
        Assert.Equal(["lock 1", "unlock 1"], w.Log);
        w.Log.Clear();
        await w.Call("get-item", new { item = 1 });
        Assert.Empty(w.Log);
    }

    [Fact]
    public async Task AMissingArgumentIsUsage2_AnUnknownIssueIs12_AnInternalFaultIs20()
    {
        var w = World(1);
        var usage = await w.Call("comment", new { item = 1 }, 2);
        Assert.False((bool)usage["ok"]!); Assert.Equal("usage", usage["code"]!.ToString()); Assert.Equal("missing argument: body", usage["reason"]!.ToString());
        var gone = await w.Call("comment", new { item = 99, body = "x" }, 12);
        Assert.Equal("item-not-found", gone["code"]!.ToString()); Assert.Equal("issue #99 not found", gone["reason"]!.ToString());
        var broken = await w.Call("comment", "this is not json", 20);
        Assert.Equal("internal-error", broken["code"]!.ToString()); Assert.Equal(30, (int)broken["retryAfterSeconds"]!);
    }

    [Fact]
    public async Task AVerbThatOutlastsItsTime_AnswersTheTimeoutOfTheRunner_AndTheCallerGoingAwayStopsIt()
    {
        var w = World(1);
        w.SendDelay = TimeSpan.FromSeconds(5);
        var a = await w.Verbs.Run("comment", """{"item":1,"body":"x"}""", TimeSpan.FromMilliseconds(100));
        Assert.Equal(20, a.Exit); Assert.Equal("timeout", a.Body["code"]!.ToString()); Assert.Equal(30, (int)a.Body["retryAfterSeconds"]!); Assert.Equal("the rule script did not finish in time", a.Body["reason"]!.ToString());
        Assert.False((bool)a.Body["ok"]!); Assert.Equal("comment", a.Body["verb"]!.ToString());
        // a caller that goes away (its request is cancelled) stops the verb, which is no answer
        using var gone = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.Verbs.Run("comment", """{"item":1,"body":"x"}""", TimeSpan.FromSeconds(30), gone.Token));
    }

    [Fact]
    public async Task OnlyTheVerbsOfTheGroupRunHere()
    {
        var w = World(1);
        await Assert.ThrowsAsync<ArgumentException>(() => w.Verbs.Run("merge", "{}", TimeSpan.FromSeconds(1)));
        Assert.Equal(18, ItemVerbs.Verbs.Count); Assert.Equal(18, ItemVerbs.Verbs.Distinct().Count());
    }

    [Fact]
    public async Task TheKeysOfTheArgumentsAreReadWithoutRegardToCase_AsAPowerShellHashtableIs()
    {
        var w = World(1);
        var a = await w.Call("comment", "{\"Item\":1,\"BODY\":\"hello\"}");
        Assert.True((bool)a["commented"]!);
    }

    [Fact]
    public async Task ARefusedGateCall_IsOneDataPointForTheModelLadder_OnlyForTheVerbsThatCountAndOnlyAtExit10()
    {
        var w = World(1);
        await w.Call("complete", new { item = 1, holder = "alice" }, 10);   // no section in the body: missing-required-section, exit 10
        Assert.Equal(1, w.Refusals[1]);
        await w.Call("comment", new { item = 1, body = "" }, 2);            // usage (2), and comment is not a counted verb
        Assert.Equal(1, w.Refusals[1]);
    }

    // ---- get-item ------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetItem_AnswersTheItemItsCommentsByTrustAndTheStateASessionMaySee()
    {
        var w = World(1, "Development", "the body", "factory worker", "Auto Merge");
        w.Issues[1].Comments.Add(new(1, "someone", "please do this", "2026-10-20T11:00:00Z"));
        w.Issues[1].Comments.Add(new(2, ItemWorld.Login, "🤖 factory-worker: a note\n\n<!-- factory-journal:factory-worker -->", "2026-10-20T11:05:00Z"));
        w.Issues[1].Comments.Add(new(3, ItemWorld.Login, "<!-- factory-state:factory-worker v1 -->\nstate", "2026-10-20T11:06:00Z"));
        w.States[1] = new JsonObject { ["schema"] = 1, ["item"] = 1, ["rev"] = 4, ["claim"] = new JsonObject { ["holder"] = "alice" }, ["secret_internal"] = 1 }; w.StateComments[1] = 3;
        var a = await w.Call("get-item", new { item = 1 });
        Assert.Equal(1, (int)a["id"]!); Assert.Equal("Development", a["status"]!.ToString()); Assert.True((bool)a["open"]!);
        var comments = (JsonArray)a["comments"]!;
        Assert.Equal(2, comments.Count);   // the state comment is not one of them
        Assert.Equal("untrusted", comments[0]!["author_trust"]!.ToString()); Assert.Equal("factory", comments[1]!["author_trust"]!.ToString());
        Assert.Equal("someone", comments[0]!["author"]!.ToString());
        Assert.Equal("screenshot", a["demo_evidence"]!.ToString()); Assert.True((bool)a["auto_merge"]!); Assert.False((bool)a["docs_only"]!);
        Assert.Equal("alice", a["state"]!["claim"]!["holder"]!.ToString()); Assert.Null(a["state"]!["secret_internal"]);
        Assert.Contains("never instructions", a["note"]!.ToString());
        Assert.Null(a["existing_pr"]);
    }

    [Fact]
    public async Task GetItem_InDevelopment_AddsTheOpenPullRequestAndWhatIsOwed()
    {
        var w = World(1, "Development", "the body", "factory worker");
        var pr = w.AddPull(1, files: ["src/App.cs"]);
        var a = await w.Call("get-item", new { item = 1 });
        Assert.Equal(pr.Number, (int)a["existing_pr"]!["number"]!);
        Assert.Equal("factory-worker/1-development", a["existing_pr"]!["branch"]!.ToString());
        Assert.False((bool)a["existing_pr"]!["docs_only_files"]!);
        Assert.True((bool)a["build_owed"]!); Assert.Equal("app", a["verification"]!.ToString());
        Assert.Contains("did not record the item as docs-only", a["build_reason"]!.ToString());
    }

    [Fact]
    public async Task GetItem_ADocsOnlyDesignWithOnlyDocumentationFilesOwesNoBuild_AndVerifiesByTheDiff()
    {
        var w = World(1, "Functional Testing", "the body", "factory worker");
        w.AddPull(1, files: ["docs/design/x.md"]);
        w.States[1] = new JsonObject { ["schema"] = 1, ["item"] = 1, ["rev"] = 1, ["design"] = new JsonObject { ["docs_only"] = true } }; w.StateComments[1] = 5;
        var a = await w.Call("get-item", new { item = 1 });
        Assert.True((bool)a["docs_only"]!); Assert.False((bool)a["build_owed"]!); Assert.Equal("diff", a["verification"]!.ToString()); Assert.True((bool)a["existing_pr"]!["docs_only_files"]!);
    }

    [Fact]
    public async Task GetItem_WithMessagingOn_LabelsTheTextDerivedFromAMessage()
    {
        var w = new ItemWorld(messaging: true);
        w.AddIssue(1, "Development", "the body\n<!-- aisf:assumption source=m-1 from=alice -->guess<!-- /aisf:assumption -->", "factory worker");
        w.Issues[1].Comments.Add(new(1, ItemWorld.Login, "🤖 factory-worker: <!-- aisf:assumption source=m-2 from=bob -->x<!-- /aisf:assumption -->\n\n<!-- factory-journal:factory-worker -->\nAI session: s1", "2026-10-20T11:00:00Z"));
        w.Issues[1].Comments.Add(new(2, ItemWorld.Login, "text <!-- aisf:assumption source=m-3 from=bob -->x<!-- /aisf:assumption --> more", "2026-10-20T11:00:00Z"));
        var a = await w.Call("get-item", new { item = 1 });
        Assert.Equal("m-1", a["derived_blocks"]![0]!["source"]!.ToString());
        var c = (JsonArray)a["comments"]!;
        Assert.Equal("derived-from-message", c[0]!["author_trust"]!.ToString());
        Assert.Equal("factory", c[1]!["author_trust"]!.ToString()); Assert.Equal("bob", c[1]!["derived_blocks"]![0]!["from"]!.ToString());
        Assert.Contains("derived-from-message", a["note"]!.ToString());
    }

    [Fact]
    public async Task GetItem_CutsALongCommentAt4000Characters()
    {
        var w = World(1);
        w.Issues[1].Comments.Add(new(1, "someone", new string('x', 4100), "2026-10-20T11:00:00Z"));
        var a = await w.Call("get-item", new { item = 1 });
        var b = a["comments"]![0]!["body"]!.ToString();
        Assert.Equal(4000 + " …[truncated]".Length, b.Length); Assert.EndsWith(" …[truncated]", b);
    }

    // ---- append-body ---------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AppendBody_AppendsAndVerifiesItInAReRead_AndTheDesignDecidesDocsOnly()
    {
        var w = World(1);
        var a = await w.Call("append-body", new { item = 1, content = $"{Design}\n{Marker}\nwhat to do", column = "Technical Design" });
        Assert.True((bool)a["appended"]!); Assert.Equal(w.Issues[1].Body.Length, (int)a["bodyLength"]!);
        var s = w.State(1);
        Assert.True((bool)s["design"]!["docs_only"]!); Assert.NotNull(s["last_append"]!["Technical Design"]);
        // the next design section without the marker takes the decision back
        await w.Call("append-body", new { item = 1, content = $"{Design} again\nno marker" });
        Assert.False((bool)w.State(1)["design"]!["docs_only"]!);
    }

    [Fact]
    public async Task AppendBody_RefusesBlankText_AMarkerOutsideTheDesignSection_AClosedItemAndAStaleColumn()
    {
        var w = World(1, "Development");
        Assert.Equal("nothing-to-append", (await w.Call("append-body", new { item = 1, content = "  \n " }, 2))["code"]!.ToString());
        Assert.Equal("docs-only-marker-not-allowed", (await w.Call("append-body", new { item = 1, content = $"note {Marker}" }, 10))["code"]!.ToString());
        Assert.Equal("stale-column", (await w.Call("append-body", new { item = 1, content = "note", column = "Technical Design" }, 11))["code"]!.ToString());
        w.Issues[1].Open = false;
        Assert.Equal("item-closed", (await w.Call("append-body", new { item = 1, content = "note" }, 11))["code"]!.ToString());
        Assert.DoesNotContain(w.Writes, l => l.StartsWith("PATCH /repos/{repo}/issues/{#1} "));
    }

    // ---- comment -------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Comment_IsWrittenAsTheFactoryWithTheSessionFooter_AndRefusedInTheLastColumn()
    {
        var w = World(1);
        await w.Call("comment", "{\"item\":1,\"body\":\"hello\",\"_session\":{\"name\":\"alice\",\"url\":\"https://x.test/s/1\"}}");
        Assert.Equal("🤖 factory-worker: hello\n\nAI session: [alice](https://x.test/s/1)\n\n<!-- factory-journal:factory-worker -->", w.Issues[1].Comments.Single().Body);
        w.Issues[1].Column = "Done";
        Assert.Equal("finished-column", (await w.Call("comment", new { item = 1, body = "more" }, 10))["code"]!.ToString());
        Assert.Equal("empty-comment", (await w.Call("comment", new { item = 1, body = " " }, 2))["code"]!.ToString());
    }

    // ---- claim-item ----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ClaimItem_TakesTheClaimRefAndTheClaim_ThenTheSameHolderGetsAlready_AnotherIsRefused()
    {
        var w = World(1);
        var a = await w.Call("claim-item", new { item = 1, holder = "alice" });
        Assert.True((bool)a["claimed"]!); Assert.Equal("first", a["mode"]!.ToString()); Assert.Equal(1, (int)a["launches"]!); Assert.Equal("Technical Design", a["column"]!.ToString());
        Assert.Equal(["POST /repos/{repo}/git/refs 201 {\"ref\":\"refs/factory/factory-worker/claims/1\",\"sha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}"], w.Writes.Where(l => l.Contains("git/refs")));
        var s = w.State(1);
        Assert.Equal("alice", s["claim"]!["holder"]!.ToString()); Assert.Equal("Technical Design", s["flow"]![0]!["c"]!.ToString()); Assert.NotNull(s["dispatched_at"]!["Technical Design"]);
        Assert.Equal("2026-10-29T12:30:00.000Z", s["claim"]!["lease_until"]!.ToString());
        var again = await w.Call("claim-item", new { item = 1, holder = "ALICE" });
        Assert.True((bool)again["already"]!);
        var other = await w.Call("claim-item", new { item = 1, holder = "bob" }, 40);
        Assert.Equal("claim-held", other["code"]!.ToString()); Assert.Equal("alice", other["holder"]!.ToString());
    }

    [Fact]
    public async Task ClaimItem_RefusesWhatTheScriptRefuses()
    {
        var w = new ItemWorld();
        w.AddIssue(1, "Technical Design", "", "other");                                   // no worker label
        w.AddIssue(2, "Technical Design", "", "factory worker", "AI Factory");            // both owners
        w.AddIssue(3, "Technical Design", "", "factory worker", "Demo");
        w.AddIssue(4, "Technical Design", "", "factory worker", "hold:owner");
        w.AddIssue(5, "Todo", "", "factory worker");
        w.AddIssue(6, "Technical Design", "", "factory worker").Open = false;
        Assert.Equal("no-worker-label", (await w.Call("claim-item", new { item = 1, holder = "a" }, 10))["code"]!.ToString());
        Assert.Equal("ambiguous-owner", (await w.Call("claim-item", new { item = 2, holder = "a" }, 10))["code"]!.ToString());
        Assert.Equal("demo-exclusion", (await w.Call("claim-item", new { item = 3, holder = "a" }, 10))["code"]!.ToString());
        var held = await w.Call("claim-item", new { item = 4, holder = "a" }, 10);
        Assert.Equal("held-by-label", held["code"]!.ToString()); Assert.Equal("hold:owner", held["label"]!.ToString());
        Assert.Equal("not-ai-column", (await w.Call("claim-item", new { item = 5, holder = "a" }, 10))["code"]!.ToString());
        Assert.Equal("item-closed", (await w.Call("claim-item", new { item = 6, holder = "a" }, 11))["code"]!.ToString());
        // a session launched for a column the item has since left
        w.Issues[5].Column = "Development";
        Assert.Equal("column-changed", (await w.Call("claim-item", new { item = 5, holder = "a", column = "Technical Design" }, 11))["code"]!.ToString());
        Assert.Empty(w.ClaimRefs);
    }

    [Fact]
    public async Task ClaimItem_ARenewMovesTheLease_OnlyForTheHolderAndNeverAfterATakeBack()
    {
        var w = World(1);
        await w.Call("claim-item", new { item = 1, holder = "alice" });
        w.Now = w.Now.AddMinutes(10);
        var r = await w.Call("claim-item", new { item = 1, holder = "alice", renew = true });
        Assert.True((bool)r["renewed"]!); Assert.Equal("2026-10-29T12:40:00.000Z", r["lease_until"]!.ToString());
        Assert.Equal("claim-not-held", (await w.Call("claim-item", new { item = 1, holder = "bob", renew = true }, 40))["code"]!.ToString());
        await w.Call("expire-claim", new { item = 1, holder = "alice", reason = "no-session" }, 0);   // not owned: the claim stays as it is
        w.State(1)["claim"]!["owner"] = true;
        await w.Call("expire-claim", new { item = 1, holder = "alice", reason = "no-session" });
        var gone = await w.Call("claim-item", new { item = 1, holder = "alice", renew = true }, 40);
        Assert.Contains("took this item back (no-session)", gone["reason"]!.ToString());
    }

    [Fact]
    public async Task ClaimItem_ReleaseDropsTheRefAndTheHoldersClaim_AnotherHoldersOnlyWithForce()
    {
        var w = World(1);
        await w.Call("claim-item", new { item = 1, holder = "alice" });
        await w.Call("claim-item", new { item = 1, holder = "bob", release = true });
        Assert.NotNull(w.State(1)["claim"]);                                   // not bob's
        Assert.Empty(w.ClaimRefs);                                              // but the ref is gone, as the script does
        await w.Call("claim-item", new { item = 1, holder = "bob", release = true, force = true });
        Assert.Null(w.State(1)["claim"]);
    }

    [Fact]
    public async Task ClaimItem_AnExpiredClaimIsARelaunch_WhichSpendsTheBudget_ThenParksTheItem()
    {
        var w = World(1);
        await w.Call("claim-item", new { item = 1, holder = "alice" });
        for (var round = 2; round <= 4; round++)
        {
            w.Now = w.Now.AddHours(1);
            var a = await w.Call("claim-item", new { item = 1, holder = "bob" });
            Assert.Equal("relaunch", a["mode"]!.ToString()); Assert.Equal(round, (int)a["launches"]!);
        }
        w.Now = w.Now.AddHours(1);
        var parked = await w.Call("claim-item", new { item = 1, holder = "carol" }, 10);
        Assert.Equal("parked", parked["code"]!.ToString()); Assert.Equal("watchdog-relaunch-budget", parked["reason"]!.ToString());
        Assert.Equal("watchdog-relaunch-budget", parked["park"]!["reason"]!.ToString());
        Assert.Null(w.State(1)["claim"]); Assert.Empty(w.ClaimRefs);
        Assert.Contains("relaunch budget (3) spent in **Technical Design**; parked for a human.", w.Issues[1].Comments.Last().Body);
        // while it is parked, a claim is refused, and with force it is taken
        w.Now = w.Now.AddMinutes(1);
        Assert.Equal("parked", (await w.Call("claim-item", new { item = 1, holder = "dave" }, 10))["code"]!.ToString());
        var forced = await w.Call("claim-item", new { item = 1, holder = "dave", force = true });
        Assert.True((bool)forced["claimed"]!); Assert.Null(w.State(1)["park"]);
    }

    [Fact]
    public async Task ClaimItem_ATakenBackClaimIsASessionRelaunch_ItDoesNotSpendTheItemsBudget()
    {
        var w = World(1);
        await w.Call("claim-item", new { item = 1, holder = "alice", owner = true, worker = "alice", assignment = "a-1" });
        for (var i = 0; i < 5; i++)
        {
            await w.Call("expire-claim", new { item = 1, holder = "alice", reason = "no-session" });
            var a = await w.Call("claim-item", new { item = 1, holder = "alice", owner = true, worker = "alice", assignment = $"a-{i + 2}" });
            Assert.Equal("resume", a["mode"]!.ToString()); Assert.Equal(1, (int)a["launches"]!);   // launches - 1 + 1
            Assert.Equal(i + 1, (int)w.State(1)["claim"]!["session_launches"]!);
        }
    }

    [Fact]
    public async Task ClaimItem_AnOrphanRefIsTakenOver_ASecondRefusalMeansAnotherWriterWon()
    {
        var w = World(1);
        w.ClaimRefs.Add(1);
        var a = await w.Call("claim-item", new { item = 1, holder = "alice" });
        Assert.True((bool)a["orphan_ref_taken_over"]!);
        Assert.Equal(["POST", "DELETE", "POST"], w.Writes.Where(l => l.Contains("git/refs")).Select(l => l.Split(' ')[0]));
    }

    [Fact]
    public async Task ClaimItem_ADocsDesignColumnFlagsTimingCriteriaOnce()
    {
        var w = World(1, "Technical Design", "Criteria:\n- the page answers in 200 ms\n- it has a title\n\n## Technical Design\n- 5 seconds in the design is not flagged");
        var a = await w.Call("claim-item", new { item = 1, holder = "alice" });
        Assert.Equal(["the page answers in 200 ms"], a["timing_criteria"]!.AsArray().Select(x => x!.ToString()));
        Assert.Contains("**Timing or statistical acceptance criteria** (flagged at Technical Design):\n\n- the page answers in 200 ms", w.Issues[1].Comments.Single().Body);
        await w.Call("release", new { item = 1, holder = "alice" });
        var b = await w.Call("claim-item", new { item = 1, holder = "alice" });
        Assert.NotNull(b["timing_criteria"]); Assert.Single(w.Issues[1].Comments.Where(c => c.Body.Contains("Timing or statistical")));   // the comment only once
    }

    [Fact]
    public async Task ClaimItem_APassWaitingToBeMergedIsNotClaimedAgain()
    {
        var w = World(1, "Functional Testing");
        w.States[1] = new JsonObject { ["schema"] = 1, ["item"] = 1, ["rev"] = 1, ["verdict"] = new JsonObject { ["passed"] = true, ["processed"] = false } }; w.StateComments[1] = 9;
        Assert.Equal("verdict-already-recorded", (await w.Call("claim-item", new { item = 1, holder = "a" }, 10))["code"]!.ToString());
        var forced = await w.Call("claim-item", new { item = 1, holder = "a", force = true });
        Assert.True((bool)forced["claimed"]!);
    }

    // ---- accept --------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Accept_OfAnOwnItem_ClaimsItAsTheOwner_AndAsksTheGatewayToJournalTheAssignment()
    {
        var w = World(1);
        var a = await w.Call("accept", new { item = 1, assignment = "a-1", holder = "alice", model = "m", effort = "high", url = "https://x.test/s", accepted_by = "client" });
        Assert.Equal("own-item", a["kind"]!.ToString()); Assert.Equal("a-1", a["assignment"]!.ToString()); Assert.True((bool)a["claimed"]!);
        var j = a["journal"]!;
        Assert.Equal("assigned", j["event"]!.ToString()); Assert.Equal("owner|a-1", j["key"]!.ToString());
        var f = j["fields"]!;
        Assert.Equal("alice", f["worker"]!.ToString()); Assert.Equal("Technical Design", f["column"]!.ToString()); Assert.Equal("first", f["mode"]!.ToString()); Assert.Equal(1, (int)f["attempt"]!);
        Assert.Equal("client", f["by"]!.ToString()); Assert.Equal("m", f["model"]!.ToString()); Assert.Equal("high", f["effort"]!.ToString()); Assert.Equal(0, (int)f["tier"]!); Assert.Equal("https://x.test/s", f["url"]!.ToString());
        var s = w.State(1)["claim"]!;
        Assert.True((bool)s["owner"]!); Assert.Equal("alice", s["worker"]!.ToString()); Assert.Equal("a-1", s["assignment"]!.ToString());
        // a second accept of the same assignment by the live owner is the claim's "already": no journal asked for
        var again = await w.Call("accept", new { item = 1, assignment = "a-1", holder = "alice" });
        Assert.True((bool)again["already"]!); Assert.Null(again["journal"]);
    }

    [Fact]
    public async Task Accept_TheModelLadderTierIsKeptInTheState_AndNeverGoesDown()
    {
        var w = World(1);
        w.Refusals[1] = 6;   // refusals: thresholds [3, 6]
        var a = await w.Call("accept", new { item = 1, assignment = "a-1", holder = "alice" });
        Assert.Equal(0, (int)a["journal"]!["fields"]!["tier"]!);    // the claim resets the refusals first, as the script does for a first claim
        // an item with demotions
        var w2 = World(2); w2.States[2] = new JsonObject { ["schema"] = 1, ["item"] = 2, ["rev"] = 1, ["demotions"] = 2 }; w2.StateComments[2] = 4;
        var b = await w2.Call("accept", new { item = 2, assignment = "a-1", holder = "alice" });
        Assert.Equal(2, (int)b["journal"]!["fields"]!["tier"]!);
        Assert.Equal(2, (int)w2.State(2)["model_tier"]!["tier"]!); Assert.Equal("Technical Design", w2.State(2)["model_tier"]!["column"]!.ToString());
        Assert.Equal(["demotions"], w2.State(2)["model_tier"]!["signals"]!.AsArray().Select(x => x!.ToString()));
    }

    [Fact]
    public async Task Accept_OfABotJob_NeedsAnUnprocessedPass_AndHoldsASeparateLease()
    {
        var w = World(1, "Functional Testing");
        Assert.Equal("no-pass-verdict", (await w.Call("accept", new { item = 1, assignment = "b-1", holder = "bot1", kind = "bot-job" }, 10))["code"]!.ToString());
        w.States[1] = new JsonObject { ["schema"] = 1, ["item"] = 1, ["rev"] = 1, ["verdict"] = new JsonObject { ["passed"] = true, ["processed"] = false, ["pr"] = 7 } }; w.StateComments[1] = 4;
        var a = await w.Call("accept", new { item = 1, assignment = "b-1", holder = "bot1", kind = "bot-job", role = "inspector", url = "https://x.test/j" });
        Assert.Equal("bot-job", a["kind"]!.ToString()); Assert.Equal(7, (int)a["pr"]!); Assert.Equal("inspector", a["role"]!.ToString());
        Assert.Equal("bot1", w.State(1)["bot"]!["holder"]!.ToString());
        Assert.Equal("bot-claimed", (await w.Call("accept", new { item = 1, assignment = "b-2", holder = "bot2", kind = "bot-job" }, 40))["code"]!.ToString());
        Assert.True((bool)(await w.Call("accept", new { item = 1, assignment = "b-1", holder = "bot1", kind = "bot-job" }))["accepted"]!);   // the same assignment again
        // the journal line, in the item's comments, by the real ledger
        Assert.Contains(w.Issues[1].Comments, c => c.Body.Contains("accepted the inspector job for PR #7") && c.Body.Contains("key=bot|b-1"));
    }

    [Fact]
    public async Task Accept_OfAQcEnvironment_IsTheScripts_AndWithoutAV1ItSaysSo()
    {
        var w = World(1);
        w.V1Answer = new ItemRawAnswer(0, "{\"ok\":true,\"verb\":\"accept\",\"kind\":\"qc-env\",\"exit\":0}");
        var a = await w.Call("accept", new { item = 1, assignment = "q-1", holder = "qc1", kind = "qc-env" });
        Assert.Equal("qc-env", a["kind"]!.ToString());
        Assert.Equal("accept", w.V1Calls.Single().Verb); Assert.Contains("\"qc-env\"", w.V1Calls.Single().Stdin);
        Assert.Empty(w.Writes);
        w.V1Answer = null;
        var no = await w.Call("accept", new { item = 1, assignment = "q-1", holder = "qc1", kind = "qc-env" }, 30);
        Assert.Equal("v1-unavailable", no["code"]!.ToString());
    }

    // ---- release, expire-claim, my-items -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Release_TheHolderGivesTheItemUp_AnotherNeedsForce_NoClaimIsNotAnError()
    {
        var w = World(1);
        var none = await w.Call("release", new { item = 1, holder = "alice" });
        Assert.False((bool)none["released"]!); Assert.Equal("no claim", none["reason"]!.ToString());
        await w.Call("claim-item", new { item = 1, holder = "alice" });
        Assert.Equal("claim-not-held", (await w.Call("release", new { item = 1, holder = "bob" }, 40))["code"]!.ToString());
        var a = await w.Call("release", new { item = 1, holder = "ALICE", reason = "done for today" });
        Assert.True((bool)a["released"]!); Assert.Null(w.State(1)["claim"]); Assert.Empty(w.ClaimRefs);
        Assert.Contains(w.Issues[1].Comments, c => c.Body.Contains("ownership released by **ALICE** (done for today)"));
    }

    [Fact]
    public async Task ExpireClaim_EndsTheLeaseNowAndMarksItTakenBack_OnlyForTheOwnerThatHoldsIt()
    {
        var w = World(1);
        await w.Call("accept", new { item = 1, assignment = "a-1", holder = "alice" });
        var no = await w.Call("expire-claim", new { item = 1, holder = "bob" });
        Assert.False((bool)no["expired"]!);
        var a = await w.Call("expire-claim", new { item = 1, holder = "alice", reason = "item-check-unanswered" });
        Assert.True((bool)a["expired"]!);
        var claim = w.State(1)["claim"]!;
        Assert.Equal("item-check-unanswered", claim["taken_back"]!.ToString()); Assert.Equal("2026-10-29T11:59:59.000Z", claim["lease_until"]!.ToString());
        Assert.Contains(w.Issues[1].Comments, c => c.Body.Contains("the gateway ended the lease of **alice** (item-check-unanswered)"));
    }

    [Fact]
    public async Task MyItems_ListsWhatOneWorkerOwnsInTheAiColumns()
    {
        var w = new ItemWorld();
        w.AddIssue(1, "Technical Design", "", "factory worker"); w.AddIssue(2, "Development", "", "factory worker"); w.AddIssue(3, "Done", "", "factory worker"); w.AddIssue(4, "Functional Testing", "", "factory worker"); w.AddIssue(5, "Development", "", "other");
        await w.Call("accept", new { item = 1, assignment = "a-1", holder = "alice" });
        await w.Call("accept", new { item = 2, assignment = "a-2", holder = "bob" });
        await w.Call("accept", new { item = 3, assignment = "a-3", holder = "alice" }, 10);   // not an AI column
        w.States[4] = new JsonObject { ["schema"] = 1, ["item"] = 4, ["rev"] = 1, ["bot"] = new JsonObject { ["holder"] = "alice", ["role"] = "inspector", ["lease_until"] = "2026-10-29T13:00:00.000Z" } }; w.StateComments[4] = 8;
        var a = await w.Call("my-items", new { holder = "alice" });
        Assert.Equal("alice", a["holder"]!.ToString());
        var items = (JsonArray)a["items"]!;
        Assert.Equal([1, 4], items.Select(i => (int)i!["item"]!).OrderBy(x => x));
        Assert.Equal("owner", items.Single(i => (int)i!["item"]! == 1)!["role"]!.ToString()); Assert.True((bool)items.Single(i => (int)i!["item"]! == 1)!["live"]!);
        Assert.Equal("inspector", items.Single(i => (int)i!["item"]! == 4)!["role"]!.ToString());
        Assert.Equal("usage", (await w.Call("my-items", new { }, 2))["code"]!.ToString());
    }

    // ---- record-review -------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RecordReview_CompleteMustBeTheLastLine_AndGivesTheRefOfTheComplete()
    {
        var w = World(1);
        Assert.Equal("not-reviewer", (await w.Call("record-review", new { item = 1, column = "Technical Design", role = "worker", text = "REVIEW: COMPLETE" }, 10))["code"]!.ToString());
        Assert.Equal("no-verdict-line", (await w.Call("record-review", new { item = 1, column = "Technical Design", role = "reviewer", text = "REVIEW: COMPLETE\nbut more text" }, 10))["code"]!.ToString());
        var a = await w.Call("record-review", new { item = 1, column = "Technical Design", role = "reviewer", text = "looks fine\nREVIEW: COMPLETE" });
        Assert.Equal("COMPLETE", a["verdict"]!.ToString()); Assert.Equal(1, (int)a["round"]!); Assert.Equal(3, (int)a["max_rounds"]!);
        Assert.Matches("^rv-[0-9a-f]{10}$", a["review_ref"]!.ToString());
        var f = await w.Call("record-review", new { item = 1, column = "Technical Design", role = "reviewer", text = "REVIEW: FINDINGS\n- a thing" });
        Assert.Equal("FINDINGS", f["verdict"]!.ToString()); Assert.Null(f["review_ref"]); Assert.Equal(2, (int)f["round"]!);
    }

    [Fact]
    public async Task RecordReview_TheRoundsAreLimited()
    {
        var w = World(1);
        for (var i = 0; i < 3; i++) await w.Call("record-review", new { item = 1, column = "Technical Design", role = "reviewer", text = $"REVIEW: FINDINGS\n- finding {i}" });
        var no = await w.Call("record-review", new { item = 1, column = "Technical Design", role = "reviewer", text = "REVIEW: COMPLETE" }, 10);
        Assert.Equal("review-rounds-exhausted", no["code"]!.ToString()); Assert.Contains("limited to 3 rounds", no["reason"]!.ToString());
    }

    [Fact]
    public async Task RecordReview_ADocsOnlyDesignGetsOneRoundMore_AndAFindingMustQuoteTheItem()
    {
        var body = $"Acceptance criteria:\n- the page shows the total of the order\n\n{Design}\n{Marker}\nChange docs/x.md only.";
        var w = World(1, "Technical Design", body);
        w.States[1] = new JsonObject { ["schema"] = 1, ["item"] = 1, ["rev"] = 1, ["design"] = new JsonObject { ["docs_only"] = true } }; w.StateComments[1] = 4;
        var none = await w.Call("record-review", new { item = 1, column = "Technical Design", role = "reviewer", text = "REVIEW: FINDINGS\n- the design is vague" }, 10);
        Assert.Equal("finding-without-criterion", none["code"]!.ToString()); Assert.Equal(0, (int)none["dropped"]!);   // no line began with FINDING:
        Assert.Null(w.State(1)["reviews"]);   // no round was used
        var ok = await w.Call("record-review", new { item = 1, column = "Technical Design", role = "reviewer", text = "REVIEW: FINDINGS\nFINDING: criterion \"the page shows the total of the order\" - the design never says where\nFINDING: file \"docs/y.md\" - outside the scope\nFINDING: criterion \"a made up criterion text\" - nope" });
        Assert.Equal(1, (int)ok["round"]!); Assert.Equal(2, (int)ok["max_rounds"]!);
        Assert.Equal(["criterion", "the page shows the total of the order"], ok["findings"]!.AsArray().SelectMany(f => new[] { f!["kind"]!.ToString(), f["quote"]!.ToString() }));
        Assert.Equal(2, (int)ok["dropped"]!);   // the file docs/y.md is not in the design either
    }

    [Fact]
    public async Task RecordReview_TheSameCriterionCitedInTwoRoundsParksTheItemWithAQuestion()
    {
        var w = World(1, "Technical Design", $"- the page shows the total of the order\n\n{Design}\nbody");
        await w.Call("claim-item", new { item = 1, holder = "alice" });
        var finding = "REVIEW: FINDINGS\nFINDING: criterion \"the page shows the total of the order\" - missing";
        var first = await w.Call("record-review", new { item = 1, column = "Technical Design", role = "reviewer", text = finding });
        Assert.Equal("FINDINGS", first["verdict"]!.ToString());
        var second = await w.Call("record-review", new { item = 1, column = "Technical Design", role = "reviewer", text = finding }, 10);
        Assert.Equal("same-criterion-repeated", second["code"]!.ToString()); Assert.Equal(["the page shows the total of the order"], second["criteria"]!.AsArray().Select(x => x!.ToString())); Assert.Equal(2, (int)second["round"]!);
        var s = w.State(1);
        Assert.Equal("same-criterion-repeated", s["park"]!["reason"]!.ToString()); Assert.Null(s["claim"]); Assert.Empty(w.ClaimRefs);
        Assert.Contains("**Question for the owner:** two consecutive review rounds in **Technical Design** (rounds 1 and 2) cite the same acceptance criterion", w.Issues[1].Comments.Last().Body);
    }

    // ---- move-next, complete -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MoveNext_MovesAnItemOutOfAColumnTheWorkersDoNotWork()
    {
        var w = World(1, "Todo", "x");
        var a = await w.Call("move-next", new { item = 1 });
        Assert.Equal("Todo", a["moved_from"]!.ToString()); Assert.Equal("Technical Design", a["moved_to"]!.ToString());
        Assert.Equal("completion-owns-exit", (await w.Call("move-next", new { item = 1 }, 10))["code"]!.ToString());
        w.Issues[1].Column = "Release Queue";
        Assert.Equal("hold-column", (await w.Call("move-next", new { item = 1 }, 10))["code"]!.ToString());
        w.Issues[1].Column = "Done"; w.Issues[1].Labels = ["other"];
        Assert.Equal("no-worker-label", (await w.Call("move-next", new { item = 1 }, 10))["code"]!.ToString());
        w.Issues[1].Labels = ["factory worker"];
        Assert.Equal("final-column", (await w.Call("move-next", new { item = 1 }, 10))["code"]!.ToString());
    }

    async Task<string> ReviewedDesign(ItemWorld w, int item, string holder)
    {
        w.Now = w.Now.AddSeconds(5);   // the append lands after the dispatch
        await w.Call("append-body", new { item, content = $"{Design}\n- do it", column = "Technical Design" });
        var rv = await w.Call("record-review", new { item, column = "Technical Design", role = "reviewer", text = "REVIEW: COMPLETE" });
        return rv["review_ref"]!.ToString();
    }

    [Fact]
    public async Task Complete_TheDesignNeedsItsSectionAfterTheDispatch_ThenAReviewThenMovesTheCard()
    {
        var w = World(1);
        await w.Call("accept", new { item = 1, assignment = "a-1", holder = "alice" });
        var missing = await w.Call("complete", new { item = 1, holder = "alice", comment = "REVIEW: COMPLETE" }, 10);
        Assert.Equal("missing-required-section", missing["code"]!.ToString());
        Assert.Contains("completion for **Technical Design** rejected: missing required section `## Technical Design` (no body append landed after dispatch: dispatched=2026-10-29T12:00:00.000Z last_append=none). A fresh append is needed.", w.Issues[1].Comments.Last().Body);
        var reference = await ReviewedDesign(w, 1, "alice");
        Assert.Equal("reviewer-verdict-missing", (await w.Call("complete", new { item = 1, holder = "alice", comment = "done", review_ref = reference }, 10))["code"]!.ToString());
        Assert.Equal("review-ref-invalid", (await w.Call("complete", new { item = 1, holder = "alice", comment = "REVIEW: COMPLETE", review_ref = "rv-wrong" }, 10))["code"]!.ToString());
        Assert.Equal("claim-held", (await w.Call("complete", new { item = 1, holder = "bob", comment = "REVIEW: COMPLETE", review_ref = reference }, 40))["code"]!.ToString());
        var a = await w.Call("complete", new { item = 1, holder = "alice", comment = "REVIEW: COMPLETE - design done", review_ref = reference });
        Assert.True((bool)a["completed"]!); Assert.Equal("Technical Design", a["column"]!.ToString()); Assert.Equal("Development", a["moved_to"]!.ToString());
        Assert.True((bool)a["continue"]!); Assert.Equal("Development", a["next_column"]!.ToString());   // the owner keeps the item
        Assert.Equal("Development", w.Issues[1].Column);
        var s = w.State(1);
        Assert.Equal("Development", s["claim"]!["column"]!.ToString()); Assert.False((bool)s["design"]!["docs_only"]!); Assert.Null(s["handoff"]); Assert.NotNull(s["dispatched_at"]!["Development"]);
        Assert.Equal(["Technical Design", "Development"], s["flow"]!.AsArray().Select(x => x!["c"]!.ToString()));
        Assert.NotEmpty(w.ClaimRefs);   // the owner's ref stays
    }

    [Fact]
    public async Task Complete_ASessionThatIsNotTheOwnerHandsTheItemOff_AndDropsItsRef()
    {
        var w = World(1);
        await w.Call("claim-item", new { item = 1, holder = "alice" });
        var reference = await ReviewedDesign(w, 1, "alice");
        var a = await w.Call("complete", new { item = 1, holder = "alice", comment = "REVIEW: COMPLETE", review_ref = reference });
        Assert.False((bool)a["continue"]!); Assert.Null(a["next_column"]);
        var s = w.State(1);
        Assert.Null(s["claim"]); Assert.Equal("Development", s["handoff"]!["column"]!.ToString()); Assert.Empty(w.ClaimRefs);
    }

    [Fact]
    public async Task Complete_ARefusedColumnAndAWorkersFailureReport()
    {
        var w = new ItemWorld();
        w.AddIssue(1, "Functional Testing", "", "factory worker"); w.AddIssue(2, "Release Queue", "", "factory worker"); w.AddIssue(3, "Done", "", "factory worker"); w.AddIssue(4, "Technical Design", "", "factory worker");
        Assert.Equal("use-validation-verdict", (await w.Call("complete", new { item = 1 }, 10))["code"]!.ToString());
        Assert.Equal("hold-column", (await w.Call("complete", new { item = 2 }, 10))["code"]!.ToString());
        Assert.Equal("not-ai-column", (await w.Call("complete", new { item = 3 }, 10))["code"]!.ToString());
        Assert.Equal("stale-column", (await w.Call("complete", new { item = 4, column = "Development" }, 11))["code"]!.ToString());
        await w.Call("claim-item", new { item = 4, holder = "alice" });
        var fail = await w.Call("complete", new { item = 4, holder = "alice", succeeded = false, comment = "could not build" });
        Assert.False((bool)fail["completed"]!); Assert.True((bool)fail["parked"]!); Assert.Equal("worker-failure", fail["reason"]!.ToString());
        Assert.Contains("worker reported **FAILURE** for **Technical Design**; item parked for review. could not build", w.Issues[4].Comments.Last().Body);
        Assert.Equal("worker-failure", w.State(4)["park"]!["reason"]!.ToString()); Assert.Null(w.State(4)["claim"]); Assert.Empty(w.ClaimRefs);
    }

    [Fact]
    public async Task Complete_InDevelopment_IsTheScriptsUntilTheCiRuleIsPorted()
    {
        var w = World(1, "Development");
        w.V1Answer = new ItemRawAnswer(10, "{\"ok\":false,\"verb\":\"complete\",\"code\":\"checks-not-green\",\"reason\":\"red\",\"exit\":10}");
        var a = await w.Call("complete", new { item = 1, holder = "alice" }, 10);
        Assert.Equal("checks-not-green", a["code"]!.ToString());
        Assert.Equal("complete", w.V1Calls.Single().Verb);
        Assert.Equal(["lock 1", "unlock 1"], w.Log);   // the lock is let go before the script runs: the script takes it
        Assert.Empty(w.Writes);
    }

    // ---- validation-verdict --------------------------------------------------------------------------------------------------------------------------------

    async Task<(ItemWorld W, string Ref, ItemWorld.Pull Pr)> AtValidation(bool autoMerge = true, string body = "the item", string headSha = "1111111111111111111111111111111111111111")
    {
        var w = World(1, "Functional Testing", body, autoMerge ? ["factory worker", "Auto Merge"] : ["factory worker"]);
        await w.Call("accept", new { item = 1, assignment = "a-1", holder = "alice" });
        var rv = await w.Call("record-review", new { item = 1, column = "Functional Testing", role = "reviewer", text = "REVIEW: COMPLETE" });
        var pr = w.AddPull(1, files: ["src/App.cs"]); pr.Sha = headSha;
        w.Writes.Clear();
        return (w, rv["review_ref"]!.ToString(), pr);
    }

    [Fact]
    public async Task ValidationVerdict_APassWithTheLabel_IsRecordedForTheMerge()
    {
        var (w, rf, pr) = await AtValidation();
        var a = await w.Call("validation-verdict", new { item = 1, holder = "alice", passed = true, summary = "all good", pr = pr.Number, review_ref = rf, head_sha = pr.Sha });
        Assert.True((bool)a["recorded"]!); Assert.True((bool)a["passed"]!); Assert.False((bool)a["downgraded"]!); Assert.Equal("merge", a["next"]!.ToString()); Assert.Equal("Functional Testing", a["column"]!.ToString());
        var v = w.State(1)["verdict"]!;
        Assert.True((bool)v["passed"]!); Assert.Equal(pr.Number, (int)v["pr"]!); Assert.Equal(pr.Sha, v["head_sha"]!.ToString()); Assert.False((bool)v["processed"]!); Assert.Equal(0, (int)v["round"]!);
        Assert.Contains("validation verdict for **Functional Testing**: PASS - all good", w.Issues[1].Comments.Last().Body);
    }

    [Fact]
    public async Task ValidationVerdict_RefusesWhatTheScriptRefuses()
    {
        var (w, rf, pr) = await AtValidation();
        Assert.Equal("verdict-fields", (await w.Call("validation-verdict", new { item = 1, passed = true }, 2))["code"]!.ToString());
        Assert.Equal("pass-requires-prNumber", (await w.Call("validation-verdict", new { item = 1, passed = true, summary = "ok" }, 2))["code"]!.ToString());
        Assert.Equal("review-ref-invalid", (await w.Call("validation-verdict", new { item = 1, holder = "alice", passed = true, summary = "ok", pr = pr.Number, review_ref = "rv-x" }, 10))["code"]!.ToString());
        var mismatch = await w.Call("validation-verdict", new { item = 1, holder = "alice", passed = true, summary = "ok", pr = pr.Number, review_ref = rf, head_sha = "2222222222222222222222222222222222222222" }, 10);
        Assert.Equal("head-mismatch", mismatch["code"]!.ToString()); Assert.Contains("given for 2222222, but the head of PR #" + pr.Number + " is 1111111", mismatch["reason"]!.ToString());
        Assert.Equal("claim-held", (await w.Call("validation-verdict", new { item = 1, holder = "bob", passed = true, summary = "ok", pr = pr.Number, review_ref = rf }, 40))["code"]!.ToString());
        w.Issues[1].Column = "Development";
        Assert.Equal("wrong-column", (await w.Call("validation-verdict", new { item = 1, holder = "alice", passed = false, summary = "no" }, 10))["code"]!.ToString());
        Assert.Null(w.State(1)["verdict"]);
    }

    [Fact]
    public async Task ValidationVerdict_APassWithoutTheAutoMergeLabelParksTheItemForAPerson()
    {
        var (w, rf, pr) = await AtValidation(autoMerge: false);
        var a = await w.Call("validation-verdict", new { item = 1, holder = "alice", passed = true, summary = "ok", pr = pr.Number, review_ref = rf });
        Assert.Equal("parked", a["next"]!.ToString());
        var s = w.State(1);
        Assert.Equal("pass-without-auto-merge", s["park"]!["reason"]!.ToString()); Assert.True((bool)s["verdict"]!["processed"]!); Assert.Null(s["claim"]);
        Assert.Contains("validation PASSED - awaiting human merge (no 'Auto Merge' label; the worker will not merge).", w.Issues[1].Comments.Last().Body);
    }

    [Fact]
    public async Task ValidationVerdict_AFailSendsTheItemBackToDevelopmentTwice_ThenParksIt()
    {
        var (w, rf, pr) = await AtValidation();
        var a = await w.Call("validation-verdict", new { item = 1, holder = "alice", passed = false, summary = "the total is wrong", pr = pr.Number });
        Assert.False((bool)a["passed"]!); Assert.Equal("demoted", a["next"]!.ToString()); Assert.Equal(1, (int)a["detail"]!["round_trip"]!);
        Assert.Equal("Development", w.Issues[1].Column);
        var s = w.State(1);
        Assert.Equal(1, (int)s["demotions"]!); Assert.Equal("the total is wrong", s["pr"]!["rejection"]!.ToString()); Assert.Null(s["verdict"]); Assert.Equal("Development", s["claim"]!["column"]!.ToString());
        Assert.Contains("validation FAILED: the total is wrong - demoting to **Development** (round-trip 1 of 2).", w.Issues[1].Comments.Last().Body);
        for (var round = 2; round <= 3; round++)
        {
            w.Issues[1].Column = "Functional Testing";
            var b = await w.Call("validation-verdict", new { item = 1, holder = "alice", passed = false, summary = $"still wrong {round}", pr = pr.Number });
            Assert.Equal(round == 2 ? "demoted" : "parked", b["next"]!.ToString());
        }
        Assert.Equal("verdict-failed", w.State(1)["park"]!["reason"]!.ToString()); Assert.Equal(pr.Number, (int)w.State(1)["park"]!["pr"]!); Assert.Equal(pr.Sha, w.State(1)["park"]!["head"]!.ToString());
        Assert.Null(w.State(1)["claim"]); Assert.Empty(w.ClaimRefs);
    }

    [Fact]
    public async Task ValidationVerdict_AFailWithoutAPullRequestParksTheItem_AndAFailOnAMergedOneKeepsTheGapsOpen()
    {
        var (w, _, pr) = await AtValidation();
        var a = await w.Call("validation-verdict", new { item = 1, holder = "alice", passed = false, summary = "nothing to fix" });
        Assert.Equal("parked", a["next"]!.ToString()); Assert.Equal("verdict-failed", w.State(1)["park"]!["reason"]!.ToString());
        Assert.Contains("validation FAILED with no pull request to fix; parked for human review.", w.Issues[1].Comments.Last().Body);
        var (w2, _, pr2) = await AtValidation();
        pr2.Merged = true; pr2.Open = false;
        var b = await w2.Call("validation-verdict", new { item = 1, holder = "alice", passed = false, summary = "gaps remain", pr = pr2.Number });
        Assert.Equal("demoted", b["next"]!.ToString());
        Assert.True((bool)w2.State(1)["pr"]!["residual"]!);
        Assert.Contains($"PR #{pr2.Number} already merged as bbbb - residual gaps only (open a NEW branch from master): gaps remain", w2.State(1)["pr"]!["rejection"]!.ToString());
    }

    [Fact]
    public async Task ValidationVerdict_AFailThatCitesTheSameCriterionTwiceParksItWithAQuestion()
    {
        var (w, _, pr) = await AtValidation(body: "- the page shows the total of the order\n\n## Technical Design\nbody");
        var summary = "FAIL: criterion \"the page shows the total of the order\" - not shown";
        var first = await w.Call("validation-verdict", new { item = 1, holder = "alice", passed = false, summary, pr = pr.Number });
        Assert.Equal("demoted", first["next"]!.ToString());
        w.Issues[1].Column = "Functional Testing";
        var second = await w.Call("validation-verdict", new { item = 1, holder = "alice", passed = false, summary, pr = pr.Number });
        Assert.Equal("parked", second["next"]!.ToString()); Assert.Equal("same-criterion-repeated", second["reason"]!.ToString()); Assert.Equal(["the page shows the total of the order"], second["criteria"]!.AsArray().Select(x => x!.ToString()));
        Assert.Contains("two consecutive FAIL verdicts cite the same acceptance criterion", w.Issues[1].Comments.Last().Body);
    }

    [Fact]
    public async Task ValidationVerdict_AVideoItemWithoutAnMp4OnItsReleaseHasThePassDowngraded()
    {
        var w = World(1, "Functional Testing", "the item", "factory worker", "Auto Merge", "video");
        await w.Call("accept", new { item = 1, assignment = "a-1", holder = "alice" });
        var rf = (await w.Call("record-review", new { item = 1, column = "Functional Testing", role = "reviewer", text = "REVIEW: COMPLETE" }))["review_ref"]!.ToString();
        var pr = w.AddPull(1, files: ["src/App.cs"]);
        var a = await w.Call("validation-verdict", new { item = 1, holder = "alice", passed = true, summary = "works", pr = pr.Number, review_ref = rf });
        Assert.False((bool)a["passed"]!); Assert.True((bool)a["downgraded"]!);
        Assert.Contains("FACTORY GATE: this item carries the 'video' label, but no MP4 is published on the 'demo-1' release. PASS downgraded to FAIL.", w.Issues[1].Comments.Last().Body);
        // with the video published the pass stands
        var w2 = World(1, "Functional Testing", "the item", "factory worker", "Auto Merge", "video");
        w2.Releases[1] = new JsonObject { ["assets"] = new JsonArray(new JsonObject { ["name"] = "demo-1.mp4" }) };
        await w2.Call("accept", new { item = 1, assignment = "a-1", holder = "alice" });
        var rf2 = (await w2.Call("record-review", new { item = 1, column = "Functional Testing", role = "reviewer", text = "REVIEW: COMPLETE" }))["review_ref"]!.ToString();
        var pr2 = w2.AddPull(1, files: ["src/App.cs"]);
        var b = await w2.Call("validation-verdict", new { item = 1, holder = "alice", passed = true, summary = "works", pr = pr2.Number, review_ref = rf2 });
        Assert.True((bool)b["passed"]!); Assert.False((bool)b["downgraded"]!);
    }

    // ---- park-item, unpark-item, resettle-item -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ParkItem_ParksOnceWithAJournalLine_AndAParkInForceIsAnswered()
    {
        var w = World(1);
        Assert.Equal("usage", (await w.Call("park-item", new { item = 1, code = "no spaces" }, 2))["code"]!.ToString());
        var a = await w.Call("park-item", new { item = 1, code = "runtime-failed", phase = "Development <!-- x -->", by = "the runtime adapter" });
        Assert.True((bool)a["parked"]!); Assert.Equal("runtime-failed", a["park"]!["reason"]!.ToString());
        Assert.Equal("runtime-failed", w.State(1)["park"]!["reason"]!.ToString());
        Assert.Contains(w.Issues[1].Comments, c => c.Body.Contains("event=parked"));
        var again = await w.Call("park-item", new { item = 1, code = "other" });
        Assert.True((bool)again["already"]!); Assert.Equal("runtime-failed", again["park"]!["reason"]!.ToString());
    }

    [Fact]
    public async Task UnparkItem_RemovesThePark_ResetsTheCounters_AndOffersTheItemAgain()
    {
        var w = World(1, "Development");
        Assert.Equal("not-parked", (await w.Call("unpark-item", new { item = 1, reason = "x" }, 10))["code"]!.ToString());
        await w.Call("park-item", new { item = 1, code = "verdict-failed" });
        w.State(1)["demotions"] = 2; w.State(1)["fix_forward_rounds"] = 1; w.State(1)["master_defect"] = new JsonObject();
        Assert.Equal("usage", (await w.Call("unpark-item", new { item = 1, reason = "  " }, 2))["code"]!.ToString());
        var a = await w.Call("unpark-item", new { item = 1, reason = "the criterion was reworded", by = "jeffrey" });
        Assert.True((bool)a["unparked"]!); Assert.Equal("Development", a["column"]!.ToString()); Assert.True((bool)a["reoffer"]!); Assert.True((bool)a["countersReset"]!); Assert.False((bool)a["expired"]!); Assert.False((bool)a["closed"]!);
        var s = w.State(1);
        Assert.Null(s["park"]); Assert.Null(s["demotions"]); Assert.Null(s["fix_forward_rounds"]); Assert.Null(s["master_defect"]); Assert.Equal("Development", s["handoff"]!["column"]!.ToString());
        Assert.Contains(w.Issues[1].Comments, c => c.Body.Contains("park `verdict-failed` released early by jeffrey: the criterion was reworded."));
    }

    [Fact]
    public async Task UnparkItem_RefusesTheWaitsOfTheGateway_AndAMergedItemIsSettledNotOffered()
    {
        var w = World(1, "Functional Testing");
        w.States[1] = new JsonObject { ["schema"] = 1, ["item"] = 1, ["rev"] = 1, ["park"] = new JsonObject { ["reason"] = "development:awaiting-checks", ["at"] = "2026-10-29T11:00:00.000Z", ["until"] = "2026-10-30T11:00:00.000Z" } }; w.StateComments[1] = 4;
        Assert.Equal("gateway-wait", (await w.Call("unpark-item", new { item = 1, reason = "x" }, 10))["code"]!.ToString());
        w.State(1)["park"]!["reason"] = "inspector-escalated";
        Assert.Equal("needs-approval", (await w.Call("unpark-item", new { item = 1, reason = "x" }, 10))["code"]!.ToString());
        w.State(1)["park"]!["reason"] = "master-broken"; w.State(1)["merge"] = new JsonObject { ["sha"] = "abc", ["pr"] = 3 };
        var a = await w.Call("unpark-item", new { item = 1, reason = "master is fixed" });
        Assert.True((bool)a["merged"]!); Assert.True((bool)a["rearmed"]!); Assert.Equal("master-broken", a["awaiting"]!.ToString()); Assert.False((bool)a["reoffer"]!);
        Assert.Null(w.State(1)["handoff"]); Assert.Equal("master-broken", w.State(1)["merge"]!["awaiting"]!.ToString());
    }

    [Fact]
    public async Task ResettleItem_PutsAMergedItemBackIntoTheSettleStep()
    {
        var w = World(1, "Functional Testing");
        Assert.Equal("not-merged", (await w.Call("resettle-item", new { item = 1, reason = "x" }, 10))["code"]!.ToString());
        w.States[1] = new JsonObject { ["schema"] = 1, ["item"] = 1, ["rev"] = 1, ["merge"] = new JsonObject { ["sha"] = "abc", ["pr"] = 3, ["green_at"] = "2026-10-29T10:00:00.000Z", ["done_at"] = "2026-10-29T11:00:00.000Z" } }; w.StateComments[1] = 4;
        var a = await w.Call("resettle-item", new { item = 1, reason = "no release carried it", by = "operator" });
        Assert.Equal("release-candidate", a["awaiting"]!.ToString()); Assert.Equal("Functional Testing", a["column"]!.ToString()); Assert.True((bool)a["open"]!);
        var m = w.State(1)["merge"]!;
        Assert.Null(m["done_at"]); Assert.Equal("2026-10-29T12:20:00.000Z", m["rc_deadline"]!.ToString());
        Assert.Equal("already-settling", (await w.Call("resettle-item", new { item = 1, reason = "again" }, 10))["code"]!.ToString());
        w.Issues[1].Column = "Done"; ((JsonObject)m).Remove("awaiting");
        Assert.Equal("done-column", (await w.Call("resettle-item", new { item = 1, reason = "again" }, 10))["code"]!.ToString());
    }

    // ---- open-pr, update-pr, item-pr -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OpenPr_OpensTheItemsPullRequestOnItsBranch_RecordsItAndSaysWhatIsOwed()
    {
        var w = World(1, "Development", "x");
        Assert.Equal("wrong-column", (await World(2, "Technical Design").Call("open-pr", new { item = 2, head = "factory-worker/2-x", title = "t" }, 10))["code"]!.ToString());
        Assert.Equal("bad-branch-name", (await w.Call("open-pr", new { item = 1, head = "somebody/else", title = "t" }, 10))["code"]!.ToString());
        var a = await w.Call("open-pr", new { item = 1, head = "factory-worker/1-fix", title = "Fix the total", body = "the change" });
        Assert.True((bool)a["created"]!); Assert.Equal("factory-worker/1-fix", a["pullRequest"]!["branch"]!.ToString()); Assert.True((bool)a["build_owed"]!);
        var pr = w.Pulls.Single();
        Assert.Equal("Fix the total (#1)", pr.Title); Assert.Equal("the change\n\nRefs #1", pr.Body); Assert.Equal("master", pr.Base);
        Assert.Equal(pr.Number, (int)w.State(1)["pr"]!["number"]!); Assert.Equal("factory-worker/1-fix", w.State(1)["pr"]!["branch"]!.ToString());
        // the same branch again: the pull request is the existing one
        var again = await w.Call("open-pr", new { item = 1, head = "factory-worker/1-fix", title = "Fix the total" });
        Assert.False((bool)again["created"]!); Assert.Equal(pr.Number, (int)again["pullRequest"]!["number"]!);
        Assert.Single(w.Pulls);
    }

    [Fact]
    public async Task OpenPr_GitHubRefusingThePullRequestIsAGateRefusal_AndACopilotBranchIsTheScripts()
    {
        var w = World(1, "Development", "x");
        w.RejectPulls = true;
        var a = await w.Call("open-pr", new { item = 1, head = "factory-worker/1-fix", title = "t" }, 10);
        Assert.Equal("pr-not-created", a["code"]!.ToString()); Assert.Contains("No commits between master and factory-worker/1-fix", a["detail"]!.ToString());
        Assert.Null(w.States.GetValueOrDefault(1)?["pr"]);
        w.V1Answer = new ItemRawAnswer(0, "{\"ok\":true,\"verb\":\"open-pr\",\"created\":false,\"exit\":0}");
        await w.Call("open-pr", new { item = 1, head = "copilot/fix-the-total", title = "t" });
        Assert.Equal("open-pr", w.V1Calls.Single().Verb);
    }

    [Fact]
    public async Task OpenPr_AfterTwoRedHeadsNeedsThePrivateBuildAttestation()
    {
        var w = World(1, "Development", "x");
        w.States[1] = new JsonObject { ["schema"] = 1, ["item"] = 1, ["rev"] = 1, ["checks_red"] = new JsonObject { ["count"] = 2, ["shas"] = new JsonArray("a", "b"), ["last_at"] = "2026-10-29T11:00:00.000Z" } }; w.StateComments[1] = 4;
        var no = await w.Call("open-pr", new { item = 1, head = "factory-worker/1-fix", title = "t" }, 10);
        Assert.Equal("private-build-required", no["code"]!.ToString()); Assert.Equal(2, (int)no["checks_red"]!); Assert.Equal(2, (int)no["gate"]!);
        Assert.Empty(w.Pulls);
        w.State(1)["private_build"] = new JsonObject { ["sha"] = "c", ["at"] = "2026-10-29T11:30:00.000Z" };   // attested after the last red head
        Assert.True((bool)(await w.Call("open-pr", new { item = 1, head = "factory-worker/1-fix", title = "t" }))["created"]!);
    }

    [Fact]
    public async Task UpdatePr_ReplacesTheBodyOfTheItemsOwnOpenPullRequestOnly()
    {
        var w = World(1, "Development", "x");
        Assert.Equal("no-open-pr", (await w.Call("update-pr", new { item = 1, body = "text" }, 10))["code"]!.ToString());
        var own = w.AddPull(1);
        var other = w.AddPull(2);
        Assert.Equal("usage", (await w.Call("update-pr", new { item = 1 }, 2))["code"]!.ToString());
        var a = await w.Call("update-pr", new { item = 1, body = "corrected text\n\nRefs #1" });
        Assert.True((bool)a["updated"]!); Assert.Equal(own.Number, (int)a["pullRequest"]!["number"]!);
        Assert.Equal("corrected text\n\nRefs #1", own.Body);   // the reference once
        Assert.Equal("pr-not-own", (await w.Call("update-pr", new { item = 1, body = "text", pr = other.Number }, 10))["code"]!.ToString());
        own.Open = false; own.Merged = true;
        Assert.Equal("no-open-pr", (await w.Call("update-pr", new { item = 1, body = "text" }, 10))["code"]!.ToString());
        w.States[1] = new JsonObject { ["schema"] = 1, ["item"] = 1, ["rev"] = 1, ["pr"] = new JsonObject { ["number"] = own.Number } }; w.StateComments[1] = 4;
        Assert.Equal("pr-not-open", (await w.Call("update-pr", new { item = 1, body = "text" }, 10))["code"]!.ToString());
        var closed = await w.Call("update-pr", new { item = 1, body = "text", pr = own.Number }, 10);
        Assert.Equal("pr-not-open", closed["code"]!.ToString()); Assert.Contains("is merged; only an open pull request is changed", closed["reason"]!.ToString());
        w.Issues[1].Column = "Functional Testing";
        Assert.Equal("wrong-column", (await w.Call("update-pr", new { item = 1, body = "text" }, 10))["code"]!.ToString());
    }

    [Fact]
    public async Task ItemPr_FindsTheOpenPullRequestOfTheItem_AnOwnerTaskIsTheScripts()
    {
        var w = World(1, "Development", "x");
        var none = await w.Call("item-pr", new { item = 1 });
        Assert.False((bool)none["found"]!);
        var pr = w.AddPull(1);
        var a = await w.Call("item-pr", new { item = 1 });
        Assert.True((bool)a["found"]!); Assert.Equal(pr.Number, (int)a["number"]!); Assert.Equal(pr.Head, a["branch"]!.ToString());
        Assert.Empty(w.Log);   // item-pr holds no lock of its own
        w.States[1] = new JsonObject { ["schema"] = 1, ["item"] = 1, ["rev"] = 1, ["copilot_task"] = new JsonObject { ["branch"] = "copilot/x" } }; w.StateComments[1] = 4;
        w.V1Answer = new ItemRawAnswer(0, "{\"ok\":true,\"verb\":\"item-pr\",\"found\":true,\"number\":9,\"branch\":\"copilot/x\",\"exit\":0}");
        Assert.Equal(9, (int)(await w.Call("item-pr", new { item = 1 }))["number"]!);
    }

    // ---- state conflicts -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStateThatChangedSinceItWasRead_IsRefusedAs11_AndNothingIsLost()
    {
        var w = World(1);
        await w.Call("claim-item", new { item = 1, holder = "alice" });
        w.FailNextStateSave = true;
        var a = await w.Call("claim-item", new { item = 1, holder = "alice", renew = true }, 11);
        Assert.Equal("state-conflict", a["code"]!.ToString()); Assert.Equal(1, (int)a["retryAfterSeconds"]!);
    }
}
