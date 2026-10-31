using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Domain;
using Fworker.Persistence;
using Fworker.Workflows.Adoption;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.WorkItem;
using Xunit;
using static Fworker.Workflow.Tests.AdoptionFixture;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.10m: the mapping from v1's state of an item to the saga's, one case per row of the table in docs/design/fworker-wolverine-implementation.md ("W9.10m as built"): the rows are named by their letter and number in
/// the test names (P plan, S step, B budgets, K park, L lease and slot, M merge step, D delivery, Q QC rows, G merged-items row). The state comments are written as v1 writes them.
/// </summary>
public class AdoptionMappingTests
{
    // ---- skipped: not the saga's to hold --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void X1_AClosedIssueIsSkipped() => Assert.Equal("issue-closed", AdoptionMapper.Map(Snapshot(Develop, State(), open: false), Settings(), Now).SkipReason);

    [Fact]
    public void X2_AnItemWithoutAStateCommentIsSkipped_TheIntakeHasNotStartedIt() => Assert.Equal("no-state-comment", Map(Develop, null).SkipReason);

    [Fact]
    public void X3_AMergedItemThatIsSettledIsSkipped() =>
        Assert.Equal("settled", Map(Develop, State(extra: Merged("release-queue", $"\"done_at\": \"{Iso(TimeSpan.FromHours(1))}\""))).SkipReason);

    [Fact]
    public void X4_AnItemOnAHoldColumnWithoutAMergeIsSkipped() => Assert.StartsWith("hold-column", Map(Release, State()).SkipReason);

    [Fact]
    public void X5_AnOpenItemOnTheDoneColumnIsSkipped() => Assert.Equal("card-done", Map(Done, State()).SkipReason);

    [Fact]
    public void X6_AColumnOfNoStepIsSkipped() => Assert.StartsWith("column-of-no-step", Map("Todo", State()).SkipReason);

    [Fact]
    public void X7_AnItemNotOnTheBoardAndWithoutAClaimIsSkipped() => Assert.Equal("not-on-the-board", Map(null, State()).SkipReason);

    [Fact]
    public void X8_AMergedDocsItemIsSkipped_TheDocsPlanEndsAtTheMerge() =>
        Assert.Equal("docs-merged", Map(Verify, State(extra: Join("\"design\": { \"docs_only\": true }", Merged("master-green")))).SkipReason);

    [Fact]
    public void X9_APlanThisEngineDoesNotCarryIsSkipped()
    {
        var only = Settings(p => p with { CarriedPlans = [WorkPlans.Docs] });
        Assert.Equal("plan-not-carried: feature", Map(Develop, State(), only).SkipReason);
    }

    // ---- P: the plan ------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void P1_ADesignThatRecordedDocsOnlyIsTheDocsPlan() =>
        Assert.Equal(WorkPlans.Docs, Mapped(Develop, State(extra: "\"design\": { \"docs_only\": true }")).Plan);

    [Fact]
    public void P2_ADesignThatRecordedNoMarkerIsTheFeaturePlan() =>
        Assert.Equal(WorkPlans.Feature, Mapped(Develop, State(extra: "\"design\": { \"docs_only\": false }")).Plan);

    [Fact]
    public void P3_AnItemWithNoDesignDecisionIsTheFeaturePlan() => Assert.Equal(WorkPlans.Feature, Mapped(Design, State()).Plan);

    [Fact]
    public void P4_ADocsOnlyDesignWhosePullRequestChangesCodeIsTheFeaturePlan()
    {
        var m = Map(Develop, State(extra: "\"design\": { \"docs_only\": true }"), with: s => s with { PullRequest = new AdoptionPullRequest(11, "open", Head, Files: ["docs/a.md", "src/App.cs"]) });
        Assert.Equal(WorkPlans.Feature, m.State!.Plan);
        Assert.Contains(m.Notes, n => n.Contains("not documentation"));
    }

