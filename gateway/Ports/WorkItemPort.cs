using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Persistence;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Intake;
using Fworker.Workflows.Merge;
using Fworker.Workflows.WorkItem;
using Microsoft.EntityFrameworkCore;
using Wolverine;

// W6.0 and W6.1 (docs/design/fworker-wolverine-implementation.md W6): the WorkItemSaga in the Wolverine host, in the hybrid composition the owner decided on (2026-10-28). Only where the engine acts
// (Engine.V2Acts; tdd stays in the shadow run, so nothing of this exists there or in v1): v1's plant still offers the sessions and runs every verb the v2 engine does not own yet, v2's saga decides the items it
// has started (the docs-only items the intake gave it), and v1's sweep leaves those items alone. The second half is V2Ownership and SweepV2Filter: the sweep's answer is read as before, and everything it says about
// an item that has a saga (an offer, a trigger, a hold, a park, an owner) is dropped before the plant sees it, so the plant offers no session for it and notifies no owner of it. W9.10e passes the owned set into the script as well (sync-board `skip_items`: the item loop and the claim-ref reap skip those items, so v1
// settles, merges, parks and demotes none of them), and until the first read of the table (V2Ownership.Loaded) the sweeper runs no pass at all.
// W6.6 adds what makes it run (Ports/DocsPlanPort.cs: the merge, the card, the intake of docs items; Verbs/V2VerbFeed.cs: the verbs' events into the saga; the offers below), still only where the engine acts, and with
// intake.v2Plans empty in config/instance.json nothing starts a saga until the owner names the docs plan.

/// <summary>
/// The items of the home repository that have a saga which has not completed, as the numbers v1's sweep knows them by. Read from the saga's table before each sweep (a point query by status,
/// bounded in time); a read that fails keeps the last answer, because a v2 item must not be taken by v1 for the length of an outage.
/// </summary>
sealed class V2Ownership(IWorkItemStore store, RepoId home, Func<StepKind, string?>? columnOf = null)
{
    volatile HashSet<int> owned = [];
    volatile IReadOnlyList<(int Item, string Column)> offers = [];
    volatile bool loaded;
    public int Count => owned.Count;

    /// <summary>
    /// W9.10e: whether the saga's table has been read successfully at least once (or the set was given). Until then nobody knows which items are v2's, so the sweep acts on no item at all and incremental passes wait
    /// (Sweeper): an empty set that only means "not read yet" would let v1 settle, merge or park an item that has a saga. After the first read a failed read keeps the last answer, as before.
    /// </summary>
    public bool Loaded => loaded;

    public bool Owns(int item) => owned.Contains(item);

    public IReadOnlyCollection<int> Items => owned;

    /// <summary>
    /// What the plant is offered for the saga's items (W6.6): an open, unparked item on a step that a session works, in that step's column. The sweep's own offers for these items are dropped (SweepV2Filter), so
    /// the saga is what says that an item waits for a session; the sessions and the plant that places them are v1's. Empty without the columns of the docs plan.
    /// </summary>
    public IReadOnlyList<(int Item, string Column)> Offers => offers;

    /// <summary>
    /// The offers as the sweep prints them, for the items <paramref name="hasOwner"/> says no session holds: an item with an owner is not offered again (the plant would take the offer for a session that is gone and drop the
    /// owner), it goes on with its owner from column to column.
    /// </summary>
    public List<JsonObject> OfferNodes(Func<int, bool> hasOwner, IReadOnlyCollection<int>? only = null)
    {
        var nodes = new List<JsonObject>();
        foreach (var (item, column) in offers)
            if (!hasOwner(item) && (only is null || only.Contains(item)))
                nodes.Add(new JsonObject { ["item"] = item, ["column"] = column, ["reason"] = "handoff", ["v2"] = true });
        return nodes;
    }

    /// <summary>Reads the owned items; false when the read failed and the last answer stands.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(TimeSpan.FromSeconds(10));
            var states = await store.OwnedStates(limit.Token);
            var set = new HashSet<int>(); var wanted = new List<(int, string)>();
            foreach (var state in states)
            {
                if (!ItemId.TryParse(state.Item, out var id) || id.Repository != home) continue;
                set.Add(id.Number);
                // (an item that asked the scheduler for a slot (W7.3) is offered only once it holds one: a hold, a full runtime or a runtime that is off keeps the session from being offered)
                if (state.Status == WorkItemStatus.Open && !state.Parked && !state.SlotAsked && state.Step is StepKind.Design or StepKind.Develop or StepKind.Verify && columnOf?.Invoke(state.Step) is { Length: > 0 } column)
                    wanted.Add((id.Number, column));
            }
            owned = set; offers = wanted; loaded = true;
            return true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"v2 ownership: {e.GetType().Name}; the last answer stands ({owned.Count} items)");
            return false;
        }
    }

    /// <summary>For a test or a host without a database: the owned items as given.</summary>
    public void Set(IEnumerable<int> items) { owned = items.ToHashSet(); loaded = true; }
}

