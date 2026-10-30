using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>
/// W6.3 (#330): the projections of the v2 engine (state comment, board column) against a fake of GitHub's issue-comment and Projects v2 GraphQL endpoints. The
/// state comment must read back with the logic of Get-State (plugin/scripts/lib/Board.ps1): the golden writes of tests/scenarios/golden are parsed and re-rendered, and the
/// body of a C# write is checked against the shape Get-State's regex reads.
/// </summary>
public sealed class ProjectionTests
{
    const string Login = "factory-bot";
    const string Name = "factory-worker";
    static readonly RepoId Repo = new("o", "r");
    static readonly ItemId Item = new(Repo, 7);
    const string Key = "o/r#7";

    sealed record Comment(long Id, string Login, string Body);

    /// <summary>GitHub for one repository: issue comments and the GraphQL calls of the board. Every request is listed in <see cref="Calls"/>.</summary>
    sealed class FakeGitHub : HttpMessageHandler
    {
        public readonly List<Comment> Comments = [];
        public readonly List<string> Calls = [];
        public readonly List<JsonObject> GraphQl = [];
        public Action? AfterListing;   // a write by another process lands here, between the read and the save
        public Action<string>? OnCommentWrite;   // the webhook for a comment write: called with the body, while the write's own answer is still on its way
        public bool FailMutation, FailCommentWrite;
        long nextId = 1000;
        // the board
        public string Column = "Technical Design";
        public bool OnBoard = true;
        public string[] Columns = ["Backlog", "Technical Design", "Functional Testing", "Done"];
        public int MetaReads;

