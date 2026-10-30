using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10g: the plant pass of the real sync-board (plugin/scripts/lib/Verbs.Sweep.ps1) against the fake GitHub: <c>plant_steps</c> runs only the steps it names and reads no item, and <c>skip_steps</c> leaves steps out of an ordinary
/// pass. The rule script is the real one (pwsh, factory.ps1 --server sync-board); the board is the Tier 1 suites' fake. The tests return at once where pwsh or the fake (dotnet build tests/FakeGitHub -c Release) is not there.
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
    public async Task APlantPassForTheBoardGuardPlacesTheIssue_AndDoesNothingElse()
    {
        if (rig is null || !HasPwsh()) return;
        var lonely = Unplaced();
        var waiting = rig.NewItem("Waits in Technical Design", ["factory worker"], "Technical Design");
        var (exit, a) = await SyncBoard(new JsonObject { ["plant_steps"] = new JsonArray("board-guard") });
        Assert.Equal(0, exit);
        Assert.True(a["plant"]!.GetValue<bool>());
        Assert.Equal("Technical Design", ColumnOf(lonely));
        Assert.Contains(a["actions"]!.AsArray(), x => x!["action"]!.ToString() == "board-added" && x["item"]!.GetValue<int>() == lonely);
        Assert.Empty(a["offers"]!.AsArray());                                       // no item was read: no offers, no owners, no parks
        Assert.Null(a["design_waiting"]);                                           // the count is a step of its own
        Assert.Null(a["release_queue"]);
        Assert.Empty(Skipped(a));                                                   // every other step is not "skipped": it was not asked for
        Assert.Equal("Technical Design", ColumnOf(waiting));
    }

    [Fact]
    public async Task APlantPassForTheKeepersCount_CountsTheKeepersColumn_AndPlacesNothing()
    {
        if (rig is null || !HasPwsh()) return;
        var lonely = Unplaced();
        rig.NewItem("Waits 1", ["factory worker"], "Technical Design"); rig.NewItem("Waits 2", ["factory worker"], "Technical Design");
        rig.NewItem("Builds", ["factory worker"], "Development"); rig.NewItem("Not ours", ["bug"], "Technical Design");
        var (exit, a) = await SyncBoard(new JsonObject { ["plant_steps"] = new JsonArray("design-waiting") });
        Assert.Equal(0, exit);
        Assert.Equal(2, a["design_waiting"]!.GetValue<int>());
        Assert.Null(ColumnOf(lonely));                                              // the board guard was not asked for
        Assert.Empty(a["actions"]!.AsArray());
    }

    [Fact]
    public async Task APlantPassForTheReapAndTheReplenishment_RunsAndAnswers_OnABoardWithNothingToDo()
    {
        if (rig is null || !HasPwsh()) return;
        rig.NewItem("Builds", ["factory worker"], "Development");
        var (exit, a) = await SyncBoard(new JsonObject { ["plant_steps"] = new JsonArray("claim-ref-reap", "replenish"), ["free_workers"] = 1, ["replenish_waiting"] = 0 });
        Assert.Equal(0, exit);
        Assert.Empty(a["errors"]!.AsArray());
        Assert.True(a["steps"]!.AsObject().ContainsKey("claim-ref-reap"));
        Assert.Null(a["fenced"]);
        Assert.Empty(a["offers"]!.AsArray());
    }

    [Fact]
    public async Task AnOrdinaryPassThatIsToldToLeaveTheStepsOut_LeavesThemOut_AndOneThatIsNotTold_DoesThemAsBefore()
    {
        if (rig is null || !HasPwsh()) return;
        var lonely = Unplaced();
        var (exit, left) = await SyncBoard(new JsonObject { ["skip_steps"] = new JsonArray("claim-ref-reap", "board-guard", "qc-sync", "replenish") });
        Assert.Equal(0, exit);
        Assert.False(left["plant"]!.GetValue<bool>());
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
    public async Task APlantPassForTheGap_RenewsTheClaimThatRanOutInsideIt_AndOnlyThat()
    {
        if (rig is null || !HasPwsh()) return;
        var now = DateTime.UtcNow;
        var inside = await OwnedClaimThatLapsed(now.AddMinutes(-5));
        var before = await OwnedClaimThatLapsed(now.AddMinutes(-90));   // ran out before the gateway went down: not the gap's
        var gap = new JsonObject { ["since"] = now.AddMinutes(-20).ToString("o"), ["until"] = now.ToString("o") };

        var (exit0, _) = await SyncBoard(new JsonObject { ["skip_steps"] = new JsonArray("claim-ref-reap", "board-guard", "qc-sync", "replenish") });   // no gap is handed to an ordinary pass where the job has it
        Assert.Equal(0, exit0);
        Assert.False(ClaimOf(inside).ContainsKey("gap_renewed"));

        var (exit, a) = await SyncBoard(new JsonObject { ["plant_steps"] = new JsonArray("gap-renew"), ["gap"] = gap });
        Assert.Equal(0, exit);
        var renewed = a["actions"]!.AsArray().Where(x => x!["action"]!.ToString() == "claim-renewed-after-gap").Select(x => x!["item"]!.GetValue<int>()).ToArray();
        Assert.Equal([inside], renewed);
        Assert.True(ClaimOf(inside).ContainsKey("gap_renewed"));
        Assert.True(DateTime.Parse(ClaimOf(inside)["lease_until"]!.ToString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal) > now);
        Assert.False(ClaimOf(before).ContainsKey("gap_renewed"));
    }

    [Fact]
    public async Task APassThatNamesItemsIsNeverAPlantPass()
    {
        if (rig is null || !HasPwsh()) return;
        var n = rig.NewItem("Builds", ["factory worker"], "Development");
        var (exit, a) = await SyncBoard(new JsonObject { ["plant_steps"] = new JsonArray("board-guard"), ["items"] = new JsonArray(n) });
        Assert.Equal(0, exit);
        Assert.False(a["plant"]!.GetValue<bool>());
        Assert.True(a["incremental"]!.GetValue<bool>());
    }
}
