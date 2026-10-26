// The state of composing the gateway (gateway-composition.md 3 B, task C5a): the configuration read at start, and the objects the AddGateway*
// steps make while they compose that a later step, or a handler still in Program.cs, needs. Program.cs makes it once, before the builder; the
// steps fill it in the order Program.cs calls them (GatewayServiceCollectionExtensions.cs). Each object is made exactly where Program.cs made it
// before C5a, so the start-up lines and file reads keep their order.
//
// It shrinks as stage C goes on: what a step registers as a service leaves it once nothing outside the container reads it (C5b: the runtimes
// become RuntimeRegistry; C6: the middleware takes its collaborators by injection; C7: the handlers move into endpoint modules).
sealed class GatewayComposition(GatewayOptions gateway, InstanceConfig instance, TimingOptions timing, RevocationOptions revocation, QcSettings qc,
    MessagingSettings messaging, KeeperSettings keeper, Func<string> linkBase)
{
    // the configuration (C3), registered by AddGatewayConfiguration
    public GatewayOptions Gateway { get; } = gateway;
    public InstanceConfig Instance { get; } = instance;
    public TimingOptions Timing { get; } = timing;
    public RevocationOptions Revocation { get; } = revocation;
    public QcSettings Qc { get; } = qc;
    public MessagingSettings Messaging { get; } = messaging;
    public KeeperSettings Keeper { get; } = keeper;
    // the gateway's own address for links (FACTORY_PUBLIC_URL, else the address a caller last used; the middleware notes it): the plant's session pages
    public Func<string> LinkBase { get; } = linkBase;

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
    public GatewayRuntimes Runtimes { get; set; } = null!;

    // AddGatewayBackground
    public int SweepMinSeconds { get; set; }

    // Made by Program.cs after Build() and handed over here before app.Run(), until C5b registers them in the container (C5d): the prompt files,
    // read by the on-demand manager's bootstrap text at a launch; the worker sessions at /mcp, swept by McpSessionSweeper at each pass
    public PromptStore? Prompts { get; set; }
    public WorkerSessions? McpSessions { get; set; }
}