        public Comment Add(string login, string body) { var c = new Comment(nextId++, login, body); Comments.Add(c); return c; }
        public void Replace(long id, string body) { var i = Comments.FindIndex(c => c.Id == id); Comments[i] = Comments[i] with { Body = body }; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath; var q = req.RequestUri.Query;
            var body = req.Content == null ? "" : req.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            Calls.Add($"{req.Method} {path}{(q.Contains("page=") ? q : "")}");
            if (path.EndsWith("/graphql")) return Task.FromResult(Graph(JsonNode.Parse(body)!.AsObject()));
            var m = Regex.Match(path, @"^/repos/o/r/issues/7/comments$");
            if (m.Success)
            {
                if (req.Method == HttpMethod.Get)
                {
                    var page = int.Parse(Regex.Match(q, @"[?&]page=(\d+)").Groups[1].Value);
                    var r = Ok(new JsonArray(Comments.Skip((page - 1) * 100).Take(100).Select(Json).ToArray()));
                    AfterListing?.Invoke();
                    return Task.FromResult(r);
                }
                if (FailCommentWrite) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
                OnCommentWrite?.Invoke(JsonNode.Parse(body)!["body"]!.ToString());
                return Task.FromResult(Ok(Json(Add(Login, JsonNode.Parse(body)!["body"]!.ToString())), HttpStatusCode.Created));
            }
            m = Regex.Match(path, @"^/repos/o/r/issues/comments/(\d+)$");
            if (m.Success)
            {
                var id = long.Parse(m.Groups[1].Value); var c = Comments.FirstOrDefault(x => x.Id == id);
                if (c == null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                if (req.Method == HttpMethod.Patch && FailCommentWrite) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
                if (req.Method == HttpMethod.Patch) { OnCommentWrite?.Invoke(JsonNode.Parse(body)!["body"]!.ToString()); Replace(id, JsonNode.Parse(body)!["body"]!.ToString()); c = Comments.First(x => x.Id == id); }
                return Task.FromResult(Ok(Json(c)));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        HttpResponseMessage Graph(JsonObject req)
        {
            GraphQl.Add(req);
            var query = req["query"]!.ToString(); var v = req["variables"]!.AsObject();
            if (query.StartsWith("query($o:String!,$n:Int!)"))
            {
                MetaReads++;
                return Ok(new JsonObject { ["data"] = new JsonObject { ["organization"] = new JsonObject { ["projectV2"] = new JsonObject
                {
                    ["id"] = "PVT_1",
                    ["fields"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject(), new JsonObject
                    {
                        ["id"] = "FIELD_status", ["name"] = "Status",
                        ["options"] = new JsonArray(Columns.Select(c => (JsonNode)new JsonObject { ["id"] = "opt_" + c.Replace(' ', '_'), ["name"] = c }).ToArray()),
                    }) },
                } } } });
            }
            if (query.StartsWith("query($o:String!,$r:String!"))
                return Ok(new JsonObject { ["data"] = new JsonObject { ["repository"] = new JsonObject { ["issue"] = new JsonObject
                {
                    ["id"] = "I_7",
                    ["projectItems"] = new JsonObject { ["nodes"] = new JsonArray(OnBoard ? [(JsonNode)new JsonObject
                    {
                        ["id"] = "card_7", ["project"] = new JsonObject { ["id"] = "PVT_1" }, ["fieldValueByName"] = new JsonObject { ["name"] = Column },
                    }] : [(JsonNode)new JsonObject { ["id"] = "card_other", ["project"] = new JsonObject { ["id"] = "PVT_other" } }]) },
                } } } });
            if (query.StartsWith("mutation(") && FailMutation) return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);
            if (query.StartsWith("mutation(")) { Column = Columns.First(c => "opt_" + c.Replace(' ', '_') == v["o"]!.ToString()); return Ok(new JsonObject { ["data"] = new JsonObject() }); }
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        static JsonObject Json(Comment c) => new() { ["id"] = c.Id, ["user"] = new JsonObject { ["login"] = c.Login }, ["body"] = c.Body };
        static HttpResponseMessage Ok(JsonNode n, HttpStatusCode s = HttpStatusCode.OK) => new(s) { Content = new StringContent(n.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    sealed class Rig
    {
        public readonly FakeGitHub Api = new();
        public readonly InMemoryProjectionVersions Versions = new();
        public readonly ProjectionEcho Echo;
        public readonly StateCommentProjection State;
        public readonly BoardColumnProjection Board;
        public readonly List<string> Log = [];
        public DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

        public Rig()
        {
            var http = new HttpClient(Api) { BaseAddress = new Uri("http://fake/") };
            var cred = new StaticCredentialSource([], new GitHubCredential("t", "token"));
            var client = new GitHubClientFactory(http, cred, new GitHubBudget()).For(Repo).GetAwaiter().GetResult();
            var store = new GitHubJournalStore(client, new JournalIdentity(Name, _ => Task.FromResult(Login), []), () => Now, Log.Add);
            Echo = new ProjectionEcho(Versions);
            State = new StateCommentProjection(store, Versions, Echo);
            Board = new BoardColumnProjection(http, cred, new BoardRef("o", 3), Versions, Echo, () => new DateTimeOffset(Now));
        }

        public Comment StateComment() => Api.Comments.First(c => c.Login == Login && c.Body.StartsWith(ItemStateComment.Marker(Name), StringComparison.Ordinal));
    }

    // ---- the state comment: the format v1's Get-State reads -----------------------------------------------------------------------------------

    /// <summary>Get-State's reading of a comment: the first json fence, parsed.</summary>
    static JsonObject GetStateReads(string body)
    {
        var m = Regex.Match(body, @"(?s)```json\s*(.*?)\s*```");
        Assert.True(m.Success);
        return JsonNode.Parse(m.Groups[1].Value)!.AsObject();
    }

    static string? GoldenDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "tests", "scenarios", "golden"))) return Path.Combine(d.FullName, "tests", "scenarios", "golden");
        return null;
    }

    [Fact]
    public void TheGoldenStateComments_OfV1_ParseWithTheLogicOfGetState_AndRenderBackWithTheSameState()
    {
        var dir = GoldenDir();
        if (dir == null) return;   // (a checkout without the golden writes: nothing to compare)
        var seen = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.writes.txt", SearchOption.AllDirectories).Take(400))
            foreach (var line in File.ReadLines(file))
            {
                var m = Regex.Match(line, @"^[+=] (?:rule|harness|gateway) (?:POST|PATCH) \S*issues/\S+ 20\d (\{.*\})$");
                if (!m.Success || !line.Contains("factory-state:")) continue;
                var body = JsonNode.Parse(m.Groups[1].Value)!["body"]!.ToString();
                if (!body.StartsWith("<!--", StringComparison.Ordinal)) continue;   // (a long body is recorded as its length and hash only)
                var name = Regex.Match(body, @"^<!-- factory-state:(\S+) v1 -->\n").Groups[1].Value;
                Assert.NotEqual("", name);
                Assert.StartsWith(ItemStateComment.Marker(name), body);
                var state = ItemStateComment.Parse(body);
                Assert.NotNull(state);
                Assert.Equal(GetStateReads(body).ToJsonString(), state!.ToJsonString());
                // v2 renders the same state in the shape v1 reads: the same marker, the fence, and the same state back
                var rendered = ItemStateComment.Body(name, 1, state);
                Assert.StartsWith(ItemStateComment.Marker(name), rendered);
                Assert.Equal(state.ToJsonString(), ItemStateComment.Parse(rendered)!.ToJsonString());
                Assert.Equal(state.ToJsonString(), GetStateReads(rendered).ToJsonString());
                seen++;
            }
        Assert.True(seen > 0, "no golden state comment was found");
    }

