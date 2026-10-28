// W4.0: the recurring jobs of the durable ingress (design W4.3) as slots only: a name and an interval, nothing scheduled and no handler. W4.3 fills a slot by
// naming its handler here. Attached to the Wolverine host only where the engine acts (Engine.V2Acts), so shadow (tdd) and v1 register nothing.
sealed record RecurringJobSlot(string Name, TimeSpan Every);

sealed class RecurringJobSlots(IReadOnlyList<RecurringJobSlot> slots)
{
    public const string BoardReconciler = "board-reconciler", CatalogReread = "catalog-reread";
    public IReadOnlyList<RecurringJobSlot> Slots { get; } = slots;
    public static RecurringJobSlots Default() => new([new(BoardReconciler, TimeSpan.FromMinutes(5)), new(CatalogReread, TimeSpan.FromMinutes(15))]);

    /// <summary>Adds the slots to the host's services and nothing else (no job is scheduled). Returns the slots registered.</summary>
    public static RecurringJobSlots Attach(WolverineHostService host)
    {
        var slots = Default(); var previous = host.RuntimeCommands;
        host.RuntimeCommands = (services, w) => { previous?.Invoke(services, w); services.AddSingleton(slots); };
        return slots;
    }
}
