using Wolverine;
using Xunit;

namespace Gateway.Tests;

/// <summary>#491: Wolverine's generated code goes to a writable directory under the temp path, not to /app/Internal (not writable in the image). No database needed.</summary>
public sealed class WolverineCodegenPathTests
{
    [Fact]
    public void OutputPath_IsUnderTheTempDirectory_AndWritable()
    {
        var w = new WolverineOptions();
        WolverineHostService.ConfigureCodeGeneration(w);
        var path = w.CodeGeneration.GeneratedCodeOutputPath;
        Assert.False(string.IsNullOrEmpty(path));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "fworker-wolverine-codegen"), path);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(path), StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));
        var probe = Path.Combine(path, $"probe-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probe, "x");
        try { Assert.Equal("x", File.ReadAllText(probe)); } finally { File.Delete(probe); }
    }

    [Fact]
    public void WithoutThePreGeneratedCode_NothingIsLoadedFromTheAssembly()
    {
        var w = new WolverineOptions();
        WolverineHostService.ConfigureCodeGeneration(w, preGenerated: false);
        Assert.Equal(JasperFx.CodeGeneration.TypeLoadMode.Dynamic, w.CodeGeneration.TypeLoadMode);
    }

    [Fact]
    public void ThePreGeneratedCode_IsOnInTheDeployedSettings_AndOffInTheDefaults()
    {
        string Read(params string[] path)
        {
            var root = AppContext.BaseDirectory;
            for (var d = new DirectoryInfo(root); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) { root = d.FullName; break; }
            return File.ReadAllText(Path.Combine(new[] { root }.Concat(path).ToArray()));
        }
        Assert.False(System.Text.Json.Nodes.JsonNode.Parse(Read("config", "instance.json"))!["wolverine"]!["preGeneratedCode"]!.GetValue<bool>());
        Assert.True(System.Text.Json.Nodes.JsonNode.Parse(Read("config", "deploy.overrides.json"))!["wolverine"]!["preGeneratedCode"]!.GetValue<bool>());
    }

    [Fact]
    public void TypeLoadMode_StaysAuto()
    {
        var w = new WolverineOptions();
        WolverineHostService.ConfigureCodeGeneration(w);
        Assert.Equal(JasperFx.CodeGeneration.TypeLoadMode.Auto, w.CodeGeneration.TypeLoadMode);
    }
}