    [Fact]
    public void P5_ADocsOnlyDesignWhosePullRequestChangesDocumentsIsTheDocsPlan() =>
        Assert.Equal(WorkPlans.Docs, Mapped(Develop, State(extra: "\"design\": { \"docs_only\": true }"), with: s => s with { PullRequest = new AdoptionPullRequest(11, "open", Head, Files: ["docs/a.md"]) }).Plan);

    [Fact]
    public void P6_ALegacyItemWithoutADesignDecisionButWithTheMarkerInItsBodyIsTheDocsPlan()
    {
        var legacy = Settings(legacyBefore: Now);   // created two days ago, before the cutoff; past its design column
        Assert.Equal(WorkPlans.Docs, Mapped(Develop, State(), legacy, s => s with { Body = "A fix. <!-- aisf:docs-only -->" }).Plan);
    }

    // ---- S: the step ------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void S1_TheDesignColumnIsTheDesignStep() => Assert.Equal(StepKind.Design, Mapped(Design, State()).Step);

    [Fact]
    public void S2_TheDevelopmentColumnIsTheDevelopStep() => Assert.Equal(StepKind.Develop, Mapped(Develop, State()).Step);

    [Fact]
    public void S3_TheValidationColumnWithoutAPassIsTheVerifyStep() => Assert.Equal(StepKind.Verify, Mapped(Verify, State()).Step);

    [Fact]
    public void S4_AFailVerdictInTheValidationColumnIsStillTheVerifyStep() =>
        Assert.Equal(StepKind.Verify, Mapped(Verify, State(extra: "\"verdict\": { \"passed\": false, \"pr\": 11, \"head_sha\": \"" + Head + "\", \"processed\": false }")).Step);

    [Fact]
    public void S5_APassInTheValidationColumnIsTheMergeStep_WithTheHeadItCovers()
    {
        var s = Mapped(Verify, State(extra: Pass()));
        Assert.Equal(StepKind.Merge, s.Step);
        Assert.Equal((Head, "all criteria met"), (s.VerdictHead, s.VerdictSummary));
    }

    [Fact]
    public void S6_DevelopmentDoneAndWaitingForCi_IsVerifyWithTheChecksPending()
    {
        var awaiting = $$"""
            "awaiting_checks": { "pr": 11, "sha": "{{Head}}", "comment": "Completed by the gateway.", "since": "{{Iso(TimeSpan.FromMinutes(3))}}", "column": "Development" }, {{Park(AdoptionMapper.AwaitingChecksPark)}}
            """;
        var s = Mapped(Develop, State(extra: awaiting));
        Assert.Equal(StepKind.Verify, s.Step);
        Assert.Equal((11, Head, "pending", Head), (s.PullRequest, s.Head, s.ChecksState, s.ChecksHead));
        Assert.False(s.Parked);   // (the gateway's own wait is no park of the item)
    }

    [Fact]
    public void S7_AMergedItemIsTheDeliverStep_WithItsCommitAndPullRequest()
    {
        var s = Mapped(Verify, State(extra: Merged("master-green")));
        Assert.Equal((StepKind.Deliver, WorkItemStatus.Open, Sha, 11, ""), (s.Step, s.Status, s.MergeCommit, s.PullRequest, s.SlotId));
        Assert.Null(s.LeaseDue);
    }

    [Fact]
    public void S8_TheClaimsColumnPlacesAnItemThatIsOffTheBoard() => Assert.Equal(StepKind.Develop, Mapped(null, State(extra: Claim(Develop, true, TimeSpan.FromMinutes(10)))).Step);

    [Fact]
    public void S9_TheRevisionSourceStartAndArmOfTheRowAreTheAdoptions()
    {
        var s = Mapped(Develop, State(rev: 12, extra: $"\"flow\": [ {{ \"c\": \"Technical Design\", \"at\": \"{Iso(TimeSpan.FromHours(3))}\" }} ]"));
        Assert.Equal((1, "1.1", 12, "adopted from v1 (state revision 12)", Now - TimeSpan.FromHours(3)), (s.Generation, s.ArmId, s.CommentRev, s.Source, s.StartedAt));
        Assert.False(s.DesignMarker!.Value);   // (past its design column, with no decision recorded: not docs-only, as the design step's exit leaves it)
    }

