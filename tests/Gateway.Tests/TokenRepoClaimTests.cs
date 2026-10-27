using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// The repository claim of the item (i1) and role (r1) tokens (W1.8, a protected area: credentials and authorization; docs/design/system-gateway.md H7).
/// Absent means the home repository, so every token minted before the claim stays valid; a claim must name the home repository (any letter case);
/// anything else is refused, and a claim cannot be added, removed or changed without the secret because the MAC covers the whole payload.
/// </summary>
public sealed class TokenRepoClaimTests
{
    const string Secret = "unit-test-secret-0123456789abcdef", Admin = "unit-test-admin-token";
    static readonly long Later = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
    static readonly RepoId Home = RepoId.Parse("Acme/Orders");
    static CredentialPolicy Policy(RepoId home = default) => new("tdd", true, new KeeperSettings(), home);
    static MessagingSettings Messaging() => new();
    static Principal? Auth_(CredentialPolicy p, string token, Revocations? rev = null) => p.Authenticate("Bearer " + token, Admin, Secret, Messaging(), rev ?? new Revocations());
    static string Role(string repo = "") => Auth.RoleToken(Secret, new RoleClaims("olga", "worker", "manual", "", "tdd", Later, 7, "a-1", "dev", Repo: repo));
    static string Item(string repo = "", int rev = 0) => Auth.MintItemToken(Secret, 7, "owner", "a-1", "olga", TimeSpan.FromHours(1), rev, "", repo);

    // a token as the gateway minted it before the claim existed, built by hand so it does not share the minting code
    static string Hand(string kind, JsonObject o)
    {
        var p = Auth.B64(Encoding.UTF8.GetBytes(o.ToJsonString()));
        return $"{kind}.{p}.{Auth.Mac(Secret, kind == "r1" ? "role:" + p : p)}";
    }
    static JsonObject OldItem() => new() { ["i"] = 7, ["r"] = "owner", ["a"] = "a-1", ["w"] = "olga", ["x"] = Later };
    static JsonObject OldRole() => new() { ["v"] = 1, ["n"] = "olga", ["r"] = "worker", ["m"] = "manual", ["ag"] = "", ["e"] = "tdd", ["x"] = Later, ["i"] = 7, ["a"] = "a-1", ["p"] = "dev" };

    [Fact]
    public void Minting_WithoutAClaim_WritesNoClaim_SoTheTokenIsTheOldFormat()
    {
        Assert.False(Payload(Item())!.ContainsKey("rp"));
        Assert.False(Payload(Role())!.ContainsKey("rp"));
    }

    [Fact]
    public void Minting_WithTheClaim_WritesItAndTheGatewayAcceptsIt()
    {
        Assert.Equal("acme/orders", Payload(Item("acme/orders"))!["rp"]!.ToString());
        Assert.Equal("acme/orders", Payload(Role("acme/orders"))!["rp"]!.ToString());
        var p = Policy(Home);
        var i = Auth_(p, Item("acme/orders"))!; var r = Auth_(p, Role("acme/orders"))!;
        Assert.Equal(("item", 7, "acme/orders"), (i.Kind, i.Item, i.Repo));
        Assert.Equal(("role", 7, "acme/orders"), (r.Kind, r.Item, r.Repo));
    }

    [Fact]
    public void OldTokens_WithoutTheClaim_MeanTheHomeRepository_AndStayValid()
    {
        var p = Policy(Home);
        Assert.Equal("acme/orders", Auth_(p, Hand("i1", OldItem()))!.Repo);
        Assert.Equal("acme/orders", Auth_(p, Hand("r1", OldRole()))!.Repo);
        // and where the configuration names no home, an old token is still valid; it just carries no repository
        Assert.Equal("", Auth_(Policy(), Hand("i1", OldItem()))!.Repo);
        Assert.NotNull(Auth_(Policy(), Hand("r1", OldRole())));
    }

    [Theory]
    [InlineData("ACME/ORDERS")]
    [InlineData("Acme/orders")]
    [InlineData(" acme/orders ")]
    public void TheClaim_IsComparedWithoutRegardToCase(string claim)
    {
        var p = Policy(Home);
        Assert.NotNull(Auth_(p, Item(claim))); Assert.NotNull(Auth_(p, Role(claim)));
    }

