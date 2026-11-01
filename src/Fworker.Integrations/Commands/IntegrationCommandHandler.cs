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
    /// <summary>The error of a handling that ends because another handling of the same key holds the claim: Transient, so the caller waits and asks again.</summary>
    public const string PendingError = "another handling of this command is in progress";
    /// <summary>The error of a handling that ends because an earlier handling may have done the effect and it cannot be found out yet: Transient, the next handling looks again.</summary>
    public const string UnknownError = "the effect of an earlier handling is not known yet";

    readonly IIdempotencyLedger _ledger;
    readonly Func<TCommand, CancellationToken, Task<EffectLookup>>? _lookup;

    /// <param name="lookup">W9.10c: how to find out whether an earlier, cut-off handling did the effect (runbook runs by worker name, a pull request's merge state, a journal key). Without one an expired claim is taken over and the call runs again, which is right for a call that finds its effect already done.</param>
    protected IntegrationCommandHandler(IIdempotencyLedger ledger, Func<TCommand, CancellationToken, Task<EffectLookup>>? lookup = null) { _ledger = ledger; _lookup = lookup; }

    /// <summary>The call itself. Return a result for an answer; throw for a transport failure (it is classified). A call that finds its effect already done returns Success.</summary>
    protected abstract Task<CommandResult> Execute(TCommand command, CancellationToken ct);

    /// <summary>Looks for the effect of an earlier handling whose claim ran out. Throwing means the effect could not be looked up (it counts as unknown).</summary>
    protected virtual Task<EffectLookup> LookUpEffect(TCommand command, CancellationToken ct) => _lookup is { } l ? l(command, ct) : Task.FromResult(EffectLookup.Absent);

    /// <summary>
    /// W9.10c. The key is claimed in one statement before the side effect (<see cref="IIdempotencyLedger.Claim"/>), so two handlings of it never both call: the second ends with a Transient
    /// <see cref="PendingError"/> (the caller waits and asks again, and then finds the recorded result). A claim that ran out belongs to a handling that died or was cut off: the effect is looked up
    /// first and only when it is not there is the call made. An answer that settles is recorded (which ends the claim); a retryable answer ends the claim, since the call did not take effect; a
    /// transport failure may have taken effect, so the claim is expired at once and the next handling looks for the effect before it calls again.
    /// </summary>
    public async Task<CommandResult> Handle(TCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey)) return CommandResult.Failed(FailureKind.Permanent, $"{command.Name}: no idempotency key");
        var key = command.IdempotencyKey;
        if (_ledger.TryGet(key) is { } done) return AnsweredFromLedger(command, done);
        var claim = _ledger.Claim(key);
        switch (claim.State)
        {
            case ClaimState.Done: return AnsweredFromLedger(command, claim.Result!);
            case ClaimState.Pending: return CommandResult.Failed(FailureKind.Transient, $"{command.Name}: {PendingError}");
            case ClaimState.TakenOver:
            {
                EffectLookup found;
                try { found = await LookUpEffect(command, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { _ledger.ExpireClaim(key); throw; }
                catch (Exception e) { found = EffectLookup.Unknown(e.GetType().Name); }
                if (found.Kind == EffectKind.Found) { _ledger.Record(key, found.Result!); return found.Result!; }
                if (found.Kind == EffectKind.Unknown)
                {
                    _ledger.ExpireClaim(key);
                    return CommandResult.Failed(FailureKind.Transient, $"{command.Name}: {UnknownError}" + (found.Note == "" ? "" : $" ({found.Note})"));
                }
                break;   // Absent: the earlier handling did not get as far as the effect
            }
        }
        CommandResult result;
        try { result = await Execute(command, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { _ledger.ExpireClaim(key); throw; }   // the host stops: the effect may be half done, the next process looks
        catch (Exception e)
        {
            result = ErrorClassifier.ToResult(ErrorClassifier.Classify(e), $"{command.Name}: {e.Message}");
            // a transport failure may have reached the other side: the next handling looks for the effect first
            if (result.Failure == FailureKind.Transient) { _ledger.ExpireClaim(key); return result; }
        }
        // only a settled outcome is remembered (and ends the claim): a retryable failure must run again. A refusal of the credential (401, a 403 that is no rate limit) is a fault of the
        // environment that a fix clears, not an outcome of the request: it is answered to the caller as Permanent but never remembered, so the next handling after the fix calls the service again
        if (result.Failure == FailureKind.Permanent && AuthFaults.Is(result)) _ledger.ReleaseClaim(key);
        else if (result.Ok || result.Failure is FailureKind.Permanent or FailureKind.NotSupported) _ledger.Record(key, result);
        else _ledger.ReleaseClaim(key);
        return result;
    }

    /// <summary>A re-request whose key the ledger already holds is answered from it and runs nothing: its own outcome in the log, so a request that never runs (a key that should have changed) can be seen (W9.10o).</summary>
    static CommandResult AnsweredFromLedger(TCommand command, CommandResult done)
    {
        try { Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["command"] = command.Name, ["outcome"] = "ledger-answered", ["ok"] = done.Ok }.ToJsonString()); } catch { }
        return done;
    }

    /// <summary>The message for the saga when the result is a failure that will not be retried here; null when it is a success or will be retried.</summary>
    public static IntegrationFailed? FailureMessage(TCommand command, CommandResult result) =>
        result.Ok || result.Failure is FailureKind.Transient or FailureKind.RateLimited ? null : IntegrationFailed.From(command, result);
}

