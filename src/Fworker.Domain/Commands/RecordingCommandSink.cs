using Fworker.Contracts.Commands;
using Fworker.Contracts.Seams;

namespace Fworker.Domain.Commands;

// The recording sink of seam S2 (docs/design/fworker-wolverine-refactor.md 4): a command port that captures commands and executes nothing. Shadow mode
// and the golden-write comparison read what it holds. Registered in v1 but unused (nothing sends to it).
public sealed class RecordingCommandSink : ICommandPort
{
    readonly object gate = new();
    readonly List<IntegrationCommand> commands = new();

    /// <summary>The commands recorded so far, in the order they were sent.</summary>
    public IReadOnlyList<IntegrationCommand> Commands { get { lock (gate) return commands.ToList(); } }

    public Task<CommandResult> SendAsync(IntegrationCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (gate) commands.Add(command);
        return Task.FromResult(CommandResult.Success());
    }

    /// <summary>The recorded commands of one stable name ("StartRuntimeSession", ...).</summary>
    public IReadOnlyList<IntegrationCommand> Of(string name) { lock (gate) return commands.Where(c => c.Name == name).ToList(); }

    /// <summary>One line per command, "Name key", the form the golden comparison diffs against v1's writes.</summary>
    public IReadOnlyList<string> Lines() { lock (gate) return commands.Select(c => c.Name + " " + c.IdempotencyKey).ToList(); }

    public void Clear() { lock (gate) commands.Clear(); }
}
