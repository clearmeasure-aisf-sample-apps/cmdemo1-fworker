using System.Text.Json.Nodes;
using Fworker.Domain;

namespace Fworker.Workflows.Capacity;

// W7.0: what the CapacityScheduler is tuned with, read from config/instance.json for the gateway's environment. There is no flag: the scheduler exists wherever the engine acts (Engine.V2Acts), and its defaults
// are the design's (delivery-speed.md 14). The runtimes are the ones the plant knows by their kind (the API's names: octopus, cae-job, cursor-cloud, copilot-cloud); a runtime that is off in the configuration is
// not in the facts of the admission chain, so a request for it waits as runtime-off. A malformed value falls back to the default field by field and a malformed block to "nothing is on": the scheduler admits
// nothing it was not told about.

/// <summary>The kinds of runtime the scheduler knows, as the API and the routing name them.</summary>
public static class RuntimeKinds
{
    public const string Octopus = "octopus";
    public const string CaeJob = "cae-job";
    public const string CursorCloud = "cursor-cloud";
    public const string CopilotCloud = "copilot-cloud";
    public static readonly string[] All = [Octopus, CaeJob, CursorCloud, CopilotCloud];
}

/// <summary>
/// The limits of one runtime. <paramref name="PerHostCapacity"/> is more than 0 only for a runtime with hosts (octopus): its sessions run on hosts the host reports name, <paramref name="PerHostBurst"/> is the limit with
/// headroom (0 or lower than the capacity: no burst). <paramref name="Toolchains"/> is what the runtime offers; null means unrestricted (the configuration names none: the toolchain rule does not apply).
/// </summary>
public sealed record SchedulerRuntime(string Kind, int MaxConcurrent, int MaxExecutionsPerDay, int PerHostCapacity = 0, int PerHostBurst = 0, IReadOnlyList<string>? Toolchains = null)
{
    public bool HasHosts => PerHostCapacity > 0;
}