    [Theory]
    [InlineData("acme/other")]
    [InlineData("other/orders")]
    [InlineData("acme/orders/extra")]
    [InlineData("acme")]
    [InlineData("acme/orders#7")]
    [InlineData("a b/c")]
    public void AForeignOrMalformedClaim_IsRefused_ByBothKinds_AndNamedForTheRefusalCode(string claim)
    {
        var p = Policy(Home);
        foreach (var t in new[] { Item(claim), Role(claim) })
        {
            Assert.Null(Auth_(p, t));
            Assert.Equal(claim, p.ForeignRepository("Bearer " + t, Secret));
        }
    }

    [Fact]
    public void AClaimOfAnyNonStringShape_IsRefused()
    {
        var p = Policy(Home);
        foreach (var bad in new JsonNode?[] { JsonValue.Create(7), JsonValue.Create(true), new JsonObject(), new JsonArray("acme/orders"), JsonValue.Create("") })
        {
            var i = OldItem(); i["rp"] = bad?.DeepClone(); var r = OldRole(); r["rp"] = bad?.DeepClone();
            Assert.Null(Auth_(p, Hand("i1", i))); Assert.Null(Auth_(p, Hand("r1", r)));
        }
        // JSON null is the same as an absent claim for System.Text.Json; a null must not be a way to "claim" anything else, and it grants only the home
        var n = OldItem(); n["rp"] = null;
        Assert.Equal("acme/orders", Auth_(p, Hand("i1", n))!.Repo);
    }

    [Fact]
    public void WhereTheHomeIsUnset_NoClaimIsAccepted()
    {
        var p = Policy();
        Assert.Null(Auth_(p, Item("acme/orders"))); Assert.Null(Auth_(p, Role("acme/orders")));
    }

    [Fact]
    public void TheClaimCannotBeAddedRemovedOrChanged_TheMacCoversThePayload()
    {
        var p = Policy(Home);
        foreach (var t in new[] { Item("other/orders"), Role("other/orders"), Hand("i1", OldItem()), Hand("r1", OldRole()) })
        {
            var parts = t.Split('.');
            var o = JsonNode.Parse(Encoding.UTF8.GetString(Auth.UnB64(parts[1])))!.AsObject();
            foreach (var edit in new Action<JsonObject>[] { x => x.Remove("rp"), x => x["rp"] = "acme/orders", x => x["rp"] = "other/orders" })
            {
                var copy = JsonNode.Parse(o.ToJsonString())!.AsObject(); edit(copy);
                var forged = $"{parts[0]}.{Auth.B64(Encoding.UTF8.GetBytes(copy.ToJsonString()))}.{parts[2]}";
                if (forged == t) continue;   // the edit changed nothing
                Assert.Null(Auth_(p, forged));
                Assert.Null(p.ForeignRepository("Bearer " + forged, Secret));   // a bad MAC is not even named
            }
        }
    }

    [Fact]
    public void AClaimOfAnI1_DoesNotValidateAsAnR1_AndTheOtherWayRound()
    {
        var p = Policy(Home);
        var parts = Item("other/orders").Split('.');
        Assert.Null(Auth_(p, "r1." + parts[1] + "." + parts[2]));
        var rparts = Role("other/orders").Split('.');
        Assert.Null(Auth_(p, "i1." + rparts[1] + "." + rparts[2]));
        Assert.Null(p.ForeignRepository("Bearer r1." + parts[1] + "." + parts[2], Secret));
    }

    [Fact]
    public void ARevokedOrExpiredToken_StaysRefused_WithAHomeClaim_AndAForeignClaimStaysRefused()
    {
        var p = Policy(Home);
        var rev = new Revocations(); rev.Raise("olga", 1);
        Assert.Null(Auth_(p, Item("acme/orders"), rev)); Assert.Null(Auth_(p, Role("acme/orders"), rev));
        var old = Auth.RoleToken(Secret, new RoleClaims("olga", "worker", "manual", "", "tdd", 1, Repo: "acme/orders"));
        Assert.Null(Auth_(p, old));
        Assert.Null(Auth_(p, Item("acme/orders", rev: 5)));
    }