    [Fact]
    public async Task AStateWrittenByV2_HasTheBodyShapeOfSaveState_AndReadsBackAsTheSameState()
    {
        var r = new Rig();
        var outcome = await r.State.Apply(Item, 1, s =>
        {
            s["claim"] = new JsonObject { ["by"] = "w1", ["lease_until"] = "2026-10-28T10:00:00.000Z" };
            s["pr"] = new JsonObject { ["number"] = 12, ["branch"] = "bot/7-x" };
            s["notes"] = new JsonArray("a", "b");
        });
        Assert.Equal(ProjectionOutcome.Applied, outcome);
        var c = r.StateComment();
        Assert.Matches(new Regex(@"\A<!-- factory-state:factory-worker v1 -->\nstate of #7 for `factory-worker` \(machine-read; edits by others are ignored\)\n\n```json\n\{\n.*\n\}\n```\n\z", RegexOptions.Singleline), c.Body);
        var s = GetStateReads(c.Body);
        Assert.Equal(7, (int)s["item"]!);
        Assert.Equal(1, (int)s["rev"]!);
        Assert.Equal("2026-10-28T09:00:00.000Z", s["updated"]!.ToString());
        Assert.Equal("bot/7-x", s["pr"]!["branch"]!.ToString());
        Assert.Equal(1, (int)s["schema"]!);
        Assert.Equal(["POST /repos/o/r/issues/7/comments"], r.Api.Calls.Where(x => x.StartsWith("POST")).ToList());   // created: there was none
    }

    [Fact]
    public async Task TheFirstCommentWithTheMarkerOfTheGatewaysLogin_IsUpdated_ARevisionHigher_NeverASecondOne()
    {
        var r = new Rig();
        r.Api.Add("somebody", ItemStateComment.Marker(Name) + "\nimitation\n\n```json\n{\"rev\":50}\n```\n");   // another login's comment with the marker is nobody's state
        var own = r.Api.Add(Login, ItemStateComment.Body(Name, 7, new JsonObject { ["schema"] = 1, ["item"] = 7, ["rev"] = 4, ["keep"] = "me" }));
        await r.State.Apply(Item, 1, s => s["x"] = 1);
        await r.State.Apply(Item, 2, s => s["x"] = 2);
        Assert.Equal(2, r.Api.Comments.Count);
        var s = GetStateReads(r.Api.Comments.Single(c => c.Id == own.Id).Body);
        Assert.Equal((6, 2, "me"), ((int)s["rev"]!, (int)s["x"]!, s["keep"]!.ToString()));
        Assert.DoesNotContain(r.Api.Calls, c => c.StartsWith("POST"));
    }

    [Fact]
    public async Task ARevisionConflict_ReadsTheStateAgain_AppliesAgain_AndKeepsTheOtherWritersChange()
    {
        var r = new Rig();
        var own = r.Api.Add(Login, ItemStateComment.Body(Name, 7, new JsonObject { ["schema"] = 1, ["item"] = 7, ["rev"] = 1 }));
        var interferences = 0;
        r.Api.AfterListing = () =>
        {
            if (interferences++ > 0) return;
            // another process saves between our read and our save: revision 2 with its own key
            r.Api.Replace(own.Id, ItemStateComment.Body(Name, 7, new JsonObject { ["schema"] = 1, ["item"] = 7, ["rev"] = 2, ["other"] = "kept" }));
        };
        var mutations = 0;
        var outcome = await r.State.Apply(Item, 1, s => { mutations++; s["mine"] = true; });
        Assert.Equal(ProjectionOutcome.Applied, outcome);
        Assert.Equal(2, mutations);
        var s = GetStateReads(r.Api.Comments.Single().Body);
        Assert.Equal((3, "kept", true), ((int)s["rev"]!, s["other"]!.ToString(), (bool)s["mine"]!));
        Assert.Equal(2, r.Api.Calls.Count(c => c == "GET /repos/o/r/issues/7/comments?per_page=100&page=1"));   // read, conflict, read again
    }