    // ---- B: the budgets ---------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void B1_TheReviewRoundsOfTheStepsColumnAreCarried_AndACompleteReviewIsRecorded()
    {
        var reviews = """ "reviews": { "Development": { "round": 2, "last": "COMPLETE", "ref": "rv-abc" }, "Technical Design": { "round": 3, "last": "FINDINGS" } } """;
        var s = Mapped(Develop, State(extra: reviews));
        Assert.Equal(2, s.ReviewRounds["Develop"]);
        Assert.Equal(["Develop"], s.ReviewsComplete);
        Assert.DoesNotContain("Design", s.ReviewRounds.Keys);
    }

    [Fact]
    public void B2_FixForwardRoundsAndDemotionsAreCarried()
    {
        var s = Mapped(Develop, State(extra: "\"fix_forward_rounds\": 2, \"demotions\": 1"));
        Assert.Equal((2, 1), (s.FixForwardRounds, s.Demotions));
    }

    [Theory]
    [InlineData("watchdog-relaunch-budget")]
    [InlineData("session-relaunch-budget")]
    [InlineData("validation-no-verdict")]
    public void B3_ARelaunchBudgetParkIsTheLaunchesOfThePhase(string code)
    {
        var s = Mapped(Develop, State(extra: Park(code, more: "\"launches\": 4, \"session_launches\": 2")));
        Assert.Equal((true, code, 4), (s.Parked, s.ParkReason, s.Launches["Develop"]));
    }

    [Fact]
    public void B4_AWorkerFailureParkIsTheFailureBudgetOfThePhaseSpent()
    {
        var s = Mapped(Develop, State(extra: Park("worker-failure")));
        Assert.Equal(Settings().Policy.Recovery.MaxFailuresPerPhase, s.Failures["Develop"]);
    }

    [Fact]
    public void B5_TheLaunchesOfTheClaimInTheStepsColumnAreTheLaunchesOfThePhase()
    {
        var s = Mapped(Develop, State(extra: Claim(Develop, true, TimeSpan.FromMinutes(10), launches: 2)));
        Assert.Equal(2, s.Launches["Develop"]);
    }

    [Fact]
    public void B6_TheLaunchesOfAClaimInAnotherColumnAreNotThisSteps()
    {
        var s = Mapped(Develop, State(extra: Claim(Design, true, TimeSpan.FromMinutes(-10), launches: 3)));
        Assert.Empty(s.Launches);
    }

    // ---- K: the park ------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void K1_ALiveParkIsTheSagasParkWithItsReasonAndItsEnd()
    {
        var s = Mapped(Develop, State(extra: Park("same-criterion-repeated", TimeSpan.FromHours(5))));
        Assert.Equal((true, "same-criterion-repeated", Now + TimeSpan.FromHours(5)), (s.Parked, s.ParkReason, s.ParkUntil));
        Assert.Null(s.LeaseDue);
        Assert.Empty(s.Outbox);
    }

    [Fact]
    public void K2_AnExpiredParkIsNoPark()
    {
        var s = Mapped(Develop, State(extra: $$"""  "park": { "reason": "same-criterion-repeated", "at": "{{Iso(TimeSpan.FromDays(2))}}", "until": "{{Iso(TimeSpan.FromHours(1))}}" } """));
        Assert.False(s.Parked);
    }

    [Fact]
    public void K3_InspectorEscalatedOnAPassIsTheMergeStepHeld_AndTheOwnerWaitsOutTheIdleTime()
    {
        var s = Mapped(Verify, State(extra: Join(Pass(processed: false), Park("inspector-escalated"))));
        Assert.Equal((StepKind.Merge, true, "inspector-escalated", Head), (s.Step, s.Parked, s.ParkReason, s.VerdictHead));
        Assert.Equal(Now - TimeSpan.FromMinutes(30) + TimeSpan.FromMinutes(60), s.OwnerIdleDue);
        Assert.Null(s.MergeRequest);
    }

