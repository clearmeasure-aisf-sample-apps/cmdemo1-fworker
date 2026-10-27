using System.Text.Json.Nodes;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// VerbPolicy (task C8): which credential may call which verb, with which fields; the verbs the gateway answers with use-endpoint; the keeper's body
/// checks; the manager's reason; the exit-to-status table. The expected values are the ones the rules had in Auth.cs and in the verb route before C8.
/// </summary>
public class VerbPolicyTests
{
    static JsonObject Body(string json) => (JsonObject)JsonNode.Parse(json)!;
    static Principal Item(string role, int item = 7, string runtime = "") => new("item", "olga", item, role, "a-1", Runtime: runtime);

    [Fact]
    public void Admin_PassesTheBodyUnchanged()
    {
        var b = Body("""{"item":7,"holder":"anyone"}""");
        var (body, refusal) = VerbPolicy.AuthorizeVerb(new Principal("admin"), "merge", b);
        Assert.Same(b, body); Assert.Null(refusal);
    }

    [Fact]
    public void RoleToken_CallsNoVerb()
    {
        var (body, refusal) = VerbPolicy.AuthorizeVerb(new Principal("role", "olga", Role: "worker"), "get-item", Body("{}"));
        Assert.Null(body); Assert.Equal("a role token cannot call 'get-item' directly: use the tools at /mcp", refusal);
    }

    [Fact]
    public void Operator_CallsNoVerb()
    {
        var (body, refusal) = VerbPolicy.AuthorizeVerb(new Principal("operator", "ops"), "get-item", Body("{}"));
        Assert.Null(body); Assert.Equal("an operator session credential cannot call 'get-item': it calls no verb", refusal);
    }

    [Fact]
    public void Manager_KeepsOnlyTheFieldsOfTheVerb_AndNamesItselfOnManagerAction()
    {
        var (body, refusal) = VerbPolicy.AuthorizeVerb(new Principal("manager", "pm"), "manager-action",
            Body("""{"item":3,"action":"unpark","policy":"p","reason":"r","worker":"w","level":"high","by":"someone else","merge":true}"""));
        Assert.Null(refusal);
        Assert.Equal("""{"item":3,"action":"unpark","policy":"p","reason":"r","worker":"w","level":"high","by":"pm"}""", body!.ToJsonString());
        var (read, _) = VerbPolicy.AuthorizeVerb(new Principal("manager", "pm"), "pr-checks", Body("""{"item":3,"pr":4,"sha":"abc","extra":1}"""));
        Assert.Equal("""{"item":3,"pr":4,"sha":"abc"}""", read!.ToJsonString());
        var (no, why) = VerbPolicy.AuthorizeVerb(new Principal("manager", "pm"), "merge", Body("{}"));
        Assert.Null(no); Assert.Equal("the manager role cannot call 'merge'", why);
    }

    [Fact]
    public void Keeper_KeepsOnlyTheFieldsOfTheVerb_AndNamesItselfOnTheWritingVerbs()
    {
        var k = new Principal("keeper", "kay");
        var (read, _) = VerbPolicy.AuthorizeVerb(k, "backlog", Body("""{"x":1,"by":"z"}"""));
        Assert.Equal("{}", read!.ToJsonString());
        var (write, _) = VerbPolicy.AuthorizeVerb(k, "set-priority", Body("""{"item":3,"level":"high","policy":"p","reason":"r","zzz":1}"""));
        Assert.Equal("""{"item":3,"level":"high","policy":"p","reason":"r","by":"kay"}""", write!.ToJsonString());
        var (no, why) = VerbPolicy.AuthorizeVerb(k, "merge", Body("{}"));
        Assert.Null(no); Assert.Equal("the keeper role cannot call 'merge'", why);
    }

    [Fact]
    public void Worker_MayOnlyAskMyItems_AndIsTheHolder()
    {
        var (body, refusal) = VerbPolicy.AuthorizeVerb(new Principal("worker", "olga"), "my-items", Body("""{"holder":"mallory"}"""));
        Assert.Null(refusal); Assert.Equal("olga", body!["holder"]!.ToString());
        var (no, why) = VerbPolicy.AuthorizeVerb(new Principal("worker", "olga"), "get-item", Body("{}"));
        Assert.Null(no); Assert.StartsWith("a worker credential cannot call 'get-item'", why);
    }

    [Fact]
    public void QcClick_MayCallTheQcClickVerbsOnly()
    {
        var click = new Principal("qc-click", "x");
        Assert.Null(VerbPolicy.AuthorizeVerb(click, "qc-view", Body("{}")).Refusal);
        Assert.Equal("a QC link cannot call 'merge'", VerbPolicy.AuthorizeVerb(click, "merge", Body("{}")).Refusal);
    }

