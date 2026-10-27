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
        _ => FailureKind.Transient,
    };
}