    [Fact]
    public void K4_PassWithoutAutoMergeIsTheMergeStepParkedForAPerson_WithNoIdleRelease()
    {
        var s = Mapped(Verify, State(extra: Join(Pass(processed: true), Park("pass-without-auto-merge"))));
        Assert.Equal((StepKind.Merge, true, "pass-without-auto-merge"), (s.Step, s.Parked, s.ParkReason));
        Assert.Null(s.OwnerIdleDue);
    }

    [Fact]
    public void K5_TheParkAlwaysEndsWhereTheSweepsParksEnd_WithoutTheSweepNothingExpires()
    {
        var core = Settings(p => p with { Sweep = null });
        var s = Mapped(Develop, State(extra: Park("same-criterion-repeated")), core);
        Assert.True(s.Parked);
        Assert.Null(s.ParkUntil);
    }

    [Fact]
    public void K6_AMasterBrokenParkOfAMergedItemKeepsTheFailedChecksAndTheWait()
    {
        var s = Mapped(Verify, State(extra: Join(Merged("master-broken", $"\"broken_at\": \"{Iso(TimeSpan.FromMinutes(5))}\""), Park("master-broken", more: "\"failed\": [\"Build result\", \"secret-scan\"], \"sha\": \"" + Sha + "\""))));
        Assert.Equal((StepKind.Deliver, "broken", true, "master-broken"), (s.Step, s.MasterState, s.Parked, s.ParkReason));
        Assert.Equal(["Build result", "secret-scan"], s.MasterFailed);
    }

    [Fact]
    public void K7_AMasterGreenTimeoutParkOfAMergedItemIsTheTimeoutWait()
    {
        var s = Mapped(Verify, State(extra: Join(Merged("master-timeout"), Park("master-green-timeout"))));
        Assert.Equal(("timeout", true, "master-green-timeout"), (s.MasterState, s.Parked, s.ParkReason));
    }

    [Fact]
    public void K8_AMasterBrokenWaitWhosePark_HasExpiredIsStillWatched_NotParked()
    {
        var s = Mapped(Verify, State(extra: Merged("master-broken")));
        Assert.Equal(("broken", false), (s.MasterState, s.Parked));
    }

    // ---- L: the claim, the lease and the slot -----------------------------------------------------------------------------------------------------------------

    // Cutover review: the adoption comes before the first sweep's gap renewal (Invoke-GapRenew, issue 94), so an owner's claim that ran out while no gateway was up is live for it, with a fresh lease, as the renewal makes it
    [Fact]
    public void L0_AnOwnersClaimThatRanOutInTheGap_IsAdoptedWithItsSession_AClaimThatRanOutBeforeItIsNot()
    {
        var gap = (Now - TimeSpan.FromMinutes(12), Now);
        var lapsed = AdoptionMapper.Map(Snapshot(Develop, State(extra: Claim(Develop, true, TimeSpan.FromMinutes(-5)))), Settings(), Now, gap: gap);
        var s = lapsed.State!;
        Assert.True(s.SlotKnown); Assert.True(s.EverGranted); Assert.False(s.SlotAsked);
        Assert.NotNull(lapsed.Slot);
        Assert.True(s.LeaseDue > Now);                 // (renewed, not the lease that ran out)
        Assert.Empty(s.Outbox);                        // (no request for another session)

        var before = AdoptionMapper.Map(Snapshot(Develop, State(extra: Claim(Develop, true, TimeSpan.FromMinutes(-40)))), Settings(), Now, gap: gap);
        Assert.True(before.State!.SlotAsked);          // (it lapsed before the gap: dead as ever, the item asks for a session)
        var none = AdoptionMapper.Map(Snapshot(Develop, State(extra: Claim(Develop, true, TimeSpan.FromMinutes(-5)))), Settings(), Now);
        Assert.True(none.State!.SlotAsked);            // (no gap noted: nothing renewed)
    }

