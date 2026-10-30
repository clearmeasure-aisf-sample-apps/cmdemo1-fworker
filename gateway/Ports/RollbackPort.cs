using System.Net;
using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;
using Fworker.Integrations.Octopus;
using Fworker.Workflows.Delivery;
using Wolverine;
using Wolverine.ErrorHandling;

// W8.3: the rollback of the delivery train in the gateway, only where the engine acts (Engine.V2Acts; v1, the shadow run and FACTORY_V2_OFF register nothing of it). The train (Fworker.Workflows.Delivery,
// RollbackTrain) decides: it checks the migration precondition and publishes RollbackRelease, the one deployment it creates itself (owner decision of 2026-10-29: the incident rollback of the app in tdd,
// automatic, tdd only). This file is the host side of it: the executor of RollbackRelease on the W3.0 command base (RollbackExecutor, Octopus), its durable queue and retry rule, and the read of the files
// two commits differ by (the migration precondition), a GET of GitHub's compare. Where no Octopus key, URL or space is configured the command has no executor: the train then escalates after the request expiry.

/// <summary>A rollback command was settled: executed, or refused for good (then the train is told and escalates). Public because Wolverine's generated code refers to it.</summary>
[Fworker.Contracts.Messaging.MessageIdentity("delivery.rollback-command-finished")]
public sealed record RollbackCommandFinished(string IdempotencyKey, string Incident, bool Ok, string Execution, string Error);

/// <summary>The retry rule of the command: a failure that is tried again throws, Wolverine runs the envelope again after the next pause, and the run after the last pause settles as a refusal.</summary>
public sealed class RollbackRetryException(string message) : Exception(message);

public sealed class RollbackExecutorService(RollbackReleaseHandler handler)
{
    public static readonly TimeSpan[] RetryPauses = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    public static int MaxAttempts => RetryPauses.Length + 1;

    /// <summary>The messages of one run of the command (<paramref name="attempts"/> counts from 1); throws <see cref="RollbackRetryException"/> when it is to be tried again.</summary>
    public async Task<IReadOnlyList<object>> Messages(RollbackRelease c, int attempts, CancellationToken ct)
    {
        CommandResult r;
        try { r = await handler.Handle(c, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { r = CommandResult.Failed(FailureKind.Transient, $"{c.Name}: {e.GetType().Name}"); }   // the type only: a database error's message can carry the connection string
        if (r.Ok) return [new RollbackCommandFinished(c.IdempotencyKey, c.Incident, true, r.Execution, "")];
        if (r.Failure is FailureKind.Transient or FailureKind.RateLimited)
        {
            if (attempts < MaxAttempts) throw new RollbackRetryException($"{c.Name}: {r.Failure} {r.Error}");
            r = RuntimeOutcome.ExhaustedResult(r.Error);
        }
        // refused for good: the train cannot roll back and escalates; the saga's message says why
        return [new RollbackCommandFinished(c.IdempotencyKey, c.Incident, false, "", r.Error), IntegrationFailed.From(c, r), new TrainRollbackFailed(TrainKey.Of(c.Deployable, c.Environment), c.Incident, r.Error)];
    }

    public async Task Run(RollbackRelease c, IMessageBus bus, int attempts, CancellationToken ct) { foreach (var m in await Messages(c, attempts, ct)) await bus.PublishAsync(m); }
}

/// <summary>The handlers of the rollback's messages. Public because Wolverine's generated code refers to them.</summary>
public static class RollbackHandlers
{
    public const string Queue = "rollback-commands";
    public static Task Handle(RollbackRelease m, RollbackExecutorService s, IMessageBus bus, Envelope envelope, CancellationToken ct) => s.Run(m, bus, envelope.Attempts, ct);
}

/// <summary>The log lines of the rollback. Public because Wolverine's generated code refers to them.</summary>
public static class RollbackLogHandlers
{
    public static void Handle(RollbackCommandFinished m) =>
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "rollback-command", ["key"] = m.IdempotencyKey, ["incident"] = m.Incident, ["ok"] = m.Ok, ["execution"] = m.Ok ? m.Execution : null, ["error"] = m.Ok ? null : m.Error }.ToJsonString());

    /// <summary>The end of a rollback, for the log until the incident saga (W8.1) reads it.</summary>
    public static void Handle(RollbackFinished m) =>
        Console.WriteLine(new JsonObject
        {
            ["at"] = DateTime.UtcNow.ToString("o"), ["gateway"] = "rollback", ["deployable"] = m.Deployable, ["environment"] = m.Environment, ["incident"] = m.Incident, ["outcome"] = m.Outcome,
            ["from"] = m.From, ["to"] = m.To, ["items"] = new JsonArray(m.Items.Select(i => (JsonNode?)i).ToArray()), ["reasons"] = new JsonArray(m.Reasons.Select(r => (JsonNode?)r).ToArray()),
        }.ToJsonString());
}