    [Fact]
    public async Task AConflictThatKeepsComing_IsThrownAfterTheTries_AndTheVersionIsNotRecorded()
    {
        var r = new Rig();
        var own = r.Api.Add(Login, ItemStateComment.Body(Name, 7, new JsonObject { ["schema"] = 1, ["item"] = 7, ["rev"] = 1 }));
        var rev = 1;
        r.Api.AfterListing = () => r.Api.Replace(own.Id, ItemStateComment.Body(Name, 7, new JsonObject { ["schema"] = 1, ["item"] = 7, ["rev"] = ++rev }));
        await Assert.ThrowsAsync<StateConflictException>(() => r.State.Apply(Item, 1, s => s["x"] = 1));
        Assert.Equal(3, r.Api.Calls.Count(c => c.StartsWith("GET") && c.Contains("/comments?per_page=100&page=")));
        Assert.Equal(0, await r.Versions.Applied(Key, ProjectionTargets.Comment));
    }

    [Fact]
    public async Task AStateOverTheBudget_IsTrimmedOldestFirst_ToFitTheCommentOf48000Characters_AndTheTrimIsRecorded()
    {
        var r = new Rig();
        await r.State.Apply(Item, 1, s =>
        {
            var flow = new JsonArray(); var health = new JsonObject { ["tdd"] = new JsonArray() };
            for (var i = 0; i < 400; i++)
            {
                flow.Add(new JsonObject { ["at"] = $"2026-10-27T{i / 60:00}:{i % 60:00}:00.000Z", ["note"] = new string('f', 100) });
                ((JsonArray)health["tdd"]!).Add(new JsonObject { ["at"] = $"t{i:0000}", ["read"] = new string('h', 100) });
            }
            s["flow"] = flow; s["promote_health"] = health; s["verdict"] = new JsonObject { ["passed"] = true };
        });
        var body = r.StateComment().Body;
        Assert.True(body.Length <= ItemStateComment.Budget, $"{body.Length} characters");
        var s = GetStateReads(body);
        Assert.True(((JsonArray)s["flow"]!).Count is > 0 and < 400);
        Assert.Equal("t0399", ((JsonArray)s["promote_health"]!["tdd"]!).Last()!["at"]!.ToString());   // the newest stay
        Assert.NotEqual("t0000", ((JsonArray)s["promote_health"]!["tdd"]!).First()!["at"]!.ToString());   // the oldest went
        Assert.True((bool)s["verdict"]!["passed"]!);   // nothing outside the three collections is dropped
        var trim = (JsonObject)((JsonArray)s["journal"]!["trims"]!)[0]!;
        Assert.True((int)trim["before"]! > ItemStateComment.Budget);
        Assert.NotNull(trim["dropped"]!["flow"]);
        Assert.Contains(r.Log, l => l.Contains("over the 48000-character budget"));
    }

    [Fact]
    public async Task AStateThatDoesNotFitEvenTrimmed_IsRefused_AndNothingIsWritten()
    {
        var r = new Rig();
        await Assert.ThrowsAsync<JournalRefusedException>(() => r.State.Apply(Item, 1, s => s["big"] = new string('x', 50000)));
        Assert.Empty(r.Api.Comments);
        Assert.Equal(0, await r.Versions.Applied(Key, ProjectionTargets.Comment));
    }

