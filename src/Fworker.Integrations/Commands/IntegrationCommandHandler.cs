using Fworker.Contracts.Commands;

namespace Fworker.Integrations.Commands;

/// <summary>
/// The base of the handler of one integration command type. It runs the call, turns an exception or an answer into a <see cref="CommandResult"/> through the
/// <see cref="ErrorClassifier"/>, and answers a replay of a key it already completed with the recorded result, so the side effect happens once.
/// Subclasses implement <see cref="Execute"/> only. Which failures retry and which go to the saga as <see cref="IntegrationFailed"/> is the caller's policy
/// (<see cref="CommandResult.Failure"/>): Transient and RateLimited retry, Permanent is dead-lettered.
/// </summary>
public abstract class IntegrationCommandHandler<TCommand> where TCommand : IntegrationCommand
{
    readonly IIdempotencyLedger _ledger;
    protected IntegrationCommandHandler(IIdempotencyLedger ledger) { _ledger = ledger; }

    /// <summary>The call itself. Return a result for an answer; throw for a transport failure (it is classified). A call that finds its effect already done returns Success.</summary>
    protected abstract Task<CommandResult> Execute(TCommand command, CancellationToken ct);

    public async Task<CommandResult> Handle(TCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey)) return CommandResult.Failed(FailureKind.Permanent, $"{command.Name}: no idempotency key");
        if (_ledger.TryGet(command.IdempotencyKey) is { } done)
        {
            // a re-request whose key the ledger already holds is answered from it and runs nothing: its own outcome in the log, so a request that never runs (a key that should have changed) can be seen (W9.10o)
            try { Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["command"] = command.Name, ["outcome"] = "ledger-answered", ["ok"] = done.Ok }.ToJsonString()); } catch { }
            return done;
        }
        CommandResult result;
        try { result = await Execute(command, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { result = ErrorClassifier.ToResult(ErrorClassifier.Classify(e), $"{command.Name}: {e.Message}"); }
        // only a settled outcome is remembered: a retryable failure must run again
        if (result.Ok || result.Failure is FailureKind.Permanent or FailureKind.NotSupported) _ledger.Record(command.IdempotencyKey, result);
        return result;
    }

    /// <summary>The message for the saga when the result is a failure that will not be retried here; null when it is a success or will be retried.</summary>
    public static IntegrationFailed? FailureMessage(TCommand command, CommandResult result) =>
        result.Ok || result.Failure is FailureKind.Transient or FailureKind.RateLimited ? null : IntegrationFailed.From(command, result);
}

/// <summary>Remembers the result of a key. The durable one is the v2 outbox's table; <see cref="InMemoryIdempotencyLedger"/> serves tests and a single process.</summary>
public interface IIdempotencyLedger
{
    CommandResult? TryGet(string key);
    void Record(string key, CommandResult result);
}

/// <summary>
/// A ledger that forgets its successes after a retention period (W9.10o): a key is a request, and the same request asked again long after (a promotion of the same version after a rollback) must run. Failures that are
/// settled (permanent, not supported) are kept: forgetting one would run a call that cannot work again at every redelivery.
/// </summary>
public interface IPurgeableLedger
{
    /// <summary>Forgets the successes recorded before <c>now - retention</c>; the number forgotten.</summary>
    int PurgeSuccesses(TimeSpan retention, DateTimeOffset now);
}

public sealed class InMemoryIdempotencyLedger : IIdempotencyLedger, IPurgeableLedger
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (CommandResult Result, DateTimeOffset At)> _done = new(StringComparer.Ordinal);
    public CommandResult? TryGet(string key) => _done.TryGetValue(key, out var r) ? r.Result : null;
    public void Record(string key, CommandResult result) => _done.TryAdd(key, (result, DateTimeOffset.UtcNow));
    public int PurgeSuccesses(TimeSpan retention, DateTimeOffset now)
    {
        var n = 0;
        foreach (var kv in _done.Where(kv => kv.Value.Result.Ok && kv.Value.At < now - retention).ToList()) if (_done.TryRemove(kv.Key, out _)) n++;
        return n;
    }
}
