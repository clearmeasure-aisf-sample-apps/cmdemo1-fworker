using Xunit;

namespace Gateway.Tests;

/// <summary>
/// CredentialPolicy (task C8): the credential rules that read the configuration (the environment, auth.legacyKinds, the keeper), once process-wide statics
/// of Auth. Each policy is its own object, so two with different settings live side by side (the old statics made that impossible).
/// </summary>
public class CredentialPolicyTests
{
    const string Secret = "unit-test-secret-0123456789abcdef", Admin = "unit-test-admin-token";
    static readonly long Later = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

    static CredentialPolicy Policy(string env = "tdd", bool legacy = true, KeeperSettings? keeper = null) => new(env, legacy, keeper ?? new KeeperSettings());
    static MessagingSettings Messaging() => new() { ManagerEnabled = true, ManagerNames = ["pm"], OperatorEnabled = true, OperatorNames = ["ops"] };
    static Principal? Authenticate(CredentialPolicy p, string? token, Revocations? rev = null, MessagingSettings? msg = null) =>
        p.Authenticate(token == null ? null : "Bearer " + token, Admin, Secret, msg ?? Messaging(), rev ?? new Revocations());
    static string Role(string env = "tdd", string name = "olga", string role = "worker", string mode = "manual", int rev = 0) =>
        Auth.RoleToken(Secret, new RoleClaims(name, role, mode, "", env, Later), rev);

    [Fact]
    public void TheSettingsAreTheInstancesOwn()
    {
        var keeper = new KeeperSettings { Enabled = true };
        var p = Policy(" x ".Trim(), false, keeper);
        Assert.Equal("x", p.Environment); Assert.False(p.LegacyKinds); Assert.Same(keeper, p.Keeper);
        Assert.Equal("tdd", Policy().Environment); Assert.True(Policy().LegacyKinds);
    }

    [Fact]
    public void Admin_IsRecognisedWhateverTheSettings()
    {
        Assert.Equal("admin", Authenticate(Policy(legacy: false), Admin)!.Kind);
        Assert.Null(Authenticate(Policy(), "not-the-admin"));
        Assert.Null(Authenticate(Policy(), null));
        Assert.Null(Policy().Authenticate("Basic " + Admin, Admin, Secret, Messaging(), new Revocations()));
    }

    [Fact]
    public void RoleToken_IsBoundToTheEnvironment()
    {
        var tdd = Policy("tdd"); var uat = Policy("uat");
        var p = Authenticate(tdd, Role("tdd"))!;
        Assert.Equal("role", p.Kind); Assert.Equal("olga", p.Name); Assert.Equal("worker", p.Role); Assert.Equal("tdd", p.Env);
        Assert.Null(Authenticate(tdd, Role("uat")));
        Assert.Null(Authenticate(uat, Role("tdd")));
        Assert.Equal("role", Authenticate(uat, Role("uat"))!.Kind);
    }

    [Fact]
    public void RoleToken_IsRefusedForAForgedMacAnExpiredOneAReservedNameAnUnknownRoleAndARevokedGeneration()
    {
        var p = Policy();
        var t = Role();
        Assert.Null(Authenticate(p, t[..^2] + (t[^2] == 'a' ? "bb" : "aa")));
        Assert.Null(Authenticate(p, Auth.RoleToken(Secret, new RoleClaims("olga", "worker", "manual", "", "tdd", 1))));
        Assert.Null(Authenticate(p, Role(name: "operator")));
        Assert.Null(Authenticate(p, Role(role: "emperor")));
        var rev = new Revocations(); rev.Raise("olga", 2);
        Assert.Null(Authenticate(p, Role(rev: 0), rev));
        Assert.Equal("role", Authenticate(p, Role(rev: 2), rev)!.Kind);
    }

    [Fact]
    public void RoleToken_MapsItsModeToTheRuntime()
    {
        var p = Policy();
        Assert.Equal("", Authenticate(p, Role(mode: "manual"))!.Runtime);
        Assert.Equal("cae-job", Authenticate(p, Role(mode: "hosted"))!.Runtime);
        Assert.Equal("cursor-cloud", Authenticate(p, Role(mode: "api"))!.Runtime);
        var cop = Auth.RoleToken(Secret, new RoleClaims("olga", "worker", "api", "copilot-cloud-agent", "tdd", Later));
        Assert.Equal("copilot-cloud", Authenticate(p, cop)!.Runtime);
    }

