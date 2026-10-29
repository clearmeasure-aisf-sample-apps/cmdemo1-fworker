using System.Text.Json;
using System.Text.Json.Serialization;
using Fworker.Contracts.Ids;
using Fworker.Domain;

namespace Fworker.Workflows.Capacity;

// W7.0 and W7.1: the state of the scheduler, the one JSON document of the row "factory" in the CapacityLedgers table (Fworker.Persistence), saved with a revision. It is the slot ledger of the v2 engine: who holds a
// slot where, who waits, the items in flight with their footprints (the holders of the file-overlap rule), the holds, the hosts as last reported with the footprints learned from their finished sessions, the
// executions started today per runtime, and what the last transition published (so a redelivery after a crash between the save and the publish publishes it again). Plain mutable classes: System.Text.Json round-trips
// them, and a field a later wave adds is optional in a state saved before it.

/// <summary>A request that waits for a slot: the facts of a <see cref="RequestSlot"/> kept until it is granted, replaced or cleared.</summary>
public sealed class WaitingRequest
{
    public string Item { get; set; } = "";
    public int Generation { get; set; }
    public string Runtime { get; set; } = "";
    public string Phase { get; set; } = CapacityPhases.Design;
    public PlanRank Rank { get; set; }
    public bool Generated { get; set; }
    public bool Resume { get; set; }
    public int ColumnDepth { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public List<string> RequiredToolchains { get; set; } = [];
    public Footprint Footprint { get; set; } = Footprint.Empty;
    /// <summary>The text of the waiting code last published for the item ("" before the first): a code is published once per change.</summary>
    public string Announced { get; set; } = "";

    [JsonIgnore]
    public ItemId ItemId => ItemId.Parse(Item);

    public static WaitingRequest Of(RequestSlot r) => new()
    {
        Item = r.Item.ToString(), Generation = r.Generation, Runtime = r.Runtime, Phase = r.Phase, Rank = r.Rank, Generated = r.Generated, Resume = r.Resume, ColumnDepth = r.ColumnDepth,
        RequestedAt = r.RequestedAt, RequiredToolchains = r.RequiredToolchains.ToList(), Footprint = r.Footprint,
    };

    /// <summary>The phase makes the item a holder of the file-overlap rule once granted (past Technical Design).</summary>
    [JsonIgnore]
    public bool PastDesign => !string.Equals(Phase, CapacityPhases.Design, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A granted slot: the item holds it from the grant to the end of its session. The request is kept so a refused start (host-full) puts the item back in the queue as it was.</summary>
public sealed class LedgerSlot
{
    public string SlotId { get; set; } = "";
    public string Item { get; set; } = "";
    public string Runtime { get; set; } = "";
    /// <summary>The host of a runtime with hosts, null otherwise.</summary>
    public string? Host { get; set; }
    public SlotKind Kind { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>The grant made the item an item in flight (it was none before): a start refused as host-full takes that back.</summary>
    public bool EnteredFlight { get; set; }
    public WaitingRequest Request { get; set; } = new();
}

/// <summary>An item between Development and merged: the holders the file-overlap rule compares a waiting item with. It is never held itself (D6: a holder is past Technical Design).</summary>
public sealed class InFlightItem
{
    public string Item { get; set; } = "";
    public Footprint Footprint { get; set; } = Footprint.Empty;
    public DateTimeOffset Since { get; set; }
}

/// <summary>The hold of a waiting item, kept while it waits so that an expired hold keeps admitting (an item is not held twice) and the limit is on the whole wait.</summary>
public sealed class HoldRecord
{
    public int By { get; set; }
    public DateTimeOffset Since { get; set; }
    public DateTimeOffset Until { get; set; }
}

/// <summary>A host as the scheduler knows it: its last report, the containers outside the ledger at that report, the footprint samples of its finished sessions and the time it last answered host-full.</summary>
public sealed class LedgerHost
{
    public string Name { get; set; } = "";
    public string Runtime { get; set; } = "";
    public HostMetrics Metrics { get; set; } = new(0, 0, 0, 0);
    public DateTimeOffset ReportedAt { get; set; }
    /// <summary>The containers the host ran at the report beyond the ledger's slots on it: sessions the scheduler did not grant (v1's, adopted after a restart). Kept until the next report.</summary>
    public int Outside { get; set; }
    public List<int> Samples { get; set; } = [];
    public DateTimeOffset? HostFullAt { get; set; }
}

/// <summary>What a transition published, flat so that it round-trips without type names: <see cref="Kind"/> is granted, held or expired.</summary>
public sealed class EmittedNote
{
    public string Kind { get; set; } = "";
    public string Item { get; set; } = "";
    public int Generation { get; set; }
    public string SlotId { get; set; } = "";
    public string Runtime { get; set; } = "";
    public string? Host { get; set; }
    public string SlotKind { get; set; } = "";
    public string Phase { get; set; } = "";
    public string Code { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? By { get; set; }
    public DateTimeOffset At { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public object ToMessage() => Kind switch
    {
        "granted" => new SlotGranted(ItemId.Parse(Item), Generation, SlotId, Runtime, Host, SlotKind, Phase, At, ExpiresAt),
        "held" => new Held(ItemId.Parse(Item), Generation, Code, Detail, By is null ? null : ItemId.Parse(By), At),
        _ => new SlotExpired(ItemId.Parse(Item), Generation, SlotId, Runtime, Host, At),
    };
}

public sealed class CapacityState
{
    public string Key { get; set; } = CapacityKeys.Factory;
    /// <summary>The UTC day (yyyy-MM-dd) <see cref="ExecutionsToday"/> counts.</summary>
    public string Day { get; set; } = "";
    /// <summary>Executions started today by runtime (grants, less those refused as host-full): the day budget of the v2 ledger.</summary>
    public Dictionary<string, int> ExecutionsToday { get; set; } = [];
    /// <summary>The slots granted, by qualified item. An item holds at most one.</summary>
    public Dictionary<string, LedgerSlot> Slots { get; set; } = [];
    public Dictionary<string, WaitingRequest> Queue { get; set; } = [];
    public Dictionary<string, InFlightItem> InFlight { get; set; } = [];
    public Dictionary<string, HoldRecord> Holds { get; set; } = [];
    public Dictionary<string, LedgerHost> Hosts { get; set; } = [];
    /// <summary>The slots granted so far: the number of the next slot id.</summary>
    public long SlotSeq { get; set; }
    /// <summary>When the one pending timer is due (null: none). A timer that fires with another due time is stale: it plans again and clears nothing.</summary>
    public DateTimeOffset? WakeDue { get; set; }
    /// <summary>The business keys applied, the newest last; the oldest are dropped past <see cref="CapacitySettings.KeepKeys"/>.</summary>
    public List<string> Keys { get; set; } = [];
    /// <summary>The key of the last applied input and what it published: a redelivery of that input publishes the same again.</summary>
    public string LastKey { get; set; } = "";
    public List<EmittedNote> Emitted { get; set; } = [];

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static CapacityState FromJson(string json) => JsonSerializer.Deserialize<CapacityState>(json, Json) ?? new CapacityState();
    public CapacityState Copy() => FromJson(ToJson());

    public int SlotsOf(string runtime) => Slots.Values.Count(s => s.Runtime == runtime);
    public int SlotsOn(string host) => Slots.Values.Count(s => s.Host == host);
}
