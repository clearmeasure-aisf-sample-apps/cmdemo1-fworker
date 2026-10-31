using System.Globalization;
using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;

// W9.7 (#359, docs/design/fworker-wolverine-implementation.md W9): the service verbs on v2 where the engine acts: revocations, revoke-worker (gateway/Identity/RevocationVerbs.cs), git-credential
// (GitCredentialVerb.cs), operator-releases, operator-release (gateway/Operator/OperatorReleaseVerbs.cs) and metrics (MetricsVerb.cs). Each answers as its rule script (Owner.ps1, Apps.ps1, OperatorRelease.ps1,
// Metrics.ps1) answers the same input: the same JSON fields, the same exit codes and codes of refusal, the same calls to GitHub. They touch credentials and authorization (the revoked names, the item's git
// credential, the operator's release record), so they change nothing that decides who may call: VerbPolicy, Auth and the endpoints that call them are the ones of v1. What moves is only what runs the rule: the
// internal callers (RevocationService, OperatorReleaseLoader, the revoke and record-release endpoints, the metrics endpoint) call the Runner, which asks the route first where the engine acts (W9.9a) and runs the rule script elsewhere,
// and the verb pipeline reaches git-credential and metrics through the route table (gateway/Verbs/Routes/Services.cs).

/// <summary>The arguments of a verb as the rule scripts' <c>Need</c> and <c>Test-Has</c> read them from the JSON hashtable (ConvertFrom-Json -AsHashtable: names are case sensitive): a null is absent, a missing one is a usage refusal.</summary>
sealed class ServiceVerbArgs
{
    readonly Dictionary<string, JsonNode?> map = new(StringComparer.Ordinal);

    /// <summary>The JSON object of stdin; an empty stdin is no arguments; JSON that is not an object has none either. JSON that cannot be read throws, as the rule script's reader did.</summary>
    public static ServiceVerbArgs Parse(string stdin)
    {
        var a = new ServiceVerbArgs();
        if (string.IsNullOrWhiteSpace(stdin)) return a;
        if (JsonNode.Parse(stdin) is JsonObject o)
            foreach (var kv in o) a.map[kv.Key] = kv.Value;
        return a;
    }

    public bool Has(string k) => map.TryGetValue(k, out var v) && v is not null;
    public JsonNode? Get(string k) => map.TryGetValue(k, out var v) ? v : null;

    /// <summary>Need: the value, or a usage refusal when it is absent or empty text.</summary>
    public JsonNode Need(string k)
    {
        if (!Has(k) || Text(map[k]) == "") throw new VerbReject(2, "usage", $"missing argument: {k}");
        return map[k]!;
    }

    /// <summary>"$x": the text PowerShell makes of a JSON value.</summary>
    public static string Text(JsonNode? n)
    {
        if (n is null) return "";
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b ? "True" : "False";
            if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
            if (v.TryGetValue<double>(out var d)) return d.ToString("R", CultureInfo.InvariantCulture);
        }
        return n.ToJsonString();
    }

    /// <summary>[int]$x: a number is rounded to the nearest whole (halves to the even one), text that is a number is read as one, true is 1; anything else throws, which the wrapper answers as an internal error (20).</summary>
    public static int Int(JsonNode? n)
    {
        double d;
        if (n is JsonValue v && v.TryGetValue<bool>(out var b)) d = b ? 1 : 0;
        else if (n is JsonValue v2 && v2.TryGetValue<long>(out var l)) d = l;
        else if (n is JsonValue v3 && v3.TryGetValue<double>(out var dd)) d = dd;
        else
        {
            var t = Text(n).Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(t[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex)) d = hex;
            else if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw new InvalidCastException($"Cannot convert value \"{t}\" to type \"System.Int32\".");
        }
        var r = Math.Round(d, MidpointRounding.ToEven);
        if (double.IsNaN(r) || r < int.MinValue || r > int.MaxValue) throw new OverflowException($"Value was either too large or too small for an Int32: {Text(n)}");
        return (int)r;
    }
}

/// <summary>The service verbs of v2. One instance per gateway; <see cref="RunAsync"/> is the answer of the rule script for a verb, in the shape the pipeline and the internal callers read it.</summary>
sealed class ServiceVerbs(ServiceGitHub gh, GitHubBudget? budget)
{
    public static IReadOnlyList<string> Verbs => ServiceRoutes.Verbs;
    public static bool Handles(string verb) => Verbs.Contains(verb);

