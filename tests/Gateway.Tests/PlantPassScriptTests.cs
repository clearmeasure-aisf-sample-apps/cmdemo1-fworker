using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10g: <c>skip_steps</c> of the real sync-board (plugin/scripts/lib/Verbs.Sweep.ps1) against the fake GitHub: an ordinary pass leaves the steps out that the gateway's recurring jobs take (the jobs are C#: PlantSteps), and the first full pass after a gap
/// still renews the claims that ran out inside it. The rule script is the real one (pwsh, factory.ps1 --server sync-board); the board is the Tier 1 suites' fake. The tests return at once where pwsh or the fake (dotnet build tests/FakeGitHub -c Release) is not there.
/// </summary>
public sealed class PlantPassScriptTests : IDisposable
{
    readonly FakeGitHubRig? rig = FakeGitHubRig.Start();
    readonly string dir = Directory.CreateTempSubdirectory("plant-pass-").FullName;
    string? config;

    public void Dispose() { rig?.Dispose(); try { Directory.Delete(dir, true); } catch { } }

    static bool HasPwsh() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Any(d => File.Exists(Path.Combine(d, "pwsh")));

    Task<(int Exit, JsonObject Answer)> SyncBoard(JsonObject args) => Script("sync-board", args);

    async Task<(int Exit, JsonObject Answer)> Script(string verb, JsonObject args)
    {
        var root = FakeGitHubRig.Root();
        if (config is null)
        {
            var cfg = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config", "instance.json")))!;
            cfg["octopus"]!["url"] = $"http://127.0.0.1:{rig!.Port}/octopus";
            cfg["timing"]!["sweepSeenCount"] = 1;
            config = Path.Combine(dir, "instance.test.json"); File.WriteAllText(config, cfg.ToJsonString());
        }
        var psi = new ProcessStartInfo("pwsh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "plugin", "scripts", "factory.ps1"), "--server", verb }) psi.ArgumentList.Add(a);
        psi.Environment["FACTORY_CONFIG"] = config; psi.Environment["FACTORY_STATE_DIR"] = Path.Combine(dir, "state"); psi.Environment["FACTORY_DATA_DIR"] = Path.Combine(dir, "data");
        psi.Environment["GITHUB_API_URL"] = $"http://127.0.0.1:{rig!.Port}"; psi.Environment["GH_TOKEN"] = "fake-operator"; psi.Environment["OCTOPUS_API_KEY"] = "fake";
        psi.Environment["FACTORY_FAKE_APPS"] = "1"; psi.Environment["FACTORY_ENV_SCOPE"] = "0"; psi.Environment["FACTORY_ENVIRONMENT"] = "tdd"; psi.Environment["FACTORY_AUTHOR_APP"] = "0";
        psi.Environment["FACTORY_PLUGIN_SCRIPTS"] = Path.Combine(root, "plugin", "scripts"); psi.Environment["DOTNET_MultiCoreJitMinNumCpus"] = "65535";
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync(args.ToJsonString()); p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await p.WaitForExitAsync(cts.Token);
        var text = (await output).Trim();
        Assert.True(text.Length > 0, "no answer; stderr: " + await error);
        return (p.ExitCode, (JsonObject)JsonNode.Parse(text.Split('\n')[^1])!);
    }

    static string[] Skipped(JsonObject a) => (a["skipped"] as JsonArray ?? []).Select(x => x!.ToString()).ToArray();
    int Unplaced() => rig!.Fake("issue", new { title = "Created by a verb that has not placed it", body = "x", labels = new[] { "factory worker" } })!["number"]!.GetValue<int>();
    string? ColumnOf(int n) => rig!.State()["issues"]!.AsArray().First(i => i!["number"]!.GetValue<int>() == n)!["column"]?.ToString();

    [Fact]
    public async Task AnOrdinaryPassThatIsToldToLeaveTheStepsOut_LeavesThemOut_AndOneThatIsNotTold_DoesThemAsBefore()
    {
        if (rig is null || !HasPwsh()) return;
        var lonely = Unplaced();
        var (exit, left) = await SyncBoard(new JsonObject { ["skip_steps"] = new JsonArray("claim-ref-reap", "board-guard", "qc-sync", "replenish") });
        Assert.Equal(0, exit);
        Assert.Null(ColumnOf(lonely));                                              // the board guard is the job's
        Assert.Contains("board-guard-job", Skipped(left)); Assert.Contains("claim-ref-reap-job", Skipped(left)); Assert.Contains("replenish-job", Skipped(left));
        Assert.NotNull(left["design_waiting"]);                                     // the item loop still counts the keeper's column

        var (exit2, as1) = await SyncBoard(new JsonObject());
        Assert.Equal(0, exit2);
        Assert.Equal("Technical Design", ColumnOf(lonely));                         // v1's sweep: the guard places it, as before
        Assert.DoesNotContain("board-guard-job", Skipped(as1));
    }

    static readonly System.Text.RegularExpressions.Regex Fence = new("(?s)```json\\s*(.*?)\\s*```");

    JsonObject StateComment(int n) => rig!.State()["issues"]!.AsArray().First(i => i!["number"]!.GetValue<int>() == n)!["comments"]!.AsArray().First(c => c!["body"]!.ToString().StartsWith("<!-- factory-state:"))!.AsObject();

    JsonObject ClaimOf(int n) => (JsonObject)JsonNode.Parse(Fence.Match(StateComment(n)["body"]!.ToString()).Groups[1].Value)!["claim"]!;

    // an item whose owner session holds a claim that lapsed at `lapsed`: claimed by the verb, then the state comment edited as the scenarios do (Set-ItemState): owner true, the lease ended
    async Task<int> OwnedClaimThatLapsed(DateTime lapsed)
    {
        var n = rig!.NewItem("Owned, lapsed", ["factory worker"], "Development");
        var (exit, _) = await Script("claim-item", new JsonObject { ["item"] = n, ["column"] = "Development", ["holder"] = "gina" });
        Assert.Equal(0, exit);
        var comment = StateComment(n); var body = comment["body"]!.ToString();
        var m = Fence.Match(body);
        var st = (JsonObject)JsonNode.Parse(m.Groups[1].Value)!;
        st["claim"]!["owner"] = true; st["claim"]!["lease_until"] = lapsed.ToString("o");
        rig.Api("PATCH", $"issues/comments/{comment["id"]}", new JsonObject { ["body"] = body[..m.Groups[1].Index] + st.ToJsonString() + body[(m.Groups[1].Index + m.Groups[1].Length)..] });
        return n;
    }

    [Fact]
    public async Task TheFirstFullPassAfterAGap_WithTheJobsSkipsSet_RenewsTheClaimThatRanOutInsideIt_AndOffersNoRelaunch()
    {
        if (rig is null || !HasPwsh()) return;
        var now = DateTime.UtcNow;
        var inside = await OwnedClaimThatLapsed(now.AddMinutes(-5));
        var before = await OwnedClaimThatLapsed(now.AddMinutes(-90));   // ran out before the gateway went down: not the gap's
        var gap = new JsonObject { ["since"] = now.AddMinutes(-20).ToString("o"), ["until"] = now.ToString("o") };
        JsonArray Skips() => new("claim-ref-reap", "board-guard", "qc-sync", "replenish");
        static bool Relaunch(JsonObject a, int n) => a["offers"]!.AsArray().Any(o => o!["item"]!.GetValue<int>() == n && o["reason"]!.ToString() == "relaunch");

        // (issue 94, the review of W9.10g) without the gap, the first pass offers the lapsed claim again: a second session would take the item the owner is about to come back to
        var (exit0, nogap) = await SyncBoard(new JsonObject { ["skip_steps"] = Skips(), ["free_workers"] = 1 });
        Assert.Equal(0, exit0);
        Assert.True(Relaunch(nogap, inside), nogap["offers"]!.ToJsonString());
        Assert.False(ClaimOf(inside).ContainsKey("gap_renewed"));

        var (exit, a) = await SyncBoard(new JsonObject { ["skip_steps"] = Skips(), ["free_workers"] = 1, ["gap"] = gap });
        Assert.Equal(0, exit);
        var renewed = a["actions"]!.AsArray().Where(x => x!["action"]!.ToString() == "claim-renewed-after-gap").Select(x => x!["item"]!.GetValue<int>()).ToArray();
        Assert.Equal([inside], renewed);
        Assert.False(Relaunch(a, inside), a["offers"]!.ToJsonString());
        Assert.True(ClaimOf(inside).ContainsKey("gap_renewed"));
        Assert.True(DateTime.Parse(ClaimOf(inside)["lease_until"]!.ToString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal) > now);
        Assert.False(ClaimOf(before).ContainsKey("gap_renewed"));
    }
}