    // ---- versions ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AVersionThatIsNotNewer_IsSkipped_BeforeAnythingIsRead_ForEveryTarget()
    {
        var r = new Rig();
        Assert.Equal(ProjectionOutcome.Applied, await r.State.Apply(Item, 5, s => s["x"] = 1));
        Assert.Equal(ProjectionOutcome.Applied, await r.Board.Move(Item, 5, "Functional Testing"));
        r.Api.Calls.Clear(); r.Api.GraphQl.Clear();
        foreach (var v in new long[] { 5, 4 })
        {
            Assert.Equal(ProjectionOutcome.Stale, await r.State.Apply(Item, v, s => s["x"] = 99));
            Assert.Equal(ProjectionOutcome.Stale, await r.Board.Move(Item, v, "Done"));
        }
        Assert.Empty(r.Api.Calls);
        Assert.Equal(1, (int)GetStateReads(r.StateComment().Body)["x"]!);
        Assert.Equal("Functional Testing", r.Api.Column);
        Assert.Equal(5, await r.Versions.Applied(Key, ProjectionTargets.Comment));
        Assert.Equal(ProjectionOutcome.Applied, await r.State.Apply(Item, 6, s => s["x"] = 2));
    }

    [Fact]
    public async Task TheVersions_AreKeptPerItemAndTarget_AndOnlyGrow()
    {
        var v = new InMemoryProjectionVersions();
        var t = DateTimeOffset.UnixEpoch;
        Assert.Equal(0, await v.Applied("o/r#1", "card"));
        Assert.True(await v.Advance("o/r#1", "card", 3, t));
        Assert.False(await v.Advance("o/r#1", "card", 3, t));
        Assert.False(await v.Advance("o/r#1", "card", 2, t));
        Assert.True(await v.Advance("o/r#1", "comment", 1, t));
        Assert.True(await v.Advance("o/r#2", "card", 1, t));
        Assert.Equal((3, 1, 1), (await v.Applied("o/r#1", "card"), await v.Applied("o/r#1", "comment"), await v.Applied("o/r#2", "card")));
    }

    // ---- the board column --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheColumnMove_SendsTheMutationOfMoveCard_WithTheIdsLookedUp()
    {
        var r = new Rig();
        Assert.Equal(ProjectionOutcome.Applied, await r.Board.Move(Item, 1, "Functional Testing"));
        var mutation = r.Api.GraphQl.Single(g => g["query"]!.ToString().StartsWith("mutation("));
        Assert.Equal(BoardColumnProjection.MoveMutation, mutation["query"]!.ToString());
        Assert.Equal("mutation($p:ID!,$i:ID!,$f:ID!,$o:String!){ updateProjectV2ItemFieldValue(input:{projectId:$p,itemId:$i,fieldId:$f,value:{singleSelectOptionId:$o}}){ projectV2Item{ id } } }", mutation["query"]!.ToString());
        var v = mutation["variables"]!;
        Assert.Equal(("PVT_1", "card_7", "FIELD_status", "opt_Functional_Testing"), (v["p"]!.ToString(), v["i"]!.ToString(), v["f"]!.ToString(), v["o"]!.ToString()));
        Assert.Equal("Functional Testing", r.Api.Column);
        var meta = r.Api.GraphQl.First();
        Assert.Equal(("o", 3), (meta["variables"]!["o"]!.ToString(), (int)meta["variables"]!["n"]!));
        var card = r.Api.GraphQl[1]["variables"]!;
        Assert.Equal(("o", "r", 7), (card["o"]!.ToString(), card["r"]!.ToString(), (int)card["n"]!));
    }

    [Fact]
    public async Task TheProjectMeta_IsCached_ForTenMinutes_AndAnUnknownColumnRefreshesItOnceBeforeItIsRefused()
    {
        var r = new Rig();
        await r.Board.Move(Item, 1, "Functional Testing");
        await r.Board.Move(Item, 2, "Done");
        Assert.Equal(1, r.Api.MetaReads);   // looked up once
        r.Now = r.Now.AddMinutes(11);
        await r.Board.Move(Item, 3, "Backlog");
        Assert.Equal(2, r.Api.MetaReads);   // the cache ran out
        var ex = await Assert.ThrowsAsync<ProjectionRefusedException>(() => r.Board.Move(Item, 4, "Nowhere"));
        Assert.Contains("no column 'Nowhere'", ex.Message);
        Assert.Equal(3, r.Api.MetaReads);   // refreshed once for the unknown column
        r.Api.Columns = [.. r.Api.Columns, "Shipping"];
        Assert.Equal(ProjectionOutcome.Applied, await r.Board.Move(Item, 4, "Shipping"));   // a column added since: found by the refresh
        Assert.Equal(4, r.Api.MetaReads);
        Assert.Equal(4, await r.Versions.Applied(Key, ProjectionTargets.Card));   // (the refused move did not record its version, so 4 was free for Shipping)
    }

