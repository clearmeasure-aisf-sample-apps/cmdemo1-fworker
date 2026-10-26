using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The endpoint filters of task C7a (gateway/Http/Filters/): each guard's refusal, written once in GatewayGuards, its order, and that a filter
/// calls the endpoint only when it lets the request through. Pure: no gateway runs. EndpointPinTests shows the same guards on the mapped routes,
/// over HTTP and in process.
/// </summary>
public sealed class EndpointFilterTests
{
    static GatewayOptions Options(bool ownerMode) => new() { ScriptsDir = "s", Entry = "s/factory.ps1", ApiToken = "t", WebhookSecret = "w", OwnerMode = ownerMode };

    static MessagingSettings Messaging(bool enabled = true, bool manager = true, bool op = true) => new() { Enabled = enabled, ManagerEnabled = manager, OperatorEnabled = op };

    sealed class Caller(Principal who, Principal? admitted = null) : ICallerContext
    {
        public Principal Who => who;
        public bool OperatorActive => admitted != null;
        public Principal Admitted => admitted ?? who;
        public string By => "operator";
        public JsonObject? SessionLinkOf(Principal p) => null;
    }

    // runs one filter around an endpoint that answers "endpoint"; returns the answer and whether the endpoint ran
    static async Task<(int Status, string Body, bool Ran)> Run(IEndpointFilter filter)
    {
        var ran = false;
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        var result = await filter.InvokeAsync(EndpointFilterInvocationContext.Create(ctx), _ => { ran = true; return ValueTask.FromResult<object?>(Results.Text("endpoint")); });
        await ((IResult)result!).ExecuteAsync(ctx);
        return (ctx.Response.StatusCode, System.Text.Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray()), ran);
    }

    static void Refused((int Status, string Body, bool Ran) a, int status, string? code, string? reason = null, int? exit = 30)
    {
        Assert.False(a.Ran, "the endpoint ran behind a refusing filter");
        Assert.Equal(status, a.Status);
        if (code == null) { Assert.Equal("", a.Body); return; }
        var j = JsonNode.Parse(a.Body)!.AsObject();
        Assert.Equal(new[] { "ok", "code", "reason", "exit" }.Concat(j.ContainsKey("retryAfterSeconds") ? new[] { "retryAfterSeconds" } : Array.Empty<string>()).Order(), j.Select(kv => kv.Key).Order());
        Assert.False((bool)j["ok"]!);
        Assert.Equal(code, (string?)j["code"]);
        if (reason != null) Assert.Equal(reason, (string?)j["reason"]);
        Assert.Equal(exit, (int?)j["exit"]);
    }

    static void Passed((int Status, string Body, bool Ran) a) { Assert.True(a.Ran); Assert.Equal(200, a.Status); Assert.Equal("endpoint", a.Body); }

    const string NotOwner = "the gateway is not running in owner mode";
    const string MsgOff = "peer messaging is not enabled on this gateway (messaging.enabled)";
    const string MgrOff = "the supervisor is not enabled on this gateway (manager.enabled)";
    const string OpOff = "the operator session is not enabled on this gateway (operator.enabled)";

    [Fact]
    public async Task OwnerMode_RefusesOutsideOwnerMode()
    {
        Refused(await Run(new OwnerModeFilter(Options(false))), 403, "not-owner-mode", NotOwner);
        Passed(await Run(new OwnerModeFilter(Options(true))));
    }

    [Fact]
    public async Task MessagingOn_OwnerModeFirst_ThenTheRunTimeSwitch()
    {
        Refused(await Run(new MessagingOnFilter(Options(false), Messaging(enabled: false))), 403, "not-owner-mode", NotOwner);
        var m = Messaging(enabled: false);
        var f = new MessagingOnFilter(Options(true), m);
        Refused(await Run(f), 403, "messaging-disabled", MsgOff);
        m.Enabled = true;   // read at the request, not when the filter was made
        Passed(await Run(f));
    }

    [Fact]
    public async Task SupervisorOn_MessagingFirst_ThenManagerEnabled()
    {
        Refused(await Run(new SupervisorOnFilter(Options(false), Messaging(manager: false))), 403, "not-owner-mode", NotOwner);
        Refused(await Run(new SupervisorOnFilter(Options(true), Messaging(enabled: false, manager: false))), 403, "messaging-disabled", MsgOff);
        Refused(await Run(new SupervisorOnFilter(Options(true), Messaging(manager: false))), 403, "manager-disabled", MgrOff);
        Passed(await Run(new SupervisorOnFilter(Options(true), Messaging())));
    }

    [Fact]
    public async Task OperatorOn_OperatorEnabledFirst_ThenMessaging()
    {
        Refused(await Run(new OperatorOnFilter(Options(false), Messaging(enabled: false, op: false))), 403, "operator-disabled", OpOff);
        Refused(await Run(new OperatorOnFilter(Options(false), Messaging(enabled: false))), 403, "not-owner-mode", NotOwner);
        Refused(await Run(new OperatorOnFilter(Options(true), Messaging(enabled: false))), 403, "messaging-disabled", MsgOff);
        Passed(await Run(new OperatorOnFilter(Options(true), Messaging())));
    }

    [Fact]
    public void GatewayGuards_PassWhenEverySwitchIsOn()
    {
        Assert.Null(GatewayGuards.NotOwnerMode(true));
        Assert.Null(GatewayGuards.MessagingOff(true, Messaging()));
        Assert.Null(GatewayGuards.SupervisorOff(true, Messaging()));
        Assert.Null(GatewayGuards.OperatorOff(true, Messaging()));
        // the messaging guard does not look at the supervisor or the operator
        Assert.Null(GatewayGuards.MessagingOff(true, Messaging(manager: false, op: false)));
    }

    [Fact]
    public async Task Admin_WhoOrAdmitted_BareOrWithReason()
    {
        var worker = new Caller(new Principal("worker", "alice"));
        var opSession = new Caller(new Principal("operator", "operator-session"), new Principal("admin", "operator-session"));
        var admin = new Caller(new Principal("admin"));
        // the bare 403 of the read endpoints: no body
        Refused(await Run(new AdminFilter(CallerView.Who, null, _ => worker)), 403, null);
        Refused(await Run(new AdminFilter(CallerView.Admitted, null, _ => worker)), 403, null);
        // Who: the operator session is "operator" and refused; Admitted: it acts as the admin
        Refused(await Run(new AdminFilter(CallerView.Who, null, _ => opSession)), 403, null);
        Passed(await Run(new AdminFilter(CallerView.Admitted, null, _ => opSession)));
        Passed(await Run(new AdminFilter(CallerView.Who, null, _ => admin)));
        // with a reason: 403 forbidden, exit 30
        Refused(await Run(new AdminFilter(CallerView.Admitted, "only the operator", _ => worker)), 403, "forbidden", "only the operator", 30);
    }

    [Fact]
    public async Task SupervisorBudget_OnlyTheSupervisorSpendsSteps_RefusedWhenSpent()
    {
        var steps = 0;
        (bool, int) Spent() { steps++; return (false, 42); }
        (bool, int) Left() { steps++; return (true, 0); }
        var supervisor = new Caller(new Principal("manager", "supervisor"));
        var a = await Run(new SupervisorBudgetFilter(true, Spent, _ => supervisor));
        Refused(a, 429, "step-budget", "the supervisor's step budget of this hour is spent; keep waiting for events and do not work around it", 20);
        Assert.Equal(42, (int?)JsonNode.Parse(a.Body)!["retryAfterSeconds"]);
        Assert.Equal(1, steps);
        Passed(await Run(new SupervisorBudgetFilter(true, Left, _ => supervisor)));
        Assert.Equal(2, steps);
        // any other caller, and any caller outside owner mode, passes without spending a step
        Passed(await Run(new SupervisorBudgetFilter(true, Spent, _ => new Caller(new Principal("admin")))));
        Passed(await Run(new SupervisorBudgetFilter(true, Spent, _ => new Caller(new Principal("keeper", "backlog-keeper")))));
        Passed(await Run(new SupervisorBudgetFilter(false, Spent, _ => supervisor)));
        Assert.Equal(2, steps);
    }
}
