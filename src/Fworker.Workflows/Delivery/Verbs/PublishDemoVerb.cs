using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.Delivery.Verbs;

// W9.4: publish-demo (Verbs.Pr.ps1 Verb_publish_demo): the demo video of a `video` item (issue #42). Called only by the gateway's POST /api/v1/demo/{item}, which has streamed the MP4 to a temp file, checked its
// size and its header, and authorized the caller. Creates release demo-<n> if missing (pre-release, on the default branch, the shape of the demo releases a person published) and uploads the asset demo-<n>.mp4,
// replacing one of that name. Idempotent.
public static class PublishDemoVerb
{
    public const string Verb = "publish-demo";

    static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static async Task<JsonObject> Run(RuleKit kit, JsonObject a, string tempRoot, CancellationToken ct)
    {
        var n = VerbArgs.ItemNumber(a);
        var file = J.S(VerbArgs.Need(a, "file"));
        var root = Path.GetFullPath(tempRoot);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;   // (GetTempPath ends with one; a sibling folder of the same prefix is not inside it)
        var full = Path.GetFullPath(file);
        if (!full.StartsWith(root, StringComparison.Ordinal) || !Regex.IsMatch(Path.GetFileName(full), $"^demo-{n}-[0-9a-f]{{32}}\\.mp4$", RegexOptions.IgnoreCase) || !File.Exists(full))
            throw new VerbReject(2, "use-endpoint", "send the MP4 as the body of POST /api/v1/demo/{item} (client: factory.ps1 publish-demo --item N --file F)");
        var info = await kit.GetItemInfo(n, ct); VerbArgs.AssertOpen(info);
        var videoLabel = J.S(kit.Cfg, "videoLabel");
        if (!info.HasLabel(videoLabel)) throw new VerbReject(10, "not-video-item", $"item #{n} does not carry the '{videoLabel}' label: it owes no demo video");
        if (!(Eq(info.Column ?? "", kit.Column("development")) || Eq(info.Column ?? "", kit.Column("validation"))))
            throw new VerbReject(10, "wrong-column", $"item is in '{info.Column}'; a demo video is published in {kit.Column("development")} or {kit.Column("validation")}");
        var st = await kit.GetState(n, ct); var s = st.State;
        var holder = J.Has(a, "holder") ? J.S(a, "holder") : "anonymous";
        if (J.Has(s, "claim") && kit.ClaimLive(s) && J.S(s["claim"], "holder") != holder && holder != "anonymous")
            throw new VerbReject(40, "claim-held", $"the claim is held by {J.S(s["claim"], "holder")}");
        var tag = $"demo-{n}"; var assetName = $"demo-{n}.mp4";
        var rel = await GhApi.Get(kit.Gh, $"{kit.RepoPath}/releases/tags/{tag}", ct, 404);
        var created = false;
        JsonNode? release = rel.Body;
        if (!rel.Ok)
        {
            var r = await kit.Gh.Send(HttpMethod.Post, $"{kit.RepoPath}/releases", new JsonObject
            {
                ["tag_name"] = tag, ["target_commitish"] = kit.DefaultBranch, ["name"] = $"Demo video for #{n}",
                ["body"] = $"Functional Validation demo media for #{n} (published by the factory worker gateway).", ["prerelease"] = true, ["draft"] = false,
            }, ct);
            if (r.Status == 201) { release = r.Body; created = true; }
            else if (r.Status == 422) release = (await GhApi.Get(kit.Gh, $"{kit.RepoPath}/releases/tags/{tag}", ct)).Body;   // created at the same moment by another call
            else throw new VerbReject(20, "github-error", $"GitHub {r.Status} creating release {tag}", new JsonObject { ["retryAfterSeconds"] = 30 });
        }
        var replaced = false;
        foreach (var old in J.Items(release?["assets"]).Where(x => x is not null && J.S(x, "name") == assetName).ToList())
        {
            await GhApi.Call(kit.Gh, HttpMethod.Delete, $"{kit.RepoPath}/releases/assets/{J.S(old, "id")}", null, ct, [404]);
            replaced = true;
        }
        var upload = Regex.Replace(J.S(release, "upload_url"), @"\{.*$", "") + $"?name={assetName}";
        GhResult? up = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try { up = await kit.Gh.UploadFile(upload, "video/mp4", full, ct); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                if (attempt >= 2) throw new VerbReject(20, "upload-failed", $"the upload to GitHub did not finish: {e.Message}", new JsonObject { ["retryAfterSeconds"] = 60 });
                continue;
            }
            if (up.Status == 201) break;
            if (attempt >= 2 || up.Status is not (502 or 503 or 504)) throw new VerbReject(20, "upload-failed", $"GitHub {up.Status} on the asset upload", new JsonObject { ["retryAfterSeconds"] = 60 });
        }
        var asset = up!.Body;
        var size = J.L(asset?["size"]);
        await kit.AddComment(n, $"demo video {(replaced ? "replaced" : "published")}: [{assetName}]({J.S(asset, "browser_download_url")}) on release [{tag}]({J.S(release, "html_url")}) ({Math.Round(size / 1048576.0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)} MB).", ct);
        return J.Obj(("ok", true), ("id", n), ("release", tag), ("release_url", J.S(release, "html_url")), ("asset", assetName), ("asset_url", J.S(asset, "browser_download_url")), ("size", size), ("created", created), ("replaced", replaced));
    }
}
