using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The dashboard page and its connect prompts, pinned byte for byte. The constants were recorded from master before the page moved out of
/// Program.cs into embedded resources (gateway/Templates, docs/design/gateway-composition.md 3 G); the move must not change one byte of what is
/// served, nor of what the page's generator writes. A deliberate change of the page or of a prompt records new constants in the same pull request.
/// </summary>
public sealed class DashboardPageTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    const int PageLength = 68671;
    const string PageSha256 = "4bf00ce50b14f5a7dff436866397e608a97256f734364652cdb22ff2ca6ec79f";
    const string ConnectPromptSha256 = "0e5e3faabce77ff09204b66265d354b4d85bfb0823f9c0e8d375be21f9f47876";
    const string WorkerPromptSha256 = "90113fc07e21b63f79e3d669318bb306c15c09f664989aa6789c54be87cc2b70";
    const string WorkerMessagingPromptSha256 = "e9a7b6e5d866a5c928b648ab485a7209bd63b22484006b4176ce0cd494d6dcc1";
    const string SupervisorPromptSha256 = "ccc6aa67c27a485f95c77c2924cb189f94af8ea3bbd58b267e566858d373c965";
    const string OperatorPromptSha256 = "c96bc2a5904e988839abebfa280515933e2136c24873526d56ba9db17c9f63f0";

    // the connect-prompt block: whole lines, from buildPrompt through operatorPrompt, up to the messaging section of the page's script
    const string BlockStart = "function buildPrompt(p){", BlockEnd = "// ---- messaging and the supervisor (worker-messaging.md 7)";

    [Fact]
    public async Task Dashboard_IsServedByteIdentical()
    {
        if (!StubPwsh.Supported) return;
        var (status, type, body) = await Get("/dashboard");
        Assert.Equal(200, status);
        Assert.Equal("text/html; charset=utf-8", type);
        // the actual values in the message, whole, for a deliberate change of the page
        Assert.True(PageLength == body.Length && PageSha256 == Sha(body), $"the served page differs: {body.Length} bytes, sha256 {Sha(body)}");
    }

    [Fact]
    public async Task ConnectPromptBlock_IsPinned_AndIsTheEmbeddedResource()
    {
        if (!StubPwsh.Supported) return;
        var block = Block(Encoding.UTF8.GetString((await Get("/dashboard")).Body));
        var sha = Sha(Encoding.UTF8.GetBytes(block));
        Assert.True(ConnectPromptSha256 == sha, $"the connect-prompt block differs: sha256 {sha}");
        using var st = typeof(DashboardPage).Assembly.GetManifestResourceStream("connect-prompt.js");
        Assert.NotNull(st);
        using var ms = new MemoryStream(); st!.CopyTo(ms);
        Assert.Equal(block, Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task ConnectPrompts_RenderAsPinned()
    {
        if (!StubPwsh.Supported) return;
        // the page's generator runs in node, as docs/deploy/gen-connect-prompt.sh and Tier 1 (messaging.ps1, Msg-Node) run it; without node there is
        // nothing to run it in (the CI runners have node)
        var node = Which("node");
        if (node == null) return;
        var m = Regex.Match(Encoding.UTF8.GetString((await Get("/dashboard")).Body), @"/\*render-start\*/(.*)/\*render-end\*/", RegexOptions.Singleline);
        Assert.True(m.Success, "no render block in the dashboard");
        var dir = Path.Combine(Path.GetTempPath(), "dashboard-tests-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "block.js"), m.Groups[1].Value);
            // fake arguments only: the credentials are made-up strings of the right shape, like messaging.ps1's
            var cloud = "{env:'tdd',url:'https://gw.example',token:'w1.tok.sig',name:'cloud-1',type:'claude-cloud',cap:'2',repo:'https://github.com/o/r',qcProfile:'',qcAddress:'',qcCapacity:''";
            File.WriteAllText(Path.Combine(dir, "run.js"),
                "const fs=require('fs');const f=new Function(fs.readFileSync(__dirname+'/block.js','utf8')+'\\nreturn buildPrompt;');const b=f();\n" +
                "console.log(JSON.stringify([b(" + cloud + ",messaging:false,asking:false}),b(" + cloud + ",messaging:true,asking:true}),\n" +
                "b({env:'uat',url:'https://gw.example',token:'m1.supervisor.1.sig',name:'supervisor',supervisor:true}),\n" +
                "b({env:'tdd',url:'https://gw.example',token:'o1.operator-session.1.sig',name:'operator-session',operator:true})]));\n");
            var psi = new ProcessStartInfo(node, Path.Combine(dir, "run.js")) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync(); var errTask = p.StandardError.ReadToEndAsync();
            Assert.True(p.WaitForExit(30000), "node did not finish within 30 s");
            var output = await outTask; var err = await errTask;
            Assert.True(p.ExitCode == 0, "node: " + err);
            var prompts = JsonNode.Parse(output)!.AsArray().Select(x => (string)x!).ToList();
            Assert.Equal(4, prompts.Count);
            Assert.StartsWith("You are joining the software factory as a factory worker for the TDD environment", prompts[0]);
            Assert.NotEqual(prompts[0], prompts[1]);
            var actual = prompts.Select(x => Sha(Encoding.UTF8.GetBytes(x))).ToList();
            var pinned = new[] { WorkerPromptSha256, WorkerMessagingPromptSha256, SupervisorPromptSha256, OperatorPromptSha256 };
            Assert.True(pinned.SequenceEqual(actual), "the rendered prompts differ (worker, worker with messaging, supervisor, operator): sha256 " + string.Join(" ", actual));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>The connect-prompt block of the page, as whole lines (with the newline of its last line).</summary>
    static string Block(string page)
    {
        var s = page.IndexOf("\n" + BlockStart, StringComparison.Ordinal);
        var e = page.IndexOf("\n" + BlockEnd, StringComparison.Ordinal);
        Assert.True(s >= 0 && e > s, "the connect-prompt block was not found in the page");
        return page[(s + 1)..(e + 1)];
    }

    /// <summary>A GET on the API port, with the response's Content-Type and raw bytes (GatewayHost.SendAsync gives neither).</summary>
    async Task<(int Status, string Type, byte[] Body)> Get(string path)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ctx = await gw.Host.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Scheme = "http";
            c.Request.Host = new HostString("127.0.0.1", GatewayEnvironment.ApiPort);
            c.Request.Path = path;
            c.Connection.LocalPort = GatewayEnvironment.ApiPort;
            c.Connection.LocalIpAddress = IPAddress.Loopback;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
        }, cts.Token);
        using var ms = new MemoryStream();
        if (ctx.Response.Body is { CanRead: true } rb) await rb.CopyToAsync(ms, cts.Token);
        return (ctx.Response.StatusCode, ctx.Response.ContentType ?? "", ms.ToArray());
    }

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static string? Which(string name)
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var f = Path.Combine(d, name);
            if (File.Exists(f)) return f;
            if (OperatingSystem.IsWindows() && File.Exists(f + ".exe")) return f + ".exe";
        }
        return null;
    }
}
