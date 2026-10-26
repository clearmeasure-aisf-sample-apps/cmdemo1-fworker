using System.Security.Cryptography;
using System.Text;

// The small HTTP helpers of the routes, moved verbatim out of Program.cs (C3); Program.cs has `using static HttpResultsX;`, so every call site reads as before.
internal static class HttpResultsX
{
    internal static bool FixedEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    internal static async Task Refuse(HttpContext c, int status, string code, string reason, int exit = 30) { c.Response.StatusCode = status; await c.Response.WriteAsJsonAsync(new { ok = false, code, reason, exit }); }
    internal static long ParseCursor(string header, string epoch)
    {
        var i = header.LastIndexOf('.');
        return i > 0 && header[..i] == epoch && long.TryParse(header[(i + 1)..], System.Globalization.NumberStyles.None, null, out var n) ? n : 0;
    }
    internal static IResult Fail(int status, string code, string reason, int exit) => Results.Json(new { ok = false, code, reason, exit }, statusCode: status);
    internal static async Task<(string Text, bool TooLarge)> ReadBody(HttpContext ctx, int max)
    {
        var buf = new byte[max + 1]; var n = 0; int r;
        while (n <= max && (r = await ctx.Request.Body.ReadAsync(buf.AsMemory(n, max + 1 - n))) > 0) n += r;
        return n > max ? ("", true) : (Encoding.UTF8.GetString(buf, 0, n), false);
    }
}
