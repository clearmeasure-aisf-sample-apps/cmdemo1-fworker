using Fworker.Contracts.Commands;

namespace Fworker.Integrations.Commands;

/// <summary>What the caller of a failed call does next (docs/design/wolverine-factory.md 4.4, the table "Error classification before any dead letter").</summary>
public enum ErrorAction
{
    /// <summary>The call worked, or the thing is already there (409 or 422 "already exists") for an idempotent command: record and continue.</summary>
    Success,
    /// <summary>Retry with cooldown and jitter (5xx, timeout, a 401 after an installation token expired once).</summary>
    Retry,
    /// <summary>Reschedule at <see cref="Classification.RetryAfter"/> or the reset; pause that API.</summary>
    Reschedule,
    /// <summary>A real refusal: <see cref="IntegrationFailed"/> to the saga, the command is dead-lettered.</summary>
    Fail,
}

/// <summary>The verdict on one answer. <see cref="Kind"/> maps to the <see cref="FailureKind"/> of a <see cref="CommandResult"/>.</summary>
public sealed record Classification(ErrorAction Action, FailureKind Kind, TimeSpan? RetryAfter = null, string Reason = "");

/// <summary>What the classifier needs of an HTTP answer, without tying it to HttpResponseMessage (the tests and the clients both build it).</summary>
public sealed record HttpOutcome(int Status, string Body = "", TimeSpan? RetryAfter = null, DateTimeOffset? RateLimitReset = null, int? RateLimitRemaining = null, bool TimedOut = false);

/// <summary>Classifies an answer as transient (retry), rate-limited (reschedule) or permanent (fail), the one rule every integration command uses.</summary>
public static class ErrorClassifier
{
    public static Classification Classify(HttpOutcome o, bool idempotentCommand, bool tokenRefreshed = false, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        if (o.TimedOut) return new(ErrorAction.Retry, FailureKind.Transient, null, "timeout");
        if (o.Status is >= 200 and < 300) return new(ErrorAction.Success, FailureKind.None);
        if (o.Status >= 500) return new(ErrorAction.Retry, FailureKind.Transient, null, $"http {o.Status}");
        if (o.Status == 401)
            // an installation token lasts one hour: the first 401 is an expired token (refresh and retry), a second one after the refresh is a bad credential
            return tokenRefreshed ? new(ErrorAction.Fail, FailureKind.Permanent, null, "401 after a token refresh") : new(ErrorAction.Retry, FailureKind.Transient, null, "401, refresh the token");
        if ((o.Status is 403 or 429) && RateLimitWait(o, at) is { } wait) return new(ErrorAction.Reschedule, FailureKind.RateLimited, wait, o.Status == 429 ? "429" : "403 rate limit");
        if ((o.Status is 409 or 422) && idempotentCommand && IsAlreadyThere(o.Body)) return new(ErrorAction.Success, FailureKind.None, null, "already exists");
        if (o.Status == 408) return new(ErrorAction.Retry, FailureKind.Transient, null, "http 408");
        return new(ErrorAction.Fail, FailureKind.Permanent, null, $"http {o.Status}");
    }

    /// <summary>A transport failure: a timeout or a dropped connection is transient; anything else is a bug and permanent.</summary>
    public static Classification Classify(Exception e) => e switch
    {
        TaskCanceledException or TimeoutException or HttpRequestException or IOException => new(ErrorAction.Retry, FailureKind.Transient, null, e.GetType().Name),
        _ => new(ErrorAction.Fail, FailureKind.Permanent, null, e.GetType().Name),
    };

    public static CommandResult ToResult(Classification c, string error) =>
        c.Action == ErrorAction.Success ? CommandResult.Success() : CommandResult.Failed(c.Kind, string.IsNullOrEmpty(error) ? c.Reason : error);

    // 429 always means wait; 403 only when it carries Retry-After, an exhausted primary limit (remaining 0 with a reset) or a secondary-limit message.
    static TimeSpan? RateLimitWait(HttpOutcome o, DateTimeOffset now)
    {
        if (o.RetryAfter is { } ra) return ra < TimeSpan.Zero ? TimeSpan.Zero : ra;
        if (o.RateLimitRemaining == 0 && o.RateLimitReset is { } reset) return reset > now ? reset - now : TimeSpan.Zero;
        if (o.Body.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase) || o.Body.Contains("rate limit exceeded", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromMinutes(1);
        return o.Status == 429 ? TimeSpan.FromSeconds(30) : null;
    }

    static bool IsAlreadyThere(string body) =>
        body.Contains("already exists", StringComparison.OrdinalIgnoreCase) || body.Contains("already queued", StringComparison.OrdinalIgnoreCase) || body.Contains("already_exists", StringComparison.OrdinalIgnoreCase);
}
