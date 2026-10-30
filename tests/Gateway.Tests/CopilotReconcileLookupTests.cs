using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Integrations.Commands;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W9.10c (#479): what the Copilot reservation reconciler asks GitHub, against a fake of the tasks API and the pull request's comments. The real API's fields are the operator's to confirm on a live run;
/// these pin the decisions made on the answers: a miss releases only when nothing unreadable could be the task, a resume is found by its comment, and the owner record is written for what was found.
/// </summary>
public sealed class CopilotReconcileLookupTests
{
    sealed class Api : HttpMessageHandler
    {
        public readonly Dictionary<string, (HttpStatusCode, string)> Routes = new();
        public readonly List<string> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.RequestUri!.AbsolutePath;
            Requests.Add(request.Method + " " + key + request.RequestUri.Query);
            var (code, body) = Routes.TryGetValue(key, out var r) ? r : (HttpStatusCode.NotFound, "{}");
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    sealed class Prompt : ICopilotPromptBuilder { public string Build(int item, string phase, string code, ItemPr? pr, string? why) => "prompt"; }

    static readonly DateTime Since = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    const string Tasks = "/agents/repos/o/r/tasks";

    static (CopilotCloudRuntime, Api) Runtime(Func<int, CopilotOwner?>? owner = null, List<CopilotOwner>? saved = null)
    {
        var api = new Api();
        var rt = new CopilotCloudRuntime(new CopilotCloudSettings { ApiUrl = "https://api.test", Repository = "o/r", Token = "t" }, new CopilotCodes(), new Prompt(), new HttpClient(api));
        if (owner != null) rt.LoadOwner = (item, _) => Task.FromResult(owner(item));
        if (saved != null) rt.SaveOwner = o => { saved.Add(o); return Task.CompletedTask; };
        return (rt, api);
    }

    static string List(params string[] tasks) => $$"""{"tasks":[{{string.Join(",", tasks)}}]}""";
    static string TaskJson(string id, DateTime at, string name = "", string prompt = "") =>
        new JsonObject { ["id"] = id, ["created_at"] = at.ToString("o"), ["name"] = name, ["prompt"] = prompt, ["html_url"] = "https://github.com/tasks/" + id }.ToJsonString();

    // ---- the task of a launch ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TaskForItem_FindsTheTaskThatNamesTheItem_AndNotAnotherItemsOrAnOlderOne()
    {
        var (rt, api) = Runtime();
        api.Routes[Tasks] = (HttpStatusCode.OK, List(TaskJson("old", Since.AddHours(-1), "work on #12"), TaskJson("other", Since.AddMinutes(1), "work on #120"), TaskJson("mine", Since.AddMinutes(2), "work on #12")));
        var found = await rt.TaskForItem(12, Since, default);
        Assert.Equal("mine", found!.Value.TaskId);
    }

    [Fact]
    public async Task TaskForItem_NothingCreatedSinceTheReservation_IsAMiss_SoTheReservationMayBeReleased()
    {
        var (rt, api) = Runtime();
        api.Routes[Tasks] = (HttpStatusCode.OK, List(TaskJson("old", Since.AddHours(-1), "work on #12")));
        Assert.Null(await rt.TaskForItem(12, Since, default));
    }

    [Fact]
    public async Task TaskForItem_ATaskSinceTheReservationWithNoTextToMatch_IsNotAMiss()
    {
        var (rt, api) = Runtime();
        api.Routes[Tasks] = (HttpStatusCode.OK, List(TaskJson("blank", Since.AddMinutes(1))));   // the list carries no name, title or prompt: it could be the item's task
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.TaskForItem(12, Since, default));
    }

    [Fact]
    public async Task TaskForItem_AFullPageSinceTheReservation_IsNotAMiss()
    {
        var (rt, api) = Runtime();
        api.Routes[Tasks] = (HttpStatusCode.OK, List(Enumerable.Range(0, 100).Select(i => TaskJson("t" + i, Since.AddMinutes(1 + i), "work on #" + (1000 + i))).ToArray()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.TaskForItem(12, Since, default));
    }

    [Fact]
    public async Task TaskForItem_GitHubDown_Throws()
    {
        var (rt, api) = Runtime();
        api.Routes[Tasks] = (HttpStatusCode.BadGateway, "{}");
        await Assert.ThrowsAsync<HttpRequestException>(() => rt.TaskForItem(12, Since, default));
    }

    // ---- the resume of a follow-up session --------------------------------------------------------------------------------------------------

    static CopilotOwner Owner(int pr = 40, int sessions = 2) => new() { Item = 12, TaskId = "task-1", Url = "https://github.com/tasks/task-1", Pr = pr, Branch = "copilot/x", Sessions = sessions };

    [Fact]
    public async Task ResumeForItem_FindsTheCopilotCommentOnTheOwnersPullRequest_AndTheSessionItStarted()
    {
        var (rt, api) = Runtime(_ => Owner());
        api.Routes["/repos/o/r/issues/40/comments"] = (HttpStatusCode.OK, $$"""[{"created_at":"{{Since.AddHours(-2):o}}","body":"@copilot earlier"},{"created_at":"{{Since.AddSeconds(30):o}}","body":"@copilot do the next step"}]""");
        api.Routes[Tasks + "/task-1"] = (HttpStatusCode.OK, """{"id":"task-1","sessions":[{},{},{}]}""");
        var found = await rt.ResumeForItem(12, Since, default);
        Assert.Equal(("task-1#2", "https://github.com/tasks/task-1"), found!.Value);   // three sessions: the newest is the one the comment started
        Assert.Contains(api.Requests, r => r.StartsWith("GET /repos/o/r/issues/40/comments?since=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResumeForItem_NoCommentSinceTheReservation_IsAMiss_AndAPersonsCommentIsNotTheResume()
    {
        var (rt, api) = Runtime(_ => Owner());
        api.Routes["/repos/o/r/issues/40/comments"] = (HttpStatusCode.OK, $$"""[{"created_at":"{{Since.AddHours(-2):o}}","body":"@copilot earlier"},{"created_at":"{{Since.AddSeconds(30):o}}","body":"looks good to me"}]""");
        Assert.Null(await rt.ResumeForItem(12, Since, default));
    }

    [Fact]
    public async Task ResumeForItem_WithoutAnOwnerOrAPullRequest_CannotLook_SoItThrows()
    {
        var (none, _) = Runtime(_ => null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.ResumeForItem(12, Since, default));
        var (noPr, _) = Runtime(_ => new CopilotOwner { Item = 12, TaskId = "task-1" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => noPr.ResumeForItem(12, Since, default));
    }

    // ---- the owner record of what was found ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RecordReconciledOwner_ALaunchFound_BecomesTheOwner_WithItsBranchAndPullRequest_AndTheOldTaskIsKeptAsPrevious()
    {
        var saved = new List<CopilotOwner>();
        var (rt, api) = Runtime(_ => new CopilotOwner { Item = 12, TaskId = "task-0", Pr = 0 }, saved);
        api.Routes[Tasks + "/task-2"] = (HttpStatusCode.OK, """{"id":"task-2","artifacts":[{"type":"branch","data":{"head_ref":"copilot/y"}}]}""");
        api.Routes["/repos/o/r/pulls"] = (HttpStatusCode.OK, """[{"number":41,"head":{"ref":"copilot/y"}}]""");
        await rt.RecordReconciledOwner(12, "Development", CopilotReservation.LaunchMode, "task-2", "https://github.com/tasks/task-2", default);
        var o = Assert.Single(saved);
        Assert.Equal(("task-2", "https://github.com/tasks/task-2", "Development", 1), (o.TaskId, o.Url, o.Phase, o.Sessions));
        Assert.Equal(["task-0"], o.Previous);
        Assert.Same(o, rt.OwnerOf(12));   // the process's copy too: the next start resumes this task
    }

    [Fact]
    public async Task RecordReconciledOwner_AResumeFound_CountsTheSessionOnTheExistingOwner_AndNeedsOne()
    {
        var saved = new List<CopilotOwner>();
        var (rt, _) = Runtime(_ => Owner(sessions: 2), saved);
        await rt.RecordReconciledOwner(12, "Functional Testing", CopilotReservation.ResumeMode, "task-1#4", "u", default);
        var o = Assert.Single(saved);
        Assert.Equal((5, "Functional Testing", "task-1"), (o.Sessions, o.Phase, o.TaskId));
        var (bare, _) = Runtime(_ => null, saved);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bare.RecordReconciledOwner(12, "Development", CopilotReservation.ResumeMode, "task-1#4", "u", default));
    }

    [Fact]
    public async Task RecordReconciledOwner_TheOwnerRecordCannotBeWritten_Throws_SoTheReservationStaysReserved()
    {
        var (rt, _) = Runtime(_ => Owner());
        rt.SaveOwner = _ => throw new InvalidOperationException("copilot-owner answered exit 1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.RecordReconciledOwner(12, "Development", CopilotReservation.ResumeMode, "task-1#2", "u", default));
    }
}
