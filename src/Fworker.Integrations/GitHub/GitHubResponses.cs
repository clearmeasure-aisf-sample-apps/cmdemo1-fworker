using System.Globalization;
using Fworker.Integrations.Commands;

namespace Fworker.Integrations.GitHub;

/// <summary>The one place that turns a GitHub answer into what <see cref="ErrorClassifier"/> reads: status, body, Retry-After and the rate-limit headers.</summary>
internal static class GitHubResponses
{
    internal static HttpOutcome ToOutcome(HttpResponseMessage resp, string body)
    {
        static string? H(HttpResponseMessage r, string h) => r.Headers.TryGetValues(h, out var v) ? v.FirstOrDefault() : null;
        var reset = long.TryParse(H(resp, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var rs) ? DateTimeOffset.FromUnixTimeSeconds(rs) : (DateTimeOffset?)null;
        int? remaining = int.TryParse(H(resp, "x-ratelimit-remaining"), NumberStyles.None, CultureInfo.InvariantCulture, out var rem) ? rem : null;
        return new HttpOutcome((int)resp.StatusCode, body, resp.Headers.RetryAfter?.Delta, reset, remaining);
    }
}
