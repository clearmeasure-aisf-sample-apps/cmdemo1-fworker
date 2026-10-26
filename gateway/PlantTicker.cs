using System.Text.Json.Nodes;

sealed class PlantTicker(Plant plant, QcHub qc, Messenger messenger, Runner runner) : BackgroundService
{
    // the ticks act on the plant and on GitHub (lapsed offers, take-backs, QC): only in the process that holds the leader lease (Leader.cs)
    public Func<bool>? MayAct { get; init; }
    // The journal comment of an escalated question (worker-messaging.md 3.8). The journal verb waits up to 45 s for the item lock and answers exit 20
    // while an owner's merge holds it, so the comment is tried again every mgrEscalateRetrySeconds for mgrEscalateRetryMinutes; a final failure is
    // written to stdout with its code and kept on the question for the dashboard. The comment carries a name and a number of minutes, never text.
    sealed class PendingEscalation { public EscalationJob Job = null!; public DateTime NextAt, Deadline; public bool Running; }
    readonly List<PendingEscalation> escalations = new();

    void Escalate(List<EscalationJob> fresh)
    {
        var now = DateTime.UtcNow; List<PendingEscalation> due;
        lock (escalations)
        {
            foreach (var j in fresh) plant.OperatorQuestionEscalated(j);
            foreach (var j in fresh) escalations.Add(new PendingEscalation { Job = j, NextAt = now, Deadline = now.AddMinutes(plant.Msg.EscalateRetryMinutes) });
            due = escalations.Where(p => !p.Running && p.NextAt <= now).ToList();
            foreach (var p in due) p.Running = true;
        }
        foreach (var p in due) _ = Task.Run(async () =>
        {
            var code = 20; var why = "";
            try
            {
                var body = new JsonObject { ["item"] = p.Job.Item, ["event"] = "question-escalated", ["key"] = "q|" + p.Job.Id,
                    ["fields"] = new JsonObject { ["worker"] = p.Job.Worker, ["minutes"] = p.Job.Minutes, ["supervisor"] = p.Job.Supervisor } };
                (code, var output) = await runner.RunAsync("journal", body.ToJsonString(), TimeSpan.FromSeconds(60));
                var j = JsonNode.Parse(output);
                why = j?["code"]?.ToString() ?? "";
                if (code == 0 && j?["posted"]?.GetValue<bool>() != true) { code = 1; why = "not-posted"; }
            }
            catch (Exception e) { Console.Error.WriteLine($"journal question-escalated for #{p.Job.Item} failed: {e.Message}"); }
            lock (escalations)
            {
                p.Running = false;
                if (code == 0) { escalations.Remove(p); return; }
                if (code == 20 && DateTime.UtcNow < p.Deadline) { p.NextAt = DateTime.UtcNow.AddSeconds(plant.Msg.EscalateRetrySeconds); return; }
                escalations.Remove(p);
            }
            if (why == "") why = "exit-" + code;
            Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["escalation"] = "journal-failed", ["id"] = p.Job.Id, ["item"] = p.Job.Item, ["code"] = why }.ToJsonString());
            plant.QuestionJournalFailed(p.Job.Id, why);
        });
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (MayAct?.Invoke() == false) { try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { } continue; }
            try { plant.Tick(); } catch (Exception e) { Console.Error.WriteLine($"plant tick: {e.Message}"); }
            try { plant.LeaseTick(); } catch (Exception e) { Console.Error.WriteLine($"lease tick: {e.Message}"); }
            try { plant.ManagerTick(); } catch (Exception e) { Console.Error.WriteLine($"manager tick: {e.Message}"); }
            try { await qc.Tick(); } catch (Exception e) { Console.Error.WriteLine($"qc tick: {e.Message}"); }
            // expiry of undelivered messages and open questions, and the pruning of the limit windows (only while a feature is on)
            if (plant.Msg.On) { try { Escalate(messenger.Tick()); } catch (Exception e) { Console.Error.WriteLine($"messenger tick: {e.Message}"); } }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { }
        }
    }
}