static class SweepV2Filter
{
    // arrays of objects with an "item", arrays of numbers, and objects keyed by the number of the item
    static readonly string[] ObjectLists = ["offers", "triggers", "held", "parks", "pass_unmerged", "flags", "release_queue"];
    static readonly string[] NumberLists = ["unconfirmed", "unparked", "holds", "settling"];
    static readonly string[] KeyedByNumber = ["owners", "owned", "pins", "priorities", "runtimes", "profiles"];

    /// <summary>Removes from the sweep's answer everything it says about the items <paramref name="owns"/> names; returns how many entries it removed. The answer's own log of what the script did (actions, errors) is left as it is.</summary>
    public static int Apply(JsonNode sweep, Func<int, bool> owns)
    {
        if (sweep is not JsonObject o) return 0;
        var removed = 0;
        foreach (var name in ObjectLists)
            if (o[name] is JsonArray a) removed += RemoveWhere(a, x => x is JsonObject xo && Number(xo["item"]) is { } n && owns(n));
        foreach (var name in NumberLists)
            if (o[name] is JsonArray a) removed += RemoveWhere(a, x => Number(x) is { } n && owns(n));
        foreach (var name in KeyedByNumber)
            if (o[name] is JsonObject d)
                foreach (var key in d.Select(kv => kv.Key).ToList())
                    if (int.TryParse(key, out var n) && owns(n)) { d.Remove(key); removed++; }
        return removed;
    }

    static int? Number(JsonNode? n) => n is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<string>(out var s) && int.TryParse(s, out i))) ? i : null;

    static int RemoveWhere(JsonArray a, Func<JsonNode?, bool> drop)
    {
        var n = 0;
        for (var i = a.Count - 1; i >= 0; i--) if (drop(a[i])) { a.RemoveAt(i); n++; }
        return n;
    }
}

/// <summary>What the gateway builds for the saga: its service, the history writer, the ownership the sweep reads and the intake settings.</summary>
sealed record WorkItemParts(WorkItemService Service, WorkItemHistory History, IWorkItemStore Store, V2Ownership? Ownership, IntakeSettings Intake, DocsPlanParts Docs, MergeTrainParts Merge, FeaturePlanParts Feature);

/// <summary>The feature plan's ports (W7.3, Ports/FeaturePlanPort.cs): the slot request the saga's SlotWanted becomes, the notice of a fix-forward round to the owner session, and the count of the merge queue.</summary>
sealed record FeaturePlanParts(SlotRequestPort Slots, FeatureNotice Notice, MergeQueueCount Queue, SlotRequestSettings SlotSettings);

/// <summary>The merge train (W7.2): its service on the database of the host, the history writer of its decisions and the settings it runs with (<c>mergeStrategy</c>: direct or train).</summary>
sealed record MergeTrainParts(MergeTrainService Service, MergeTrainHistory History, MergeTrainSettings Settings);

