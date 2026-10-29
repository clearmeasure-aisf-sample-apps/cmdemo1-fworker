using Fworker.Contracts.Ids;
using Fworker.Domain;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflow.Tests;

/// <summary>The docs-only port of the tests: the design marker decides, and the answers about files and the merge are the test's.</summary>
sealed class FakeDocsPort : IDocsOnlyPort
{
    public bool? Files;
    public MergeDocsOnlyVerdict? Merge;
    public bool ItemIsDocsOnly(bool designMarker) => designMarker;
    public bool? FilesAreDocumentation(IReadOnlyList<string> files) => Files;
    public MergeDocsOnlyVerdict? MergeIsDocsOnly(IReadOnlyList<BuildCheck> checks, IReadOnlyList<string>? pullRequestFiles) => Merge;
}

/// <summary>The saga on an in-memory store with a clock the test moves (W6.1): sends an input like the handler does and keeps every decision and timer it asked for.</summary>
sealed class SagaFixture
{
    public static readonly ItemId Item = new("acme", "orders", 7);
    public static readonly DateTimeOffset T0 = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    public readonly InMemoryWorkItemStore Store = new();
    public readonly FakeDocsPort Docs = new();
    public readonly WorkItemSaga Saga;
    public readonly WorkItemService Svc;
    public DateTimeOffset Now = T0;
    public readonly List<WorkItemDecided> Decisions = [];
    public readonly List<WorkItemTimer> Timers = [];
    public readonly List<object> Commands = [];

    public SagaFixture(WorkItemPolicy? policy = null)
    {
        Saga = new WorkItemSaga(Docs, policy);
        Svc = new WorkItemService(Store, Saga, () => Now);
    }

    public string Key => Item.ToString();
    public WorkItemState State => Store.States[Key];
    public string Arm => State.ArmId;

    public async Task<WorkItemResult> Send(WorkItemInput input)
    {
        var r = await Svc.Handle(input);
        Decisions.AddRange(r.Decisions);
        Timers.AddRange(r.Timers);
        Commands.AddRange(r.Sent);
        return r;
    }

    public IEnumerable<string> Actions => Decisions.Select(d => d.Action);

    public Task<WorkItemResult> Start(string? plan = null, bool hotfix = false) => Send(new StartWorkItem(Item, "intake", plan, hotfix));
    public Task<WorkItemResult> Done() => Send(new StepCompleted(Item, State.Generation, State.ArmId, $"done|{State.ArmId}"));
    int keys;
    public Task<WorkItemResult> Design(bool marker, string? key = null) => Send(new DesignRecorded(Item, State.Generation, State.ArmId, key ?? $"design|{State.ArmId}|{marker}|{++keys}", marker));
    public Task<WorkItemResult> Budget(BudgetKind kind, string? key = null) => Send(new BudgetSpent(Item, State.Generation, State.ArmId, key ?? $"budget|{Guid.NewGuid():N}", kind, "test"));
    public Task<WorkItemResult> Merged(string commit = "abcdef0123456789abcdef0123456789abcdef01") => Send(new MergeSeen(Item, $"merged|{commit}", commit, 11));
    public Task<WorkItemResult> Delivered(string env, string release = "1.0.5") => Send(new DeliverySeen(Item, $"delivered|{env}|{release}", env, release));
    public Task<WorkItemResult> Lost(string env, string key = "lost-1") => Send(new DeliveryLost(Item, key, env));

    public Task<WorkItemResult> Review(bool complete = true) => Send(new ReviewRecorded(Item, State.Generation, State.ArmId, $"review|{State.ArmId}|{++keys}", complete));
    public Task<WorkItemResult> PullRequest(string head = "aaa1111", int pr = 11, params string[] files) => Send(new PullRequestChanged(Item, 0, $"pr|{pr}|{head}|{++keys}", pr, head, files.Length == 0 ? ["docs/a.md"] : files));
    public Task<WorkItemResult> Verdict(bool passed, string head = "aaa1111", int pr = 11, string summary = "ok") => Send(new VerdictRecorded(Item, State.Generation, State.ArmId, $"verdict|{State.ArmId}|{++keys}", passed, pr, head, summary));
    public Task<WorkItemResult> Attempted(MergeResult result) => Send(new MergeAttempted(Item, State.Generation, State.ArmId, $"merge-attempt|{State.ArmId}|{State.MergeRequest!.Attempt}", State.MergeRequest.Attempt, result));

    /// <summary>Moves the item one step along its plan, doing what the docs plan asks first (a COMPLETE review, a pull request, a PASS on its head).</summary>
    public async Task Advance()
    {
        if (State.Plan == WorkPlans.Docs)
            switch (State.Step)
            {
                case StepKind.Design: await Review(); await Done(); return;
                case StepKind.Develop: await PullRequest(); await Review(); await Done(); return;
                case StepKind.Verify: await Verdict(true); return;
            }
        await Done();
    }

    /// <summary>Walks a started item through the steps up to (not including) <paramref name="until"/>.</summary>
    public async Task WalkTo(StepKind until)
    {
        for (var i = 0; State.Step != until && State.IsOpen; i++)
        {
            var was = (State.Step, State.ArmId);
            await Advance();
            if (i > 8 || (State.Step, State.ArmId) == was) throw new InvalidOperationException($"the item does not move on from {State.Step}");
        }
    }
}
