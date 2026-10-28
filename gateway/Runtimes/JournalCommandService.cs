using System.Globalization;
using System.Text.Json.Nodes;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Wolverine;
using Wolverine.ErrorHandling;

// W3.2: the host side of the journal command. The plant's journal lines (matched, offered, launched, assigned, waiting ...) are published as WriteJournalLine to a
// durable local queue partitioned by item, and the handler writes them through the JournalLedger (the C# port of Add-Journal), in the order they were published.
// Only where the engine mode is V2 (tdd is Shadow, prod and uat are v1: their journal is the rule script's `journal` verb, unchanged).
// The handler is public static because Wolverine's generated code refers to it; the service it takes is the host's.
public sealed class JournalCommandService
{
    readonly WriteJournalLineHandler handler;
    internal JournalCommandService(WriteJournalLineHandler handler) { this.handler = handler; }

    // A failure that is tried again throws, and Wolverine's rule for CommandRetryException retries the envelope (the partition holds the item's later lines meanwhile).
    public async Task Run(WriteJournalLine c, IMessageBus bus, CancellationToken ct)
    {
        var o = await handler.Process(c, ct);
        if (o.Retry) throw new CommandRetryException($"{c.Name}: {o.Result.Failure} {o.Result.Error}");
        if (o.Failed != null) await bus.PublishAsync(o.Failed);
        if (o.Message != null) await bus.PublishAsync(o.Message);
    }
}

public static class JournalCommandHandlers
{
    public const string Queue = "journal-lines";
    public static Task Handle(WriteJournalLine m, JournalCommandService s, IMessageBus bus, CancellationToken ct) => s.Run(m, bus, ct);
    // the result of a line has no one to tell yet; the plant's journal is best-effort and counts nothing
    public static void Handle(JournalLineWritten m) { }
}

// The sink the plant's journal delegate calls under the V2 engine: the line becomes a command in the outbox. It only publishes, so a link of the plant's chain
// (Plant.Chain, Plant.ReserveLaunched) ends at once and the next line is published after it: the order of the chain is the order of the queue, and the queue
// runs the lines of one item one at a time.
sealed class JournalCommandSink
{
    readonly Func<WriteJournalLine, Task<bool>> publish;
    readonly Func<DateTime> utcNow;
    readonly double waitingMinutes;
    readonly Dictionary<int, DateTime> waitingSent = new();

    public JournalCommandSink(Func<WriteJournalLine, Task<bool>> publish, double waitingMinutes, Func<DateTime>? utcNow = null)
    { this.publish = publish; this.waitingMinutes = waitingMinutes; this.utcNow = utcNow ?? (() => DateTime.UtcNow); }

    public async Task Send(int item, string ev, JsonObject fields, string key)
    {
        var now = utcNow();
        // a waiting line has no key of its own (the verb takes the ticks of the write) and is written once per timing.journalWaitingMinutes per item: the ledger decides
        // from the state, this only spares the outbox the repeats of every sweep within the window of the last one it sent
        var keyless = ev == "waiting" || key.Length == 0;
        if (ev == "waiting")
        {
            lock (waitingSent)
            {
                if (waitingSent.TryGetValue(item, out var last) && (now - last).TotalMinutes < waitingMinutes) return;
                waitingSent[item] = now;
            }
        }
        var k = keyless ? now.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : key;
        var cmd = new WriteJournalLine(JournalKeys.For(item, ev, k), item, ev, (JsonObject)fields.DeepClone(), k);
        if (!await publish(cmd))
        {
            if (ev == "waiting") lock (waitingSent) waitingSent.Remove(item);
            throw new InvalidOperationException("the Wolverine host is not up");
        }
    }
}

// The store is made at the first line: a gateway without a GitHub token (neither GH_TOKEN nor the author App) starts
// all the same, and every journal command then fails for good with the reason, instead of the host failing to start.
sealed class LazyJournalStore(Func<CancellationToken, Task<IJournalStore>> make) : IJournalStore
{
    IJournalStore? made;
    async Task<IJournalStore> Store(CancellationToken ct) => made ??= await make(ct);
    public async Task<ItemState> Read(int item, CancellationToken ct) => await (await Store(ct)).Read(item, ct);
    public async Task Save(int item, ItemState holder, CancellationToken ct) => await (await Store(ct)).Save(item, holder, ct);
    public async Task<long?> Post(int item, string body, CancellationToken ct) => await (await Store(ct)).Post(item, body, ct);
    public async Task<PatchOutcome> Patch(long commentId, string body, CancellationToken ct) => await (await Store(ct)).Patch(commentId, body, ct);
}

