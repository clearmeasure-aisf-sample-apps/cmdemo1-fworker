using Fworker.Contracts.Seams;
using Xunit;

namespace Gateway.Tests;

/// <summary>Seam S1 (task W1.4, gateway/Verbs/VerbRouters.cs): the engine branch resolves to v1 for every mode and environment, and VerbPipeline calls the interface.</summary>
public sealed class VerbRouterTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("verb-router-").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    static Func<string, string?> Env() => _ => null;

    EngineOptions Engine(string json, string environment)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return EngineOptions.From(InstanceConfig.Load(path, Env()), environment, Env());
    }

    [Theory]
    [InlineData("tdd", "v1")]
    [InlineData("uat", "v1")]
    [InlineData("prod", "v2")]
    public void EveryModeResolvesToTheV1Router(string environment, string expected)
    {
        var engine = Engine(EngineOptionsTests.BlockFor("\"prod\""), environment);
        Assert.Equal(expected, EngineOptions.Label(engine.Mode));
        var router = VerbRouterFactory.For(engine, new Runner("/nonexistent/factory.ps1"));
        Assert.IsType<V1VerbRouter>(router);
    }

    [Fact]
    public void VerbPipelineTakesTheRouterNotTheRunner()
    {
        var ctor = typeof(VerbPipeline).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Single();
        var types = ctor.GetParameters().Select(p => p.ParameterType).ToList();
        Assert.Contains(typeof(IVerbRouter), types);
        Assert.DoesNotContain(typeof(Runner), types);
    }

    [Fact]
    public void TheAnswerCarriesTheExitCodeAndTheOutput()
    {
        var a = new VerbAnswer(10, "{\"ok\":false}");
        var (code, output) = (a.Code, a.Output);
        Assert.Equal(10, code);
        Assert.Equal("{\"ok\":false}", output);
    }
}
