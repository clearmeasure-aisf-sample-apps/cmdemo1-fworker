using Fworker.Contracts.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ErrorHandling;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10g: the retry policy of the derivation of ItemMerged. A GitHub that stays away is retried every hour for ever where the engine acts (never the dead letters), and, as before, ends in the dead letters after the hour in the
/// shadow run. The policy is read from the handler chain the Wolverine host builds (no database: the host runs in memory): the rule that matches a GitHub failure, its slots (the attempts) and its open-ended source. The switch
/// is a static, so these tests do not run beside one another.
/// </summary>
[Collection("itemmerged-policy")]
public sealed class ItemMergedRetryPolicyTests
{
    static FailureRule? RuleFor(Exception failure, bool indefinitely)
    {
        var before = ItemMergedDerivationHandler.RetryIndefinitely;
        ItemMergedDerivationHandler.RetryIndefinitely = indefinitely;
        try
        {
            using var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().UseWolverine(o =>
            {
                o.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ItemMergedDerivationHandler));
                o.Durability.Mode = DurabilityMode.MediatorOnly;
            }).Build();
            host.StartAsync().GetAwaiter().GetResult();
            var graph = host.Services.GetRequiredService<Wolverine.Runtime.Handlers.HandlerGraph>();
            var chain = graph.Chains.SingleOrDefault(c => c.Handlers.Any(h => h.HandlerType == typeof(ItemMergedDerivationHandler))) ?? throw new InvalidOperationException("chains: " + string.Join("; ", graph.Chains.Select(c => c.MessageType + " <- " + string.Join(",", c.Handlers.Select(h => h.HandlerType)))));
            // (Wolverine applies a handler's Configure while it compiles the chain, which a host that has handled nothing has not done: the method is called here, on the chain it would be called on)
            var untouched = chain.Failures.Count();
            ItemMergedDerivationHandler.Configure(chain);
            if (untouched != 0) throw new InvalidOperationException("the chain already carries rules: the test would count them twice");
            return chain.Failures.FirstOrDefault(r => r.Match.Matches(failure));
        }
        finally { ItemMergedDerivationHandler.RetryIndefinitely = before; }
    }

    static string Describe(FailureRule? rule) => rule?.ToString() ?? "";

    [Fact]
    public void WhereTheEngineActs_AGitHubThatStaysAway_IsRetriedEveryHour_ForEver_AndNeverMovedToTheDeadLetters()
    {
        var text = Describe(RuleFor(new HttpRequestException("down"), indefinitely: true));
        Assert.Contains("attempt 6: Schedule Retry in 3600 seconds; then repeat: Schedule Retry in 3600 seconds", text);
        Assert.DoesNotContain("error queue", text);
        Assert.Contains("attempt 1: Retry inline with a delay of 00:00:05", text);   // the quick attempts are as before
    }

    [Fact]
    public void InTheShadowRun_TheHourIsTheLastRetry_ThenTheDeadLetters_AsBefore()
    {
        var text = Describe(RuleFor(new HttpRequestException("down"), indefinitely: false));
        Assert.Contains("attempt 6: Schedule Retry in 3600 seconds; attempt 7: Move to error queue", text);
        Assert.DoesNotContain("repeat", text);
    }

    [Fact]
    public void AFailureThatIsNotATransientOne_HasNoRuleOfItsOwn_SoItIsNotRetriedForEver()
    {
        Assert.Null(RuleFor(new InvalidOperationException("an app that is not installed"), indefinitely: true));
    }
}
