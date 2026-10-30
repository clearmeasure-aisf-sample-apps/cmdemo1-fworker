using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;

namespace Fworker.Workflows.ReadModels;

/// <summary>How many calls of an item a gate refused (<c>refusals.json</c> of the gateway's state directory, written by the server wrapper on every exit 10 of a gate verb). Missing or unreadable is none.</summary>
public interface IRefusalCounts
{
    int For(int item);
}

/// <summary>
/// W9.5: <c>model-tier</c> (Models.ps1, the server half of the model choice): WHEN to escalate. The item's history (relaunches, review findings, demotions, fix-forward rounds, gate refusals) becomes a tier number, never lower than before in the
/// same column. A rule, so a looping model cannot argue its way out of it. The tier is recorded in the item's state (model_tier) when it changed; the call runs under the item's lock, as the script's does.
/// </summary>
public sealed class ModelTierReadModel(ItemReads reads, IItemStateStore states, IItemLock locks, IRefusalCounts refusals)
{
    /// <summary><c>Get-TierFor</c>: the number of thresholds the value has reached (thresholds[i] is the value at which a signal asks for tier i+1).</summary>
    public static int TierFor(double value, IReadOnlyList<double> thresholds)
    {
        var t = 0;
        for (var i = 0; i < thresholds.Count; i++) if (value >= thresholds[i]) t = i + 1;
        return t;
    }

    sealed record Signal(string Name, int Value, int Tier);

    /// <summary><c>Verb_model_tier</c>.</summary>
    public async Task<JsonObject> ModelTier(VerbArgs a, CancellationToken ct)
    {
        var n = a.ItemNumber();
        return await locks.Use(n, () => Run(a, n, ct), ct);
    }

    async Task<JsonObject> Run(VerbArgs a, int n, CancellationToken ct)
    {
        var settings = reads.Settings; var e = settings.Escalation;
        var info = await reads.Info(n, ct);
        JsonObject? result = null;
        await states.Update(n, s =>
        {
            var col = a.Has("column") && a.Text("column").Length > 0 ? a.Text("column") : info.Column ?? "";
            var sig = new List<Signal>();
            var launches = Ps.At(s, "claim", "launches") is { } l ? Ps.ToInt(l) : 0;
            sig.Add(new("launches", launches, TierFor(launches, e.Launches)));
            var rounds = 0;
            if (Ps.At(s, "reviews") is JsonObject reviews && Ps.Prop(reviews, col) is { } rev && Ps.Str(rev, "last").Equals("FINDINGS", StringComparison.OrdinalIgnoreCase)) rounds = Ps.ToInt(Ps.Prop(rev, "round"));
            sig.Add(new("findingsRounds", rounds, TierFor(rounds, e.FindingsRounds)));
            var dem = Ps.Prop(s, "demotions") is { } d ? Ps.ToInt(d) : 0;
            sig.Add(new("demotions", dem, TierFor(dem, e.Demotions)));
            var ff = Ps.Prop(s, "fix_forward_rounds") is { } f ? Ps.ToInt(f) : 0;
            sig.Add(new("fixForwardRounds", ff, TierFor(ff, e.FixForwardRounds)));
            var rf = refusals.For(n);
            sig.Add(new("refusals", rf, TierFor(rf, e.Refusals)));
            var floor = e.MinTierByColumn.TryGetValue(col, out var fl) ? fl : 0;
            var tier = floor; foreach (var x in sig) if (x.Tier > tier) tier = x.Tier;
            var prev = Ps.Prop(s, "model_tier") is { } mt && Ps.Ieq(Ps.Str(mt, "column"), col) ? Ps.ToInt(Ps.Prop(mt, "tier")) : 0;
            if (prev > tier) tier = prev;
            var save = false;
            if (Ps.Prop(s, "model_tier") is not { } cur || !Ps.Ieq(Ps.Str(cur, "column"), col) || Ps.ToInt(Ps.Prop(cur, "tier")) != tier)
            {
                s["model_tier"] = new JsonObject { ["column"] = col, ["tier"] = tier, ["at"] = Ps.ToIso(reads.Gh.Now()), ["signals"] = ChecksFacts.Strings(sig.Where(x => x.Tier > 0).Select(x => x.Name)) };
                save = true;
            }
            var why = sig.Where(x => x.Tier >= 1 && x.Tier == tier).Select(x => $"{x.Name}={x.Value}");
            var signals = new JsonObject();
            foreach (var x in sig) signals[x.Name] = new JsonObject { ["value"] = x.Value, ["tier"] = x.Tier };
            var profile = settings.TestLabel.Length > 0 && info.HasLabel(settings.TestLabel) ? "testing" : null;
            result = new JsonObject { ["ok"] = true, ["id"] = n, ["column"] = col, ["tier"] = tier, ["reasons"] = ChecksFacts.Strings(why), ["signals"] = signals, ["profile"] = profile };
            return Task.FromResult(save);
        }, ct, 3);
        return result!;
    }
}
