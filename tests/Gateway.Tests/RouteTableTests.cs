using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>The endpoints of the gateway's EndpointDataSource as (method, RawText), a missing method read as GET (as InProcess reads it).</summary>
public static class Endpoints
{
    public static IReadOnlyList<(string Method, string Pattern)> Of(IServiceProvider services) =>
        services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? new[] { "GET" }).Select(m => (m, e.RoutePattern.RawText ?? "")))
            .ToList();
}

/// <summary>The route-and-auth table in two-port mode: every mapped route is in the table, and each answers as pinned without credentials.</summary>
public sealed class RouteTableTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    [Fact]
    public void Completeness_EveryMappedRouteIsInTheTableAndNoOther()
    {
        if (!StubPwsh.Supported) return;
        var mapped = Endpoints.Of(gw.Host.Services).Select(x => $"{x.Method} {x.Pattern}").ToHashSet(StringComparer.Ordinal);
        var table = RouteTable.Rows.Select(r => $"{r.Method} {r.Pattern}").ToList();
        Assert.Equal(table.Count, table.Distinct(StringComparer.Ordinal).Count());
        var notInTable = mapped.Except(table, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var notMapped = table.Except(mapped, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (notInTable.Count + notMapped.Count > 0)
            Assert.Fail("the mapped routes and RouteTable.Rows differ\n  mapped, not in the table: " + string.Join(", ", notInTable) + "\n  in the table, not mapped: " + string.Join(", ", notMapped));
    }

    [Theory]
    [MemberData(nameof(RouteTable.TwoPortCases), MemberType = typeof(RouteTable))]
    public async Task Unauthenticated_AnswersAsPinned(string method, string pattern, int port)
    {
        if (!StubPwsh.Supported) return;
        var row = RouteTable.Find(method, pattern);
        var a = await gw.Host.SendAsync(method, RouteTable.PathOf(pattern), port);
        if (port == GatewayEnvironment.HookPort && !(method == "POST" && pattern is "/webhooks/github" or "/webhooks/octopus"))
        {
            Assert.Equal(404, a.Status);
            return;
        }
        RouteTable.AssertAnswer(row, a);
        if (row.Status == 302) Assert.Equal("/dashboard", a.Location);
    }

    [Fact]
    public async Task AdminSmoke_TheOperatorTokenIsAdmittedOnTheApiPort()
    {
        if (!StubPwsh.Supported) return;
        var a = await gw.Host.SendAsync("GET", "/api/v1/status", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(200, a.Status);
        Assert.Equal(true, (bool?)a.Json?["ownerMode"]);
    }
}

/// <summary>The same table in container mode (FACTORY_SINGLE_PORT): one port, the API column for every route.</summary>
public sealed class SinglePortRouteTableTests(SinglePortGateway gw) : IClassFixture<SinglePortGateway>
{
    [Fact]
    public void Completeness_TheSameRoutesAreMapped()
    {
        if (!StubPwsh.Supported) return;
        var mapped = Endpoints.Of(gw.Host.Services).Select(x => $"{x.Method} {x.Pattern}").OrderBy(x => x, StringComparer.Ordinal).ToList();
        var table = RouteTable.Rows.Select(r => $"{r.Method} {r.Pattern}").OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(table, mapped);
    }

    [Theory]
    [MemberData(nameof(RouteTable.SinglePortCases), MemberType = typeof(RouteTable))]
    public async Task Unauthenticated_AnswersAsPinned(string method, string pattern)
    {
        if (!StubPwsh.Supported) return;
        var row = RouteTable.Find(method, pattern);
        var a = await gw.Host.SendAsync(method, RouteTable.PathOf(pattern), GatewayEnvironment.ApiPort);
        RouteTable.AssertAnswer(row, a);
    }
}