/// <summary>
/// Which permanent failures are faults of the credential or its permission (a 401 after a token refresh, a 403 that is no rate limit) and not outcomes of the request. They clear when the credential or the
/// permission is fixed, so the ledger must not freeze them (the outage of the missing User-Agent stored a 403 under every merge and journal key, and a resume after the fix was answered from the ledger).
/// Only what is provably that class matches: the merge outcome's reason code, and the text of a GitHub 401 or 403 the journal store throws. A 404 or 422 is never one.
/// </summary>
public static class AuthFaults
{
    static readonly System.Text.RegularExpressions.Regex Text = new(@"^[A-Za-z]+: GitHub (401|403) on |^[A-Za-z]+: 401 after a token refresh", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool Is(CommandResult r)
    {
        if (r.Ok || r.Failure != FailureKind.Permanent) return false;
        var e = r.Error ?? "";
        if (e.StartsWith("merge/", StringComparison.Ordinal))
        {
            try { return System.Text.Json.Nodes.JsonNode.Parse(e["merge/".Length..])?["Reason"]?.GetValue<string>() == "github-forbidden"; }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return false; }
        }
        return Text.IsMatch(e);
    }
}

public enum EffectKind { Absent, Found, Unknown }

/// <summary>What looking for the effect of an earlier handling found: Found (with the result that handling would have recorded), Absent (it did not happen, so the call may be made) or Unknown (it cannot be told now).</summary>
public readonly record struct EffectLookup(EffectKind Kind, CommandResult? Result = null, string Note = "")
{
    public static EffectLookup Absent => new(EffectKind.Absent);
    public static EffectLookup Found(CommandResult result) => new(EffectKind.Found, result);
    public static EffectLookup Unknown(string note = "") => new(EffectKind.Unknown, null, note);
}

public enum ClaimState
{
    /// <summary>The key was free: this handling holds the claim and may make the call.</summary>
    Claimed,
    /// <summary>A claim of an earlier handling had run out and this handling holds it now: look for the effect before the call.</summary>
    TakenOver,
    /// <summary>Another handling holds a live claim: wait or end.</summary>
    Pending,
    /// <summary>The key has a recorded result.</summary>
    Done,
}

/// <summary>The node and the epoch (the leader lease's) that hold a claim: a claim is only ended or expired by its owner.</summary>
public sealed record ClaimOwner(string Node, long Epoch)
{
    public static ClaimOwner ThisProcess(long epoch = 0) => new($"{Environment.MachineName}:{Environment.ProcessId}", epoch);
}

public sealed record ClaimOutcome(ClaimState State, CommandResult? Result = null, ClaimOwner? Holder = null, DateTime? ExpiresAt = null);

