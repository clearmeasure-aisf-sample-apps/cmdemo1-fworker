using System.Text.Json.Nodes;

namespace Gateway.Tests;

/// <summary>
/// The arguments the prompts of the Cursor and Copilot runtimes are rendered with in <see cref="RuntimePromptTests"/>, and the bootstrap prompt
/// files they are rendered from. The texts in Pins/runtime-prompts.json were recorded from master before the prompt builders moved out of Program.cs
/// (task C5b, docs/design/gateway-composition.md 3 B and G): the move must not change one byte of a prompt. A deliberate change of a prompt records
/// new texts in the same pull request.
/// </summary>
public static class RuntimePromptCases
{
    public const int Item = 7;
    public const string Code = "c0de42";
    public const string GatewayUrl = "https://gw.example";

    // every phase branch of the builders, a phase that needs trimming, and one with a placeholder in it (the Copilot follow-up replaces {item} after
    // the text is put together, so a value that holds {item} shows whether that order was kept)
    static readonly string[] Phases = { "Development", "Functional Testing", "Technical Design", " development ", "Col {item}" };
    static readonly string?[] Whys = { null, "", "  the checks failed  ", "asks {item} {code} {why}" };
    static readonly (int Number, string Branch)?[] Prs = { null, (42, "copilot/fix-7"), (43, "copilot/{item}-x") };

    /// <summary>Copilot: (id, phase, pull request, why).</summary>
    public static IEnumerable<(string Id, string Phase, (int Number, string Branch)? Pr, string? Why)> Copilot() =>
        from ph in Phases from pr in Prs from why in Whys
        select ($"copilot|{ph}|{(pr is { } p ? $"#{p.Number} {p.Branch}" : "no-pr")}|{(why == null ? "new" : $"why:{why}")}", ph, pr, why);

    /// <summary>Cursor: (id, phase, why).</summary>
    public static IEnumerable<(string Id, string Phase, string? Why)> Cursor() =>
        from ph in Phases from why in Whys
        select ($"cursor|{ph}|{(why == null ? "new" : $"why:{why}")}", ph, why);

    /// <summary>
    /// Writes the bootstrap prompts the builders read (bootstrap/copilot.md, bootstrap/api.md), with an include and a section per agent, so the
    /// pinned texts do not change with the real prompts/. <paramref name="withBootstrap"/> false leaves the directory empty: the builders then
    /// render without a bootstrap text.
    /// </summary>
    public static string WritePrompts(string dir, bool withBootstrap)
    {
        Directory.CreateDirectory(dir);
        if (!withBootstrap) return dir;
        Directory.CreateDirectory(Path.Combine(dir, "bootstrap"));
        Directory.CreateDirectory(Path.Combine(dir, "common"));
        File.WriteAllText(Path.Combine(dir, "common", "pin.md"),
            "---\nrole: common\nphase: pin\nversion: 1\ntools: []\nincludes: []\n---\nShared rules of the pin, kept literal: {item} {code}.\n");
        File.WriteAllText(Path.Combine(dir, "bootstrap", "copilot.md"),
            "---\nrole: bootstrap\nphase: api-copilot\nversion: 3\ntools: [next_event, item_accept]\nincludes: [common/pin.md]\nagents:\n  copilot-cloud-agent: \"Only for the Copilot agent.\"\n  cursor-cloud-agent: \"Never in a Copilot prompt.\"\n---\n" +
            "Copilot bootstrap, line one.\n\nLine three, with `code` and a #{item} that stays as written.\n");
        File.WriteAllText(Path.Combine(dir, "bootstrap", "api.md"),
            "---\nrole: bootstrap\nphase: api\nversion: 5\ntools: [next_event]\nincludes: []\nagents:\n  cursor-cloud-agent: \"Only for the Cursor agent.\"\n---\n" +
            "Cursor bootstrap, line one.\nLine two.\n");
        return dir;
    }

    /// <summary>The pinned texts: { "bootstrap": { id: text }, "empty": { id: text } }.</summary>
    public static JsonObject Pins() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pins", "runtime-prompts.json")))!;
}

/// <summary>A gateway whose prompts are <see cref="RuntimePromptCases.WritePrompts"/>'s, with the Copilot gateway URL set (the runtimes stay off).</summary>
public class PromptPinGateway : GatewayFixture
{
    protected virtual bool WithBootstrap => true;

    protected override void Configure(GatewayEnvironment env)
    {
        env.Set("FACTORY_PROMPTS", RuntimePromptCases.WritePrompts(Path.Combine(env.Dir, "prompts-pin"), WithBootstrap));
        env.Set("FACTORY_COPILOT_GATEWAY_URL", RuntimePromptCases.GatewayUrl);
    }
}

/// <summary>The same, with an empty prompts directory: no bootstrap text.</summary>
public sealed class PromptPinGatewayNoBootstrap : PromptPinGateway
{
    protected override bool WithBootstrap => false;
}
