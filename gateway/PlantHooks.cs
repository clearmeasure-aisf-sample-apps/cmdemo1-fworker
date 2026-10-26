using System.Globalization;
using System.Text.Json.Nodes;

// What the plant asks of the rule scripts and the event hub for an owner's lease (gateway/Lease.cs): the plant decides, the rule scripts change
// the claim. Given to the plant's constructor (gateway-composition.md 3 B), in owner mode only; without it the plant renews and takes back nothing.
interface IPlantHooks
{
    // renew the owner's claim (claim-item --renew); the new lease end, or null when the rule script answered none
    Task<DateTime?> RenewAsync(int item, string holder);
    // end the owner's claim (expire-claim); false when the rule script refused
    Task<bool> TakeBackAsync(int item, string holder, string reason);
    // after a take-back: a sweep for the item, so it is offered again
    void RequestSweep(int item);
}

// The rule-script hooks: the same verbs, inputs, time limits and log lines Program.cs ran as lambdas set after Build().
sealed class PlantRuleHooks(Runner runner, EventHub hub) : IPlantHooks
{
    public async Task<DateTime?> RenewAsync(int item, string holder)
    {
        var (rc, ro) = await runner.RunAsync("claim-item", new JsonObject { ["item"] = item, ["holder"] = holder, ["renew"] = true }.ToJsonString(), TimeSpan.FromSeconds(120));
        DateTime? until = null;
        if (rc == 0) try { if (DateTime.TryParse(JsonNode.Parse(ro)?["lease_until"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u)) until = u; } catch { }
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["lease"] = "renew", ["item"] = item, ["holder"] = holder, ["exit"] = rc }.ToJsonString());
        return until;
    }

    public async Task<bool> TakeBackAsync(int item, string holder, string reason)
    {
        var (rc, _) = await runner.RunAsync("expire-claim", new JsonObject { ["item"] = item, ["holder"] = holder, ["reason"] = reason }.ToJsonString(), TimeSpan.FromSeconds(120));
        Console.WriteLine(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["lease"] = "taken-back", ["item"] = item, ["holder"] = holder, ["reason"] = reason, ["exit"] = rc }.ToJsonString());
        // an answer of "not held by that owner" is no failure: the claim is gone or another owner holds it, so the plant lets go too
        return rc == 0;
    }

    public void RequestSweep(int item) => hub.RequestSweep(item);
}
