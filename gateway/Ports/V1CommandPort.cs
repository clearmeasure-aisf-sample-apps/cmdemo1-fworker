using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;

// v1's implementation of seam S2 (task W1.5): it delegates to today's code paths and changes none of them. A runtime start or stop goes to the runtime
// adapter (IWorkerRuntime.Provision and Deprovision, the calls RuntimeManager makes); the other commands are side effects v1 performs inside the rule
// scripts, so the port answers NotSupported for them. Registered but unused: RuntimeManager still calls the adapters itself.
sealed class V1CommandPort : ICommandPort
{
    readonly Func<string, IWorkerRuntime?> runtimeOfKind;
    public V1CommandPort(Func<string, IWorkerRuntime?> runtimeOfKind) { this.runtimeOfKind = runtimeOfKind; }

    public async Task<CommandResult> SendAsync(IntegrationCommand command, CancellationToken ct = default)
    {
        switch (command)
        {
            case StartRuntimeSession s:
            {
                var rt = runtimeOfKind(s.Runtime);
                if (rt == null) return CommandResult.Failed(FailureKind.Permanent, "runtime off: " + s.Runtime);
                try
                {
                    var r = await rt.Provision(new ProvisionRequest(s.WorkerName, s.Item, s.Phase, s.ModelProfile, s.Nonce, s.Role), ct);
                    return r.Ok ? CommandResult.Success(r.Execution, r.Url) : CommandResult.Failed(Classify(r.Error), r.Error);
                }
                catch (Exception e) { return CommandResult.Failed(FailureKind.Transient, "start-error: " + e.GetType().Name); }
            }
            case StopRuntimeSession p:
            {
                var rt = runtimeOfKind(p.Runtime);
                if (rt == null) return CommandResult.Failed(FailureKind.Permanent, "runtime off: " + p.Runtime);
                try { await rt.Deprovision(p.WorkerName, p.Reason, ct); return CommandResult.Success(); }
                catch (Exception e) { return CommandResult.Failed(FailureKind.Transient, "stop-error: " + e.GetType().Name); }
            }
            default:
                return CommandResult.Failed(FailureKind.NotSupported, command.Name + " is performed inside the rule scripts in v1");
        }
    }

    // the error strings RuntimeManager.ProvisionCore already tells apart ("no capacity", "quota" are not failures of the item)
    static FailureKind Classify(string error) => error switch
    {
        "no capacity" or "quota" => FailureKind.RateLimited,
        _ when RefusedByTheRuntime(error) => FailureKind.Permanent,
        _ => FailureKind.Transient,
    };

    // "launch-<status>[: answer]" (the adapters' answer of a refused launch) with a 4xx status other than 408, 425 and 429: the runtime said no to this request, and asking again changes nothing, so the start is a
    // failure of the item at once, as v1 counts it, instead of waiting out the command's retries (2 s, 10 s, 30 s, 60 s) for the same answer. A 5xx, a timeout and a transport fault stay Transient: they are tried again.
    static bool RefusedByTheRuntime(string error)
    {
        if (!error.StartsWith("launch-", StringComparison.Ordinal)) return false;
        var digits = error.AsSpan("launch-".Length);
        var end = 0; while (end < digits.Length && char.IsAsciiDigit(digits[end])) end++;
        if (end != 3 || (end < digits.Length && digits[end] != ':')) return false;
        var status = int.Parse(digits[..3]);
        return status is >= 400 and < 500 and not (408 or 425 or 429);
    }
}