    public ServiceGitHub Gh => gh;
    internal Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;
    // the metrics read many comments: not more than two at once (the rule script's "slow" class of the runner gate allows half of its slots)
    readonly SemaphoreSlim metricsGate = new(2, 2);

    public V2VerbHandler Handler(string verb) => (stdin, timeout) => RunAsync(verb, stdin, timeout);

    public async Task<VerbAnswer> RunAsync(string verb, string stdin, TimeSpan timeout)
    {
        // a call of the gateway itself that can wait is not started while the budget is low (Runner.RunAsync, GitHubBudget.DeferredCalls)
        if (budget is { } b && GitHubBudget.DeferredCalls.Contains(verb) && b.Low() is { Low: true } low)
        {
            b.CountCallRefusal();
            return new VerbAnswer(20, System.Text.Json.JsonSerializer.Serialize(new { ok = false, verb, code = "github-budget", reason = $"the gateway's GitHub budget ({low.Resource}) is below its reserve; this call waits for the reset", retryAfterSeconds = Math.Max(1, (int)(low.ResetAt - DateTime.UtcNow).TotalSeconds), resetAt = low.ResetAt.ToString("o"), exit = 20 }));
        }
        using var cts = new CancellationTokenSource(timeout);
        var scope = gh.Open(cts.Token);
        JsonObject result; var exit = 0;
        try
        {
            var args = ServiceVerbArgs.Parse(stdin);
            result = await Dispatch(verb, args, scope);
            result["verb"] = verb;
            if (!result.ContainsKey("ok")) result["ok"] = true;
        }
        catch (VerbReject r)
        {
            result = new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = r.Code, ["reason"] = r.Message };
            foreach (var kv in r.Extra) result[kv.Key] = kv.Value?.DeepClone();
            exit = r.Exit;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Report(scope);
            return new VerbAnswer(20, System.Text.Json.JsonSerializer.Serialize(new { ok = false, verb, code = "timeout", reason = "the rule script did not finish in time", retryAfterSeconds = 30, exit = 20 }));
        }
        catch (Exception e)
        {
            // factory.ps1: an error nobody expected is 20 internal-error with its message, and the log says where (never a credential: none is part of a message)
            Console.Error.WriteLine($"[{verb}] internal error in {verb} : {e.Message}");
            result = new JsonObject { ["ok"] = false, ["verb"] = verb, ["code"] = "internal-error", ["reason"] = e.Message, ["retryAfterSeconds"] = 30 };
            exit = 20;
        }
        Report(scope);
        result["exit"] = exit;
        return new VerbAnswer(exit, result.ToJsonString(ServiceGitHub.Relaxed));
    }

    // the rate limit this run saw goes to the gateway's budget, as the _github field of a rule script's answer does (Runner takes it out)
    void Report(ServiceGitHub.CallScope scope) { if (scope.RateReport() is { } report) budget?.Observe(report); }

    async Task<JsonObject> Dispatch(string verb, ServiceVerbArgs a, ServiceGitHub.CallScope s)
    {
        switch (verb)
        {
            case "revocations": return await RevocationVerbs.Revocations(s);
            case "revoke-worker": return await RevocationVerbs.RevokeWorker(a, s);
            case "git-credential": return await GitCredentialVerb.Run(a, s);
            case "operator-releases": return await OperatorReleaseVerbs.Releases(a, s);
            case "operator-release": return await OperatorReleaseVerbs.Record(a, s);
            case "metrics":
                await metricsGate.WaitAsync(s.Ct);
                try { return await MetricsVerb.Run(a, s, Now); }
                finally { metricsGate.Release(); }
            default: throw new VerbReject(2, "unknown-verb", $"unknown verb '{verb}'");
        }
    }
}

static class ServiceVerbRegistration
{
    /// <summary>The service verbs (the gateway's own calls reach their routes through the Runner, W9.9a). Singletons made when first used; nothing here runs unless the engine acts.</summary>
    public static IServiceCollection AddGatewayServiceVerbs(this IServiceCollection services, GatewayComposition c)
    {
        services.AddSingleton(sp => new ServiceVerbs(new ServiceGitHub(c.Instance, Environment.GetEnvironmentVariable), c.GitHubBudget));
        return services;
    }
}
