using System.Text;

// The dashboard page (GET /dashboard), from two embedded resources of this assembly (Gateway.csproj, LogicalName), as gateway/Mcp/fworker-mcp.mjs is
// served. Not wwwroot: Tier 1 runs the build output in Production (tests/scenarios/lib.ps1:42), where static web assets of the project directory are
// not mapped. Not prompts/: PromptStore parses and hashes every file there. (docs/design/gateway-composition.md 3 G)
//   dashboard.html     the page, exactly as served, except that the connect-prompt generator is the one line /*connect-prompt*/
//   connect-prompt.js  that generator (buildPrompt through operatorPrompt), whole lines with the last newline, so that the page's text is exactly the
//                      former raw string. It has no header on purpose: its bytes are the page's. Tier 1 and docs/deploy/gen-connect-prompt.sh run it
//                      from the served page (between /*render-start*/ and /*render-end*/), not from this file.
// Both files are ASCII with LF line endings and dashboard.html ends without a newline: tests/Gateway.Tests/DashboardPageTests.cs pins the served
// bytes and the rendered prompts.
static class DashboardPage
{
    const string Marker = "/*connect-prompt*/\n";

    public static readonly string Html = Load();

    static string Load()
    {
        var page = Resource("dashboard.html");
        var at = page.IndexOf(Marker, StringComparison.Ordinal);
        // exactly once: a second marker (or none) would serve a page without the generator, or with half of it, and nothing else would notice
        if (at < 0 || page.IndexOf(Marker, at + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("the embedded dashboard.html must hold the line /*connect-prompt*/ exactly once");
        return string.Concat(page.AsSpan(0, at), Resource("connect-prompt.js"), page.AsSpan(at + Marker.Length));
    }

    static string Resource(string name)
    {
        using var st = typeof(DashboardPage).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"the embedded resource {name} is missing from the gateway assembly");
        // UTF-8 without byte-order-mark detection: the text is the file's bytes, nothing taken off and nothing added
        using var rd = new StreamReader(st, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        return rd.ReadToEnd();
    }
}