    [Fact]
    public void ItemToken_IsForItsOwnItemOnly_AndTheIdentityFieldsAreTheCredentials()
    {
        var (body, refusal) = VerbPolicy.AuthorizeVerb(Item("owner"), "comment", Body("""{"item":7,"holder":"mallory","body":"hi"}"""));
        Assert.Null(refusal); Assert.Equal("olga", body!["holder"]!.ToString());
        Assert.Equal("this token is for item #7 only", VerbPolicy.AuthorizeVerb(Item("owner"), "comment", Body("""{"item":8}""")).Refusal);
        Assert.Equal("this token is for item #7 only", VerbPolicy.AuthorizeVerb(Item("owner"), "comment", Body("{}")).Refusal);
    }

    [Theory]
    [InlineData("owner", "merge", true)]
    [InlineData("owner", "approve-pr", false)]
    [InlineData("inspector", "approve-pr", true)]
    [InlineData("inspector", "merge", false)]
    [InlineData("qc", "qc-report", true)]
    [InlineData("qc", "comment", false)]
    [InlineData("nobody", "get-item", false)]
    public void ItemToken_RoleDecidesTheVerbSet(string role, string verb, bool allowed)
    {
        var (body, refusal) = VerbPolicy.AuthorizeVerb(Item(role), verb, Body("""{"item":7}"""));
        if (allowed) { Assert.Null(refusal); Assert.NotNull(body); }
        else { Assert.Null(body); Assert.Equal($"the {role} role cannot call '{verb}'", refusal); }
    }

    [Fact]
    public void Accept_SetsAssignmentKindAndRole_PerRole()
    {
        var (own, _) = VerbPolicy.AuthorizeVerb(Item("owner"), "accept", Body("""{"item":7}"""));
        Assert.Equal("""{"item":7,"holder":"olga","assignment":"a-1","kind":"own-item","role":"owner"}""", own!.ToJsonString());
        var (insp, _) = VerbPolicy.AuthorizeVerb(Item("inspector"), "accept", Body("""{"item":7}"""));
        Assert.Equal("bot-job", insp!["kind"]!.ToString());
        var (qc, _) = VerbPolicy.AuthorizeVerb(Item("qc"), "accept", Body("""{"item":7}"""));
        Assert.Equal("""{"item":7,"holder":"olga","assignment":"a-1","role":"qc","kind":"qc-env"}""", qc!.ToJsonString());
    }

    [Fact]
    public void GitCredential_IsForTheOwnerOfARuntimeWorkersItemOnly()
    {
        Assert.Equal("'git-credential' is only for the owner of an item that a runtime worker holds", VerbPolicy.AuthorizeVerb(Item("owner"), "git-credential", Body("""{"item":7}""")).Refusal);
        Assert.Equal("'git-credential' is only for the owner of an item that a runtime worker holds", VerbPolicy.AuthorizeVerb(Item("inspector", runtime: "cae-job"), "git-credential", Body("""{"item":7}""")).Refusal);
        Assert.Equal("this token is for item #7 only", VerbPolicy.AuthorizeVerb(Item("owner", runtime: "cae-job"), "git-credential", Body("""{"item":9}""")).Refusal);
        var (body, refusal) = VerbPolicy.AuthorizeVerb(Item("owner", runtime: "cae-job"), "git-credential", Body("""{"item":7,"holder":"mallory","x":1}"""));
        Assert.Null(refusal); Assert.Equal("""{"item":7,"holder":"olga","runtime":"cae-job"}""", body!.ToJsonString());
    }

    [Theory]
    [InlineData("revoke-worker", "use POST /api/v1/workers/{name}/revoke")]
    [InlineData("revocations", "use POST /api/v1/workers/{name}/revoke")]
    [InlineData("plant-snapshot-save", "the plant snapshot is written and read by the gateway itself, at its stop and its start")]
    [InlineData("leader-lease", "the plant snapshot is written and read by the gateway itself, at its stop and its start")]
    [InlineData("transcript-prune", "the plant snapshot is written and read by the gateway itself, at its stop and its start")]
    [InlineData("operator-release", "use POST /api/v1/operator/record-release")]
    [InlineData("operator-releases", "use POST /api/v1/operator/record-release")]
    [InlineData("ci-release", "the gateway releases the Copilot agent's held workflow runs by itself")]
    [InlineData("park-item", "the gateway parks an item runtime-failed by itself")]
    [InlineData("copilot-owner", "the gateway records a Copilot task's ownership of an item by itself")]
    [InlineData("copilot-settle", "the gateway settles a Copilot Development task by itself, at its launch and its end")]
    [InlineData("unpark-item", "use POST /api/v1/items/{n}/unpark (client: factory.ps1 unpark --item N --reason \"...\")")]
    [InlineData("resettle-item", "use POST /api/v1/items/{n}/resettle")]
    [InlineData("publish-demo", "send the MP4 as the body of POST /api/v1/demo/{item} (client: factory.ps1 publish-demo --item N --file F)")]
    public void UseEndpointReason_IsTheTextTheRouteAnswered(string verb, string reason) => Assert.Equal(reason, VerbPolicy.UseEndpointReason(verb));

