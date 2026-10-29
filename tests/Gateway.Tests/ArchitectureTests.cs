using System.Text.RegularExpressions;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The architecture test (task C8, the stabilisation gate's first condition; docs/design/gateway-composition.md 2, "Rules of the target"): the source of
/// the gateway is read as text and the rules of the composition root are asserted. A change that puts behaviour back into Program.cs, maps a route
/// outside an endpoint module, starts a hosted service outside AddGatewayBackground or brings back a process-wide static fails here, whatever else it
/// does. The messages say which file and which line broke which rule.
/// </summary>
public class ArchitectureTests
{
    /// <summary>The line budget of Program.cs: a composition root is a few dozen lines (the design says at most 60).</summary>
    public const int ProgramLineBudget = 60;

    static readonly string Root = RepoRoot();
    static readonly string GatewayDir = Path.Combine(Root, "gateway");

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gateway", "Program.cs")) && File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) return d.FullName;
        throw new InvalidOperationException("the repository root (gateway/Program.cs) was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Every gateway source file, relative path with forward slashes, without bin and obj.</summary>
    static IEnumerable<(string Path, string Text)> Sources() =>
        Directory.EnumerateFiles(GatewayDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}") && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}"))
            .Select(f => (System.IO.Path.GetRelativePath(Root, f).Replace('\\', '/'), File.ReadAllText(f)));

    /// <summary>The code of a file: line comments and block comments blanked (keeping the lines), so a word in a comment breaks no rule.</summary>
    internal static string Code(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", m => new string('\n', m.Value.Count(ch => ch == '\n')), RegexOptions.Singleline);
        // a // that is not inside a string literal on its line (the sources keep "://" in strings: the scan looks at the quotes before it)
        var lines = text.Split('\n').Select(l =>
        {
            for (var i = 0; i + 1 < l.Length; i++)
                if (l[i] == '/' && l[i + 1] == '/' && l.Take(i).Count(ch => ch == '"') % 2 == 0) return l[..i];
            return l;
        });
        return string.Join('\n', lines);
    }

    static string LineOf(string text, int index) => (text[..index].Count(ch => ch == '\n') + 1).ToString();

    // ---- Program.cs is the composition root ------------------------------------------------------------------------------------------------------

    [Fact]
    public void ProgramCs_IsWithinItsLineBudget()
    {
        var lines = File.ReadAllLines(Path.Combine(GatewayDir, "Program.cs")).Length;
        Assert.True(lines <= ProgramLineBudget, $"gateway/Program.cs has {lines} lines; the composition root is at most {ProgramLineBudget} (gateway-composition.md 2). Move the behaviour into gateway/Composition/, gateway/Endpoints/ or a service.");
    }

    [Theory]
    [InlineData(@"\bTask\.Run\b", "Task.Run: a background loop is a hosted service (AddGatewayBackground)")]
    [InlineData(@"\bwhile\b", "while: a loop is a service's, not the root's")]
    [InlineData(@"\bfor(each)?\s*\(", "for/foreach: a loop is a service's, not the root's")]
    [InlineData(@"\bif\s*\(", "if: a condition is a registration method's (it reads the composition)")]
    [InlineData(@"\btry\b", "try: error handling is a service's")]
    [InlineData(@"=>", "a lambda: a handler or a callback belongs in an endpoint module or a service")]
    [InlineData(@"\bnew\s+Thread\b", "a thread")]
    [InlineData(@"\bApplicationStopping\b", "an ApplicationStopping callback: the owning service registers it (UseGatewayLifetime orders them)")]
    [InlineData(@"\bConsole\.", "Console: the start-up lines belong to the registrations")]
    [InlineData(@"\bEnvironment\.GetEnvironmentVariable\s*\(", "an environment read: GatewayComposition.FromEnvironment reads the configuration")]
    [InlineData("\"\"\"", "a raw string literal: string content is a file or a template (gateway/Templates/)")]
    [InlineData(@"\bMap(Get|Post|Put|Delete|Patch|Methods|Group|Fallback)\s*\(", "a route mapped in Program.cs: an endpoint module maps its own routes")]
    public void ProgramCs_HasNoBehaviour(string forbidden, string why)
    {
        var code = Code(File.ReadAllText(Path.Combine(GatewayDir, "Program.cs")));
        var hit = Regex.Match(code, forbidden);
        Assert.False(hit.Success, $"gateway/Program.cs line {(hit.Success ? LineOf(code, hit.Index) : "")}: {why}");
    }

    /// <summary>The Map* and Use* calls Program.cs may make; each is defined in gateway/Composition, gateway/Http or gateway/Endpoints.</summary>
    static readonly string[] AllowedProgramCalls = ["UseGatewayLifetime", "UseGatewayPipeline", "MapGatewayEndpoints"];

    [Fact]
    public void ProgramCs_CallsOnlyTheAllowedMapAndUseMethods()
    {
        var code = Code(File.ReadAllText(Path.Combine(GatewayDir, "Program.cs")));
        var calls = Regex.Matches(code, @"\b(Map|Use)\w*\s*\(").Select(m => Regex.Match(m.Value, @"\w+").Value).ToList();
        var bad = calls.Where(c => !AllowedProgramCalls.Contains(c)).ToList();
        Assert.True(bad.Count == 0, "gateway/Program.cs calls " + string.Join(", ", bad) + "; the allowed Map*/Use* calls are " + string.Join(", ", AllowedProgramCalls));
        // and the allowed ones are really there, once each, in this order: the pipeline is the lifetime, the pipeline, the routes
        var seen = calls.Where(AllowedProgramCalls.Contains).ToList();
        Assert.Equal(AllowedProgramCalls, seen);
    }

    [Fact]
    public void ProgramCs_RegistersOnlyThroughAddGatewayMethods()
    {
        var code = Code(File.ReadAllText(Path.Combine(GatewayDir, "Program.cs")));
        var adds = Regex.Matches(code, @"\.(Add\w+)\s*[<(]").Select(m => m.Groups[1].Value).ToList();
        var bad = adds.Where(a => !a.StartsWith("AddGateway")).ToList();
        Assert.True(bad.Count == 0, "gateway/Program.cs registers " + string.Join(", ", bad) + " itself; a registration belongs to an AddGateway* method (gateway/Composition/)");
        Assert.NotEmpty(adds);
    }

    [Fact]
    public void ProgramCs_HasNoTopLevelStateBeyondTheCompositionBuilderAndApp()
    {
        // a `var x = ...` that is none of the root's three objects is state captured by what follows (rule 2: every piece of state has one owning class)
        var code = Code(File.ReadAllText(Path.Combine(GatewayDir, "Program.cs")));
        var names = Regex.Matches(code, @"^\s*var\s+(\w+)\s*=", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["composition", "builder", "app"], names);
    }

    // ---- routes: only endpoint modules map them --------------------------------------------------------------------------------------------------

    static readonly Regex RouteMapping = new(@"\.\s*Map(Get|Post|Put|Delete|Patch|Methods|Group|Fallback|Mcp|Hub|Health\w*)?\s*\(", RegexOptions.Compiled);
    static readonly Regex ModuleDeclaration = new(@"\bclass\s+\w+[^{;]*[:,]\s*IEndpointModule\b", RegexOptions.Compiled);

    [Fact]
    public void RoutesAreMappedOnlyInEndpointModules()
    {
        var offenders = new List<string>();
        foreach (var (path, text) in Sources())
        {
            var code = Code(text);
            // EndpointModules.cs declares IEndpointModule and calls module.Map(app) once: the one place a module is asked to map
            if (ModuleDeclaration.IsMatch(code) || Regex.IsMatch(code, @"\binterface\s+IEndpointModule\b")) continue;
            foreach (Match m in RouteMapping.Matches(code)) offenders.Add($"{path}:{LineOf(code, m.Index)}");
        }
        Assert.True(offenders.Count == 0, "a route is mapped outside a class that implements IEndpointModule (gateway/Endpoints/EndpointModules.cs says how a module is written): " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryEndpointModuleIsRegistered()
    {
        var registry = File.ReadAllText(Path.Combine(GatewayDir, "Endpoints", "EndpointModules.cs"));
        var missing = new List<string>();
        foreach (var (path, text) in Sources())
            foreach (Match m in Regex.Matches(Code(text), @"\bclass\s+(\w+)[^{;]*[:,]\s*IEndpointModule\b"))
                if (!Regex.IsMatch(registry, @"\b" + m.Groups[1].Value + @"\b|Add\w+<IEndpointModule>\(sp\s*=>\s*sp\.GetRequiredService<" + m.Groups[1].Value + ">")) missing.Add($"{m.Groups[1].Value} ({path})");
        Assert.True(missing.Count == 0, "an endpoint module that AddGatewayEndpoints does not register maps nothing: " + string.Join(", ", missing));
    }

    // ---- background work: only AddGatewayBackground starts it --------------------------------------------------------------------------------------

    static readonly Regex HostedRegistration = new(@"\bAddHostedService\b|AddSingleton<IHostedService>|AddTransient<IHostedService>|AddScoped<IHostedService>", RegexOptions.Compiled);

    /// <summary>The methods of GatewayServiceCollectionExtensions.cs that are AddGatewayBackground or helpers it calls (the order of the start is theirs).</summary>
    static readonly string[] BackgroundMethods = ["AddGatewayBackground", "AddGatewayCloudOwners", "AddGatewayManagerCopilot"];

    [Fact]
    public void HostedServicesAreRegisteredOnlyInAddGatewayBackground()
    {
        var offenders = new List<string>();
        foreach (var (path, text) in Sources())
        {
            var code = Code(text);
            foreach (Match m in HostedRegistration.Matches(code))
            {
                if (path != "gateway/Composition/GatewayServiceCollectionExtensions.cs") { offenders.Add($"{path}:{LineOf(code, m.Index)}"); continue; }
                var method = Regex.Matches(code[..m.Index], @"static\s+\w[\w<>]*\s+(\w+)\s*\(").Cast<Match>().LastOrDefault()?.Groups[1].Value ?? "";
                if (!BackgroundMethods.Contains(method)) offenders.Add($"{path}:{LineOf(code, m.Index)} (in {method})");
            }
        }
        Assert.True(offenders.Count == 0, "a hosted service is registered outside AddGatewayBackground (the host starts them in registration order and stops them in reverse): " + string.Join(", ", offenders));
    }

    [Fact]
    public void AddGatewayBackgroundHelpersAreCalledFromIt()
    {
        var code = Code(File.ReadAllText(Path.Combine(GatewayDir, "Composition", "GatewayServiceCollectionExtensions.cs")));
        var start = code.IndexOf("IServiceCollection AddGatewayBackground(", StringComparison.Ordinal);
        Assert.True(start > 0, "AddGatewayBackground is gone");
        var body = code[start..];
        foreach (var helper in BackgroundMethods.Skip(1)) Assert.Contains(helper + "(", body);
    }

    // ---- state: no process-wide statics that Program.cs sets --------------------------------------------------------------------------------------

    [Fact]
    public void AuthHoldsNoMutableStatic()
    {
        var code = Code(File.ReadAllText(Path.Combine(GatewayDir, "Auth.cs")));
        // a static field or property that is not readonly or const (a method has a parenthesis before any = or ;)
        var bad = Regex.Matches(code, @"^\s*(public|internal|private)?\s*static\s+(?!readonly\b|const\b|class\b)[^(=;{]+?\s\w+\s*(=|;|\{\s*get)", RegexOptions.Multiline)
            .Select(m => $"line {LineOf(code, m.Index)}: {m.Value.Trim()}").ToList();
        Assert.True(bad.Count == 0, "gateway/Auth.cs holds a mutable static (the environment, auth.legacyKinds and the keeper are CredentialPolicy's, injected): " + string.Join("; ", bad));
    }

    [Fact]
    public void NoCodeReadsTheStaticsThatMovedIntoServices()
    {
        var offenders = new List<string>();
        foreach (var (path, text) in Sources().Concat(TestSources()))
        {
            var code = Code(text);
            foreach (Match m in Regex.Matches(code, @"\bAuth\.(Environment|LegacyKinds|Keeper)\b|\bGatewayMcp\.Tools\b")) offenders.Add($"{path}:{LineOf(code, m.Index)} {m.Value}");
        }
        Assert.True(offenders.Count == 0, "a static that C8 moved into a service is used again: " + string.Join(", ", offenders));
    }

    static IEnumerable<(string Path, string Text)> TestSources() =>
        Directory.EnumerateFiles(Path.Combine(Root, "tests", "Gateway.Tests"), "*.cs", SearchOption.TopDirectoryOnly)
            .Where(f => !f.EndsWith("ArchitectureTests.cs"))
            .Select(f => (System.IO.Path.GetRelativePath(Root, f).Replace('\\', '/'), File.ReadAllText(f)));

    // ---- policies: pure rules --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void PoliciesAreExactlyTheOnesTheDesignNames()
    {
        var files = Directory.EnumerateFiles(Path.Combine(GatewayDir, "Policies"), "*.cs").Select(f => System.IO.Path.GetFileNameWithoutExtension(f)).Order().ToList();
        // PolicyRefusal is their shared answer type
        Assert.Equal(new[] { "CredentialPolicy", "DerivedTextRule", "MetricsAccessPolicy", "PolicyRefusal", "PresencePolicy", "ReleaseRecordPolicy", "VerbPolicy" }.Order(), files);
    }

    [Theory]
    [InlineData(@"\bPlant\b", "the plant (a policy gets what it needs as arguments)")]
    [InlineData(@"\bHttpContext\b|\bIResult\b|\bResults\.|Microsoft\.AspNetCore", "HTTP (the endpoint turns a PolicyRefusal into its answer)")]
    [InlineData(@"\bConsole\.", "the console (the caller logs)")]
    [InlineData(@"\bRunner\b|\bEventHub\b|\bMessenger\b|\bQcHub\b|\bILeadership\b", "a service of the gateway")]
    [InlineData(@"\bFile\.|\bDirectory\.|\bProcess\b|\bHttpClient\b", "the file system, a process or the network")]
    public void PoliciesDependOnNoneOfTheGatewaysServices(string forbidden, string why)
    {
        var offenders = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(GatewayDir, "Policies"), "*.cs"))
        {
            var code = Code(File.ReadAllText(f));
            foreach (Match m in Regex.Matches(code, forbidden)) offenders.Add($"{System.IO.Path.GetFileName(f)}:{LineOf(code, m.Index)}");
        }
        Assert.True(offenders.Count == 0, "gateway/Policies/ names " + why + ": " + string.Join(", ", offenders));
    }

    // ---- the projects of src/ point inward (docs/design/fworker-wolverine-refactor.md 3, task W1.0) ----------------------------------------------

    /// <summary>The src/ projects and the only projects each may reference. Contracts depends on nothing; Domain on Contracts; the host (gateway) may reference all of them.</summary>
    static readonly Dictionary<string, string[]> AllowedProjectReferences = new()
    {
        ["Fworker.Contracts"] = [],
        ["Fworker.Domain"] = ["Fworker.Contracts"],
        ["Fworker.Persistence"] = ["Fworker.Contracts"],
        ["Fworker.Database"] = [],
        ["Fworker.Integrations"] = ["Fworker.Contracts", "Fworker.Domain", "Fworker.Persistence"],
        ["Fworker.Workflows"] = ["Fworker.Contracts", "Fworker.Domain", "Fworker.Persistence"],
    };

    /// <summary>The names of the projects a csproj references (the file name without .csproj), from its ProjectReference items.</summary>
    internal static List<string> ProjectReferences(string csprojText) =>
        Regex.Matches(csprojText, @"<ProjectReference\s+[^>]*Include\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)
            .Select(m => System.IO.Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/').Split('/').Last())).ToList();

    static string Csproj(string relative) => File.ReadAllText(System.IO.Path.Combine(Root, relative));

    public static IEnumerable<object[]> SrcProjects() => AllowedProjectReferences.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(SrcProjects))]
    public void SrcProjectReferencesOnlyWhatTheDependencyDirectionAllows(string project)
    {
        var refs = ProjectReferences(Csproj($"src/{project}/{project}.csproj"));
        var bad = refs.Where(r => !AllowedProjectReferences[project].Contains(r)).ToList();
        Assert.True(bad.Count == 0, $"src/{project} references {string.Join(", ", bad)}; it may reference only [{string.Join(", ", AllowedProjectReferences[project])}] (projects point inward, refactor design 3).");
    }

    /// <summary>The only NuGet packages each src/ project may have (W2.0 names them: refactor design R2, ADR 0001). Contracts, Domain and Integrations stay free of
    /// packages: the rules and the contracts must not know the engine, and Integrations (W3) reaches the outside through its own clients. Persistence holds the
    /// DbContext (EF Core, SQL Server) and Wolverine's EF Core storage; Workflows holds the sagas (WolverineFx). The SQL Server transport, the HTTP endpoints and the
    /// telemetry packages belong to the host (gateway/Gateway.csproj), where the Wolverine registration is.</summary>
    static readonly Dictionary<string, string[]> AllowedPackages = new()
    {
        ["Fworker.Contracts"] = [],
        ["Fworker.Domain"] = [],
        ["Fworker.Persistence"] = ["Microsoft.EntityFrameworkCore.SqlServer", "WolverineFx.EntityFrameworkCore", "System.Security.Cryptography.Pkcs"],
        // the schema of the gateway's database, built by DbUp from plain .sql files (docs/design/fworker-database.md); it depends on nothing of this repository. Pkcs lifts the one SqlClient brings (advisories)
        ["Fworker.Database"] = ["dbup-sqlserver", "Microsoft.Data.SqlClient", "System.Security.Cryptography.Pkcs"],
        ["Fworker.Integrations"] = [],
        ["Fworker.Workflows"] = ["WolverineFx"],
    };

    internal static List<(string Name, string Version)> PackageReferences(string csprojText) =>
        Regex.Matches(csprojText, @"<PackageReference\s+[^>]*Include\s*=\s*""([^""]+)""[^>]*Version\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();

    [Theory]
    [MemberData(nameof(SrcProjects))]
    public void SrcProjectsAddOnlyThePackagesThePlanNames(string project)
    {
        var text = Csproj($"src/{project}/{project}.csproj");
        var all = Regex.Matches(text, @"<PackageReference\b", RegexOptions.IgnoreCase).Count;
        var named = PackageReferences(text);
        Assert.True(all == named.Count, $"src/{project}: every PackageReference needs Include and Version");
        var bad = named.Where(p => !AllowedPackages[project].Contains(p.Name)).Select(p => p.Name).ToList();
        Assert.True(bad.Count == 0, $"src/{project} references the package(s) {string.Join(", ", bad)}; it may have only [{string.Join(", ", AllowedPackages[project])}].");
    }

    [Theory]
    [MemberData(nameof(SrcProjects))]
    public void EveryPackageIsPinnedToOneExactVersion(string project)
    {
        // pinned (W2.0): a plain version number, no range, wildcard or floating version
        var versions = PackageReferences(Csproj($"src/{project}/{project}.csproj")).Concat(PackageReferences(Csproj("gateway/Gateway.csproj")));
        var loose = versions.Where(p => !Regex.IsMatch(p.Version, @"^\d+\.\d+\.\d+$")).Select(p => $"{p.Name} {p.Version}").ToList();
        Assert.True(loose.Count == 0, "not pinned to an exact version: " + string.Join(", ", loose));
    }

    [Fact]
    public void WolverinePackagesShareOneVersion()
    {
        var all = new[] { "gateway/Gateway.csproj", "src/Fworker.Persistence/Fworker.Persistence.csproj", "src/Fworker.Workflows/Fworker.Workflows.csproj" }
            .SelectMany(f => PackageReferences(Csproj(f))).Where(p => p.Name.StartsWith("WolverineFx", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(all);
        Assert.Single(all.Select(p => p.Version).Distinct());
    }

    [Fact]
    public void ContractsDependsOnNothing() =>
        Assert.Empty(ProjectReferences(Csproj("src/Fworker.Contracts/Fworker.Contracts.csproj")));

    [Fact]
    public void DomainDependsOnlyOnContracts() =>
        Assert.Equal(["Fworker.Contracts"], ProjectReferences(Csproj("src/Fworker.Domain/Fworker.Domain.csproj")));

    [Fact]
    public void WorkflowsDoNotReferenceIntegrations() =>
        // a saga reaches the outside only through the integration commands of Contracts (S2)
        Assert.DoesNotContain("Fworker.Integrations", ProjectReferences(Csproj("src/Fworker.Workflows/Fworker.Workflows.csproj")));

    [Fact]
    public void NoSrcProjectReferencesTheHost()
    {
        foreach (var dir in Directory.EnumerateDirectories(System.IO.Path.Combine(Root, "src")))
            foreach (var f in Directory.EnumerateFiles(dir, "*.csproj"))
                Assert.DoesNotContain("Gateway", ProjectReferences(File.ReadAllText(f)));
    }

    [Fact]
    public void OnlyTheHostAndTheTestsReferenceTheDatabaseProject()
    {
        // nothing in src/ references Fworker.Database: the schema is applied by the host, at start
        foreach (var dir in Directory.EnumerateDirectories(System.IO.Path.Combine(Root, "src")))
            foreach (var f in Directory.EnumerateFiles(dir, "*.csproj"))
                Assert.DoesNotContain("Fworker.Database", ProjectReferences(File.ReadAllText(f)));
    }

    /// <summary>The schema is built by the scripts of Fworker.Database and by nothing else: no DDL in C#, no EF migrations or EnsureCreated, and nothing that asks Wolverine to build its tables.</summary>
    static readonly string[] SchemaBuilders = ["CREATE TABLE", "ALTER TABLE", "CREATE INDEX", ".Database.Migrate", "EnsureCreated", "SetupResources(", "ResetResourceState("];

    static IEnumerable<(string Path, string Code)> SourcesOfGatewayAndSrc() =>
        new[] { GatewayDir, System.IO.Path.Combine(Root, "src") }
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}") && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}"))
            .Select(f => (System.IO.Path.GetRelativePath(Root, f).Replace('\\', '/'), Code(File.ReadAllText(f))));

    [Fact]
    public void SchemaIsBuiltOnlyByTheScripts()
    {
        var offenders = new List<string>();
        foreach (var (path, code) in SourcesOfGatewayAndSrc())
            foreach (var word in SchemaBuilders)
                for (var i = code.IndexOf(word, StringComparison.OrdinalIgnoreCase); i >= 0; i = code.IndexOf(word, i + 1, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{path}:{LineOf(code, i)} {word}");
        Assert.True(offenders.Count == 0, "the schema is built by the scripts of src/Fworker.Database/scripts/Update (DbUp) and by nothing else; found: " + string.Join(", ", offenders));
    }

    [Fact]
    public void WolverineNeverBuildsItsStorage()
    {
        var code = Code(File.ReadAllText(System.IO.Path.Combine(GatewayDir, "Composition", "WolverineHostService.cs")));
        Assert.Contains("AutoBuildMessageStorageOnStartup = AutoCreate.None", code);
        Assert.Contains("OverrideAutoCreateResources(AutoCreate.None)", code);
        Assert.DoesNotContain("AutoCreate.CreateOrUpdate", code);
        Assert.DoesNotContain("AutoCreate.All", code);
    }

    [Fact]
    public void TheSchemaRulesSeeAViolation_WhenOneIsPlanted()
    {
        Assert.Contains(SchemaBuilders, w => Code("var sql = \"CREATE TABLE x (a int)\";").Contains(w, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(SchemaBuilders, w => Code("await db.Database.MigrateAsync();").Contains(w, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(SchemaBuilders, w => Code("// CREATE TABLE in a comment\nvar x = 1;").Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheHostReferencesEverySrcProject()
    {
        var refs = ProjectReferences(Csproj("gateway/Gateway.csproj"));
        Assert.Equal(AllowedProjectReferences.Keys.Order(), refs.Where(r => r.StartsWith("Fworker.")).Order());
    }

    [Fact]
    public void EverySrcDirectoryIsInTheTable()
    {
        // a new project under src/ needs its row in AllowedProjectReferences, or it would escape the rules above
        var dirs = Directory.EnumerateDirectories(System.IO.Path.Combine(Root, "src")).Select(System.IO.Path.GetFileName).Order().ToList();
        Assert.Equal(AllowedProjectReferences.Keys.Order(), dirs);
    }

    [Fact]
    public void TheSolutionHoldsEverySrcAndTestProject()
    {
        var sln = File.ReadAllText(System.IO.Path.Combine(Root, "FactoryWorker.sln"));
        foreach (var p in AllowedProjectReferences.Keys.Concat(["Fworker.Domain.Tests", "Fworker.Workflow.Tests", "Gateway", "Gateway.Tests"]))
            Assert.Contains($"{p}.csproj", sln);
    }

    [Fact]
    public void TheDockerfileCopiesSrcBeforeRestoringTheGateway()
    {
        var docker = File.ReadAllText(System.IO.Path.Combine(Root, "Dockerfile"));
        var copy = docker.IndexOf("COPY src/", StringComparison.Ordinal);
        var restore = docker.IndexOf("dotnet restore", StringComparison.Ordinal);
        Assert.True(copy >= 0 && copy < restore, "the Dockerfile must copy src/ before `dotnet restore`: the gateway references the src/ projects");
    }

    [Fact]
    public void TheProjectReferenceScanSeesAViolation_WhenOneIsPlanted()
    {
        Assert.Equal(["Fworker.Domain"], ProjectReferences("<ProjectReference Include=\"../Fworker.Domain/Fworker.Domain.csproj\" />"));
        Assert.Equal(["Gateway"], ProjectReferences("<ProjectReference Include=\"..\\..\\gateway\\Gateway.csproj\"/>"));
    }

    // ---- the checker itself ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Code_BlanksCommentsButKeepsStringsAndLines()
    {
        var code = Code("var a = \"http://x\"; // while (true) {}\n/* Task.Run\n  foreach */ var b = 1;");
        Assert.Contains("\"http://x\"", code);
        Assert.DoesNotContain("while", code);
        Assert.DoesNotContain("Task.Run", code);
        Assert.DoesNotContain("foreach", code);
        Assert.Equal(2, code.Count(ch => ch == '\n'));
    }

    [Fact]
    public void TheRulesSeeAViolation_WhenOneIsPlanted()
    {
        // the patterns are not vacuous: each one matches the thing it forbids
        Assert.Matches(RouteMapping, "app.MapGet(\"/x\", () => 1);");
        Assert.Matches(RouteMapping, "app.MapMcp(\"/mcp\");");
        Assert.Matches(HostedRegistration, "services.AddHostedService(sp => x);");
        Assert.Matches(HostedRegistration, "services.AddSingleton<IHostedService>(sp => x);");
        Assert.Matches(ModuleDeclaration, "sealed class FooEndpoints(Plant plant) : IEndpointModule");
        Assert.Matches(ModuleDeclaration, "sealed class QcHub : IEndpointModule");
        Assert.DoesNotMatch(RouteMapping, "var x = dict.Keys.ToList();");
    }
}
