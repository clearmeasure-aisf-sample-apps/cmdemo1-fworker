using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Integrations.Commands;
using Fworker.Workflows.ReadModels;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The fake GitHub and Octopus of the Tier 1 suites (tests/FakeGitHub), started as a process of its own on a free port, for the tests that put the read models of W9.5 against what the scenarios put the rule scripts against. It is found where
/// <c>dotnet build tests/FakeGitHub -c Release</c> puts it; where it was not built <see cref="Start"/> answers null and the tests that need it return at once (the Tier 1 job of CI builds it and runs the same cases through the rule scripts and the gateway).
/// </summary>
public sealed class FakeGitHubRig : IDisposable
{
    public const string Owner = "clearmeasure-aisf-sample-apps", Repo = "cmdemo1-workorders";
    readonly Process process;
    public int Port { get; }
    public HttpClient Http { get; }
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "fake-rig-" + Guid.NewGuid().ToString("N")[..10]);

    FakeGitHubRig(Process process, int port)
    {
        this.process = process; Port = port;
        Http = new HttpClient(new Fworker.Integrations.GitHub.GitHubUserAgentHandler(new HttpClientHandler { AllowAutoRedirect = false })) { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(60) };
        Directory.CreateDirectory(Dir);
    }

    public static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gateway", "Program.cs")) && File.Exists(Path.Combine(d.FullName, "config", "instance.json"))) return d.FullName;
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The rig, or null when tests/FakeGitHub has not been built.</summary>
    public static FakeGitHubRig? Start()
    {
        var dll = new[] { "Release", "Debug" }.Select(c => Path.Combine(Root(), "tests", "FakeGitHub", "bin", c, "net10.0", "FakeGitHub.dll")).FirstOrDefault(File.Exists);
        if (dll is null) return null;
        int port; using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["FAKE_PORT"] = port.ToString();
        psi.Environment.Remove("FAKE_RECORD_DIR");
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        var rig = new FakeGitHubRig(p, port);
        var until = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { if (rig.Http.PostAsync("_fake/reset", new StringContent("{}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult().IsSuccessStatusCode) return rig; } catch (HttpRequestException) { }
            if (DateTime.UtcNow > until || p.HasExited) { rig.Dispose(); throw new InvalidOperationException("the fake GitHub did not start"); }
            Thread.Sleep(200);
        }
    }

    public void Dispose()
    {
        try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } } catch { }
        Http.Dispose();
        try { Directory.Delete(Dir, true); } catch { }
    }

    /// <summary>A control call of the fake (<c>/_fake/{name}</c>) and its answer.</summary>
    public JsonNode? Fake(string name, object? body = null)
    {
        var json = body is null ? "{}" : body is JsonNode n ? n.ToJsonString() : System.Text.Json.JsonSerializer.Serialize(body);
        using var resp = Http.PostAsync($"_fake/{name}", new StringContent(json, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.True(resp.IsSuccessStatusCode, $"/_fake/{name}: {(int)resp.StatusCode} {text}");
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    /// <summary>A call to the fake's GitHub API (as the product would make it).</summary>
    public JsonNode? Api(string method, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), $"repos/{Owner}/{Repo}/{path}");
        if (body is not null) req.Content = new StringContent(body is JsonNode n ? n.ToJsonString() : System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = Http.SendAsync(req).GetAwaiter().GetResult();
        var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.True(resp.IsSuccessStatusCode, $"{method} {path}: {(int)resp.StatusCode} {text}");
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    public JsonObject State() => (JsonObject)Fake("state")!;

    /// <summary>An item on the board, in a column.</summary>
    public int NewItem(string title, string[]? labels = null, string column = "Development", string body = "x")
        => Fake("issue", new { title, body, labels = labels ?? ["factory worker", "Auto Merge"], column })!["number"]!.GetValue<int>();

    /// <summary>The branch of an item's development, pushed with its files and checks of a profile (green or red), and the pull request opened for it.</summary>
    public (int Pr, string Sha) NewPr(int item, string[]? files = null, string profile = "green")
    {
        var branch = $"factory-worker/{item}-development";
        var sha = Fake("push-branch", new { branch, files = files ?? ["src/App.cs"], profile })!["sha"]!.ToString();
        var pr = Api("POST", "pulls", new { head = branch, title = $"#{item} change", body = "Closes #" + item })!["number"]!.GetValue<int>();
        return (pr, sha);
    }

    public string Push(int item, string[]? files = null, string profile = "green") => Fake("push-branch", new { branch = $"factory-worker/{item}-development", files = files ?? ["src/App.cs"], profile })!["sha"]!.ToString();

    /// <summary>The instance configuration of the repository with Octopus pointed at the fake.</summary>
    internal InstanceConfig Instance(Action<JsonObject>? edit = null)
    {
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "config", "instance.json")))!;
        root["octopus"]!["url"] = $"http://127.0.0.1:{Port}/octopus";
        edit?.Invoke(root);
        var path = Path.Combine(Dir, "instance." + Guid.NewGuid().ToString("N")[..6] + ".json");
        File.WriteAllText(path, root.ToJsonString());
        return InstanceConfig.Load(path, _ => null);
    }

    public sealed class Clock
    {
        public DateTime Now = DateTime.UtcNow;
        public readonly List<TimeSpan> Waits = [];
    }

    /// <summary>The read models over the fake: a clock the waits move, no real waiting, a state directory of this rig.</summary>
    internal (ReadModelParts Parts, Clock Clock) Parts(Action<JsonObject>? edit = null, Dictionary<string, string?>? env = null, IRefusalCounts? refusals = null)
    {
        var clock = new Clock();
        var values = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "fake-token", ["GITHUB_API_URL"] = $"http://127.0.0.1:{Port}", ["FACTORY_STATE_DIR"] = Path.Combine(Dir, "state"), ["FACTORY_ENVIRONMENT"] = "tdd",
            ["OCTOPUS_API_KEY"] = "API-FAKE", ["FACTORY_WEBHOOK_SECRET"] = new string('w', 40),
        };
        if (env is not null) foreach (var kv in env) values[kv.Key] = kv.Value;
        var github = new HttpClient(new Fworker.Integrations.GitHub.GitHubUserAgentHandler(new HttpClientHandler { AllowAutoRedirect = false })) { BaseAddress = new Uri($"http://127.0.0.1:{Port}/"), Timeout = Timeout.InfiniteTimeSpan };
        var parts = ReadModelParts.Build(Instance(edit), null, k => values.GetValueOrDefault(k), github, new HttpClient(new Fworker.Integrations.GitHub.GitHubUserAgentHandler()), null, null, null, refusals,
            (t, ct) => { clock.Waits.Add(t); clock.Now += t; return Task.CompletedTask; }, () => clock.Now);
        return (parts, clock);
    }

    /// <summary>The state comment of an item as the product reads it, or null.</summary>
    public JsonObject? StateComment(int item)
    {
        var comments = Api("GET", $"issues/{item}/comments?per_page=100") as JsonArray ?? [];
        foreach (var c in comments)
            if (c?["body"]?.ToString() is { } b && b.StartsWith(ItemStateComment.Marker("factory-worker"), StringComparison.Ordinal)) return ItemStateComment.Parse(b);
        return null;
    }

    /// <summary>Writes the item's state comment (what the rule scripts' Save-State writes), as the gateway's login.</summary>
    public void SetState(int item, Action<JsonObject> edit)
    {
        var s = StateComment(item) ?? new JsonObject { ["schema"] = 1, ["item"] = item };
        edit(s);
        var body = ItemStateComment.Body("factory-worker", item, s);
        var comments = Api("GET", $"issues/{item}/comments?per_page=100") as JsonArray ?? [];
        var existing = comments.FirstOrDefault(c => c?["body"]?.ToString().StartsWith(ItemStateComment.Marker("factory-worker"), StringComparison.Ordinal) == true);
        if (existing is null) Api("POST", $"issues/{item}/comments", new { body });
        else Api("PATCH", $"issues/comments/{existing["id"]}", new { body });
    }
}
