using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The prompts of the Cursor and Copilot runtimes, pinned byte for byte against the texts Program.cs rendered before task C5b moved them into
/// services (gateway/Templates/RuntimePrompts.cs, the text in runtime-prompts.txt). Pins/runtime-prompts.json holds every case of
/// <see cref="RuntimePromptCases"/>, with the bootstrap prompts and without.
/// </summary>
public sealed class RuntimePromptTests
{
    [Theory]
    [InlineData(true, "bootstrap")]
    [InlineData(false, "empty")]
    public void Builders_RenderEveryCaseAsPinned(bool withBootstrap, string set)
    {
        var dir = Path.Combine(Path.GetTempPath(), "runtime-prompts-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var prompts = new PromptStore(RuntimePromptCases.WritePrompts(dir, withBootstrap));
            Assert.Empty(prompts.Errors);
            RuntimePromptAssert.AllPinned(new CopilotPromptBuilder(prompts, new CopilotCloudSettings { GatewayUrl = RuntimePromptCases.GatewayUrl }), new CursorPromptBuilder(prompts), set);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Template_HasExactlyTheSectionsTheBuildersUse()
    {
        var names = RuntimePromptTemplates.Embedded.Names.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "copilot-connect", "copilot-followup-development", "copilot-followup-first", "copilot-followup-functional-testing", "copilot-followup-head",
            "copilot-followup-other", "copilot-pr-development", "copilot-pr-other", "cursor-followup", "cursor-followup-development", "cursor-work-item", "what-changed",
        }, names);
    }

    [Fact]
    public void Template_Format_LinesWithTheirNewline_LeadingBlankLine_CommentsBeforeTheFirstSection_OnePassPlaceholders()
    {
        var t = RuntimePromptTemplates.Parse("# a comment {x}\n@@ a\n\nline {x}\r\n@@ b\nlast {y}");
        Assert.Equal(new[] { "a", "b" }, t.Names.Order(StringComparer.Ordinal));
        var v = new Dictionary<string, string> { ["x"] = "{y}", ["y"] = "Y" };
        Assert.Equal("\nline {y}\n", t.Render("a", v));   // a value is not read for placeholders
        Assert.Equal("last Y\n", t.Render("b", v));       // a last line without a newline gets one
        Assert.Throws<InvalidOperationException>(() => t.Render("b", new Dictionary<string, string>()));
        Assert.Throws<InvalidOperationException>(() => t.Render("c", v));
    }

    [Fact]
    public void Registry_ManagersByKind_InRuntimeLoopOrder()
    {
        var store = new PromptStore(Path.Combine(Path.GetTempPath(), "no-prompts-" + Guid.NewGuid().ToString("N")[..10]));
        var settings = new RuntimeSettings(); var nonces = new RuntimeNonces();
        var cae = new RuntimeManager(settings, new CaeJobRuntime(settings.CaeJob), nonces);
        var cursor = new RuntimeManager(settings, new CursorCloudRuntime(new CursorCloudSettings(), new CursorPromptBuilder(store)), nonces);
        var copilot = new RuntimeManager(settings, new CopilotCloudRuntime(new CopilotCloudSettings(), new CopilotCodes(), new CopilotPromptBuilder(store, new CopilotCloudSettings())), nonces);
        var octopus = new RuntimeManager(settings, new OctopusRunbookRuntime(settings.Octopus), nonces);
        var all = new RuntimeRegistry { CaeJob = cae, Cursor = cursor, Copilot = copilot, Octopus = octopus, CopilotCiRelease = new CopilotCiRelease(), CopilotCodes = new CopilotCodes() };
        Assert.Equal(new[] { cae, cursor, copilot, octopus }, all.Managers);
        foreach (var m in all.Managers) Assert.Same(m, all.ByKind(m.Kind));   // the kind the API names is the adapter's own
        Assert.Null(all.ByKind("nope"));
        Assert.Null(all.ByKind("Octopus"));
        var some = new RuntimeRegistry { Copilot = copilot, CopilotCiRelease = new CopilotCiRelease(), CopilotCodes = new CopilotCodes() };
        Assert.Equal(new[] { copilot }, some.Managers);
        Assert.Null(some.ByKind("cae-job"));
        Assert.Equal("functional-testing", RuntimeRegistry.PhaseSlug(" Functional Testing "));
    }
}