/// <summary>The merge train's settings from config/instance.json: <c>mergeStrategy</c> (direct, train) and <c>timing.mergeTrainEntryExpirySeconds</c> / <c>timing.mergeTrainHeadPatienceSeconds</c> / <c>timing.mergeTrainWaitSeconds</c> (0: the saga's own pause).</summary>
static class MergeTrainSettingsReader
{
    public static MergeTrainSettings From(JsonNode? root, Action<string>? warn = null)
    {
        string? name = null; JsonNode? timing = null;
        try { name = root?["mergeStrategy"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null; } catch { }
        try { timing = root?["timing"]; } catch { }
        var (strategy, warning) = MergeTrainSettings.ParseStrategy(name);
        if (warning is not null) warn?.Invoke(warning);
        return new MergeTrainSettings(strategy, Seconds(timing, "mergeTrainEntryExpirySeconds"), Seconds(timing, "mergeTrainHeadPatienceSeconds"), Seconds(timing, "mergeTrainWaitSeconds"));
    }

    static TimeSpan? Seconds(JsonNode? n, string name)
    {
        try { return n?[name] is JsonValue v && v.TryGetValue<int>(out var i) && i > 0 ? TimeSpan.FromSeconds(i) : null; } catch { return null; }
    }
}

/// <summary>What the docs plan needs of GitHub (W6.6). Every reader is null without a credential (GH_TOKEN or the author App): the merge then answers "merge-unavailable" (a wait), the card stays where it is, nothing is read for the intake.</summary>
sealed record DocsPlanParts(DocsPlanSettings Settings, DocsMergePort Merge, DocsProjection Projection, IMergeFactsReader? Facts, IBoardReader? Boards, PullRequestHeads? Heads, Func<BoardRef?> Board)
{
    /// <summary>The intake job; null when it cannot read the board and the items. The caller brings the plant's facts (free slots, whether a design waits) and the lease.</summary>
    public DocsIntakeJob? IntakeJob(WorkItemIntake intake, RepoId home, IReadOnlyList<string> aiColumns, Func<int> freeSlots, Func<bool> designWaiting, Func<bool> mayAct, Func<bool> budgetLow) =>
        Boards is null || Facts is null ? null : new DocsIntakeJob(Boards, Facts, intake, Board, home, Settings, aiColumns, freeSlots, designWaiting, mayAct, budgetLow);
}

/// <summary>The settings of D4's docs-only test (Fworker.Domain.DocsOnly), read from the configuration the rule scripts read: docsOnlyPaths, docsOnlyMarker, timing.docsOnlyLegacyBefore, requiredBodySection, videoLabel, masterCodeChecks.</summary>
static class DocsOnlySettingsReader
{
    public static DocsOnlySettings From(JsonNode? root)
    {
        JsonNode? timing = null; try { timing = root?["timing"]; } catch { }
        DateTimeOffset? legacy = null;
        try { if (timing?["docsOnlyLegacyBefore"] is JsonValue v && v.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)) legacy = at; } catch { }
        var columns = new List<string>();
        try { if (root?["requiredBodySection"] is JsonObject sections) columns.AddRange(sections.Select(kv => kv.Key)); } catch { }
        return new DocsOnlySettings(Strings(root, "docsOnlyPaths"), Text(root, "docsOnlyMarker") ?? "<!-- aisf:docs-only -->", legacy, columns, Text(root, "videoLabel") ?? "video", Strings(root, "masterCodeChecks"));
    }

    static string? Text(JsonNode? n, string name) { try { return n?[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null; } catch { return null; } }

    static List<string> Strings(JsonNode? n, string name)
    {
        var l = new List<string>();
        try { if (n?[name] is JsonArray a) foreach (var x in a) if (x is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) l.Add(s); } catch { }
        return l;
    }
}

static class WorkItemWiring
{
    /// <summary>The saga's parts on the database of the host. The docs-only test is D4's, with the settings of the configuration; the docs plan's rules are those of <see cref="DocsPlanSettingsReader"/>.</summary>
    public static WorkItemParts Make(GatewayComposition c, string connectionString, IDocsOnlyPort? docs = null, Func<BoardRef?>? boardRef = null, Func<string, string?>? env = null, HttpClient? http = null, Func<int, JsonObject, bool>? route = null)
    {
        Func<FactoryDbContext> open = () => Open(connectionString);
        var root = c.Instance.Root;
        var settings = DocsPlanSettingsReader.From(root);
        var store = new SqlWorkItemStore(open);
        var service = new WorkItemService(store, new WorkItemSaga(docs ?? new DomainDocsOnlyPort(DocsOnlySettingsReader.From(root)), settings.Policy));
        var ownership = c.Instance.Home is { } home ? new V2Ownership(store, home, settings.ColumnOf) : null;
        var train = MergeTrainSettingsReader.From(root, w => Console.Error.WriteLine("merge train: " + w));
        var merge = new MergeTrainParts(new MergeTrainService(new SqlMergeTrainStore(open), train), new MergeTrainHistory(open), train);
        var queue = new MergeQueueCount(store);
        var docsParts = MakeDocsPlan(c, settings, open, connectionString, boardRef ?? (() => null), env ?? Environment.GetEnvironmentVariable, http, queue);
        var slotSettings = SlotRequestSettings.From(root, CapacitySettings.From(root, c.Engine.Environment), settings.SlotRuntime);
        var feature = new FeaturePlanParts(new SlotRequestPort(docsParts.Facts, slotSettings), new FeatureNotice(route ?? ((_, _) => false), settings.ColumnOf(StepKind.Develop) ?? "Development"), queue, slotSettings);
        return new WorkItemParts(service, new WorkItemHistory(open), store, ownership, IntakeSettings.From(root, Environment.GetEnvironmentVariable), docsParts, merge, feature);
    }

