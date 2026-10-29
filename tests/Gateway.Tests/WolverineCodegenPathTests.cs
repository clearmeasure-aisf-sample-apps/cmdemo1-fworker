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
    public void TypeLoadMode_StaysAuto()
    {
        var w = new WolverineOptions();
        WolverineHostService.ConfigureCodeGeneration(w);
        Assert.Equal(JasperFx.CodeGeneration.TypeLoadMode.Auto, w.CodeGeneration.TypeLoadMode);
    }
}