    [Fact]
    public async Task ACardAlreadyInTheColumn_IsNotWritten_AnItemOffTheBoard_IsRefused()
    {
        var r = new Rig();
        Assert.Equal(ProjectionOutcome.Unchanged, await r.Board.Move(Item, 1, "technical design"));
        Assert.DoesNotContain(r.Api.GraphQl, g => g["query"]!.ToString().StartsWith("mutation("));
        Assert.Equal(1, await r.Versions.Applied(Key, ProjectionTargets.Card));
        r.Api.OnBoard = false;
        var ex = await Assert.ThrowsAsync<ProjectionRefusedException>(() => r.Board.Move(Item, 2, "Done"));
        Assert.Contains("not on the board", ex.Message);
        Assert.Equal(1, await r.Versions.Applied(Key, ProjectionTargets.Card));
    }

    // ---- echo suppression -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheEventOfTheProjectionsOwnStateWrite_IsAnEcho_AnyOtherStateIsNews()
    {
        var r = new Rig();
        await r.State.Apply(Item, 4, s => s["x"] = 1);
        var own = r.StateComment().Body;
        Assert.True(await r.Echo.IsStateCommentEcho(Key, own));
        // a v1 rule script or a person changed the comment after: a different revision is news
        var other = ItemStateComment.Body(Name, 7, new JsonObject { ["item"] = 7, ["rev"] = 2, ["x"] = 5 });
        Assert.False(await r.Echo.IsStateCommentEcho(Key, other));
        Assert.False(await r.Echo.IsStateCommentEcho("o/r#8", own));   // another item
        Assert.False(await r.Echo.IsStateCommentEcho(Key, "no fence at all"));
        // a newer projection version superseded the write: its own event is the only echo left
        Assert.True(await r.Versions.Advance(Key, ProjectionTargets.Comment, 9, DateTimeOffset.UtcNow));
        Assert.False(await r.Echo.IsStateCommentEcho(Key, own));
        await r.State.Apply(Item, 10, s => s["x"] = 2);
        Assert.False(await r.Echo.IsStateCommentEcho(Key, own));   // (revision 1 is not revision 2)
        Assert.True(await r.Echo.IsStateCommentEcho(Key, r.StateComment().Body));
    }

    [Fact]
    public async Task TheEventOfTheCardWrite_IsAnEcho_ForTheColumnWritten()
    {
        var r = new Rig();
        await r.Board.Move(Item, 2, "Done");
        Assert.True(await r.Echo.IsEcho(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("done")));
        Assert.False(await r.Echo.IsEcho(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("Backlog")));   // a person moved it back
        r.Echo.Forget(Key);
        Assert.False(await r.Echo.IsEcho(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("done")));
    }

    [Fact]
    public async Task TheWebhookOfTheStateWrite_ThatArrivesBeforeSaveReturns_IsRecognizedAsTheEcho_ForAnUpdateAndForACreate()
    {
        var r = new Rig();
        var echoes = new List<bool>();
        r.Api.OnCommentWrite = body => echoes.Add(r.Echo.IsStateCommentEcho(Key, body).GetAwaiter().GetResult());
        await r.State.Apply(Item, 1, s => s["x"] = 1);   // creates the comment (revision 1)
        await r.State.Apply(Item, 2, s => s["x"] = 2);   // updates it (revision 2)
        Assert.Equal([true, true], echoes);
    }

    [Fact]
    public async Task AStateSaveThatFails_TakesItsEchoRecordBack_ARealEditWithThatRevisionIsNews()
    {
        var r = new Rig();
        r.Api.FailCommentWrite = true;
        await Assert.ThrowsAnyAsync<Exception>(() => r.State.Apply(Item, 1, s => s["x"] = 1));
        var planned = ItemStateComment.Body(Name, 7, new JsonObject { ["item"] = 7, ["rev"] = 1, ["x"] = 1 });
        Assert.False(await r.Echo.IsStateCommentEcho(Key, planned));
        Assert.Equal(0, await r.Versions.Applied(Key, ProjectionTargets.Comment));
    }