/// <summary>The builders the gateway registers, through its container: the prompt files of FACTORY_PROMPTS and the Copilot gateway URL of the settings.</summary>
public sealed class RuntimePromptGatewayTests(PromptPinGateway gw) : IClassFixture<PromptPinGateway>
{
    [Fact]
    public void RegisteredBuilders_RenderAsPinned()
    {
        if (!StubPwsh.Supported) return;
        Assert.NotNull(gw.ReadyAfter);
        var sp = gw.Host.Services;
        RuntimePromptAssert.AllPinned(sp.GetRequiredService<ICopilotPromptBuilder>(), sp.GetRequiredService<ICursorPromptBuilder>(), "bootstrap");
        Assert.Empty(sp.GetRequiredService<PromptStore>().Errors);
    }

    [Fact]
    public void Registry_IsRegistered_WithNoRuntimeOn()
    {
        if (!StubPwsh.Supported) return;
        var reg = gw.Host.Services.GetRequiredService<RuntimeRegistry>();
        Assert.Empty(reg.Managers);
        Assert.Null(reg.ByKind("octopus"));
    }

    // what the item-pr lookup made of the rule verb's answers before C5b, recorded from master
    [Theory]
    [InlineData("{\"found\":true,\"number\":12,\"branch\":\"b-12\"}", "", "12 b-12")]
    [InlineData("{\"found\":true,\"number\":\"13\",\"branch\":\"b-13\"}", "", "13 b-13")]
    [InlineData("{\"found\":false}", "", "null")]
    [InlineData("{\"found\":true,\"number\":12}", "", "null")]
    [InlineData("{\"found\":true,\"number\":12,\"branch\":\"\"}", "", "null")]
    [InlineData("{\"found\":true,\"number\":\"x\",\"branch\":\"b\"}", "", "null")]
    [InlineData("{\"found\":true,\"number\":12,\"branch\":\"b-12\"}", "1", "null")]
    [InlineData("", "", "null")]
    public async Task ItemPrLookup_AnswersAsBefore(string json, string exit, string expected)
    {
        if (!StubPwsh.Supported) return;
        var dir = gw.Env.Dir;
        lock (gw)
        {
            File.Delete(Path.Combine(dir, "item-pr.json")); File.Delete(Path.Combine(dir, "item-pr.exit"));
            if (json != "") File.WriteAllText(Path.Combine(dir, "item-pr.json"), json);
            if (exit != "") File.WriteAllText(Path.Combine(dir, "item-pr.exit"), exit);
        }
        var got = await gw.Host.Services.GetRequiredService<IItemPrLookup>().FindAsync(5, CancellationToken.None);
        Assert.Equal(expected, got == null ? "null" : $"{got.Number} {got.Branch}");
    }
}

/// <summary>The same through a gateway whose prompts directory holds no bootstrap prompt.</summary>
public sealed class RuntimePromptNoBootstrapGatewayTests(PromptPinGatewayNoBootstrap gw) : IClassFixture<PromptPinGatewayNoBootstrap>
{
    [Fact]
    public void RegisteredBuilders_RenderAsPinned()
    {
        if (!StubPwsh.Supported) return;
        Assert.NotNull(gw.ReadyAfter);
        RuntimePromptAssert.AllPinned(gw.Host.Services.GetRequiredService<ICopilotPromptBuilder>(), gw.Host.Services.GetRequiredService<ICursorPromptBuilder>(), "empty");
    }
}

static class RuntimePromptAssert
{
    public static void AllPinned(ICopilotPromptBuilder copilot, ICursorPromptBuilder cursor, string set)
    {
        var pins = (JsonObject)RuntimePromptCases.Pins()[set]!;
        var seen = 0;
        foreach (var (id, ph, pr, why) in RuntimePromptCases.Copilot())
        {
            Same(pins, id, copilot.Build(RuntimePromptCases.Item, ph, RuntimePromptCases.Code, pr is { } p ? new ItemPr(p.Number, p.Branch) : null, why)); seen++;
        }
        foreach (var (id, ph, why) in RuntimePromptCases.Cursor()) { Same(pins, id, cursor.Build(RuntimePromptCases.Item, ph, why)); seen++; }
        Assert.Equal(pins.Count, seen);   // every pinned text was rendered
    }

    static void Same(JsonObject pins, string id, string actual)
    {
        Assert.True(pins.ContainsKey(id), $"no pinned text for {id}");
        var expected = pins[id]!.GetValue<string>();
        if (expected == actual) return;
        var at = 0; while (at < expected.Length && at < actual.Length && expected[at] == actual[at]) at++;
        Assert.Fail($"{id}: differs at {at}: expected …{expected[Math.Max(0, at - 40)..Math.Min(expected.Length, at + 40)]}… got …{actual[Math.Max(0, at - 40)..Math.Min(actual.Length, at + 40)]}…");
    }
}
