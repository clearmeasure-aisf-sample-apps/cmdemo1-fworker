using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// The plugin directory a worker downloads (task C7a): its files and their content hash, which /_build shows and the archive carries. The hash is
// computed once, at the first ask, and kept for the life of the process (the image's plugin does not change while it runs).
sealed class PluginArchive(GatewayOptions gateway, BuildInfo build)
{
    string? hashCache;

    public string Root => Path.GetFullPath(Path.Combine(gateway.ScriptsDir, ".."));

    // every file below the root but the archive's own markers and build output, in ordinal order
    public static IEnumerable<string> Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(f => { var r = Path.GetRelativePath(root, f).Replace('\\', '/'); return r != ".plugin-version" && r != ".plugin-managed" && !r.Contains("/bin/") && !r.Contains("/obj/"); })
        .OrderBy(f => f, StringComparer.Ordinal);

    public string Hash()
    {
        if (hashCache != null) return hashCache;
        var root = Root; var sb = new StringBuilder();
        foreach (var f in Files(root)) sb.Append(Path.GetRelativePath(root, f).Replace('\\', '/')).Append('\n').Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))).Append('\n');
        return hashCache = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    // the tar.gz: the files, then .plugin-version (content hash and the gateway's version) and .plugin-managed (marks a downloaded copy, the only
    // kind `factory.ps1 plugin-sync` ever updates). Null when this gateway has no plugin to give.
    public MemoryStream? Archive()
    {
        var root = Root;
        if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "scripts", "factory.ps1"))) return null;
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tw = new TarWriter(gz, leaveOpen: false))
        {
            foreach (var f in Files(root))
                tw.WriteEntry(f, Path.GetRelativePath(root, f).Replace('\\', '/'));
            void Add(string name, string text) => tw.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
            Add(".plugin-version", JsonSerializer.Serialize(new { hash = Hash(), gatewayVersion = build.Version }));
            Add(".plugin-managed", $"downloaded from this gateway on {DateTime.UtcNow:o}; factory.ps1 plugin-sync may replace it\n");
        }
        ms.Position = 0;
        return ms;
    }
}

// The plugin (skills, agents, scripts) as a tar.gz for a worker that cannot reach the source: a hand-made cloud session pulls exactly the version this
// gateway runs. Needs a worker credential, the supervisor's credential (its connect prompt downloads the same plugin: the scripts and the
// factory-supervisor skill), the backlog keeper's, the operator session's or the operator token; nothing outside the plugin directory is included.
sealed class PluginEndpoints(PluginArchive plugin) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/v1/plugin", (HttpContext c) => Download(c));

    IResult Download(HttpContext c)
    {
        // Who, not Admitted: the operator session is let in by its own kind
        if (c.Caller().Who.Kind is not ("admin" or "worker" or "manager" or "operator" or "keeper")) return Results.StatusCode(403);
        var archive = plugin.Archive();
        return archive == null ? Results.NotFound() : Results.File(archive, "application/gzip", "factory-worker-plugin.tar.gz");
    }
}
