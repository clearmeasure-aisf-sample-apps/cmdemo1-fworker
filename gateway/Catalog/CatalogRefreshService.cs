using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;

// Refreshes the system catalog (W1.2): one read at the start, then every settings.RefreshSeconds. A failed read keeps the last good snapshot (the
// loader's rule) and is logged once per change of outcome, never every interval. Registered as a hosted service only where the catalog is switched on
// (AddGatewayBackground), so a default deployment starts the same services in the same order as before. Informational: nothing reads the
// snapshot but /api/v1/status.
sealed class CatalogRefreshService : BackgroundService
{
    readonly ISystemCatalog catalog;
    readonly CatalogSettings settings;
    readonly TimeProvider time;
    string? lastLogged;
    long refreshes, failures;
    DateTimeOffset? lastAttempt;

    public CatalogRefreshService(ISystemCatalog catalog, CatalogSettings settings, TimeProvider? time = null)
    {
        this.catalog = catalog; this.settings = settings; this.time = time ?? TimeProvider.System;
        Interval = TimeSpan.FromSeconds(settings.RefreshSeconds);
    }

    /// <summary>The pause between reads; a test shortens it.</summary>
    internal TimeSpan Interval { get; init; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { Console.Error.WriteLine($"catalog: {e.Message}"); }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One refresh, counted and logged on a change of its outcome.</summary>
    internal async Task<CatalogRefreshResult> RefreshOnceAsync(CancellationToken ct)
    {
        var r = await catalog.RefreshAsync(ct);
        Interlocked.Increment(ref refreshes);
        lastAttempt = time.GetUtcNow();
        if (!r.Succeeded) Interlocked.Increment(ref failures);
        var outcome = r.Succeeded ? "ok" : r.Error;
        if (outcome != lastLogged)
        {
            lastLogged = outcome;
            var line = new JsonObject
            {
                ["at"] = time.GetUtcNow().ToString("o"), ["catalog"] = r.Succeeded ? "loaded" : "kept-last-good",
                ["source"] = r.Snapshot.Source.ToString(), ["mismatches"] = r.Snapshot.Mismatches.Count,
            };
            if (!r.Succeeded) line["error"] = r.Error;
            Console.WriteLine(line.ToJsonString());
        }
        return r;
    }

    /// <summary>The catalog block of /api/v1/status: the snapshot in use, its mismatch report with instance.json, and the refresh counters.</summary>
    public JsonObject Status()
    {
        var s = catalog.Current;
        var repos = new JsonArray();
        foreach (var r in s.Repositories)
            repos.Add(new JsonObject { ["repository"] = r.Id.ToString(), ["deployables"] = new JsonArray(r.Deployables.Select(d => (JsonNode)JsonValue.Create(d.Name)!).ToArray()) });
        var mismatches = new JsonArray();
        foreach (var m in s.Mismatches) mismatches.Add(m);
        return new JsonObject
        {
            ["enabled"] = true,
            ["source"] = s.Source == CatalogSource.SystemJson ? "system.json" : "instance.json",
            ["loadedAt"] = s.LoadedAt.ToString("o"),
            ["from"] = $"{settings.Owner}/{settings.Repository}/{settings.Path}" + (settings.Ref.Length > 0 ? "@" + settings.Ref : ""),
            ["refreshSeconds"] = settings.RefreshSeconds,
            ["lastAttempt"] = lastAttempt?.ToString("o"),
            ["refreshes"] = Interlocked.Read(ref refreshes),
            ["failures"] = Interlocked.Read(ref failures),
            ["lastError"] = catalog.LastError,
            ["home"] = s.Home.ToString(),
            ["board"] = s.Board is { } b ? new JsonObject { ["owner"] = b.Owner, ["number"] = b.Number } : null,
            ["repositories"] = repos,
            ["mismatches"] = mismatches,
        };
    }
}
