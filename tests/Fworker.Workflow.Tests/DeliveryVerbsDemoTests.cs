using System.Text.Json.Nodes;
using Fworker.Workflows.Delivery.Verbs;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>W9.4: publish-demo on v2 (Verbs.Pr.ps1 Verb_publish_demo): the release demo-N and its asset, the refusals, the retry of the upload.</summary>
public sealed class DeliveryVerbsDemoTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("demo-test-").FullName;
    static readonly CancellationToken Ct = CancellationToken.None;
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    string Mp4(int item) { var p = Path.Combine(dir, $"demo-{item}-{Guid.NewGuid():N}.mp4"); File.WriteAllBytes(p, new byte[64]); return p; }

    (FakeGh Gh, FakeStateStore Store, RuleKit Kit) Make(string column = "Functional Testing", string[]? labels = null)
    {
        var gh = new FakeGh().WithBoard(n => Gql.Item(n, column, true, labels ?? ["video"]));
        gh.Write(HttpMethod.Post, "repos/acme/orders/issues/5/comments");
        var store = new FakeStateStore();
        return (gh, store, VerbFixture.Kit(gh, store));
    }

    static JsonObject Release(params long[] assets) => new()
    {
        ["html_url"] = "https://github.com/acme/orders/releases/tag/demo-5", ["upload_url"] = "https://uploads.example/repos/acme/orders/releases/1/assets{?name,label}",
        ["assets"] = new JsonArray(assets.Select(id => (JsonNode?)new JsonObject { ["id"] = id, ["name"] = "demo-5.mp4" }).ToArray()),
    };

    [Fact]
    public async Task CreatesTheReleaseAndUploadsTheAsset_AndSaysSo()
    {
        var (gh, _, kit) = Make();
        gh.Get("repos/acme/orders/releases/tags/demo-5", null, 404);
        gh.On(HttpMethod.Post, "repos/acme/orders/releases", _ => new GhResult(201, Release()));
        gh.Uploads.Enqueue(new GhResult(201, new JsonObject { ["size"] = 3145728, ["browser_download_url"] = "https://dl.example/demo-5.mp4" }));
        var r = await PublishDemoVerb.Run(kit, J.Obj(("item", 5), ("file", Mp4(5)), ("holder", "w1")), dir, Ct);
        Assert.Equal(("demo-5", "demo-5.mp4", true, false, 3145728L), (r["release"]!.GetValue<string>(), r["asset"]!.GetValue<string>(), r["created"]!.GetValue<bool>(), r["replaced"]!.GetValue<bool>(), r["size"]!.GetValue<long>()));
        Assert.Equal("https://dl.example/demo-5.mp4", r["asset_url"]!.GetValue<string>());
        var create = gh.Writes.First(w => w.Path == "repos/acme/orders/releases").Body!;
        Assert.Equal(("demo-5", "master", "Demo video for #5", true, false), (create["tag_name"]!.GetValue<string>(), create["target_commitish"]!.GetValue<string>(), create["name"]!.GetValue<string>(), create["prerelease"]!.GetValue<bool>(), create["draft"]!.GetValue<bool>()));
        Assert.Equal("https://uploads.example/repos/acme/orders/releases/1/assets?name=demo-5.mp4", gh.UploadedTo.Single());
        Assert.Equal("\U0001F916 factory-worker: demo video published: [demo-5.mp4](https://dl.example/demo-5.mp4) on release [demo-5](https://github.com/acme/orders/releases/tag/demo-5) (3 MB).\n\n<!-- factory-journal:factory-worker -->",
            gh.Writes.Last().Body!["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASecondPublishReplacesTheAsset()
    {
        var (gh, _, kit) = Make();
        gh.Get("repos/acme/orders/releases/tags/demo-5", Release(77, 78));
        gh.Write(HttpMethod.Delete, "repos/acme/orders/releases/assets/", status: 204);
        var r = await PublishDemoVerb.Run(kit, J.Obj(("item", 5), ("file", Mp4(5))), dir, Ct);
        Assert.Equal((false, true), (r["created"]!.GetValue<bool>(), r["replaced"]!.GetValue<bool>()));
        Assert.Equal(["DELETE repos/acme/orders/releases/assets/77", "DELETE repos/acme/orders/releases/assets/78"], gh.Writes.Where(w => w.Method == HttpMethod.Delete).Select(w => $"{w.Method.Method} {w.Path}").ToArray());
        Assert.Contains("demo video replaced:", gh.Writes.Last().Body!["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARaceForTheReleaseIsTheOtherCallsRelease()
    {
        var (gh, _, kit) = Make();
        var reads = 0;
        gh.On(HttpMethod.Get, "repos/acme/orders/releases/tags/demo-5", _ => ++reads == 1 ? new GhResult(404, null) : new GhResult(200, Release()));
        gh.On(HttpMethod.Post, "repos/acme/orders/releases", _ => new GhResult(422, new JsonObject { ["message"] = "Validation Failed" }));
        var r = await PublishDemoVerb.Run(kit, J.Obj(("item", 5), ("file", Mp4(5))), dir, Ct);
        Assert.False(r["created"]!.GetValue<bool>());
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task AnyOtherAnswerToTheReleaseIsARetryableGitHubError()
    {
        var (gh, _, kit) = Make();
        gh.Get("repos/acme/orders/releases/tags/demo-5", null, 404);
        gh.On(HttpMethod.Post, "repos/acme/orders/releases", _ => new GhResult(500, null));
        var e = await Assert.ThrowsAsync<VerbReject>(() => PublishDemoVerb.Run(kit, J.Obj(("item", 5), ("file", Mp4(5))), dir, Ct));
        Assert.Equal((20, "github-error", "GitHub 500 creating release demo-5", 30), (e.Exit, e.Code, e.Message, e.Extra!["retryAfterSeconds"]!.GetValue<int>()));
    }

    [Fact]
    public async Task TheUploadIsTriedTwiceOnA502_AndThenRefusedWithTheStatus()
    {
        var (gh, _, kit) = Make();
        gh.Get("repos/acme/orders/releases/tags/demo-5", Release());
        gh.Uploads.Enqueue(new GhResult(502, null));
        gh.Uploads.Enqueue(new GhResult(201, new JsonObject { ["size"] = 10, ["browser_download_url"] = "u" }));
        await PublishDemoVerb.Run(kit, J.Obj(("item", 5), ("file", Mp4(5))), dir, Ct);
        Assert.Equal(2, gh.UploadedTo.Count);
        gh.UploadedTo.Clear();
        gh.Uploads.Enqueue(new GhResult(502, null)); gh.Uploads.Enqueue(new GhResult(503, null));
        var e = await Assert.ThrowsAsync<VerbReject>(() => PublishDemoVerb.Run(kit, J.Obj(("item", 5), ("file", Mp4(5))), dir, Ct));
        Assert.Equal((20, "upload-failed", "GitHub 503 on the asset upload"), (e.Exit, e.Code, e.Message));
        gh.UploadedTo.Clear();
        gh.Uploads.Enqueue(new GhResult(422, null));   // not a status worth a second try
        e = await Assert.ThrowsAsync<VerbReject>(() => PublishDemoVerb.Run(kit, J.Obj(("item", 5), ("file", Mp4(5))), dir, Ct));
        Assert.Equal("GitHub 422 on the asset upload", e.Message);
        Assert.Single(gh.UploadedTo);
    }

    [Fact]
    public async Task OnlyAFileTheEndpointMade_InTheTempFolder_IsPublished()
    {
        var (_, _, kit) = Make();
        foreach (var file in new[] { "/etc/hostname", Path.Combine(dir, "demo-5-short.mp4"), Path.Combine(dir, "demo-6-" + new string('a', 32) + ".mp4"), Path.Combine(dir, "demo-5-" + new string('a', 32) + ".mp4") })
        {
            var e = await Assert.ThrowsAsync<VerbReject>(() => PublishDemoVerb.Run(kit, J.Obj(("item", 5), ("file", file)), dir, Ct));
            Assert.Equal((2, "use-endpoint"), (e.Exit, e.Code));   // (the last one does not exist)
        }
        var elsewhere = Path.Combine(Path.GetTempPath(), $"demo-5-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(elsewhere, new byte[8]);
        try { Assert.Equal("use-endpoint", (await Assert.ThrowsAsync<VerbReject>(() => PublishDemoVerb.Run(kit, J.Obj(("item", 5), ("file", elsewhere)), dir, Ct))).Code); }
        finally { File.Delete(elsewhere); }
    }

    [Fact]
    public async Task ARefusalOfTheItem_NoVideoLabel_WrongColumn_AClaimOfAnother()
    {
        var (_, _, plain) = Make(labels: []);
        var e = await Assert.ThrowsAsync<VerbReject>(() => PublishDemoVerb.Run(plain, J.Obj(("item", 5), ("file", Mp4(5))), dir, Ct));
        Assert.Equal((10, "not-video-item", "item #5 does not carry the 'video' label: it owes no demo video"), (e.Exit, e.Code, e.Message));
        var (_, _, design) = Make(column: "Technical Design");
        e = await Assert.ThrowsAsync<VerbReject>(() => PublishDemoVerb.Run(design, J.Obj(("item", 5), ("file", Mp4(5))), dir, Ct));
        Assert.Equal((10, "wrong-column", "item is in 'Technical Design'; a demo video is published in Development or Functional Testing"), (e.Exit, e.Code, e.Message));
        var (gh, store, kit) = Make();
        gh.Get("repos/acme/orders/releases/tags/demo-5", Release());
        store.Of(5)["claim"] = new JsonObject { ["holder"] = "w2", ["lease_until"] = "2026-10-29T13:00:00.000Z" };
        e = await Assert.ThrowsAsync<VerbReject>(() => PublishDemoVerb.Run(kit, J.Obj(("item", 5), ("file", Mp4(5)), ("holder", "w1")), dir, Ct));
        Assert.Equal((40, "claim-held", "the claim is held by w2"), (e.Exit, e.Code, e.Message));
        await PublishDemoVerb.Run(kit, J.Obj(("item", 5), ("file", Mp4(5)), ("holder", "anonymous")), dir, Ct);   // an anonymous caller is never refused by a claim
        store.Of(5)["claim"]!["lease_until"] = "2026-10-29T11:00:00.000Z";   // a lease that ran out holds nothing
        await PublishDemoVerb.Run(kit, J.Obj(("item", 5), ("file", Mp4(5)), ("holder", "w1")), dir, Ct);
    }
}