    [Fact]
    public async Task ABoardMoveThatFails_TakesItsEchoRecordBack_AndRestoresTheEarlierOne()
    {
        var r = new Rig();
        await r.Board.Move(Item, 1, "Done");
        r.Api.FailMutation = true;
        await Assert.ThrowsAsync<ProjectionRefusedException>(() => r.Board.Move(Item, 2, "Backlog"));
        Assert.False(await r.Echo.IsEcho(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("Backlog")));   // a person's later move to Backlog is news
        Assert.True(await r.Echo.IsEcho(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("Done")));   // the earlier write is still the newest applied
        Assert.Equal(1, await r.Versions.Applied(Key, ProjectionTargets.Card));
    }

    [Fact]
    public async Task AnOlderApplyThatComesLate_NeverReplacesTheRecordOfANewerOne()
    {
        var r = new Rig();
        var newer = r.Echo.Record(Key, ProjectionTargets.Card, 5, ProjectionEcho.ColumnFingerprint("Done"));
        var older = r.Echo.Record(Key, ProjectionTargets.Card, 3, ProjectionEcho.ColumnFingerprint("Backlog"));
        Assert.True(newer.Taken); Assert.False(older.Taken);
        Assert.True(await r.Echo.IsEcho(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("Done")));
        Assert.False(await r.Echo.IsEcho(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("Backlog")));
        r.Echo.Unrecord(older);   // (a mark that was not taken changes nothing)
        Assert.True(await r.Echo.IsEcho(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("Done")));
        // the same version again (a retry after a failure) does take the record, and a newer one then replaces it
        Assert.True(r.Echo.Record(Key, ProjectionTargets.Card, 5, ProjectionEcho.ColumnFingerprint("Backlog")).Taken);
        Assert.True(r.Echo.Record(Key, ProjectionTargets.Card, 6, ProjectionEcho.ColumnFingerprint("Technical Design")).Taken);
    }

    // ---- W9.10h: the latest few writes, for the webhook of a write that is no longer the newest ------------------------------------------------

    [Fact]
    public async Task TheWebhookOfAnEarlierWrite_IsStillTheProjections_UntilItsWindowIsOver_ButTheNewestIsTheOnlyStrictEcho()
    {
        var now = new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);
        var echo = new ProjectionEcho(new InMemoryProjectionVersions(), () => now);
        echo.Record(Key, ProjectionTargets.Card, 1, ProjectionEcho.ColumnFingerprint("Development"));
        now = now.AddSeconds(20);
        echo.Record(Key, ProjectionTargets.Card, 2, ProjectionEcho.ColumnFingerprint("Functional Testing"));
        var window = TimeSpan.FromMinutes(2);
        Assert.True(echo.WroteRecently(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("development"), window));   // (any case)
        Assert.True(echo.WroteRecently(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("Functional Testing"), window));
        Assert.False(echo.WroteRecently(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("Technical Design"), window));   // never written
        Assert.False(echo.WroteRecently("o/r#8", ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("Development"), window));   // another item
        Assert.False(echo.WroteRecently(Key, ProjectionTargets.Comment, ProjectionEcho.ColumnFingerprint("Development"), window));   // another target
        Assert.False(await echo.IsEcho(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("Development")));   // (the strict echo is the newest write only)
        now = now.AddMinutes(3);
        Assert.False(echo.WroteRecently(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("Development"), window));
        Assert.False(echo.WroteRecently(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("Functional Testing"), window));
    }

    [Fact]
    public void AWriteThatFailed_IsNotAmongTheRecentOnes_AndOnlyTheLatestFewAreKept_AndForgetClearsThem()
    {
        var now = new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);
        var echo = new ProjectionEcho(new InMemoryProjectionVersions(), () => now);
        var window = TimeSpan.FromMinutes(2);
        var mark = echo.Record(Key, ProjectionTargets.Card, 1, ProjectionEcho.ColumnFingerprint("Backlog"));
        echo.Unrecord(mark);
        Assert.False(echo.WroteRecently(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("Backlog"), window));
        for (var v = 2; v < 2 + ProjectionEcho.RecentWrites + 1; v++) echo.Record(Key, ProjectionTargets.Card, v, ProjectionEcho.ColumnFingerprint("column " + v));
        Assert.False(echo.WroteRecently(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("column 2"), window));   // (the oldest was dropped)
        Assert.True(echo.WroteRecently(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("column 3"), window));
        echo.Forget(Key);
        Assert.False(echo.WroteRecently(Key, ProjectionTargets.Card, ProjectionEcho.ColumnFingerprint("column 3"), window));
    }
}
