using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// W9.7 (#359): `git-credential` on v2, from plugin/scripts/lib/Apps.ps1 (Verb_git_credential, New-AppJwt). A GitHub installation token of the author App for the item of a runtime worker, scoped by GitHub itself to the
// work-orders repository with contents write and nothing else, valid one hour (docs/design/cloud-worker-runtimes.md 2.6). Who may ask is decided before this runs, by VerbPolicy.AuthorizeVerb (an item token of role owner
// minted for a runtime worker, for its own item) and the pipeline (the item is owned through an accepted assignment): neither is touched. The App key never leaves this class; the token is minted per call, handed to the
// caller and kept nowhere (not in a log, not on disk, not in memory beyond the answer). A token GitHub granted wider than asked for is revoked at once and nothing is handed out.
static class GitCredentialVerb
{
    public static async Task<JsonObject> Run(VerbArgs a, ServiceGitHub.CallScope s)
    {
        var gh = s.Gh;
        var item = VerbArgs.Int(a.Need("item"));
        var repo = gh.Repo; var owner = gh.Owner;   // Get-Cfg: a configuration that cannot be read is 30 config-missing, before anything is asked of GitHub
        var info = gh.AppInfo();
        var apiBase = gh.ApiBase;
        string jwt; string installation;
        if (gh.FakeApps)
        {
            // Tier 1: no key exists; the fake takes a JWT-shaped placeholder
            jwt = "eyJhbGciOiJSUzI1NiJ9." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"iss\":\"0\"}")).TrimEnd('=') + ".fake"; installation = "0";
        }
        else
        {
            if (string.IsNullOrEmpty(info.Installation)) throw new VerbReject(30, "app-not-installed", "the 'author' GitHub App has no installation recorded");
            var pem = gh.Env("FACTORY_APP_AUTHOR_KEY");
            if (string.IsNullOrEmpty(pem)) pem = gh.Keyring("github-app-private-key", "author");
            if (!string.IsNullOrEmpty(pem) && pem.Trim() == "unset") pem = null;   // the vault placeholder written before a key exists
            if (string.IsNullOrEmpty(pem)) throw new VerbReject(30, "secret-missing", "no private key for the author GitHub App");
            jwt = Jwt(pem, info.Id); pem = null; installation = info.Installation;
        }
        var request = new JsonObject { ["repositories"] = new JsonArray(repo), ["permissions"] = new JsonObject { ["contents"] = "write" } };
        using var mint = Request(HttpMethod.Post, $"app/installations/{installation}/access_tokens", jwt, request);
        jwt = "";
        using var resp = await gh.Http.SendAsync(mint, s.Ct);
        var status = (int)resp.StatusCode;
        if (status != 201) throw new VerbReject(20, "app-token-failed", $"GitHub answered {status} when minting the item's git credential", new JsonObject { ["retryAfterSeconds"] = 30 });
        var granted = JsonNode.Parse(await resp.Content.ReadAsStringAsync(s.Ct)) as JsonObject ?? new JsonObject();
        var token = VerbArgs.Text(granted["token"]);
        // GitHub answers the scope it granted (every App token also carries metadata: read): a token wider than asked for is revoked, not handed out
        var grant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (granted["permissions"] is JsonObject perms) foreach (var kv in perms) grant[kv.Key] = VerbArgs.Text(kv.Value);
        var repos = (granted["repositories"] as JsonArray ?? []).Where(r => r is not null)
            .Select(r => (Name: VerbArgs.Text(r!["name"]), Full: VerbArgs.Text(r["full_name"]))).ToList();
        var okPerms = grant.TryGetValue("contents", out var contents) && string.Equals(contents, "write", StringComparison.OrdinalIgnoreCase)
            && grant.Keys.All(k => k.Equals("contents", StringComparison.OrdinalIgnoreCase) || (k.Equals("metadata", StringComparison.OrdinalIgnoreCase) && string.Equals(grant[k], "read", StringComparison.OrdinalIgnoreCase)));
        var okRepo = repos.Count == 1 && string.Equals(repos[0].Name, repo, StringComparison.OrdinalIgnoreCase)
            && (repos[0].Full == "" || string.Equals(repos[0].Full, $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase));
        if (!(okPerms && okRepo))
        {
            try
            {
                using var revoke = Request(HttpMethod.Delete, "installation/token", token, null);
                using var _ = await gh.Http.SendAsync(revoke, s.Ct);
            }
            catch (Exception e) when (e is not OperationCanceledException) { }
            throw new VerbReject(20, "git-credential-scope", "GitHub granted a different scope than asked for (one repository, contents write); the token was revoked and nothing is handed out");
        }
        return new JsonObject
        {
            ["ok"] = true, ["item"] = item, ["token"] = token, ["expires_at"] = ExpiresAt(granted["expires_at"]), ["repository"] = $"{owner}/{repo}",
            ["permissions"] = new JsonObject { ["contents"] = "write" }, ["username"] = "x-access-token",
        };
    }

    // "$($r.expires_at)": the rule script's JSON reader turned the ISO time into a DateTime and PowerShell wrote it as text: UTC, in the invariant culture's pattern (MM/dd/yyyy HH:mm:ss), whatever the gateway's time zone.
    // The credential helper of the runner image reads it with `date -d`, which takes this form as well as ISO 8601; v2 keeps the form, as every consumer of v1's answer has seen it.
    internal static string ExpiresAt(JsonNode? expires)
    {
        var text = VerbArgs.Text(expires);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToString(CultureInfo.InvariantCulture) : text;
    }

    static HttpRequestMessage Request(HttpMethod method, string path, string bearer, JsonNode? body)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("User-Agent", ServiceGitHub.UserAgent);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(ServiceGitHub.Relaxed), Encoding.UTF8, "application/json");
        return req;
    }

    // New-AppJwt: RS256, issued a minute ago, valid nine minutes. A key whose line ends are written as \n (an environment variable) is read as well.
    static string Jwt(string pem, JsonNode? appId)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var pay = B64(Encoding.UTF8.GetBytes(new JsonObject { ["iat"] = now - 60, ["exp"] = now + 540, ["iss"] = appId?.DeepClone() }.ToJsonString()));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem.Replace("\\n", "\n"));
        return $"{head}.{pay}.{B64(rsa.SignData(Encoding.UTF8.GetBytes($"{head}.{pay}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
    }

    // secret-tool lookup service factory-worker kind <kind> app <app>: the operator's keyring, for a gateway on the operator's machine. No shell: the arguments are fixed. Where there is no secret-tool the start fails,
    // which the script's call did as well (an internal error, 20).
    internal static string SecretTool(string kind, string app)
    {
        var psi = new ProcessStartInfo("secret-tool") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "lookup", "service", "factory-worker", "kind", kind, "app", app }) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("secret-tool did not start");
        var output = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
        return output.ReplaceLineEndings("\n").TrimEnd('\n');   // (the lines, joined by newlines)
    }
}
