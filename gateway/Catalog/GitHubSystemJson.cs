using System.Net;
using System.Net.Http.Headers;

// The GitHub source of the system catalog: one GET of the contents API for system.json of cmdemo1-system, answered as the raw file. Read-only, one
// call per refresh, no retry of its own (the next refresh is the retry). 404 is "not available" (null: the loader keeps its last good copy and
// reports it); any other refusal throws with the status, which the loader keeps as the error.
sealed class GitHubSystemJson
{
    readonly CatalogSettings settings;
    readonly HttpClient http;

    public GitHubSystemJson(CatalogSettings settings, HttpClient? http = null)
    {
        this.settings = settings;
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string Url
    {
        get
        {
            var path = string.Join('/', settings.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
            var url = $"{settings.ApiUrl}/repos/{Uri.EscapeDataString(settings.Owner)}/{Uri.EscapeDataString(settings.Repository)}/contents/{path}";
            return settings.Ref.Length > 0 ? $"{url}?ref={Uri.EscapeDataString(settings.Ref)}" : url;
        }
    }

    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("fworker-gateway", "1"));
        if (settings.Token.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)res.StatusCode} for {settings.Owner}/{settings.Repository}/{settings.Path}");
        return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