/// <summary>The files two commits differ by, from GitHub's compare. READ ONLY: a GET. A compare of fixed commits cannot change, so it is asked once per rollback.</summary>
sealed class HttpMigrationProbe(HttpClient github) : IMigrationProbe
{
    /// <summary>GitHub lists at most this many files in a compare; a list that long may be cut.</summary>
    public const int FileCap = 300;

    public async Task<ChangedFiles?> ChangedBetween(RepoId repository, string fromCommit, string toCommit, CancellationToken ct)
    {
        using var resp = await github.GetAsync($"repos/{repository}/compare/{fromCommit}...{toCommit}", ct);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity) return null;
        resp.EnsureSuccessStatusCode();   // anything else is a fault of the read: the train looks again
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) as JsonObject;
        if (json is null) return null;
        var listed = json["files"] as JsonArray ?? [];
        // (a renamed file counts under both names: a migration moved out of the scripts is a change of them too)
        var files = listed.SelectMany(f => new[] { f?["filename"]?.ToString() ?? "", f?["previous_filename"]?.ToString() ?? "" }).Where(f => f.Length > 0).Distinct().ToList();
        return new ChangedFiles(json["status"]?.ToString(), files, json["files"] is JsonArray && listed.Count < FileCap);
    }
}

static class RollbackWiring
{
    /// <summary>
    /// The executor of RollbackRelease and the read of the migration precondition, for a host whose engine acts. The command has no executor without an Octopus URL, space and key (the train escalates);
    /// without GitHub access (no GH_TOKEN or author App) the train cannot show that no migration was added and escalates every rollback.
    /// </summary>
    public static void Attach(WolverineHostService host, GatewayComposition c, Func<string, string?> env, Func<bool> mayAct, IIdempotencyLedger ledger)
    {
        var root = c.Instance.Root as JsonObject ?? new JsonObject();
        var octopus = root["octopus"] as JsonObject ?? new JsonObject();
        string O(string name) => octopus[name]?.ToString() ?? "";
        var key = env("OCTOPUS_RUNBOOK_API_KEY") is { Length: > 0 } k ? k : env("OCTOPUS_API_KEY");
        RollbackExecutorService? service = null;
        if (O("url").Length > 0 && O("space").Length > 0 && !string.IsNullOrEmpty(key))
        {
            var names = (octopus["environments"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>();
            var http = new HttpClient { BaseAddress = new Uri(O("url").TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
            service = new RollbackExecutorService(new RollbackReleaseHandler(ledger, new RollbackExecutor(http, new RollbackSettings(O("space"), names, () => env("OCTOPUS_RUNBOOK_API_KEY") is { Length: > 0 } rk ? rk : env("OCTOPUS_API_KEY"), mayAct))));
        }
        IMigrationProbe? probe = null;
        if (Fworker.Integrations.GitHub.GitHubTokenProvider.FromEnvironment(env) is { } tokens)
        {
            var api = env("GITHUB_API_URL");
            var github = new HttpClient(new Fworker.Integrations.GitHub.GitHubAuthHandler(tokens)) { BaseAddress = new Uri((string.IsNullOrWhiteSpace(api) ? "https://api.github.com" : api.TrimEnd('/')) + "/"), Timeout = TimeSpan.FromSeconds(60) };
            github.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            github.DefaultRequestHeaders.UserAgent.ParseAdd("factory-worker-gateway");
            probe = new HttpMigrationProbe(github);
        }
        var previous = host.RuntimeCommands;
        host.RuntimeCommands = (services, w) =>
        {
            previous?.Invoke(services, w);
            Register(services, w, service, probe);
        };
    }

    /// <summary>What the host gets: the log handlers always, the read of the migration precondition when GitHub can be read, and the executor with its durable queue and retry rule when Octopus is configured.</summary>
    internal static void Register(IServiceCollection services, WolverineOptions w, RollbackExecutorService? service, IMigrationProbe? probe, bool durable = true)
    {
        w.Discovery.IncludeType(typeof(RollbackLogHandlers));
        if (probe is not null) services.AddSingleton(probe);   // (the delivery train takes it as an optional part)
        if (service is null) return;
        services.AddSingleton(service);
        w.Discovery.IncludeType(typeof(RollbackHandlers));
        w.PublishMessage<RollbackRelease>().ToLocalQueue(RollbackHandlers.Queue);
        w.PublishMessage<RollbackCommandFinished>().ToLocalQueue(RollbackHandlers.Queue);
        // one command at a time: two rollbacks of the same release must not both pass the ledger's check before either has recorded its result
        var queue = w.LocalQueue(RollbackHandlers.Queue).Sequential();
        if (durable) queue.UseDurableInbox();
        // a transient failure is tried again with growing pauses; the run after the last pause settles as a refusal (the train escalates), so the dead letters are the fallback only
        w.Policies.OnException<RollbackRetryException>().RetryWithCooldown(RollbackExecutorService.RetryPauses).Then.MoveToErrorQueue();
    }
}
