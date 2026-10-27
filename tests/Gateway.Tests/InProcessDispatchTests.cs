using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The gateway's own InProcess (the one the container's McpTools holds, with the gateway's Admit), reached the way a session reaches it: an MCP
/// tools/call at /mcp. InProcessPatternTests builds an InProcess of its own; this pins the one Program.cs wires (C5a: it finds the endpoints
/// through EndpointDataSource instead of the WebApplication).
/// </summary>
public sealed class InProcessDispatchTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    async Task<(int Status, string Body)> CallTool(string tool, JsonObject? args = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var rpc = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() } };
        var bytes = Encoding.UTF8.GetBytes(rpc.ToJsonString());
        var ctx = await gw.Host.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Scheme = "http";
            c.Request.Host = new HostString("127.0.0.1", GatewayEnvironment.ApiPort);
            c.Request.Path = "/mcp";
            c.Connection.LocalPort = GatewayEnvironment.ApiPort;
            c.Connection.LocalIpAddress = IPAddress.Loopback;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers.Authorization = "Bearer " + GatewayEnvironment.AdminToken;
            c.Request.Headers.Accept = "application/json, text/event-stream";
            c.Request.ContentType = "application/json";
            c.Request.Body = new MemoryStream(bytes);
            c.Request.ContentLength = bytes.Length;
        }, cts.Token);
        var text = "";
        if (ctx.Response.Body is { CanRead: true } rb) { using var rd = new StreamReader(rb); text = await rd.ReadToEndAsync(cts.Token); }
        return (ctx.Response.StatusCode, text);
    }

    // the JSON-RPC answer, from a JSON body or from the data line of a one-event stream; then the tool's first text content
    static string ToolText(string body)
    {
        var json = body.TrimStart().StartsWith('{') ? body : string.Join("", body.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l["data:".Length..].Trim()));
        var answer = JsonNode.Parse(json)!;
        Assert.True(answer["error"] is null, "JSON-RPC error: " + json);
        return answer["result"]?["content"]?[0]?["text"]?.ToString() ?? "";
    }

    [Fact]
    public async Task FactoryStatus_ReachesStatusEndpoint_ThroughTheGatewaysInProcess()
    {
        if (!StubPwsh.Supported) return;
        var (status, body) = await CallTool("factory_status");
        Assert.True(status == 200, $"/mcp answered {status}: {body[..Math.Min(300, body.Length)]}");
        var text = ToolText(body);
        // the body of GET /api/v1/status, as the admin gets it over HTTP
        var j = JsonNode.Parse(text) as JsonObject;
        Assert.True(j != null && j.ContainsKey("plant") && j.ContainsKey("lastEventId") && (bool?)j["ownerMode"] == true, "factory_status: " + text[..Math.Min(300, text.Length)]);
    }

    [Fact]
    public async Task Metrics_ReachesMetricsEndpoint_ThroughTheGatewaysInProcess()
    {
        if (!StubPwsh.Supported) return;
        var (status, body) = await CallTool("metrics");
        Assert.True(status == 200, $"/mcp answered {status}: {body[..Math.Min(300, body.Length)]}");
        var http = await gw.Host.SendAsync("GET", "/api/v1/metrics", GatewayEnvironment.ApiPort, GatewayEnvironment.AdminToken);
        Assert.Equal(200, http.Status);
        // the tool's text is the endpoint's JSON: the same top-level fields as the HTTP answer
        var viaTool = JsonNode.Parse(ToolText(body)) as JsonObject;
        var viaHttp = http.Json as JsonObject;
        Assert.NotNull(viaTool);
        Assert.NotNull(viaHttp);
        Assert.Equal(viaHttp!.Select(kv => kv.Key).Order(), viaTool!.Select(kv => kv.Key).Order());
    }
}
