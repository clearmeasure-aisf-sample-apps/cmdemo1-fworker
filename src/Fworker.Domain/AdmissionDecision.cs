namespace Fworker.Domain;

// Admission (D1): the rules that decide whether the plan may start a session for a ranked item, beyond `Depends on` (docs/design/delivery-speed.md 3.2,
// 4.2 and 6; wolverine-factory.md 3.2 CapacityScheduler; system-gateway.md H4 and H8). Specification tests: v1 has the runtime limits in
// RuntimeManager.CanStart and the rest not at all. Pure values (no clock, no I/O, no config): the caller passes the facts and the settings. The CapacityScheduler (W7.1,
// Fworker.Workflows.Capacity) calls this where the engine acts. `Depends on` is rule 1 of the chain and stays where it is (Test-DependencyMet, then the item saga).

/// <summary>The waiting codes of the admission rules. They are the strings the plan journals when an item's wait changes (delivery-speed.md 3.2).</summary>
public static class AdmissionCodes
{
    /// <summary>The runtime is not in the facts (v1: runtime-off).</summary>
    public const string RuntimeOff = "runtime-off";
    public const string RuntimeAtCapacity = "runtime-at-capacity";
    public const string RuntimeBudget = "runtime-budget";
    /// <summary>The runtime or host does not offer a toolchain the item's repository needs (R7).</summary>
    public const string Toolchain = "toolchain";
    /// <summary>A generated (keeper-replenish) item waits while a real item still waits for a slot (delivery-speed.md 6).</summary>
    public const string GeneratedLast = "generated-last";
    /// <summary>Held for file overlap; the detail names the holding item (`file-overlap owner/repo#N`).</summary>
    public const string FileOverlap = "file-overlap";
    /// <summary>The host answered host-full at its start: a non-failure block of that host for hostFullSeconds (delivery-speed.md 4.2).</summary>
    public const string HostFull = "host-full";
}

/// <summary>Admit, or block with a waiting code and a detail. A block is a wait, never a failure: it counts toward no failure counter.</summary>
public sealed record AdmissionDecision(bool Admitted, string Code, string Detail)
{
    public static readonly AdmissionDecision Admit = new(true, "", "");

    public static AdmissionDecision Block(string code, string detail = "") => new(false, code, detail);

    /// <summary>The text of the item's waiting code: `code`, or `code detail` (for example `file-overlap acme/app#12`); empty when admitted.</summary>
    public string Waiting => Admitted ? "" : Detail.Length == 0 ? Code : $"{Code} {Detail}";
}
