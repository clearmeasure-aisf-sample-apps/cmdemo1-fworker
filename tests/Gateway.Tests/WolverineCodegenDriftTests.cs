using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The handler code of Wolverine is generated at build time and compiled into the gateway (gateway/Internal/Generated, WolverineCodegen): a restart of the container no longer compiles it with Roslyn.
/// These tests keep that code and the handlers together. The host they read is the real one: the gateway as the image configures it (config/instance.json with config/deploy.overrides.json merged
/// over it, tdd where the engine acts, the credentials of every port present), its Wolverine host built by the gateway's own HostFactory and never started, so no database is needed.
/// - TheGeneratedCodeIsWhatTheHandlersGenerate: the files in the repository are exactly what the handlers and the host's options generate now (no file missing, none left over, none different).
///   A change of a handler, a message, a policy or of Wolverine itself fails here and shows which files. FWORKER_WRITE_WOLVERINE_CODEGEN=1 rewrites them (the Build section of CLAUDE.md).
/// - EveryHandlerOfTheHostHasItsTypeInTheAssembly: the compiled gateway holds a type for every code file, so the first use of a message type loads and never compiles. Fails when the files were
///   rewritten and the assembly not built again (build, then run it once more).
/// </summary>
public sealed class WolverineCodegenDriftTests
{
    static readonly string Root = new Func<string>(() =>
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gateway", "Program.cs")) && File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) return d.FullName;
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    })();

    static string Directory_ => Path.Combine(Root, "gateway", "Internal", "Generated");
    static bool Write => Environment.GetEnvironmentVariable("FWORKER_WRITE_WOLVERINE_CODEGEN") == "1";

    /// <summary>The deploy overrides merged over the defaults the way config/apply-overrides.ps1 does (objects key by key, anything else replaces; the environment blocks are added as written).</summary>
    static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (k, v) in source.ToList())
        {
            if (v is JsonObject o && target[k] is JsonObject t) Merge(t, o);
            else target[k] = v?.DeepClone();
        }
    }

    internal static async Task WithHost(Func<IHost, Task> body)
    {
        using var env = new GatewayEnvironment();
        env.Set("FACTORY_DB_CONNECTION", WolverineHostTestSupport.Unreachable);
        env.Set("GH_TOKEN", "ghp_" + new string('B', 26));   // composed at run time; nothing is called
        env.Set("OCTOPUS_API_KEY", "API-" + new string('R', 20));
        env.EditConfig = cfg =>
        {
            Merge(cfg, (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "config", "deploy.overrides.json")))!);
            ((JsonObject)cfg["octopus"]!)["url"] = "http://127.0.0.1:9";   // nothing real is called
        };
        env.Apply();
        GatewayHost? gateway = null;
        try
        {
            gateway = new GatewayHost(); _ = gateway.Server;
            var service = gateway.Services.GetRequiredService<WolverineHostService>();
            using var host = service.HostFactory(WolverineHostTestSupport.Unreachable, gateway.Services.GetRequiredService<WolverineHostOptions>());
            Assert.Equal(JasperFx.CodeGeneration.TypeLoadMode.Auto, host.Services.GetRequiredService<Wolverine.WolverineOptions>().CodeGeneration.TypeLoadMode);   // the deployed settings turn the pre-generated code on
            await body(host);
        }
        finally { if (gateway is not null) await gateway.DisposeAsync(); }
    }

    [Fact]
    public async Task TheGeneratedCodeIsWhatTheHandlersGenerate()
    {
        if (!StubPwsh.Supported) return;
        // (a host generates once: its chains keep their frames) generating from a second host gives the same text, so nothing in the code depends on the run
        SortedDictionary<string, string>? again = null;
        await WithHost(host => { again = WolverineCodegen.Generate(host); return Task.CompletedTask; });
        await WithHost(host =>
        {
            var now = WolverineCodegen.Generate(host);
            Assert.NotEmpty(now);
            again!.Should_Equal(now);
            if (Write)
            {
                if (Directory.Exists(Directory_)) Directory.Delete(Directory_, true);
                foreach (var (path, text) in now)
                {
                    var full = Path.Combine(Directory_, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, text, new UTF8Encoding(false));
                }
                return Task.CompletedTask;
            }
            var have = Directory.Exists(Directory_)
                ? Directory.EnumerateFiles(Directory_, "*.cs", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(Directory_, f).Replace('\\', '/'), f => File.ReadAllText(f).Replace("\r\n", "\n"))
                : new Dictionary<string, string>();
            var missing = now.Keys.Except(have.Keys).ToList();
            var extra = have.Keys.Except(now.Keys).ToList();
            var different = now.Keys.Intersect(have.Keys).Where(k => now[k] != have[k]).ToList();
            Assert.True(missing.Count + extra.Count + different.Count == 0,
                $"gateway/Internal/Generated is out of date with the handlers. Regenerate: FWORKER_WRITE_WOLVERINE_CODEGEN=1 dotnet test tests/Gateway.Tests --filter TheGeneratedCodeIsWhatTheHandlersGenerate. "
                + $"Missing ({missing.Count}): {string.Join(", ", missing.Take(10))}. Left over ({extra.Count}): {string.Join(", ", extra.Take(10))}. Different ({different.Count}): {string.Join(", ", different.Take(10))}.");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EveryHandlerOfTheHostHasItsTypeInTheAssembly()
    {
        if (!StubPwsh.Supported || Write) return;
        await WithHost(host =>
        {
            var (total, missing) = WolverineCodegen.NotPreGenerated(host, typeof(WolverineHostService).Assembly);
            Assert.True(total > 50, $"the host has {total} code files");
            Assert.True(missing.Count == 0, $"{missing.Count} code files are not in the gateway assembly (they would be compiled at first use): {string.Join(", ", missing.Take(10))}");
            return Task.CompletedTask;
        });
    }
}

static class SortedExtensions
{
    public static void Should_Equal(this SortedDictionary<string, string> a, SortedDictionary<string, string> b)
    {
        Assert.Equal(b.Keys, a.Keys);
        foreach (var k in b.Keys) Assert.True(a[k] == b[k], $"{k} differs between two runs");
    }
}