    [Fact]
    public void TheItemTokenCheckOfTheMcpServer_AppliesTheSameRule()
    {
        Assert.NotNull(Auth.VerifyItemToken(Secret, Item("acme/orders"), new Revocations(), Home));
        Assert.NotNull(Auth.VerifyItemToken(Secret, Hand("i1", OldItem()), new Revocations(), Home));
        Assert.Null(Auth.VerifyItemToken(Secret, Item("other/orders"), new Revocations(), Home));
        Assert.Null(Auth.VerifyItemToken(Secret, Item("acme/orders"), new Revocations()));   // no home given: no claim accepted
    }

    [Fact]
    public void AdminAndTheOtherKinds_CarryNoClaim_AndAreUntouched()
    {
        var p = Policy(Home);
        Assert.Equal("admin", Auth_(p, Admin)!.Kind);
        Assert.Equal("worker", Auth_(p, Auth.WorkerToken(Secret, "olga"))!.Kind);
        Assert.Null(p.ForeignRepository("Bearer " + Admin, Secret));
        Assert.Null(p.ForeignRepository(null, Secret));
        Assert.Equal("acme/orders", p.HomeClaim); Assert.Equal("", Policy().HomeClaim);
    }

    static JsonObject? Payload(string token) => JsonNode.Parse(Encoding.UTF8.GetString(Auth.UnB64(token.Split('.')[1])))!.AsObject();
}

/// <summary>The refusal as the gateway answers it from outside: the foreign claim has its own code, the home claim and the old token pass authentication.</summary>
public sealed class TokenRepoClaimGatewayTests(TwoPortGateway gw) : IClassFixture<TwoPortGateway>
{
    const int Api = GatewayEnvironment.ApiPort;   // the test configuration's home is the repository of config/instance.json

    [Fact]
    public async Task AForeignClaim_IsRefused403WithItsOwnCode_ForAnItemAndARoleToken()
    {
        if (!StubPwsh.Supported) return;
        var s = gw.Env.TokenSecret;
        foreach (var t in new[] { Auth.MintItemToken(s, 7, "owner", "a-1", "olga", TimeSpan.FromHours(1), 0, "", "someone/else"),
                                  Auth.RoleToken(s, new RoleClaims("olga", "worker", "manual", "", "tdd", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), Repo: "someone/else")) })
        {
            var a = await AuthPins.Raw(gw.Host, "GET", "/api/v1/status", Api, AuthPins.Bearer(t));
            Assert.Equal(403, a.Status);
            Assert.Equal("repository-not-served", a.Code);
        }
    }

    [Fact]
    public async Task TheHomeClaimAndNoClaim_PassAuthentication_AForgedClaimIsTheOrdinary401()
    {
        if (!StubPwsh.Supported) return;
        var s = gw.Env.TokenSecret; var home = "clearmeasure-aisf-sample-apps/cmdemo1-workorders";
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        foreach (var t in new[] { Auth.MintItemToken(s, 7, "owner", "a-1", "olga", TimeSpan.FromHours(1), 0, "", home), Auth.MintItemToken(s, 7, "owner", "a-1", "olga", TimeSpan.FromHours(1)),
                                  Auth.RoleToken(s, new RoleClaims("olga", "worker", "manual", "", "tdd", exp, Repo: home)), Auth.RoleToken(s, new RoleClaims("olga", "worker", "manual", "", "tdd", exp)) })
        {
            var a = await AuthPins.Raw(gw.Host, "GET", "/api/v1/status", Api, AuthPins.Bearer(t));
            Assert.NotEqual("unauthorized", a.Code); Assert.NotEqual("repository-not-served", a.Code);
        }
        var forged = Auth.MintItemToken(s, 7, "owner", "a-1", "olga", TimeSpan.FromHours(1), 0, "", "someone/else");
        var f = await AuthPins.Raw(gw.Host, "GET", "/api/v1/status", Api, AuthPins.Bearer(forged[..^2] + (forged[^2] == 'a' ? "bb" : "aa")));
        Assert.Equal(401, f.Status); Assert.Equal("unauthorized", f.Code);
    }
}