// W3.2: the one place the journal command is wired, and only where the engine mode is V2.
static class JournalCommandWiring
{
    public static JournalCommandSink Attach(WolverineHostService host, GatewayComposition c, Func<string, string?>? env = null, HttpClient? http = null, IIdempotencyLedger? ledger = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var settings = Settings(c, env);
        var store = new LazyJournalStore(ct => Task.FromResult<IJournalStore>(MakeStore(c, env, http)));
        var service = new JournalCommandService(new WriteJournalLineHandler(ledger ?? new InMemoryIdempotencyLedger(), new JournalLedger(store, settings)));
        var previous = host.RuntimeCommands;
        host.RuntimeCommands = (services, w) =>
        {
            previous?.Invoke(services, w);
            services.AddSingleton(service);
            w.Discovery.IncludeType(typeof(JournalCommandHandlers));
            w.MessagePartitioning.ByMessage<WriteJournalLine>(m => m.Item.ToString(CultureInfo.InvariantCulture));
            // one queue per partition slot: the lines of an item always take the same one and run in the order they were published; different items run side by side
            w.MessagePartitioning.PublishToPartitionedLocalMessaging(JournalCommandHandlers.Queue, 4, topology =>
            {
                topology.Message<WriteJournalLine>();
                topology.ConfigureQueues(q => q.UseDurableInbox());
            });
            w.PublishMessage<JournalLineWritten>().ToLocalQueue(RuntimeCommandHandlers.Queue);
        };
        return new JournalCommandSink(cmd => host.PublishAsync(cmd), settings.WaitingMinutes);
    }

    static JournalSettings Settings(GatewayComposition c, Func<string, string?> env)
    {
        var root = c.Instance.Root;
        var minutes = 30.0;
        try { if (root?["timing"]?["journalWaitingMinutes"] is JsonValue v && v.TryGetValue<double>(out var d)) minutes = d; } catch { }
        // the test override of the rule script (Get-Cfg): FACTORY_TIMING_<name>
        if (env("FACTORY_TIMING_journalWaitingMinutes") is { Length: > 0 } t && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var o)) minutes = o;
        var patterns = new List<string>();
        try
        {
            var secrets = root?["secrets"];
            foreach (var group in new[] { "patterns", "redactOnly" })
                if (secrets?[group] is JsonArray a) patterns.AddRange(a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0));
        }
        catch { }
        return new JournalSettings(minutes, patterns);
    }

    // The client of the store keeps one HttpClient for its life: the self-made one sets the bearer per request from the provider, so an App token that expires is minted again.
    // An injected client (the tests) is used as it is; the credential then carries the token of the provider at the time the store is made.
    static HttpClient Http(Func<string, string?> env, HttpClient? http, GitHubTokenProvider tokens)
    {
        if (http != null) return http;
        var api = env("GITHUB_API_URL");
        return new HttpClient(new GitHubAuthHandler(tokens)) { BaseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/") };
    }

    static GitHubJournalStore MakeStore(GatewayComposition c, Func<string, string?> env, HttpClient? http)
    {
        var tokens = GitHubTokenProvider.FromEnvironment(env) ?? throw new JournalRefusedException("the journal command needs GH_TOKEN or the author App (FACTORY_APP_AUTHOR_ID, _INSTALLATION, _KEY)");
        var client = Client(c, env, http, tokens);
        return new GitHubJournalStore(client, Identity(c, env, http, tokens));
    }

    static GitHubRepoClient Client(GatewayComposition c, Func<string, string?> env, HttpClient? http, GitHubTokenProvider tokens)
    {
        var repo = c.Instance.Home ?? throw new JournalRefusedException("the journal command needs owner and repo in the instance configuration");
        var factory = new GitHubClientFactory(Http(env, http, tokens), new ProviderCredentialSource(tokens), new Fworker.Integrations.GitHub.GitHubBudget());
        return factory.For(repo).GetAwaiter().GetResult();
    }

    static JournalIdentity Identity(GatewayComposition c, Func<string, string?> env, HttpClient? http, GitHubTokenProvider tokens)
    {
        var root = c.Instance.Root;
        var name = root?["name"]?.ToString() ?? "";
        var authors = root?["stateAuthors"] is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
        var login = env("FACTORY_SERVER_LOGIN");
        return new JournalIdentity(name, async ct =>
        {
            if (!string.IsNullOrEmpty(login)) return login;
            // an installation token cannot read /user: the App's login is its slug plus [bot]
            if (tokens.AppLogin is { } bot) return login = bot;
            using var req = new HttpRequestMessage(HttpMethod.Get, "user");
            req.Headers.Authorization = new("Bearer", await tokens.Token(ct));
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.UserAgent.ParseAdd("factory-worker-gateway");
            using var resp = await Http(env, http, tokens).SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            login = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["login"]?.ToString() ?? throw new JournalRefusedException("GitHub did not name the gateway's login");
            return login;
        }, authors);
    }
}
