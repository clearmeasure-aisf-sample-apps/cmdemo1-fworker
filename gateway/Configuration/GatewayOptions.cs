// The gateway's own environment variables, read once at start (C3, docs/design/fworker-wolverine-implementation.md). Same names, defaults and exception
// messages as the reads at the top of Program.cs had, and the three required variables are checked in the same order (the first missing one is named).
// env is the reader: Program passes Environment.GetEnvironmentVariable, the tests a dictionary, so they never touch the process environment.
sealed class GatewayOptions
{
    public required string ScriptsDir { get; init; }
    public required string Entry { get; init; }
    public required string ApiToken { get; init; }
    public required string WebhookSecret { get; init; }
    // every event stream writes a `: ping` comment at this interval (FACTORY_SSE_PING_SECONDS, a positive whole number, else 25)
    public int SsePingSeconds { get; init; }
    // FACTORY_SWEEP_SECONDS: any whole number (today's parse takes 0 and negatives as written), else 180
    public int SweepSeconds { get; init; }
    public bool OwnerMode { get; init; }
    public string EnvironmentName { get; init; } = "tdd";
    public string PublicUrl { get; init; } = "";
    public string TokenSecret { get; init; } = "";
    public int ApiPort { get; init; }
    public int HookPort { get; init; }
    // container mode: FACTORY_SINGLE_PORT, else ASPNETCORE_HTTP_PORTS from the image; 0 is local mode (two loopback listeners)
    public int SinglePort { get; init; }

    public static GatewayOptions FromEnvironment(Func<string, string?> env)
    {
        var scriptsDir = env("FACTORY_PLUGIN_SCRIPTS") ?? throw new InvalidOperationException("FACTORY_PLUGIN_SCRIPTS is not set");
        var apiToken = env("FACTORY_DEV_TOKEN") ?? throw new InvalidOperationException("FACTORY_DEV_TOKEN is not set");
        var webhookSecret = env("FACTORY_WEBHOOK_SECRET") ?? throw new InvalidOperationException("FACTORY_WEBHOOK_SECRET is not set");
        return new GatewayOptions
        {
            ScriptsDir = scriptsDir,
            Entry = Path.Combine(scriptsDir, "factory.ps1"),
            ApiToken = apiToken,
            WebhookSecret = webhookSecret,
            SsePingSeconds = int.TryParse(env("FACTORY_SSE_PING_SECONDS"), out var sps) && sps > 0 ? sps : 25,
            SweepSeconds = int.TryParse(env("FACTORY_SWEEP_SECONDS"), out var ss) ? ss : 180,
            OwnerMode = env("FACTORY_OWNER_MODE") == "1",
            EnvironmentName = env("FACTORY_ENVIRONMENT") ?? "tdd",
            PublicUrl = env("FACTORY_PUBLIC_URL") ?? "",
            TokenSecret = env("FACTORY_TOKEN_SECRET") ?? "",
            ApiPort = int.TryParse(env("FACTORY_API_PORT"), out var ap) ? ap : 5100,
            HookPort = int.TryParse(env("FACTORY_WEBHOOK_PORT"), out var hp) ? hp : 5101,
            SinglePort = int.TryParse(env("FACTORY_SINGLE_PORT") ?? env("ASPNETCORE_HTTP_PORTS"), out var sp) ? sp : 0,
        };
    }

    // owner mode signs per-item and per-role tokens with the token secret: a short one is refused at start, as before
    public void ValidateOwnerMode()
    {
        if (OwnerMode && TokenSecret.Length < 16) throw new InvalidOperationException("FACTORY_TOKEN_SECRET (at least 16 characters) is required in owner mode");
    }
}