    [Fact]
    public void L1_ALiveOwnersClaimIsTheStepsLease_AndASlotInTheLedger()
    {
        var m = Map(Develop, State(extra: Claim(Develop, true, TimeSpan.FromMinutes(25))));
        var s = m.State!;
        Assert.Equal(Now + TimeSpan.FromMinutes(25), s.LeaseDue);
        Assert.Equal((AdoptionMapper.AdoptedSlotId(Item.ToString(), 1), "cloud", true, true, false), (s.SlotId, s.SlotRuntime, s.EverGranted, s.SlotKnown, s.SlotAsked));
        var slot = m.Slot!;
        Assert.Equal((s.SlotId, "develop", Now - TimeSpan.FromMinutes(20), Now + TimeSpan.FromMinutes(25)), (slot.SlotId, slot.Phase, slot.GrantedAt, slot.ExpiresAt));
        Assert.Empty(s.Outbox);   // (no request: the session is live)
    }

    [Fact]
    public void L2_ASlotTheLedgerHoldsForTheItemAlreadyIsTheSagasSlot()
    {
        var held = new Fworker.Workflows.Capacity.LedgerSlot { SlotId = "s9", Item = Item.ToString(), Runtime = "local", Host = "box1" };
        var m = Map(Develop, State(extra: Claim(Develop, true, TimeSpan.FromMinutes(25))), with: s => s with { HeldSlot = held });
        Assert.Equal(("s9", "local", "box1"), (m.State!.SlotId, m.State.SlotRuntime, m.State.SlotHost));
        Assert.Null(m.Slot);
    }

    [Fact]
    public void L3_ALiveClaimOfASessionThatIsNoOwnerIsTheLeaseOnly()
    {
        var m = Map(Develop, State(extra: Claim(Develop, false, TimeSpan.FromMinutes(25))));
        Assert.Equal((Now + TimeSpan.FromMinutes(25), "", false), (m.State!.LeaseDue, m.State.SlotId, m.State.SlotAsked));
        Assert.Null(m.Slot);
    }

    [Fact]
    public void L4_AnExpiredClaimMeansTheItemAsksAgainAsAnOwnedItem()
    {
        var s = Mapped(Develop, State(extra: Claim(Develop, true, TimeSpan.FromMinutes(-5))));
        var ask = Assert.IsType<AskSlot>(Assert.Single(s.Outbox));
        Assert.Equal((true, true, true, null, true), (s.SlotAsked, s.SlotKnown, ask.Resume, s.LeaseDue, s.EverGranted));
        Assert.Equal(("develop", 2, s.ArmId, $"ask|{Item}|1|1"), (ask.Phase, ask.ColumnDepth, ask.ArmId, ask.Key));
    }

    [Fact]
    public void L5_AnItemNeverClaimedAsksForTheFirstTime()
    {
        var s = Mapped(Design, State());
        var ask = Assert.IsType<AskSlot>(Assert.Single(s.Outbox));
        Assert.Equal(("design", 1, false, false), (ask.Phase, ask.ColumnDepth, ask.Resume, s.EverGranted));
        Assert.Null(s.LeaseDue);
    }

    [Fact]
    public void L6_TheDocsPlanHasNoSlots_ItsLeaseIsTheClaimsOrTheStepsOwn()
    {
        var design = "\"design\": { \"docs_only\": true }";
        var live = Mapped(Develop, State(extra: Join(design, Claim(Develop, true, TimeSpan.FromMinutes(25)))));
        Assert.Equal((Now + TimeSpan.FromMinutes(25), "", false), (live.LeaseDue, live.SlotId, live.SlotAsked));
        var none = Mapped(Develop, State(extra: design));
        Assert.Equal(Now + TimeSpan.FromMinutes(120), none.LeaseDue);   // (a new arm's lease)
        Assert.Empty(none.Outbox);
    }

    [Fact]
    public void L7_AParkedItemHoldsNoLeaseAndNoSlot()
    {
        var s = Mapped(Develop, State(extra: Join(Claim(Develop, true, TimeSpan.FromMinutes(25)), Park("same-criterion-repeated"))));
        Assert.Equal((null, "", false, false), (s.LeaseDue, s.SlotId, s.SlotAsked, s.SlotKnown));
    }