    [Theory]
    [InlineData("get-item")]
    [InlineData("merge")]
    [InlineData("manager-action")]
    [InlineData("")]
    public void UseEndpointReason_IsNullForARuleScriptVerb(string verb) => Assert.Null(VerbPolicy.UseEndpointReason(verb));

    [Theory]
    [InlineData("""{"follow_up":true,"unpark":false,"remove":true,"chain":false}""", null)]
    [InlineData("""{"follow_up":"yes"}""", "follow_up is true or false")]
    [InlineData("""{"unpark":1}""", "unpark is true or false")]
    [InlineData("""{"remove":null,"chain":"x"}""", "chain is true or false")]
    [InlineData("""{"children":"a"}""", "children is a JSON array")]
    [InlineData("""{"children":[1,2]}""", null)]
    [InlineData("""{"follow_up":"x","children":"a"}""", "follow_up is true or false")]
    public void KeeperBodyError_NamesTheFirstWrongField(string json, string? error) => Assert.Equal(error, VerbPolicy.KeeperBodyError(Body(json)));

    [Theory]
    [InlineData("create-item", """{}""", 1)]
    [InlineData("split-item", """{"children":[1,2,3]}""", 3)]
    [InlineData("split-item", """{}""", 2)]
    [InlineData("amend-criteria", """{"follow_up":true}""", 1)]
    [InlineData("amend-criteria", """{"follow_up":false}""", 0)]
    [InlineData("amend-criteria", """{}""", 0)]
    [InlineData("set-priority", """{"item":1}""", 0)]
    public void KeeperItemsNeeded_CountsTheNewItemsOfTheVerb(string verb, string json, int need) => Assert.Equal(need, VerbPolicy.KeeperItemsNeeded(verb, Body(json)));

    [Theory]
    [InlineData("  see https://x.example/a  and  <b>@bob</b> `code` [l](u)!  ", "see https x.example/a and bbob/b code l(u)")]
    [InlineData("two\nlines\there", "two lines here")]
    [InlineData("", "")]
    public void CleanManagerReason_IsOneLineWithoutMarkersOrMentions(string reason, string clean) => Assert.Equal(clean, VerbPolicy.CleanManagerReason(JsonValue.Create(reason)));

    [Fact]
    public void CleanManagerReason_IsEmptyForNoTextAtAll()
    {
        Assert.Equal("", VerbPolicy.CleanManagerReason(null));
        Assert.Equal("", VerbPolicy.CleanManagerReason(JsonValue.Create(5)));
    }

    [Theory]
    [InlineData(0, 200)] [InlineData(21, 202)] [InlineData(2, 400)] [InlineData(12, 404)] [InlineData(10, 409)] [InlineData(11, 409)] [InlineData(40, 409)]
    [InlineData(20, 503)] [InlineData(1, 500)] [InlineData(30, 500)] [InlineData(139, 500)]
    public void StatusOfExit_IsTheRoutesTable(int exit, int status) => Assert.Equal(status, VerbPolicy.StatusOfExit(exit));

    [Fact]
    public void TheVerbSets_AreTheOnesAuthHad()
    {
        Assert.Equal(18, VerbPolicy.OwnerVerbs.Count); Assert.Contains("publish-demo", VerbPolicy.OwnerVerbs); Assert.DoesNotContain("approve-pr", VerbPolicy.OwnerVerbs);
        Assert.Equal(6, VerbPolicy.InspectorVerbs.Count); Assert.Contains("approve-pr", VerbPolicy.InspectorVerbs);
        Assert.Equal(new[] { "my-items" }, VerbPolicy.WorkerVerbs);
        Assert.Equal(new[] { "git-credential" }, VerbPolicy.GitCredentialVerbs);
        Assert.Equal(4, VerbPolicy.ManagerVerbs.Count); Assert.Equal(8, VerbPolicy.KeeperVerbs.Count);
        Assert.Equal(7, VerbPolicy.ManagerItemActions.Count); Assert.Equal(3, VerbPolicy.ManagerNameActions.Count);
        Assert.Equal(8, VerbPolicy.QcVerbs.Count); Assert.Equal(5, VerbPolicy.QcClickVerbs.Count);
        // the verbs that decide about the work itself are never the supervisor's
        foreach (var v in new[] { "merge", "approve-pr", "validation-verdict", "clear-hold", "complete", "move-next", "claim-item", "accept" }) Assert.DoesNotContain(v, VerbPolicy.ManagerVerbs);
    }
}