/// <summary>
/// Remembers the result of a key, and (W9.10c) holds the pending claim of a key whose side effect is being made. The durable one is <see cref="SqlIdempotencyLedger"/>;
/// <see cref="InMemoryIdempotencyLedger"/> serves tests and a single process.
/// </summary>
public interface IIdempotencyLedger
{
    CommandResult? TryGet(string key);
    /// <summary>The first result of a key stays. Also ends the key's claim, whoever holds it.</summary>
    void Record(string key, CommandResult result);
    /// <summary>Atomically takes the key before the side effect, unless it has a result or a live claim; an expired claim is taken over.</summary>
    ClaimOutcome Claim(string key);
    /// <summary>Ends this owner's claim of the key without a result: the call did not take effect, so the key may run again.</summary>
    void ReleaseClaim(string key);
    /// <summary>Lets this owner's claim run out now: the call may have taken effect, so the next handling looks for the effect first.</summary>
    void ExpireClaim(string key);
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

/// <summary>The cleanup of the rows an outage left (run by the hourly ledger purge; a no-op once they are gone): permanent failures that are <see cref="AuthFaults"/>. Since the handler no longer records them, every such row is from before that change.</summary>
public interface IAuthFaultPurgeable
{
    /// <summary>Forgets the recorded failures that are credential or permission faults; the number forgotten.</summary>
    int PurgeAuthFaults();
}

public sealed class InMemoryIdempotencyLedger : IIdempotencyLedger, IPurgeableLedger, IAuthFaultPurgeable
{
    /// <summary>How long a claim holds the key when its handling neither records nor releases it (a process that died): longer than any single call of a command (the HTTP clients stop at 60 s), shorter than the caller's retries (RuntimeCommandService.RetryPauses add up to 102 s), so a command whose process was killed finds the claim expired at its last retry.</summary>
    public static readonly TimeSpan DefaultClaimTtl = TimeSpan.FromSeconds(90);

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (CommandResult Result, DateTimeOffset At)> _done = new(StringComparer.Ordinal);
    readonly Dictionary<string, (ClaimOwner Owner, DateTime Expires)> _claims = new(StringComparer.Ordinal);
    readonly Func<ClaimOwner> _owner; readonly TimeSpan _ttl; readonly Func<DateTime> _utcNow;

    public InMemoryIdempotencyLedger(Func<ClaimOwner>? owner = null, TimeSpan? claimTtl = null, Func<DateTime>? utcNow = null)
    {
        var self = ClaimOwner.ThisProcess();
        _owner = owner ?? (() => self); _ttl = claimTtl ?? DefaultClaimTtl; _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public CommandResult? TryGet(string key) => _done.TryGetValue(key, out var r) ? r.Result : null;

    public void Record(string key, CommandResult result)
    {
        lock (_claims) { _done.TryAdd(key, (result, DateTimeOffset.UtcNow)); _claims.Remove(key); }
    }

    public int PurgeSuccesses(TimeSpan retention, DateTimeOffset now)
    {
        lock (_claims)
        {
            var n = 0;
            foreach (var kv in _done.Where(kv => kv.Value.Result.Ok && kv.Value.At < now - retention).ToList()) if (_done.TryRemove(kv.Key, out _)) n++;
            return n;
        }
    }

    public int PurgeAuthFaults()
    {
        lock (_claims)
        {
            var n = 0;
            foreach (var kv in _done.Where(kv => AuthFaults.Is(kv.Value.Result)).ToList()) if (_done.TryRemove(kv.Key, out _)) n++;
            return n;
        }
    }

    public ClaimOutcome Claim(string key)
    {
        lock (_claims)
        {
            if (_done.TryGetValue(key, out var r)) return new(ClaimState.Done, r.Result);
            var now = _utcNow();
            if (_claims.TryGetValue(key, out var cur) && cur.Expires > now) return new(ClaimState.Pending, Holder: cur.Owner, ExpiresAt: cur.Expires);
            var taken = _claims.ContainsKey(key);
            _claims[key] = (_owner(), now + _ttl);
            return new(taken ? ClaimState.TakenOver : ClaimState.Claimed);
        }
    }

    public void ReleaseClaim(string key) { lock (_claims) { if (_claims.TryGetValue(key, out var c) && c.Owner == _owner()) _claims.Remove(key); } }
    public void ExpireClaim(string key) { lock (_claims) { if (_claims.TryGetValue(key, out var c) && c.Owner == _owner()) _claims[key] = (c.Owner, DateTime.MinValue); } }
}
