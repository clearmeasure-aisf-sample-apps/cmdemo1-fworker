using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The gateway's own configuration reads (gateway/Configuration/, C3): the effective values from config/instance.json as the tests run it and with
/// config/deploy.overrides.json merged over it as the image is built, and the precedence and fallback rules of each reader. Every reader takes the
/// environment as a function, so nothing here touches the process environment.
/// </summary>
public sealed class ConfigurationTests : IDisposable
{
    static readonly Func<string, string?> NoEnv = _ => null;
    readonly string dir = Path.Combine(Path.GetTempPath(), "gateway-config-tests-" + Guid.NewGuid().ToString("N")[..10]);
    readonly string root = RepoRoot();

    public ConfigurationTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    string BasePath => Path.Combine(root, "config", "instance.json");
    string OverridesPath => Path.Combine(root, "config", "deploy.overrides.json");

    string Write(string name, string json) { var p = Path.Combine(dir, name); File.WriteAllText(p, json); return p; }
    static Func<string, string?> Env(params (string Name, string? Value)[] vars) { var d = vars.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal); return n => d.TryGetValue(n, out var v) ? v : null; }

    // every value Program.cs takes from InstanceConfig and TimingOptions, in one line so a difference names itself
    static string Effective(InstanceConfig c)
    {
        var t = TimingOptions.From(c);
        return string.Join("; ", $"legacyKinds={c.LegacyKinds}", $"ownerSessions={c.OwnerSessions}", $"intake={c.IntakeColumn}", $"oneItemPerSession={c.OneItemPerSession}",
            $"ack={t.AckSeconds}", $"snapshotMaxAge={t.PlantSnapshotMaxAgeMinutes}", $"passUnmergedDigest={t.PassUnmergedDigestMinutes}", $"offerMissLimit={t.OfferMissLimit}",
            $"offerCoolDown={t.OfferCoolDownMinutes}", $"sessionEndGrace={t.SessionEndGraceSeconds}", $"pinFallback={t.PinFallbackMinutes}", $"sessionRestart={t.SessionRestartMinutes}",
            $"repeatQuestionLimit={t.SessionRepeatQuestionLimit}", $"sweepMin={t.SweepMinSeconds}", $"mcpIdle={t.McpIdleMinutes}");
    }

    static void AssertPinned(InstanceConfig c, bool ownerSessions)
    {
        var t = TimingOptions.From(c);
        Assert.NotNull(c.Root);
        Assert.True(c.LegacyKinds);
        Assert.Equal(ownerSessions, c.OwnerSessions);
        Assert.Equal("Technical Design", c.IntakeColumn);
        Assert.True(c.OneItemPerSession);
        Assert.Equal(120, t.AckSeconds);
        Assert.Equal(30, t.PlantSnapshotMaxAgeMinutes);
        Assert.Equal(5, t.PassUnmergedDigestMinutes);
        Assert.Equal(2, t.OfferMissLimit);
        Assert.Equal(5, t.OfferCoolDownMinutes);
        Assert.Equal(90, t.SessionEndGraceSeconds);
        Assert.Equal(30, t.PinFallbackMinutes);
        Assert.Equal(10, t.SessionRestartMinutes);
        Assert.Equal(2, t.SessionRepeatQuestionLimit);
        Assert.Equal(60, t.SweepMinSeconds);
        Assert.Equal(30, t.McpIdleMinutes);
    }

    [Fact]
    public void SourceDefaults_Pinned()
    {
        AssertPinned(InstanceConfig.Load(BasePath, NoEnv), ownerSessions: false);
    }

    [Fact]
    public void DeployOverrides_Pinned()
    {
        var merged = Write("effective.json", ApplyOverrides.Merge(File.ReadAllText(BasePath), File.ReadAllText(OverridesPath)).ToJsonString());
        AssertPinned(InstanceConfig.Load(merged, NoEnv), ownerSessions: true);
    }

    [Fact]
    public async Task DeployOverrides_ScriptAgreesWithThePort()
    {
        // the script itself, where a real PowerShell is on the PATH (the unit job of tests.yml installs it); the stub pwsh of the host fixtures is skipped
        var pwsh = RealPwsh();
        if (pwsh == null) return;
        var outPath = Path.Combine(dir, "script.json");
        var psi = new ProcessStartInfo(pwsh) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "config", "apply-overrides.ps1"), "-Base", BasePath, "-Overrides", OverridesPath, "-Out", outPath }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120))) await p.WaitForExitAsync(cts.Token);
        Assert.True(p.ExitCode == 0, $"apply-overrides.ps1 exit {p.ExitCode}: {await stdout} {await stderr}");
        Assert.True(File.Exists(outPath), "apply-overrides.ps1 wrote no file");
        var port = Write("port.json", ApplyOverrides.Merge(File.ReadAllText(BasePath), File.ReadAllText(OverridesPath)).ToJsonString());
        Assert.Equal(Effective(InstanceConfig.Load(port, NoEnv)), Effective(InstanceConfig.Load(outPath, NoEnv)));
    }

    [Fact]
    public void ApplyOverridesPort_RefusesUnknownSettings_AddsEnvironmentBlocks()
    {
        var b = """{"a":1,"o":{"x":1},"runtimes":{"environments":{}},"api":{"cursor":{"environments":{}},"copilot":{"environments":{"tdd":{"x":1}}}}}""";
        Assert.Equal("deploy override 'nope' names no setting of the base configuration", Assert.Throws<InvalidOperationException>(() => ApplyOverrides.Merge(b, """{"nope":1}""")).Message);
        Assert.Equal("deploy override 'o.y' names no setting of the base configuration", Assert.Throws<InvalidOperationException>(() => ApplyOverrides.Merge(b, """{"o":{"y":1}}""")).Message);
        Assert.Equal("deploy override 'a' is an object, the base setting is not", Assert.Throws<InvalidOperationException>(() => ApplyOverrides.Merge(b, """{"a":{"y":1}}""")).Message);
        var m = ApplyOverrides.Merge(b, """{"a":[1,2],"o":{"x":2},"runtimes":{"environments":{"uat":{"z":1}}},"api":{"cursor":{"environments":{"prod":{}}},"copilot":{"environments":{"tdd":{"y":2}}}}}""");
        Assert.Equal("[1,2]", m["a"]!.ToJsonString());
        Assert.Equal(2, (int)m["o"]!["x"]!);
        Assert.Equal(1, (int)m["runtimes"]!["environments"]!["uat"]!["z"]!);
        Assert.NotNull(m["api"]!["cursor"]!["environments"]!["prod"]);
        // an environment block is added whole: it replaces the base block, it is not merged into it
        Assert.Equal("""{"y":2}""", m["api"]!["copilot"]!["environments"]!["tdd"]!.ToJsonString());
    }

    [Fact]
    public void Environment_BeforeFile()
    {
        var p = Write("c.json", """{"timing":{"assignAckSeconds":30,"offerMissLimit":4,"sessionRepeatQuestionLimit":3.9},"plant":{"oneItemPerSession":false}}""");
        var fileOnly = InstanceConfig.Load(p, NoEnv);
        Assert.Equal(30, TimingOptions.From(fileOnly).AckSeconds);
        Assert.Equal(4, TimingOptions.From(fileOnly).OfferMissLimit);
        Assert.Equal(3, TimingOptions.From(fileOnly).SessionRepeatQuestionLimit);   // (int) of the double, as before
        Assert.False(fileOnly.OneItemPerSession);
        var env = InstanceConfig.Load(p, Env(("FACTORY_TIMING_offerMissLimit", "7"), ("FACTORY_ONE_ITEM_PER_SESSION", "1")));
        Assert.Equal(7, TimingOptions.From(env).OfferMissLimit);
        Assert.True(env.OneItemPerSession);
    }

    [Fact]
    public void AckSeconds_Precedence()
    {
        var p = Write("c.json", """{"timing":{"assignAckSeconds":30}}""");
        Assert.Equal(10, InstanceConfig.Load(p, Env(("FACTORY_ASSIGN_ACK_SECONDS", "10"), ("FACTORY_TIMING_assignAckSeconds", "20"))).AckSeconds);
        Assert.Equal(20, InstanceConfig.Load(p, Env(("FACTORY_TIMING_assignAckSeconds", "20"))).AckSeconds);
        Assert.Equal(20, InstanceConfig.Load(p, Env(("FACTORY_ASSIGN_ACK_SECONDS", "0"), ("FACTORY_TIMING_assignAckSeconds", "20"))).AckSeconds);
        Assert.Equal(30, InstanceConfig.Load(p, Env(("FACTORY_ASSIGN_ACK_SECONDS", "x"), ("FACTORY_TIMING_assignAckSeconds", "-1"))).AckSeconds);
        Assert.Equal(120, InstanceConfig.Load(Write("z.json", """{"timing":{"assignAckSeconds":0}}"""), NoEnv).AckSeconds);
    }

    [Fact]
    public void TimingNumber_PositiveOnly_TimingValue_ZeroAllowed()
    {
        var c = InstanceConfig.Load(Write("c.json", """{"timing":{"k":0,"h":"1.5","n":-1}}"""), NoEnv);
        Assert.Equal(9, c.TimingNumber("k", 9));
        Assert.Equal(0, c.TimingValue("k", 9));
        Assert.Equal(1.5, c.TimingValue("h", 9));   // a string that parses counts, as ToString() of the node did before
        Assert.Equal(9, c.TimingValue("n", 9));
        Assert.Equal(9, c.TimingValue("absent", 9));
        foreach (var bad in new[] { "-1", "NaN", "Infinity", "-Infinity", "x", "" })
        {
            var e = InstanceConfig.Load(Write("e.json", "{}"), Env(("FACTORY_TIMING_k", bad)));
            Assert.Equal(9, e.TimingNumber("k", 9));
            Assert.Equal(9, e.TimingValue("k", 9));
        }
        var zero = InstanceConfig.Load(Write("e.json", """{"timing":{"k":4}}"""), Env(("FACTORY_TIMING_k", "0")));
        Assert.Equal(4, zero.TimingNumber("k", 9));   // "0" is not positive: the file is read next
        Assert.Equal(0, zero.TimingValue("k", 9));
        Assert.Equal(2.5, InstanceConfig.Load(Write("e.json", "{}"), Env(("FACTORY_TIMING_k", "2.5e0"))).TimingNumber("k", 9));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("TRUE", false)]   // anything else falls through to the file, which says false here
    [InlineData("yes", false)]
    [InlineData(null, false)]
    public void OneItemPerSession_Environment(string? value, bool expected)
    {
        var c = InstanceConfig.Load(Write("c.json", """{"plant":{"oneItemPerSession":false}}"""), Env(("FACTORY_ONE_ITEM_PER_SESSION", value)));
        Assert.Equal(expected, c.OneItemPerSession);
    }

    [Theory]
    [InlineData(null)]           // missing file
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void MissingOrInvalidFile_EveryFallback(string? content)
    {
        var path = content == null ? Path.Combine(dir, "absent.json") : Write("c.json", content);
        var c = InstanceConfig.Load(path, NoEnv);
        var t = TimingOptions.From(c);
        Assert.True(c.LegacyKinds);
        Assert.False(c.OwnerSessions);
        Assert.Equal("Technical Design", c.IntakeColumn);
        Assert.True(c.OneItemPerSession);
        Assert.Equal(120, t.AckSeconds);
        Assert.Equal(30, t.PlantSnapshotMaxAgeMinutes);
        Assert.Equal(5, t.PassUnmergedDigestMinutes);
        Assert.Equal(2, t.OfferMissLimit);
        Assert.Equal(5, t.OfferCoolDownMinutes);
        Assert.Equal(90, t.SessionEndGraceSeconds);
        Assert.Equal(30, t.PinFallbackMinutes);
        Assert.Equal(10, t.SessionRestartMinutes);
        Assert.Equal(2, t.SessionRepeatQuestionLimit);
        Assert.Equal(60, t.SweepMinSeconds);
        Assert.Equal(30, t.McpIdleMinutes);
    }

    [Fact]
    public void MalformedSection_FallsBackForThatAccessorOnly()
    {
        var c = InstanceConfig.Load(Write("c.json", """{"timing":[1],"auth":{"legacyKinds":false},"ownerSessions":true,"columns":{"ai":["Design"]},"plant":[true]}"""), NoEnv);
        Assert.Equal(120, c.AckSeconds);
        Assert.Equal(7, c.TimingValue("offerMissLimit", 7));
        Assert.False(c.LegacyKinds);
        Assert.True(c.OwnerSessions);
        Assert.Equal("Design", c.IntakeColumn);
        Assert.True(c.OneItemPerSession);
        // a value that is not a boolean: legacy kinds stay on, a top-level switch stays off
        var odd = InstanceConfig.Load(Write("d.json", """{"auth":{"legacyKinds":"false"},"ownerSessions":"true","columns":{"ai":[]}}"""), NoEnv);
        Assert.True(odd.LegacyKinds);
        Assert.False(odd.OwnerSessions);
        Assert.Equal("Technical Design", odd.IntakeColumn);
    }

    [Fact]
    public void PathFor_ConfigVariableElseTwoLevelsAboveScripts()
    {
        var scripts = Path.Combine(root, "plugin", "scripts");
        Assert.Equal("/x/y.json", InstanceConfig.PathFor(scripts, Env(("FACTORY_CONFIG", "/x/y.json"))));
        Assert.Equal(Path.Combine(scripts, "..", "..", "config", "instance.json"), InstanceConfig.PathFor(scripts, NoEnv));
        Assert.Equal(Path.GetFullPath(BasePath), Path.GetFullPath(InstanceConfig.PathFor(scripts, NoEnv)));
        // an empty FACTORY_CONFIG is taken as written (as before): unreadable, so every fallback
        Assert.Equal("", InstanceConfig.PathFor(scripts, Env(("FACTORY_CONFIG", ""))));
        Assert.Null(InstanceConfig.Load("", NoEnv).Root);
    }

    // the first pwsh on the PATH that is a real PowerShell, not the stub the host fixtures install (a /bin/sh script)
    static string? RealPwsh()
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in OperatingSystem.IsWindows() ? new[] { "pwsh.exe" } : new[] { "pwsh" })
            {
                var f = Path.Combine(d, name);
                if (!File.Exists(f)) continue;
                try { using var r = new StreamReader(f); if (r.ReadLine()?.StartsWith("#!/bin/sh", StringComparison.Ordinal) == true) continue; } catch { continue; }
                return f;
            }
        }
        return null;
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "config", "instance.json")) && Directory.Exists(Path.Combine(d.FullName, "gateway"))) return d.FullName;
        throw new InvalidOperationException("the repository root (config/instance.json) was not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// A port of config/apply-overrides.ps1 for the tests: objects are merged key by key, a value or an array replaces the base's, a key the base does
/// not have is refused, and the keys of runtimes.environments, api.cursor.environments and api.copilot.environments are added as written.
/// </summary>
public static class ApplyOverrides
{
    public static JsonObject Merge(string baseJson, string overridesJson)
    {
        var cfg = (JsonObject)JsonNode.Parse(baseJson)!;
        MergeInto(cfg, (JsonObject)JsonNode.Parse(overridesJson)!, "");
        return cfg;
    }

    static void MergeInto(JsonObject target, JsonObject source, string where)
    {
        foreach (var (k, v) in source.ToList())
        {
            if (where is "runtimes.environments." or "api.cursor.environments." or "api.copilot.environments.") { target[k] = v?.DeepClone(); continue; }
            if (!target.ContainsKey(k)) throw new InvalidOperationException($"deploy override '{where}{k}' names no setting of the base configuration");
            if (v is JsonObject so)
            {
                if (target[k] is not JsonObject to) throw new InvalidOperationException($"deploy override '{where}{k}' is an object, the base setting is not");
                MergeInto(to, so, $"{where}{k}.");
            }
            else target[k] = v?.DeepClone();
        }
    }
}