    static DocsPlanParts MakeDocsPlan(GatewayComposition c, DocsPlanSettings settings, Func<FactoryDbContext> open, string connectionString, Func<BoardRef?> boardRef, Func<string, string?> env, HttpClient? http, MergeQueueCount queue)
    {
        var tokens = GitHubTokenProvider.FromEnvironment(env);   // GH_TOKEN, else the author App's installation token (the token of the rule scripts)
        if (tokens is null) return new DocsPlanParts(settings, new DocsMergePort(null, null, settings), new DocsProjection(() => null, settings), null, null, null, boardRef);
        var root = c.Instance.Root;
        string Str(string name, string fallback) => root?[name]?.ToString() is { Length: > 0 } s ? s : fallback;
        var api = env("GITHUB_API_URL");
        http ??= new HttpClient(new GitHubAuthHandler(tokens)) { BaseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/"), Timeout = TimeSpan.FromSeconds(60) };
        var creds = new ProviderCredentialSource(tokens);
        var budget = c.GitHubBudget;
        Action<string, string?, string?, string?> observe = (resource, remaining, reset, limit) => budget.Observe(new JsonObject { [resource] = new JsonObject { ["remaining"] = remaining, ["reset"] = reset, ["limit"] = limit } });
        var clients = new GitHubClientFactory(http, creds, new Fworker.Integrations.GitHub.GitHubBudget());
        var claimNode = Fworker.Integrations.Commands.ClaimOwner.ThisProcess().Node;
        var merge = new MergePullRequestHandler(new SqlCommandStores(connectionString, owner: () => new Fworker.Integrations.Commands.ClaimOwner(claimNode, c.Leader?.ClaimEpoch ?? 0)).Ledger, new MergeDirect(clients));
        var facts = new HttpMergeFactsReader(http, creds, Str("name", "factory-worker"), Str("videoLabel", "video"), observe);
        var versions = new SqlProjectionVersions(open); var echo = new ProjectionEcho(versions);
        BoardColumnProjection? card = null;
        BoardColumnProjection? Card() => card ??= boardRef() is { } board ? new BoardColumnProjection(http, creds, board, versions, echo) : null;
        return new DocsPlanParts(settings, new DocsMergePort(merge, facts, settings, () => budget.Low().Low, queue.Busy), new DocsProjection(Card, settings), facts, new HttpBoardReader(http, creds, observe), new PullRequestHeads(clients), boardRef);
    }

    public static FactoryDbContext Open(string connectionString) => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(connectionString).Options);

    public static void Attach(WolverineHostService host, WorkItemParts parts)
    {
        var previous = host.RuntimeCommands;
        host.RuntimeCommands = (services, w) =>
        {
            previous?.Invoke(services, w);
            services.AddSingleton(parts.Service);
            services.AddSingleton(parts.History);
            // for the feeder of the intake (W6.6), which finds the Todo items that are docs-only and hands them to the saga through the host's own queues
            services.AddSingleton(parts.Intake);
            services.AddSingleton(sp => new WorkItemIntake(parts.Store, parts.Intake, (m, _) => sp.GetRequiredService<IMessageBus>().PublishAsync(m).AsTask()));
            // the docs plan's merge (MergeRequested, answered with MergeAttempted) and the card that follows the saga's decisions
            services.AddSingleton(parts.Docs.Merge);
            services.AddSingleton(parts.Docs.Projection);
            // W7.3: the feature plan's slot requests (the saga's SlotWanted becomes the scheduler's RequestSlot) and the notice of a fix-forward round to the owner session
            services.AddSingleton(parts.Feature.Slots);
            services.AddSingleton(parts.Feature.Notice);
            // W7.2: the merge goes through the train of its repository (the request enters it, the turn is granted to the merge port above, the answer comes back through it)
            services.AddSingleton(parts.Merge.Service);
            services.AddSingleton(parts.Merge.History);
            w.Discovery.IncludeType(typeof(WorkItemHandlers));
            w.Discovery.IncludeType(typeof(WorkItemRouting));
            w.Discovery.IncludeType(typeof(WorkItemHistoryHandler));
            w.Discovery.IncludeType(typeof(MergeTrainRouting));
            w.Discovery.IncludeType(typeof(MergeTrainHandlers));
            w.Discovery.IncludeType(typeof(MergeTrainPortHandler));
            w.Discovery.IncludeType(typeof(MergeTrainHistoryHandler));
            w.Discovery.IncludeType(typeof(DocsProjectionHandler));
            w.Discovery.IncludeType(typeof(SlotRequestHandler));
            w.Discovery.IncludeType(typeof(FeatureNoticeHandler));
            WorkItemTopology.Route(w);
            MergeTrainTopology.Route(w);
        };
    }
}
