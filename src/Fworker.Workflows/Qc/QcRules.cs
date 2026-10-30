using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Domain;

namespace Fworker.Workflows.Qc;

/// <summary>Whether a pull request may have an environment, and why not (Get-QcEligibility).</summary>
public sealed record QcEligibility(bool Ok, IReadOnlyList<string> Reasons);

/// <summary>The pure rules of the QC gate, each the port of a function of Qc.ps1 or Verbs.Inspect.ps1 with the same inputs and the same words.</summary>
public static class QcRules
{
    /// <summary>
    /// Get-QcEligibility: the pull request is open, not a draft, from the repository itself (never a fork), on the item's own branch (or the Copilot branch its state records), the item is in Validation with a PASS
    /// verdict for this pull request and no merge, it does not carry the Auto Merge label and, once the files are known, it is not documentation only.
    /// </summary>
    public static QcEligibility Eligibility(QcConfig c, int item, IReadOnlyList<string> labels, string? column, JsonObject s, JsonObject pr, IReadOnlyList<string>? files)
    {
        var why = new List<string>();
        var head = QcJson.Obj(pr, "head");
        if (QcJson.Str(pr, "state") != "open") why.Add("pr-not-open");
        if (pr.TryGetPropertyValue("draft", out var draft) && draft is JsonValue dv && dv.TryGetValue<bool>(out var isDraft) && isDraft) why.Add("draft");
        var full = QcJson.Str(QcJson.Obj(head, "repo"), "full_name");
        if (!string.Equals(full, c.FullName, StringComparison.OrdinalIgnoreCase)) why.Add("fork");
        // the item's own branch, or the copilot/* branch of the pull request its state records (a Copilot cloud agent pushes only copilot/* branches)
        var headRef = QcJson.Str(head, "ref");
        var number = QcJson.Int(pr, "number");
        var copilotOwn = headRef.StartsWith("copilot/", StringComparison.OrdinalIgnoreCase) && QcJson.Has(s, "pr") && QcJson.Has(QcJson.Obj(s, "pr"), "number") && QcJson.Int(QcJson.Obj(s, "pr"), "number") == number;
        if (!Regex.IsMatch(headRef, $"^{Regex.Escape(c.Name)}/{item}-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) && !copilotOwn) why.Add("branch-prefix");
        if (!string.Equals(column ?? "", c.ValidationColumn, StringComparison.OrdinalIgnoreCase)) why.Add("not-in-validation");
        // A PASS without the Auto Merge label is recorded as processed and parked ('pass-without-auto-merge') for a person to merge: that is exactly the item QC serves.
        var verdict = QcJson.Obj(s, "verdict");
        if (verdict is null || !QcJson.Bool(verdict, "passed") || QcJson.Int(verdict, "pr") != number || QcJson.Has(s, "merge")) why.Add("no-pass-verdict");
        if (QcJson.NameIn(c.AutoMergeLabel, labels)) why.Add("auto-merge");
        if (why.Count == 0 && files is not null && DocsOnly.FilesAreDocumentation(files, c.DocsOnlyPaths)) why.Add("docs-only");
        return new QcEligibility(why.Count == 0, why);
    }

    /// <summary>
    /// Find-HumanApproval: the login of a person in humanApprovers whose latest review of the pull request is an approval of its current head, and who did not write it; null when there is none or no one is named.
    /// <paramref name="reviews"/> are the reviews in the order GitHub lists them: the last of a reviewer is the latest.
    /// </summary>
    public static string? FindHumanApproval(JsonObject pr, IReadOnlyList<string> humans, IReadOnlyList<JsonObject> reviews)
    {
        if (humans.Count == 0) return null;
        var author = QcJson.Str(QcJson.Obj(pr, "user"), "login");
        var sha = QcJson.Str(QcJson.Obj(pr, "head"), "sha");
        var latest = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var r in reviews)
        {
            var login = QcJson.Str(QcJson.Obj(r, "user"), "login");
            if (!latest.ContainsKey(login)) order.Add(login);
            latest[login] = r;
        }
        foreach (var login in order)
        {
            var r = latest[login];
            if (QcJson.NameIn(login, humans) && !string.Equals(login, author, StringComparison.OrdinalIgnoreCase) && string.Equals(QcJson.Str(r, "state"), "APPROVED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(QcJson.Str(r, "commit_id"), sha, StringComparison.OrdinalIgnoreCase)) return login;
        }
        return null;
    }

    /// <summary>Get-QcTrust: the reasons a run needs the operator's confirmation, from the author and from the inspector's mandatory escalation codes. The anonymous link cannot supply one.</summary>
    public static List<string> TrustReasons(QcConfig c, string author, string serverLogin, IEnumerable<string> mandatoryCodes)
    {
        var reasons = new List<string>();
        var trusted = c.TrustedAuthors.Concat(c.HumanApprovers).Append(serverLogin).ToList();
        if (!QcJson.NameIn(author, trusted)) reasons.Add($"author-not-trusted:{author}");
        foreach (var code in mandatoryCodes)
            if (code is "protected-path" or "package-change" or "database-migration" or "secret-like" or "ai-directed-text") reasons.Add(code);
        return reasons;
    }

    // ---- the link token ------------------------------------------------------------------------------------------------------------------------------

    static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>New-QcToken: q1.&lt;payload&gt;.&lt;mac&gt;, the payload the pull request, the head, the purpose qc-open, the expiry, a nonce and the item, the mac an HMAC-SHA256 of "qc:" and the payload under the gateway's secret.</summary>
    public static (string Token, long Expires) NewToken(QcConfig c, int pr, string sha, int item, DateTimeOffset now, Func<string>? nonce = null)
    {
        var secret = c.TokenSecret;
        if (secret.Length < 16) throw new QcReject(30, "no-token-secret", "FACTORY_TOKEN_SECRET is not set on the gateway");
        var exp = now.AddDays(c.TimingOr0("qcLinkDays")).ToUnixTimeSeconds();
        var payload = new JsonObject { ["pr"] = pr, ["sha"] = sha, ["u"] = "qc-open", ["x"] = exp, ["n"] = (nonce ?? (() => Guid.NewGuid().ToString("N")))(), ["i"] = item }.ToJsonString();
        var p = B64(Encoding.UTF8.GetBytes(payload));
        var mac = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("qc:" + p))).ToLowerInvariant();
        return ($"q1.{p}.{mac}", exp);
    }

    /// <summary>Get-QcBaseUrl: the address people use to reach the gateway (FACTORY_PUBLIC_URL), refused when it is missing, not http(s), or one nobody else can reach.</summary>
    public static string BaseUrl(QcConfig c)
    {
        var u = c.PublicUrl;
        if (u.Length == 0) throw new QcReject(30, "public-url-not-reachable", "FACTORY_PUBLIC_URL is not set on the gateway");
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new QcReject(30, "public-url-not-reachable", "FACTORY_PUBLIC_URL is not an http(s) address");
        if (QcConfig.IsLoopbackHost(uri.Host) && !c.AllowLoopbackForTests) throw new QcReject(30, "public-url-not-reachable", $"FACTORY_PUBLIC_URL points at {uri.Host}, which nobody else can reach; set it to the address people use");
        return u;
    }

    // ---- the offer comments --------------------------------------------------------------------------------------------------------------------------

    static string Days(QcConfig c) => c.TimingOr0("qcLinkDays").ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    static string Sha7(string sha) => sha[..Math.Min(7, sha.Length)];

    public static string OfferText(QcConfig c, int pr, string sha, string link, long exp) =>
        $"QC environment available for PR #{pr} at head {Sha7(sha)}: [Start the QC environment]({link})\n" +
        "Nothing runs until someone presses Start. The environment runs on a factory worker's own computer and is reachable only from networks that can reach that computer (the worker is chosen at Start).\n" +
        $"The link works for {Days(c)} days and only for this commit.\n" +
        $"<!-- qc link pr={pr} sha={sha} exp={exp} -->";

    /// <summary>The offer comment after Start: it names the worker; the link line stays so a person can still click it.</summary>
    public static string AssignedText(int pr, string sha, string worker, string link, string exp) =>
        $"QC environment for PR #{pr} at head {Sha7(sha)} runs on **{worker}**'s computer (reachable only from networks that can reach it): [QC page]({link})\n" +
        $"This environment was requested and runs on that worker.\n<!-- qc link pr={pr} sha={sha} exp={exp} -->";

    public static string WithdrawnText(int pr, string why) => $"QC environment for PR #{pr} is no longer offered ({why}).\n<!-- qc link pr={pr} sha=withdrawn exp=0 -->";

    /// <summary>Find-QcOffer: the comment of the gateway's login that carries the offer's marker for the pull request.</summary>
    public static QcComment? FindOffer(IEnumerable<QcComment>? comments, int pr, string login) =>
        comments?.FirstOrDefault(cm => string.Equals(cm.Login, login, StringComparison.OrdinalIgnoreCase) && cm.Body.Contains($"<!-- qc link pr={pr} ", StringComparison.Ordinal));
}
