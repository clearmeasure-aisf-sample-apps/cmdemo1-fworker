using System.Security.Cryptography;
using System.Text;

namespace Fworker.Integrations.Commands;

/// <summary>
/// Idempotency keys of integration commands: the same side effect has the same key, so a replayed command does nothing twice (the outbox deduplicates on it).
/// A key is built from the command's name and the business facts that identify the effect (repository, item, line, phase, attempt), never a time or a random value.
/// Parts are length-prefixed, so ("a","bc") and ("ab","c") differ.
/// </summary>
public static class IdempotencyKeys
{
    public static string For(string commandName, params object?[] parts)
    {
        var sb = new StringBuilder(commandName);
        foreach (var p in parts)
        {
            var s = Convert.ToString(p, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            sb.Append('|').Append(s.Length).Append(':').Append(s);
        }
        return sb.ToString();
    }

    /// <summary>A short, fixed-length form of a key (a comment marker, a ref name): the first 16 hex digits of its SHA-256.</summary>
    public static string Short(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
}