    [Fact]
    public void L8_TheFootprintIsThePullRequestsFilesWhenTheyWereRead()
    {
        var s = Mapped(Develop, State(), with: x => x with { PullRequest = new AdoptionPullRequest(11, "open", Head, Files: ["src/A.cs", "src/B.cs"]) });
        Assert.Equal(["src/A.cs", "src/B.cs"], s.Footprint);
        Assert.True(s.FootprintFromPullRequest);
        Assert.Equal(["src/A.cs", "src/B.cs"], Assert.IsType<AskSlot>(Assert.Single(s.Outbox)).Footprint);
    }

    // ---- M: the Merge step ------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void M1_APassOnTheDocsPlanAsksForItsMergeAtOnce_AndIsInTheMergeLine()
    {
        var m = Map(Verify, State(extra: Join("\"design\": { \"docs_only\": true }", Pass())));
        var s = m.State!;
        Assert.Equal((StepKind.Merge, 1, 11, Head, true), (s.Step, s.MergeRequest!.Attempt, s.MergeRequest.PullRequest, s.MergeRequest.Head, s.TrainEntered));
        Assert.Equal(Now + TimeSpan.FromMinutes(30), s.LeaseDue);
        var asked = Assert.IsType<MergeRequested>(Assert.Single(WorkItemSaga.CommandsOf(s)));
        Assert.Equal((s.ArmId, Head), (asked.ArmId, asked.Head));
    }

    [Fact]
    public void M2_APassOnTheFeaturePlanWaitsForGreenChecksOfItsHead()
    {
        var s = Mapped(Verify, State(extra: Pass()));
        Assert.Equal((true, null), (s.MergeAwaitingChecks, s.MergeRequest));
        Assert.Empty(WorkItemSaga.CommandsOf(s));
    }

    [Fact]
    public void M3_AFeaturePassIsAmongTheItemsInFlight_ForTheFileOverlapRule()
    {
        var m = Map(Verify, State(extra: Pass()), with: x => x with { PullRequest = new AdoptionPullRequest(11, "open", Head, Files: ["src/A.cs"]) });
        Assert.True(m.State!.SlotKnown);
        Assert.Equal(["src/A.cs"], m.Flight!.Footprint);
        Assert.Null(m.Slot);
    }

    [Fact]
    public void M4_AParkedPassIsNotRequestedAndNotInFlight()
    {
        var m = Map(Verify, State(extra: Join(Pass(), Park("inspector-escalated"))));
        Assert.Equal((false, false, null), (m.State!.SlotKnown, m.State.TrainEntered, m.Flight));
    }

    // ---- D: the delivery --------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void D1_AWaitForMasterIsCountedFromTheMerge()
    {
        var s = Mapped(Verify, State(extra: Merged("master-green")));
        Assert.Equal(("waiting", Now - TimeSpan.FromMinutes(10), Now - TimeSpan.FromMinutes(10) + TimeSpan.FromMinutes(30)), (s.MasterState, s.MasterSince, s.MasterDue));
        Assert.Contains(WorkItemSaga.TimersOf(s), t => t.Message is MasterWatchElapsed);
    }

    [Fact]
    public void D2_AReRunOfTheFailedJobsCountsTheWaitAgainFromTheReRun()
    {
        var rerun = $"\"rerun_attempted\": true, \"rerun_at\": \"{Iso(TimeSpan.FromMinutes(2))}\", \"rerun_runs\": [ {{ \"id\": 99, \"attempt\": 1 }} ]";
        var s = Mapped(Verify, State(extra: Merged("master-green", rerun)));
        Assert.Equal((Now - TimeSpan.FromMinutes(2), Now - TimeSpan.FromMinutes(2) + TimeSpan.FromMinutes(30), 99L, 1), (s.MasterSince, s.MasterDue, s.MasterRerunRuns[0].Id, s.MasterRerunRuns[0].Attempt));
        Assert.Equal(Now - TimeSpan.FromMinutes(2), s.MasterRerunAt);
    }

