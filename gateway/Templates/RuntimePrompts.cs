using System.Text;
using System.Text.RegularExpressions;

// The prompts of the Cursor and Copilot runtimes as services (task C5b, docs/design/gateway-composition.md 3 B and G). They replace the prompt
// functions Program.cs set on the runtimes after Build(): the runtimes take a builder as a constructor parameter. The logic is here; the text is the
// embedded resource runtime-prompts.txt (Gateway.csproj, LogicalName). Not prompts/: PromptStore parses and hashes every file there (/_build's
// promptsHash) and serves it through get_prompt. The bootstrap prompt a runtime starts from is still read from prompts/ through PromptStore.
// tests/Gateway.Tests/RuntimePromptTests.cs pins every rendered text against the one Program.cs produced.

// item, phase, why (null: a new agent; else a follow-up run of the owner agent, with what changed) -> the text of a Cursor agent's run
interface ICursorPromptBuilder
{
    string Build(int item, string phase, string? why);
}

// item, phase, one-time code, the item's open pull request, why (null: a new task; else a follow-up session, with what changed) -> the prompt
interface ICopilotPromptBuilder
{
    string Build(int item, string phase, string code, ItemPr? pr, string? why);
}

// The sections of runtime-prompts.txt. A section's text is its lines, each with its newline; {name} is filled in one pass, so a value that holds
// a placeholder is left as it is (as the interpolated strings did).
sealed partial class RuntimePromptTemplates
{
    readonly Dictionary<string, string> sections;
    static readonly Lazy<RuntimePromptTemplates> embedded = new(() => Parse(Resource("runtime-prompts.txt")));

    RuntimePromptTemplates(Dictionary<string, string> sections) => this.sections = sections;

    public static RuntimePromptTemplates Embedded => embedded.Value;
    public IReadOnlyCollection<string> Names => sections.Keys;

    public static RuntimePromptTemplates Parse(string text)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var count = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;   // the file's last newline ends its last line, it does not start another
        string? name = null; var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (lines[i].StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (name != null) sections.Add(name, sb.ToString());
                name = lines[i][3..].Trim(); sb.Clear();
                continue;
            }
            if (name != null) sb.Append(lines[i]).Append('\n');   // before the first section: comments
        }
        if (name != null) sections.Add(name, sb.ToString());
        return new RuntimePromptTemplates(sections);
    }

    public string Render(string section, IReadOnlyDictionary<string, string> values)
    {
        if (!sections.TryGetValue(section, out var text)) throw new InvalidOperationException($"runtime-prompts.txt has no section {section}");
        return Placeholder().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : throw new InvalidOperationException($"runtime-prompts.txt, section {section}: no value for {m.Value}"));
    }

    [GeneratedRegex(@"\{([A-Za-z]+)\}")]
    private static partial Regex Placeholder();

    static string Resource(string name)
    {
        using var st = typeof(RuntimePromptTemplates).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"the embedded resource {name} is missing from the gateway assembly");
        using var rd = new StreamReader(st, Encoding.UTF8);
        return rd.ReadToEnd();
    }

    public static bool IsDevelopment(string phase) => phase.Trim().Equals("Development", StringComparison.OrdinalIgnoreCase);
}

// The Copilot cloud agent's task prompt (gateway/Runtimes/CopilotCloudRuntime.cs): bootstrap/copilot.md for the copilot-cloud-agent, the item's pull
// request where it has one, and the connect lines with the gateway URL (api.copilot.gatewayUrl) and the one-time code.
sealed class CopilotPromptBuilder(PromptStore prompts, CopilotCloudSettings settings) : ICopilotPromptBuilder
{
    public string Build(int item, string phase, string code, ItemPr? pr, string? why)
    {
        var t = RuntimePromptTemplates.Embedded;
        var v = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["item"] = item.ToString(), ["phase"] = phase, ["slug"] = RuntimeRegistry.PhaseSlug(phase), ["code"] = code, ["gatewayUrl"] = settings.GatewayUrl,
            ["pr"] = pr?.Number.ToString() ?? "", ["branch"] = pr?.Branch ?? "",
        };
        // one Copilot MCP configuration serves every environment: the prompt names this gateway (fworker-mcp accepts only the three gateway hosts)
        var connect = t.Render("copilot-connect", v);
        // a follow-up session of the owner task (why != null): terse, self-contained (phase, item, pull request, what changed), so its correctness does
        // not depend on Copilot's memory of the earlier sessions; then the same connect lines as a first session
        if (why != null && pr != null)
        {
            // a comment session reads the comment as a request to address: the registration comes FIRST, before any reading or change (a follow-up
            // that never connected did its work unseen, or none, and was counted as failed: #415, #416 on 2026-10-21)
            // {item} is filled after these two are put together, over their whole text, values included: that is the order the prompt had
            var late = new Dictionary<string, string>(v, StringComparer.Ordinal) { ["item"] = "{item}" };
            var f = (t.Render("copilot-followup-first", late) + t.Render("copilot-followup-head", late)).Replace("{item}", item.ToString());
            if (why.Trim() != "") f += t.Render("what-changed", new Dictionary<string, string>(v, StringComparer.Ordinal) { ["why"] = why.Trim() });
            f += RuntimePromptTemplates.IsDevelopment(phase) ? t.Render("copilot-followup-development", v)
                : phase.Trim().Equals("Functional Testing", StringComparison.OrdinalIgnoreCase) ? t.Render("copilot-followup-functional-testing", v)
                : t.Render("copilot-followup-other", v);
            return f + "\n" + Bootstrap() + connect;
        }
        var text = Bootstrap();
        // an item with its open pull request: the task starts from that branch (CopilotCloudRuntime.BaseRef), and the fix goes to that pull request
        if (pr != null) text += t.Render(RuntimePromptTemplates.IsDevelopment(phase) ? "copilot-pr-development" : "copilot-pr-other", v);
        return text + connect;
    }

    string Bootstrap() => prompts.ByPath("bootstrap/copilot.md") is { } bp ? prompts.Render(bp, "copilot-cloud-agent")["text"]!.ToString() : "";
}

// A Cursor agent's run (gateway/Runtimes/CursorCloudRuntime.cs): bootstrap/api.md for the cursor-cloud-agent and the work item's line.
sealed class CursorPromptBuilder(PromptStore prompts) : ICursorPromptBuilder
{
    public string Build(int item, string phase, string? why)
    {
        var t = RuntimePromptTemplates.Embedded;
        var v = new Dictionary<string, string>(StringComparer.Ordinal) { ["item"] = item.ToString(), ["phase"] = phase, ["slug"] = RuntimeRegistry.PhaseSlug(phase) };
        var text = prompts.ByPath("bootstrap/api.md") is { } bp ? prompts.Render(bp, "cursor-cloud-agent")["text"]!.ToString() : "";
        var tail = t.Render("cursor-work-item", v);
        if (why == null) return text + tail;
        // a follow-up run of the owner agent: terse and self-contained (phase, item, what changed); the rules are repeated, its MCP server has a new r1
        var f = t.Render("cursor-followup", v);
        if (why.Trim() != "") f += t.Render("what-changed", new Dictionary<string, string>(v, StringComparer.Ordinal) { ["why"] = why.Trim() });
        if (RuntimePromptTemplates.IsDevelopment(phase)) f += t.Render("cursor-followup-development", v);
        return f + "\n" + text + tail;
    }
}
