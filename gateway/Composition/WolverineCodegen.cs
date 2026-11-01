using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Wolverine;

// Wolverine's handler code, generated at build time and compiled into this assembly (gateway/Internal/Generated).
// Why: with TypeLoadMode.Auto and no pre-generated code, the first use of each message type compiles its handler with Roslyn inside the gateway process, and every restart of the
// container (an empty /tmp) starts that again; on tdd that was a burst of 12 to 24 "Generated code to ..." lines after each start, and the two SIGKILLs of the 2Gi container of 2026-11-01
// came 1 to 3 seconds after such a burst. With the code in the assembly the same first use loads a type.
// Mode: Auto, the static mode with a dynamic fallback. A handler whose generated type is not in the assembly (the code is out of date, or the deployed configuration adds a handler the
// generator host did not have) is compiled at its first use as before, and the start says so (Report). Static would throw for it at the first message, which for a durable
// queue is a stuck message; the drift guard (WolverineCodegenDriftTests) is what keeps the fallback from being used.
// Regenerate (needs no database): FWORKER_WRITE_WOLVERINE_CODEGEN=1 dotnet test tests/Gateway.Tests --filter TheGeneratedCodeIsWhatTheHandlersGenerate
static class WolverineCodegen
{
    /// <summary>Where the files are, relative to the repository root; compiled with the rest of gateway/ (the Web SDK globs every *.cs).</summary>
    public const string RepoDirectory = "gateway/Internal/Generated";

    /// <summary>Every code file of the host's collections (the handler chains), by relative path, as the `codegen write` command of Wolverine would write them.</summary>
    internal static SortedDictionary<string, string> Generate(IHost host)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fworker-codegen-" + Guid.NewGuid().ToString("N")[..10]);
        var rules = host.Services.GetRequiredService<WolverineOptions>().CodeGeneration;
        var before = rules.GeneratedCodeOutputPath;
        rules.GeneratedCodeOutputPath = dir;
        try
        {
            var builder = new DynamicCodeBuilder(host.Services, host.Services.GetServices<ICodeFileCollection>().ToArray())
            {
                ServiceVariableSource = host.Services.GetService<IServiceVariableSource>(),
            };
            DynamicCodeBuilder.WithinCodegenCommand = true;   // (static) as the codegen command sets it
            builder.WriteGeneratedCode(_ => { });
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                    files[Path.GetRelativePath(dir, f).Replace('\\', '/')] = File.ReadAllText(f).Replace("\r\n", "\n");
            return files;
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
            rules.GeneratedCodeOutputPath = before;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>The code files of the host, and those among them the assembly does not hold (by file name). The second list empty: every first use loads a type.</summary>
    internal static (int Total, List<string> Missing) NotPreGenerated(IHost host, System.Reflection.Assembly assembly)
    {
        var rules = host.Services.GetRequiredService<WolverineOptions>().CodeGeneration;
        var held = assembly.GetTypes().Select(t => t.FullName).ToHashSet(StringComparer.Ordinal);
        var total = 0; var missing = new List<string>();
        foreach (var c in host.Services.GetServices<ICodeFileCollection>())
            foreach (var file in c.BuildFiles())
            {
                total++;
                // the types are written to Internal.Generated, then the collection's child namespace (the files say it: "namespace Internal.Generated.WolverineHandlers")
                if (!held.Contains(rules.GeneratedNamespace + (string.IsNullOrEmpty(c.ChildNamespace) ? "" : "." + c.ChildNamespace) + "." + file.FileName)) missing.Add(file.FileName);
            }
        return (total, missing);
    }

    /// <summary>The line the start logs: how many handlers load pre-generated code and which would be compiled at their first use.</summary>
    internal static string Report(IHost host, System.Reflection.Assembly assembly)
    {
        var (total, missing) = NotPreGenerated(host, assembly);
        return missing.Count == 0 ? $"{total} generated code files, all in the assembly: nothing is compiled at first use"
            : $"{missing.Count} of {total} generated code files are not in the assembly and are compiled at their first use ({string.Join(", ", missing.Take(10))}); regenerate the code (CLAUDE.md, Build)";
    }
}