    [Theory]
    [InlineData("release-candidate")]
    [InlineData("late-rc")]
    [InlineData("release-queue")]
    public void D3_AMergedItemPastMastersCiIsGreen_AndWaitsForItsRelease(string awaiting)
    {
        var s = Mapped(Verify, State(extra: Merged(awaiting, $"\"green_at\": \"{Iso(TimeSpan.FromMinutes(5))}\", \"green_via\": \"{new string('c', 40)}\"")));
        Assert.Equal(("green", new string('c', 40), null), (s.MasterState, s.MasterGreenVia, s.MasterDue));
        Assert.Empty(s.Delivered);
    }

    [Fact]
    public void D4_ADocsPlanWatchesNoMaster()
    {
        var s = Mapped(Verify, State(extra: Merged("master-green")), Settings(p => p with { Sweep = p.Sweep! with { MasterWatch = false } }));
        Assert.Equal("", s.MasterState);
    }

    static TrainState Train(string environment, string release, string version = "1.0.7", Action<TrainState>? more = null)
    {
        var t = TrainState.New($"cmdemo1-ui/{environment}");
        t.Releases[version] = new TrainRelease { ReleaseId = "R1", Version = version, Commit = Sha, Assembled = Now.AddHours(-1) };
        t.Deployments[version] = new() { [environment] = new TrainDeployment { Phase = "Succeeded", StartedAt = Now.AddMinutes(-40), FinishedAt = Now.AddMinutes(-30) } };
        t.Current = release;
        more?.Invoke(t);
        return t;
    }

    [Fact]
    public void D5_TheTrainThatDeliveredTheItemsMerge_NamesTheReleaseItIsDeliveredIn()
    {
        var train = Train("uat", "1.0.7", more: t => { t.DeliveredItems.Add($"{Item}|{Sha}"); t.DeliveredBy["1.0.7"] = [new TrainCandidate { Item = Item.ToString(), Commit = Sha }]; });
        var s = Mapped(Verify, State(extra: Merged("release-queue", "\"release\": \"1.0.7\"")), with: x => x with { Trains = [train] });
        Assert.Equal("1.0.7", s.Delivered["uat"]);
        Assert.Equal((WorkItemStatus.Open, null), (s.Status, s.SettleUntil));   // (uat is not the final environment)
    }

    [Fact]
    public void D6_TheTrainsCurrentReleaseIsTheFallbackWhenItKeepsNoRecordOfWhatItCarried()
    {
        var train = Train("uat", "1.0.7", more: t => t.DeliveredItems.Add($"{Item}|{Sha}"));
        var s = Mapped(Verify, State(extra: Merged("release-queue")), with: x => x with { Trains = [train] });
        Assert.Equal("1.0.7", s.Delivered["uat"]);
    }

    [Fact]
    public void D7_TheReleaseV1QueuedTheItemForCountsWhenItSucceededOrWasSupersededByOneThatDid()
    {
        var train = Train("uat", "1.0.9", "1.0.9", t => t.Superseded["1.0.7"] = "1.0.9");
        var s = Mapped(Verify, State(extra: Merged("release-queue", "\"release\": \"1.0.7\"")), with: x => x with { Trains = [train] });
        Assert.Equal("1.0.9", s.Delivered["uat"]);
    }

    [Fact]
    public void D8_AReleaseThatFailedOrWasRolledBackIsNoDelivery()
    {
        var failed = Train("uat", "1.0.7", more: t => t.Deployments["1.0.7"]["uat"].Phase = "Failed");
        var rolled = Train("tdd", "1.0.7", more: t => t.Deployments["1.0.7"]["tdd"].Rollback = true);
        var s = Mapped(Verify, State(extra: Merged("release-queue", "\"release\": \"1.0.7\"")), with: x => x with { Trains = [failed, rolled] });
        Assert.Empty(s.Delivered);
    }

