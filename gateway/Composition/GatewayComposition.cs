// The state of composing the gateway (gateway-composition.md 3 B, task C5a): the configuration read at start, and the objects the AddGateway*
// steps make while they compose that a later step, or a handler still in Program.cs, needs. Program.cs makes it once, before the builder; the
// steps fill it in the order Program.cs calls them (GatewayServiceCollectionExtensions.cs). Each object is made exactly where Program.cs made it
// before C5a, so the start-up lines and file reads keep their order.
//
// It shrinks as stage C goes on: what a step registers as a service leaves it once nothing outside the container reads it (C6: the middleware
// takes its collaborators by injection; C7: the handlers move into endpoint modules). The runtimes are RuntimeRegistry since C5b; it stays here
// while AddGatewayPlant and AddGatewayBackground read it at registration.
sealed class GatewayComposition(GatewayOptions gateway, InstanceConfig instance, TimingOptions timing, RevocationOptions revocation, QcSettings qc,
    MessagingSettings messaging, KeeperSettings keeper, PublicBase publicBase)
{
    // The configuration of a process, read once from its environment and the configuration file (task C8: this was the top of Program.cs). The same
    // names, defaults and precedence as before; the same order of reads, so a refusal of the owner mode or a missing file comes at the same point.
    public static GatewayComposition FromEnvironment(Func<string, string?> env)
    {
        var gw = GatewayOptions.FromEnvironment(env);
        var scriptsDir = gw.ScriptsDir;
        var instanceConfig = InstanceConfig.Load(InstanceConfig.PathFor(scriptsDir, env), env);
        var timing = TimingOptions.From(instanceConfig);
        var revocationOptions = RevocationOptions.FromEnvironment(env);
        gw.ValidateOwnerMode();
        var qcSettings = QcSettings.Load(scriptsDir, gw.PublicUrl, gw.OwnerMode);
        var messaging = MessagingSettings.Load(scriptsDir, gw.OwnerMode);
        // the gateway's own secrets, so that a message carrying one verbatim is refused (worker-messaging.md 5.4); short development values are left out
        messaging.SecretLiterals = new[] { gw.ApiToken, gw.TokenSecret }.Where(x => x.Length >= 16).ToArray();
        // the backlog keeper session (Keeper.cs, docs/design/backlog-keeper.md): off by default; needs messaging.enabled; its names are reserved
        var keeper = KeeperSettings.Load(scriptsDir, messaging.Enabled);
        if (keeper.Enabled) messaging.Reserved.UnionWith(keeper.Names);
        // the gateway's own address for the links it writes into the journal: FACTORY_PUBLIC_URL, else the address the operator or a container last used
        // (gateway/Http/PublicBase.cs, noted by the authentication middleware)
        return new GatewayComposition(gw, instanceConfig, timing, revocationOptions, qcSettings, messaging, keeper, new PublicBase(gw.PublicUrl));
    }

    // the address for links (see PublicBase) and the credential rules of this process (gateway/Policies/CredentialPolicy.cs): the role token r1 is bound to
    // this environment (Auth.cs, agents-roles-launch.md 4); auth.legacyKinds false refuses the per-role kinds
    public PublicBase PublicBase { get; } = publicBase;
    public CredentialPolicy Credentials { get; } = new(gateway.EnvironmentName.Trim().ToLowerInvariant(), instance.LegacyKinds, keeper);

    // the configuration (C3), registered by AddGatewayConfiguration
    public GatewayOptions Gateway { get; } = gateway;
    public InstanceConfig Instance { get; } = instance;
    public TimingOptions Timing { get; } = timing;
    public RevocationOptions Revocation { get; } = revocation;
    public QcSettings Qc { get; } = qc;
    public MessagingSettings Messaging { get; } = messaging;
    public KeeperSettings Keeper { get; } = keeper;
    // the gateway's own address for links (FACTORY_PUBLIC_URL, else the address a caller last used; the middleware notes it): the plant's session pages
    public Func<string> LinkBase { get; } = publicBase.LinkBase;

    // AddGatewayCore
    public GitHubBudget GitHubBudget { get; set; } = null!;
    public Runner Runner { get; set; } = null!;
    public TranscriptStore Transcripts { get; set; } = null!;

    // AddGatewayLeadership
    public bool SnapshotOn { get; set; }
    public bool LeaderOn { get; set; }
    public GatewayLeader Leader { get; set; } = null!;
    public SweepGap SweepGap { get; set; } = null!;

    // AddGatewayIdentity
    public Revocations Revocations { get; set; } = null!;
    public RuntimeNonces RuntimeNonces { get; set; } = null!;

    // AddGatewayRuntimes
    public RuntimeRegistry Runtimes { get; set; } = null!;
    // the cloud runtimes' settings: AddGatewayBackground registers the CI release and the owner watches by them (C5c)
    public CopilotCloudSettings CopilotSettings { get; set; } = null!;
    public CursorCloudSettings CursorSettings { get; set; } = null!;

    // AddGatewayBackground
    public int SweepMinSeconds { get; set; }
}
