using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Xunit;

/// <summary>
/// Issue 94 where the engine acts (W9.10g review): the gap a restore noted belongs to the first full sweep, also with PlantJobs. That sweep begins after the restore and would offer a claim that ran out in the gap as a relaunch; a recurring job
/// that renewed the claims later (the scheduler's first run is 15 s after it starts, and a job that answers waiting-for-restore runs again one interval later) could not promise to come first.
/// </summary>
public class SweeperGapTests
{
    sealed class Rig
    {
        public readonly List<JsonObject> Calls = [];
        public readonly SweepGap Gap = new();
        public readonly TaskCompletionSource Restored = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Code;
        public readonly Sweeper Sweeper;

        public Rig(bool leaderOn)
        {
            var runner = new Runner("/nonexistent/factory.ps1");
            // (W9.9a: the Runner answers a verb that has a route from the table, as it does where the engine acts)
            var routes = new V2VerbRoutes();
            routes.Add(BoardRoutes.Verbs, "sync-board", (stdin, _) =>
            {
                lock (Calls) Calls.Add((JsonObject)JsonNode.Parse(stdin)!);
                return Task.FromResult(new VerbAnswer(Code, new JsonObject { ["ok"] = true, ["offers"] = new JsonArray(), ["actions"] = new JsonArray(), ["errors"] = new JsonArray(), ["unconfirmed"] = new JsonArray() }.ToJsonString()));
            });
            runner.Routes = routes;
            var plant = new Plant("secret", (_, _, _, _) => Task.CompletedTask, 30);
            var hub = new EventHub();
            var qc = new QcHub(runner, plant, hub, new QcSettings(), "https://gateway.example.test", "gateway-" + new string('k', 24));
            Sweeper = new Sweeper(runner, hub, plant, qc, 3600, false, null, Restored.Task) { MayAct = () => true, LeaderOn = leaderOn, Gap = Gap, PlantJobs = true };
        }

        public int Count() { lock (Calls) return Calls.Count; }

        public async Task<bool> WaitForCall(int seconds)
        {
            for (var i = 0; i < seconds * 20 && Count() == 0; i++) await Task.Delay(50);
            return Count() > 0;
        }
    }

    [Fact]
    public async Task TheFirstFullSweepAfterTheRestore_HasTheGap_AlsoWhereThePlantStepsAreJobs()
    {
        var r = new Rig(leaderOn: true);
        await r.Sweeper.StartAsync(default);
        try
        {
            // the sweep waits for the restore, however long it takes: nothing is read before it, so no claim is offered before its gap is known
            Assert.False(await r.WaitForCall(5));
            var since = DateTime.UtcNow.AddMinutes(-7);
            r.Gap.Set(since);                 // (SnapshotCoordinator sets it before it completes the restore)
            r.Restored.SetResult();
            Assert.True(await r.WaitForCall(10));
            var call = r.Calls[0];
            Assert.Equal(since.ToString("o"), call["gap"]!["since"]!.ToString());
            Assert.True(DateTime.TryParse(call["gap"]!["until"]!.ToString(), out _));
            Assert.Contains("claim-ref-reap", ((JsonArray)call["skip_steps"]!).Select(x => x!.ToString()));   // the pass is the PlantJobs one: the jobs have the other steps
            Assert.Null(r.Gap.Take());                                                                      // taken once: the next sweeps carry none
        }
        finally { await r.Sweeper.StopAsync(default); }
    }

    [Fact]
    public async Task ASweepThatFailed_GivesTheGapBack_ForTheNextOne()
    {
        var r = new Rig(leaderOn: false) { Code = 20 };
        r.Restored.SetResult();
        var since = DateTime.UtcNow.AddMinutes(-3);
        r.Gap.Set(since);
        await r.Sweeper.StartAsync(default);
        try
        {
            Assert.True(await r.WaitForCall(10));
            Assert.Equal(since.ToString("o"), r.Calls[0]["gap"]!["since"]!.ToString());
            DateTime? back = null;
            for (var i = 0; i < 40 && back is null; i++) { back = r.Gap.Take(); if (back is null) await Task.Delay(50); }   // (put back once the failed pass is handled)
            Assert.Equal(since, back);
        }
        finally { await r.Sweeper.StopAsync(default); }
    }
}
