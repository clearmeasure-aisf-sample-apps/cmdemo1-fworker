using Fworker.Contracts.Seams;

// W9.10l: the one lock of an item. Every port that writes an item's state (the item verbs, the read models, the keeper and manager verbs, the journal ledger and, as they land, the QC and delivery ports) takes an IItemLock, and the gateway
// has exactly one implementation of it: FileItemLock. It is the file `item-N.lock` of the state directory that the rule scripts lock too (Use-ItemLock), so a v2 verb, a journal line and a script that touch one item exclude one
// another for as long as scripts exist. When the scripts go (W9.11b) this class is replaced, in place, by an in-process lock (the single actor of the SQL lease guarantees one process) or a database lock; no port changes, because
// all of them hold only the interface. An architecture test (tests/Gateway.Tests/ArchitectureInventoryTests.cs) keeps it one implementation and one construction of it per process.

/// <summary>Where the rule scripts keep their files (Paths.ps1): FACTORY_STATE_DIR, else the state folder of the data directory.</summary>
static class RuleStateDirectory
{
    public static string Resolve(Func<string, string?> env)
    {
        if (env("FACTORY_STATE_DIR") is { Length: > 0 } s) return s;
        if (env("FACTORY_DATA_DIR") is { Length: > 0 } d) return Path.Combine(d, "state");
        foreach (var home in new[] { env("HOME"), env("USERPROFILE"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) })
        {
            if (string.IsNullOrEmpty(home) || !Directory.Exists(home)) continue;
            var dir = Path.Combine(home, ".local", "share", "factory-worker");
            try { Directory.CreateDirectory(dir); return Path.Combine(dir, "state"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return Path.Combine(Path.GetTempPath(), "factory-worker", "state");
    }
}

/// <summary><see cref="IItemLock"/> as <c>Use-ItemLock</c>: the file <c>item-N.lock</c> of the state directory, opened exclusively; the same file the rule scripts lock, so v1 and v2 exclude one another.</summary>
sealed class FileItemLock(string stateDir, Func<TimeSpan, CancellationToken, Task>? pause = null) : IItemLock
{
    readonly Func<TimeSpan, CancellationToken, Task> sleep = pause ?? ((t, ct) => Task.Delay(t, ct));

    // The items whose lock the current flow of work holds. A verb that holds an item's lock and calls another verb of the same item through the router (copilot-settle's takeover calls open-pr, an item verb) is one
    // operation: the nested call runs under the lock the flow has, as the one script process did. Without this the nested call waited its 45 seconds on a lock its own caller held, was refused item-busy, and the
    // takeover of a Copilot task's draft never happened (W9.9c prep: the suite copilot on v2). Another flow of work is a stranger, as before: the lock is the file's, exclusive.
    static readonly AsyncLocal<IReadOnlyList<(string Dir, int Item)>?> held = new();

    public async Task<T> Use<T>(int item, Func<Task<T>> body, int waitSeconds, CancellationToken ct)
    {
        var key = (stateDir, item);
        var outer = held.Value;
        if (outer is not null && outer.Contains(key)) return await body();
        Directory.CreateDirectory(stateDir);
        var path = Path.Combine(stateDir, $"item-{item}.lock");
        var until = DateTime.UtcNow.AddSeconds(waitSeconds);
        FileStream fs;
        while (true)
        {
            try { fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException)
            {
                if (DateTime.UtcNow > until) throw new ItemBusyException(item);
                await sleep(TimeSpan.FromMilliseconds(300), ct);
            }
        }
        using (fs)
        {
            held.Value = [.. outer ?? [], key];
            try { return await body(); }
            finally { held.Value = outer; }
        }
    }
}

static class ItemLocks
{
    /// <summary>The gateway's one lock (registered by AddGatewayCore); a table or a test built without the gateway's services gets the file lock of the state directory <paramref name="env"/> names, which is the same lock.</summary>
    public static IItemLock From(IServiceProvider services, Func<string, string?> env) =>
        services.GetService(typeof(IItemLock)) as IItemLock ?? Default(env);

    /// <summary>The file lock of the state directory <paramref name="env"/> names: what a table built without the gateway's services (a test) takes.</summary>
    public static IItemLock Default(Func<string, string?> env) => new FileItemLock(RuleStateDirectory.Resolve(env));
}
