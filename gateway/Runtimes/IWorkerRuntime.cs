using System.Text.Json.Nodes;
// A runtime adapter (docs/design/cloud-worker-runtimes.md 4.2): it creates a worker for one item when the plant has chosen its runtime, stops it,
// and lists what it runs (restart recovery and status). "local" is not an adapter: local sessions connect by themselves. The interface keeps the
// plant independent of how a runtime starts its workers, so another runtime can be added without touching the plant.
// Role: the role the worker plays when it is not the item's phase (the octopus runtime's overflow rule); "" for an own-item worker
sealed record ProvisionRequest(string WorkerName, int Item, string Phase, string ModelProfile, string Nonce, string Role = "", LaunchFields? Launch = null);
// The launch fields every runtime receives (W1.8, system-gateway.md H7): the repository the item lives in, its clone URL and default branch. Set to the home repository.
sealed record LaunchFields(string Repository, string CloneUrl, string DefaultBranch);
// Url: the page of the execution a person can open (a Copilot task: its live session, then its transcript), "" when the runtime has none
sealed record ProvisionResult(bool Ok, string Execution, string Error, string Url = "");
// Status: Running, Processing, Succeeded, Failed, Stopped, Degraded or Unknown (the execution states of Container Apps jobs)
sealed record RuntimeInstance(string Execution, string WorkerName, int Item, string Phase, string Status, DateTime? Started, DateTime? Ended, bool Adopted = false);   // Adopted: found by the runtime after its first listing (a host report); the manager adopts it then too

interface IWorkerRuntime
{
    string Kind { get; }
    int MaxConcurrent { get; }
    Task<ProvisionResult> Provision(ProvisionRequest r, CancellationToken ct);
    Task Deprovision(string workerName, string reason, CancellationToken ct);    // stops the worker's execution; idempotent
    Task<IReadOnlyList<RuntimeInstance>> List(CancellationToken ct);
}

// A runtime whose own task history says how many executions were started in a stretch of time (W0.2, docs/design/delivery-speed.md 18.1.4):
// the manager counts the day budget from it at the start and at every UTC midnight, so a restart does not give the day back. The Octopus
// runtime reads its server tasks; the Copilot and Cursor runtimes keep no history outside the process and do not implement it.
interface IRunHistory
{
    // every Start and overflow run queued at or after sinceUtc, however it ended (a stop does not take it back); throws when the history cannot be read
    Task<int> LaunchesSince(DateTime sinceUtc, CancellationToken ct);
}

// A runtime that reports more about one worker's execution for /status (the Cursor adapter: the model asked for, the model the agent
// reports, the token usage): plain values, never a credential.
interface IRuntimeDetail
{
    JsonObject? Detail(string workerName);
}