    [Fact]
    public void D9_DeliveredToTheFinalEnvironmentStartsTheSettleWindowFromTheDeployment()
    {
        var train = Train("tdd", "1.0.7", more: t => t.DeliveredItems.Add($"{Item}|{Sha}"));
        var m = Map(Verify, State(extra: Merged("release-queue", "\"release\": \"1.0.7\"")), with: x => x with { Trains = [train] });
        var s = m.State!;
        Assert.Equal((WorkItemStatus.Settling, Now - TimeSpan.FromMinutes(30) + TimeSpan.FromHours(24), "1.0.7", "1.2"), (s.Status, s.SettleUntil, s.Delivered["tdd"], s.ArmId));
        Assert.Contains(WorkItemSaga.TimersOf(s), t => t.Message is SettleWindowElapsed);
    }

    [Fact]
    public void D10_DeliveredAndPastItsSettleWindowIsSkipped()
    {
        var train = Train("tdd", "1.0.7", more: t => { t.DeliveredItems.Add($"{Item}|{Sha}"); t.Deployments["1.0.7"]["tdd"].FinishedAt = Now.AddDays(-2); });
        var m = Map(Verify, State(extra: Merged("release-queue", "\"release\": \"1.0.7\"")), with: x => x with { Trains = [train] });
        Assert.Equal("delivered-and-settled", m.SkipReason);
    }

    // ---- Q and G: the rows beside the state ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Q1_TheRecordsOfTheQcMapAreRowsOfTheTableOfEnvironments()
    {
        var qc = """ "qc": { "11": { "pr": 11, "state": "ready", "sha": "abc", "attempt": 1 }, "bad": { "pr": 0 }, "12": null } """;
        var m = Map(Verify, State(extra: qc));
        var row = Assert.Single(m.QcRows);
        Assert.Equal(("acme/orders!11", "acme/orders", Item.ToString(), "ready"), (row.Key, row.Repository, row.ItemKey, row.Record["state"]!.ToString()));
    }

    [Fact]
    public void G1_AMergedItemHasARowInTheMergedItemsTable_WithTheGatesItsLabelsAndVerdictShow()
    {
        var m = Map(Verify, State(extra: Join(Pass(processed: true), Merged("master-green", "\"by\": \"gateway\""))), with: x => x with { Body = "Depends on #3" });
        var row = m.MergedRow!;
        Assert.Equal((Item.ToString(), Sha, 11, "adopted", Now - TimeSpan.FromMinutes(10)), (row.ItemKey, row.Commit, row.PullRequest, row.Source, row.MergedAt));
        Assert.Contains("\"AutoMerge\":true", row.GatesJson);
        Assert.Contains("\"PassMerged\":true", row.GatesJson);
        Assert.Equal("""["acme/orders#3"]""", row.DependenciesJson);
    }

    [Fact]
    public void G2_TheRowsMessageIdIsFixedByTheItemAndTheCommit_SoAdoptingAgainRecordsItOnce()
    {
        var a = Map(Verify, State(extra: Merged("master-green"))).MergedRow!;
        var b = Map(Verify, State(rev: 9, extra: Merged("master-green"))).MergedRow!;
        Assert.Equal(a.MessageId, b.MessageId);
        Assert.NotEqual(a.MessageId, AdoptionMapper.AdoptionMessageId(Item.ToString(), new string('f', 40)));
    }

    [Fact]
    public void G3_AnItemThatIsNotMergedHasNoRow() => Assert.Null(Map(Verify, State()).MergedRow);

    // ---- the mapping is a function of its inputs --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSameSnapshotMapsToTheSameRow()
    {
        var state = State(extra: Join(Claim(Develop, true, TimeSpan.FromMinutes(25)), "\"fix_forward_rounds\": 1"));
        Assert.Equal(Mapped(Develop, state).ToJson(), Mapped(Develop, state).ToJson());
    }

    [Fact]
    public void ATextNumberAndANullKeyAreRead_AsPowerShellWritesThem()
    {
        var s = Mapped(Develop, State(extra: "\"fix_forward_rounds\": \"2\", \"demotions\": null, \"verdict\": null, \"claim\": null"));
        Assert.Equal((2, 0), (s.FixForwardRounds, s.Demotions));
    }
}
