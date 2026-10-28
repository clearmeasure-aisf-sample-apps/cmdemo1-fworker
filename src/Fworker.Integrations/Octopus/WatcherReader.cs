using System.Text.Json.Nodes;

namespace Fworker.Integrations.Octopus;

/// <summary>The read side of the Octopus API the watcher needs: GET only. A 404 is null; any other failure throws <see cref="OctopusReadException"/>.</summary>
public interface IOctopusReader
{
    Task<JsonNode?> Get(string path, CancellationToken ct = default);
}

/// <summary>Octopus answered something else than a body or a 404 (Invoke-Octo's 'octopus-error'): the poll ends and the next one tries again.</summary>
public sealed class OctopusReadException(string message, int status) : Exception(message) { public int Status { get; } = status; }

/// <summary>GET against the Octopus server with the headers Invoke-Octo uses (X-Octopus-ApiKey, User-Agent factory-worker-gateway, 30 s). The key is never logged.</summary>
public sealed class HttpOctopusReader(HttpClient http, string baseUrl, string apiKey) : IOctopusReader
{
    readonly string _base = baseUrl.TrimEnd('/');

    public async Task<JsonNode?> Get(string path, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_base}/{path.TrimStart('/')}");
        req.Headers.TryAddWithoutValidation("X-Octopus-ApiKey", apiKey);
        req.Headers.TryAddWithoutValidation("User-Agent", "factory-worker-gateway");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var resp = await http.SendAsync(req, cts.Token);
            if ((int)resp.StatusCode == 404) return null;
            if (!resp.IsSuccessStatusCode) throw new OctopusReadException($"Octopus answered {(int)resp.StatusCode} to GET {path}", (int)resp.StatusCode);
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        }
        catch (OctopusReadException) { throw; }
        catch (Exception e) when (!ct.IsCancellationRequested) { throw new OctopusReadException($"Octopus GET {path} failed: {e.GetType().Name}", 0); }
    }
}