public sealed record CapacitySettings(
    IReadOnlyDictionary<string, SchedulerRuntime> Runtimes,
    FileOverlapSettings Overlap,
    HeadroomSettings Headroom,
    double ContainerCpus,
    TimeSpan SlotMax,
    TimeSpan HostReportMax,
    TimeSpan SafetyWake,
    int MaxFootprintSamples = CapacitySettings.DefaultMaxFootprintSamples,
    int KeepKeys = CapacitySettings.DefaultKeepKeys)
{
    public const int DefaultMaxFootprintSamples = 100;
    public const int DefaultKeepKeys = 200;
    public const int DefaultSlotMaxMinutes = 240;
    public const int DefaultHostReportMaxMinutes = 10;
    public const int DefaultSafetyWakeMinutes = 10;

    /// <summary>
    /// The settings with only the runtimes of <paramref name="kinds"/>. The configuration says which runtimes may be on (enabled, with a limit above 0); whether one is on in this process also depends on what it needs
    /// besides (a key, a gateway URL, a job), which the gateway's own managers decide: the gateway passes the kinds its managers report as on, and the others are runtime-off to the scheduler.
    /// </summary>
    public CapacitySettings Only(IEnumerable<string> kinds)
    {
        var keep = kinds.ToHashSet(StringComparer.Ordinal);
        return this with { Runtimes = Runtimes.Where(kv => keep.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) };
    }

    /// <summary>How old a host's last report may be before the host is taken for gone and offered no slot at all (a report older than <see cref="HostReportMax"/> still gives its guaranteed slots, not a burst).</summary>
    public TimeSpan HostGone => HostReportMax * 3;

    /// <summary>What the admission chain reads: the day and file-overlap limits (the file-overlap hold ends after the same minutes in both rules).</summary>
    public AdmissionSettings Admission => new(Overlap.MaxHoldMinutes, Headroom.HostFullSeconds);

    /// <summary>Nothing is on: every request waits as runtime-off. The settings of a configuration that names no runtime.</summary>
    public static CapacitySettings None { get; } = new(new Dictionary<string, SchedulerRuntime>(), FileOverlapSettings.Default, HeadroomSettings.Defaults, 2,
        TimeSpan.FromMinutes(DefaultSlotMaxMinutes), TimeSpan.FromMinutes(DefaultHostReportMaxMinutes), TimeSpan.FromMinutes(DefaultSafetyWakeMinutes));

    /// <summary>
    /// Reads the settings from the configuration root for <paramref name="environment"/>: the runtimes block as the gateway reads it (<c>runtimes.environments.&lt;name&gt;</c> is the whole block when the file has
    /// environments, and an environment with no entry has every runtime off; the Cursor and Copilot limits are <c>api.cursor</c> and <c>api.copilot</c>, on only in the environments they name),
    /// <c>schedule.fileOverlap</c>, <c>schedule.overlapIgnore</c> and <c>scheduler</c>. The headroom keys (<c>reserveMb</c>, <c>minDiskMb</c>, <c>hostFullSeconds</c>, <c>minFootprintSamples</c>,
    /// <c>containerCpus</c>) are <c>runtimes.octopus</c>'s: the only runtime with hosts.
    /// </summary>
    public static CapacitySettings From(JsonNode? root, string environment)
    {
        var env = (environment ?? "").Trim().ToLowerInvariant();
        var scheduler = Obj(root, "scheduler");
        var toolchains = Obj(scheduler, "toolchains");
        JsonNode? runtimes = null;
        try { runtimes = Obj(root, "runtimes") is { } r && r["environments"] is JsonObject { Count: > 0 } envs ? envs[env == "" ? "tdd" : env] : Obj(root, "runtimes"); } catch { }

        var map = new Dictionary<string, SchedulerRuntime>(StringComparer.Ordinal);
        var octopus = Obj(runtimes, "octopus");
        if (Bool(octopus, "enabled") && Int(octopus, "maxConcurrent", 0) > 0)
        {
            var capacity = Math.Max(1, Int(octopus, "perHostCapacity", 2));
            map[RuntimeKinds.Octopus] = new SchedulerRuntime(RuntimeKinds.Octopus, Int(octopus, "maxConcurrent", 0), Int(octopus, "maxExecutionsPerDay", 0), capacity, Math.Max(capacity, Int(octopus, "perHostBurst", 0)), Strings(toolchains, RuntimeKinds.Octopus));
        }
        var cae = Obj(runtimes, "caeJob");
        var caeHasJob = Text(cae, "jobResourceId") is not null || (Obj(cae, "jobs") is { } jobs && jobs.Any(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0));
        if (Bool(cae, "enabled") && caeHasJob && Int(cae, "maxConcurrent", 0) > 0)
            map[RuntimeKinds.CaeJob] = new SchedulerRuntime(RuntimeKinds.CaeJob, Int(cae, "maxConcurrent", 0), Int(cae, "maxExecutionsPerDay", 0), Toolchains: Strings(toolchains, RuntimeKinds.CaeJob));
        foreach (var (kind, name) in new[] { (RuntimeKinds.CursorCloud, "cursor"), (RuntimeKinds.CopilotCloud, "copilot") })
        {
            var api = Obj(Obj(root, "api"), name);
            var enabled = Bool(api, "enabled");
            // api.<name>.environments: when it names any, the mode is on only in those (an entry may be written ready but off with "enabled": false), as the gateway reads it
            if (Obj(api, "environments") is { Count: > 0 } envs)
            {
                var entry = Obj(envs, env == "" ? "tdd" : env);
                if (entry is null || (entry["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var eb) && !eb)) enabled = false;
            }
            var max = Int(api, "maxConcurrent", 0);
            if (enabled && max > 0) map[kind] = new SchedulerRuntime(kind, max, Int(api, "maxExecutionsPerDay", 0), Toolchains: Strings(toolchains, kind));
        }

        var schedule = Obj(root, "schedule");
        var fo = Obj(schedule, "fileOverlap");
        var overlap = new FileOverlapSettings(Bool(fo, "enabled"), Int(fo, "maxHoldMinutes", FileOverlapSettings.Default.MaxHoldMinutes), Strings(schedule, "overlapIgnore") ?? []);
        var d = HeadroomSettings.Defaults;
        var headroom = new HeadroomSettings(Int(octopus, "reserveMb", d.ReserveMb), Int(octopus, "minDiskMb", d.MinDiskMb), Int(octopus, "hostFullSeconds", d.HostFullSeconds), Math.Max(1, Int(octopus, "minFootprintSamples", d.MinFootprintSamples)));
        var cpus = Dbl(octopus, "containerCpus", 2);
        return new CapacitySettings(map, overlap, headroom, cpus,
            TimeSpan.FromMinutes(Math.Max(1, Int(scheduler, "slotMaxMinutes", DefaultSlotMaxMinutes))),
            TimeSpan.FromMinutes(Math.Max(1, Int(scheduler, "hostReportMaxMinutes", DefaultHostReportMaxMinutes))),
            TimeSpan.FromMinutes(Math.Max(1, Int(scheduler, "safetyWakeMinutes", DefaultSafetyWakeMinutes))));
    }

    static JsonObject? Obj(JsonNode? n, string name) { try { return n?[name] as JsonObject; } catch { return null; } }
    static bool Bool(JsonNode? n, string name) { try { return n?[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b; } catch { return false; } }
    static int Int(JsonNode? n, string name, int fallback) { try { return n?[name] is JsonValue v && v.TryGetValue<int>(out var x) && x >= 0 ? x : fallback; } catch { return fallback; } }
    static double Dbl(JsonNode? n, string name, double fallback) { try { return n?[name] is JsonValue v && v.TryGetValue<double>(out var x) && double.IsFinite(x) && x > 0 ? x : fallback; } catch { return fallback; } }
    static string? Text(JsonNode? n, string name) { try { return n?[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null; } catch { return null; } }

    /// <summary>The strings of an array, or null when the key is absent or no array (for the toolchains: unrestricted).</summary>
    static IReadOnlyList<string>? Strings(JsonNode? n, string name)
    {
        try
        {
            if (n?[name] is not JsonArray a) return null;
            return a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "").Where(s => s.Length > 0).ToList();
        }
        catch { return null; }
    }
}
