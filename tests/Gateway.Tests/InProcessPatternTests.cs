using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>The gateway's endpoints as an IEndpointRouteBuilder, the shape InProcess reads them from (it is given the WebApplication).</summary>
sealed class EndpointRoutes(IServiceProvider services) : IEndpointRouteBuilder
{
    public IServiceProvider ServiceProvider => services;
    public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource> { services.GetRequiredService<EndpointDataSource>() };
    public IApplicationBuilder CreateApplicationBuilder() => throw new NotSupportedException();
}

/// <summary>
/// The routes the MCP tools call in process (gateway/Mcp/InProcess.cs finds them by method and RoutePattern.RawText): each exists exactly
/// once, and one call per pattern through InProcess, as the operator, answers as pinned.
/// </summary>
public sealed class InProcessPatternTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    /// <summary>method, pattern, route values ("k=v;k=v"), body, pinned status, pinned JSON code (null: none checked).</summary>
    public static readonly (string Method, string Pattern, string Route, string? Body, int Status, string? Code)[] Pinned =
    {
        ("GET", "/api/v1/status", "", null, 200, null),
        ("GET", "/api/v1/metrics", "", null, 200, null),
        ("POST", "/api/v1/messages", "", "{\"to\":\"alice\",\"type\":\"note\",\"text\":\"t\"}", 404, "no-such-peer"),
        ("GET", "/api/v1/messages", "", null, 200, null),
        ("GET", "/api/v1/messages/{id}", "id=x", null, 404, "no-such-message"),
        // manager.enabled is off in the test configuration: the endpoint's own refusal
        ("GET", "/api/v1/digest", "", null, 403, "manager-disabled"),
        ("GET", "/api/v1/questions", "", null, 403, "manager-disabled"),
        ("POST", "/api/v1/supervisor/patrol-ack", "", "{\"seq\":1}", 403, "manager-disabled"),
        // the stub answers the rule script {"ok":true}
        ("POST", "/api/v1/verbs/{verb}", "verb=status", "{}", 200, null),
    };

    public static IEnumerable<object[]> Cases() => Pinned.Select(p => new object[] { p.Method, p.Pattern });

    [Fact]
    public void PinnedPatternsExistOnce()
    {
        if (!StubPwsh.Supported) return;
        var all = Endpoints.Of(gw.Host.Services);
        foreach (var p in Pinned)
            Assert.True(all.Count(x => x.Method == p.Method && x.Pattern == p.Pattern) == 1, $"{p.Method} {p.Pattern} is mapped {all.Count(x => x.Method == p.Method && x.Pattern == p.Pattern)} times (InProcess needs exactly one)");
        Assert.Equal(8, Pinned.Select(p => p.Pattern).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task InProcessCall_AnswersAsPinned(string method, string pattern)
    {
        if (!StubPwsh.Supported) return;
        var p = Pinned.Single(x => x.Method == method && x.Pattern == pattern);
        var route = p.Route == "" ? null : p.Route.Split(';').Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]);
        var inProcess = new InProcess(new EndpointRoutes(gw.Host.Services), (_, _, next) => next());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (status, body) = await inProcess.Call(method, pattern, new Principal("admin"), route, "", p.Body, cts.Token);
        var code = new Answer(status, body, "").Code;
        Assert.True(p.Status == status && (p.Code == null || p.Code == code), $"{method} {pattern}: wanted {p.Status} {p.Code}, got {status} {code}: {body[..Math.Min(300, body.Length)]}");
    }
}
