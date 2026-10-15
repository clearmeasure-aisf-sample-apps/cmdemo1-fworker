// A runtime adapter (docs/design/cloud-worker-runtimes.md 4.2): it creates a worker for one item when the plant has chosen its runtime, stops it,
// and lists what it runs (restart recovery and status). "local" is not an adapter: local sessions connect by themselves. The interface keeps the
// plant independent of how a runtime starts its workers, so another runtime can be added without touching the plant.
sealed record ProvisionRequest(string WorkerName, int Item, string Phase, string ModelProfile, string Nonce);
sealed record ProvisionResult(bool Ok, string Execution, string Error);
// Status: Running, Processing, Succeeded, Failed, Stopped, Degraded or Unknown (the execution states of Container Apps jobs)
sealed record RuntimeInstance(string Execution, string WorkerName, int Item, string Phase, string Status, DateTime? Started, DateTime? Ended);

interface IWorkerRuntime
{
    string Kind { get; }
    int MaxConcurrent { get; }
    Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct);
    Task Deprovision(string workerName, string reason, CancellationToken ct);    // stops the worker's execution; idempotent
    Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct);
}