    [Fact]
    public void LegacyKinds_AreAcceptedOnlyWhileAuthLegacyKindsIsTrue()
    {
        var on = Policy(legacy: true); var off = Policy(legacy: false);
        var w1 = Auth.WorkerToken(Secret, "olga");
        var i1 = Auth.MintItemToken(Secret, 7, "owner", "a-1", "olga", TimeSpan.FromHours(1));
        var m1 = Auth.ManagerToken(Secret, "pm", 1);
        var o1 = Auth.OperatorToken(Secret, "ops", 1);
        Assert.Equal("worker", Authenticate(on, w1)!.Kind);
        Assert.Equal("item", Authenticate(on, i1)!.Kind);
        Assert.Equal("manager", Authenticate(on, m1)!.Kind);
        Assert.Equal("operator", Authenticate(on, o1)!.Kind);
        foreach (var t in new[] { w1, i1, m1, o1 }) Assert.Null(Authenticate(off, t));
        // the role token and the admin token are not legacy
        Assert.Equal("role", Authenticate(off, Role())!.Kind);
        Assert.Equal("admin", Authenticate(off, Admin)!.Kind);
    }

    [Fact]
    public void RuntimeWorkerToken_CarriesItsRuntimeAndExpiry()
    {
        var w2 = Auth.WorkerToken2(Secret, "olga", "cae-job", Later);
        var p = Authenticate(Policy(), w2)!;
        Assert.Equal("worker", p.Kind); Assert.Equal("cae-job", p.Runtime); Assert.Equal(Later, p.Expires);
        Assert.Null(Authenticate(Policy(), Auth.WorkerToken2(Secret, "olga", "cae-job", 1)));
        Assert.Null(Authenticate(Policy(), Auth.WorkerToken2(Secret, "olga", "local", Later)));
    }

    [Fact]
    public void ManagerAndOperatorTokens_NeedTheirFeatureAndAListedName()
    {
        var p = Policy();
        var msg = Messaging();
        Assert.Null(Authenticate(p, Auth.ManagerToken(Secret, "stranger", 1), msg: msg));
        msg.ManagerEnabled = false;
        Assert.Null(Authenticate(p, Auth.ManagerToken(Secret, "pm", 1), msg: msg));
        Assert.Null(Authenticate(p, Auth.OperatorToken(Secret, "ops", 2), msg: Messaging()));   // another generation
        var m2 = Messaging(); m2.OperatorEnabled = false;
        Assert.Null(Authenticate(p, Auth.OperatorToken(Secret, "ops", 1), msg: m2));
    }

    [Fact]
    public void KeeperToken_FollowsThisPoliciesKeeperSettings_NotAProcessWideOne()
    {
        var token = Auth.KeeperToken(Secret, "kay", 1);
        var withKeeper = Policy(keeper: new KeeperSettings { Enabled = true, Names = ["kay"], TokenGeneration = 1 });
        var without = Policy();
        Assert.Equal("keeper", Authenticate(withKeeper, token)!.Kind);
        Assert.Null(Authenticate(without, token));
        Assert.Null(Authenticate(Policy(keeper: new KeeperSettings { Enabled = true, Names = ["kay"], TokenGeneration = 2 }), token));
        Assert.Null(Authenticate(Policy(keeper: new KeeperSettings { Enabled = true, Names = ["other"] }), token));
    }

    [Fact]
    public void ItemToken_IsVerifiedByMacExpiryAndRevocationGeneration()
    {
        var t = Auth.MintItemToken(Secret, 7, "owner", "a-1", "olga", TimeSpan.FromHours(1), runtime: "cae-job");
        var p = Authenticate(Policy(), t)!;
        Assert.Equal(("item", "olga", 7, "owner", "a-1", "cae-job"), (p.Kind, p.Name, p.Item, p.Role, p.Assignment, p.Runtime));
        Assert.Null(Authenticate(Policy(), Auth.MintItemToken(Secret, 7, "owner", "a-1", "olga", TimeSpan.FromHours(-1))));
        var rev = new Revocations(); rev.Raise("olga", 1);
        Assert.Null(Authenticate(Policy(), t, rev));
        Assert.Null(Authenticate(Policy(), t + "x"));
    }

    [Fact]
    public void NoSecret_AcceptsOnlyTheAdminToken()
    {
        Assert.Null(Policy().Authenticate("Bearer " + Role(), Admin, "", Messaging(), new Revocations()));
        Assert.Equal("admin", Policy().Authenticate("Bearer " + Admin, Admin, "", Messaging(), new Revocations())!.Kind);
    }
}
