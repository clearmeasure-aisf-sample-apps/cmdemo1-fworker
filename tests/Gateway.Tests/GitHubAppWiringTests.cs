using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Fworker.Contracts.Catalog;
using Xunit;

namespace Gateway.Tests;

/// <summary>A gateway with the author App's identity and no GH_TOKEN (tdd) gets the v2 parts that read GitHub: the Make methods answer null only where there is no identity at all.</summary>
public sealed class GitHubAppWiringTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("app-wiring").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    sealed class NoCatalog : ISystemCatalog
    {
        public CatalogSnapshot Current => throw new NotSupportedException();
        public string? LastError => null;
        public Task<CatalogRefreshResult> RefreshAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    GatewayComposition Compose()
    {
        var cfg = Path.Combine(dir, "instance.json");
        File.WriteAllText(cfg, new JsonObject { ["owner"] = "o", ["repo"] = "r", ["name"] = "factory-worker" }.ToJsonString());
        var vars = new Dictionary<string, string> { ["FACTORY_PLUGIN_SCRIPTS"] = dir, ["FACTORY_DEV_TOKEN"] = "dev", ["FACTORY_WEBHOOK_SECRET"] = "hook", ["FACTORY_CONFIG"] = cfg };
        return GatewayComposition.FromEnvironment(k => vars.GetValueOrDefault(k));
    }

    static Func<string, string?> Env(bool app, string? ghToken = null)
    {
        var d = new Dictionary<string, string?> { ["GH_TOKEN"] = ghToken ?? "unset" };
        if (app)
        {
            d["FACTORY_APP_AUTHOR_ID"] = "1"; d["FACTORY_APP_AUTHOR_INSTALLATION"] = "2"; d["FACTORY_APP_AUTHOR_SLUG"] = "s";
            d["FACTORY_APP_AUTHOR_KEY"] = RSA.Create(2048).ExportRSAPrivateKeyPem();   // a throwaway key made now
        }
        return k => d.GetValueOrDefault(k);
    }

    [Fact]
    public void AppIdentityWithoutGhToken_MakesTheParts()
    {
        var c = Compose(); var env = Env(app: true); var home = c.Instance.Home!.Value;
        Assert.NotNull(ItemMergedWiring.Make(c, home, "Server=x", env));
        Assert.NotNull(BoardReconcilerJob.Make(c, new NoCatalog(), new IngressInboxPort(_ => Task.FromResult(true)), env));
        Assert.NotNull(DeliveryShadowWiring.Make(c, new NoCatalog(), "Server=x", env));
    }

    [Fact]
    public void NoIdentity_MakesNothing()
    {
        var c = Compose(); var env = Env(app: false); var home = c.Instance.Home!.Value;
        Assert.Null(ItemMergedWiring.Make(c, home, "Server=x", env));
        Assert.Null(BoardReconcilerJob.Make(c, new NoCatalog(), new IngressInboxPort(_ => Task.FromResult(true)), env));
        Assert.Null(DeliveryShadowWiring.Make(c, new NoCatalog(), "Server=x", env));
    }

    [Fact]
    public void GhToken_StillMakesTheParts()
    {
        var c = Compose(); var env = Env(app: false, ghToken: "ghp_" + new string('C', 26));
        Assert.NotNull(ItemMergedWiring.Make(c, c.Instance.Home!.Value, "Server=x", env));
    }
}
