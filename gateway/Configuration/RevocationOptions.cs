using System.Globalization;

// The read of the revoked credentials at start (Program.cs, Auth.cs): its switches, read once (C3). The state and the retry loop stay in Program.cs.
sealed class RevocationOptions
{
    // FACTORY_TEST_SKIP_REVOCATIONS=1 (the scenario suites' second gateways, whose GitHub is a port nothing listens on): nothing is read, and revoke is refused
    public bool SkipRevocations { get; init; }
    // FACTORY_REVOCATIONS_ATTEMPT_SECONDS (45, 1..120): the time limit of one read
    public double AttemptSeconds { get; init; }
    // FACTORY_REVOCATIONS_ALARM_SECONDS (180, 1..3600): failing for this long logs the error line
    public double AlarmSeconds { get; init; }
    // the gateway has a GitHub identity: an App key (FACTORY_AUTHOR_APP=1) or GH_TOKEN; the vault placeholder 'unset' counts as none
    public bool GitHubConfigured { get; init; }

    public static RevocationOptions FromEnvironment(Func<string, string?> env)
    {
        double RevSeconds(string name, double fallback, double min, double max) => double.TryParse(env(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : fallback;
        return new RevocationOptions
        {
            SkipRevocations = env("FACTORY_TEST_SKIP_REVOCATIONS") == "1",
            AttemptSeconds = RevSeconds("FACTORY_REVOCATIONS_ATTEMPT_SECONDS", 45, 1, 120),
            AlarmSeconds = RevSeconds("FACTORY_REVOCATIONS_ALARM_SECONDS", 180, 1, 3600),
            GitHubConfigured = IsGitHubConfigured(env),
        };
    }

    public static bool Unset(string? v) => string.IsNullOrWhiteSpace(v) || v.Trim() == "unset";

    static bool IsGitHubConfigured(Func<string, string?> env)
    {
        if (env("FACTORY_AUTHOR_APP") == "1")
        {
            if (!string.IsNullOrEmpty(env("FACTORY_FAKE_APPS"))) return true;   // an absent variable may still have a key in the keyring of a local gateway: only an empty or placeholder value is known to be none
            var key = env("FACTORY_APP_AUTHOR_KEY");
            return key == null || !Unset(key);
        }
        return !Unset(env("GH_TOKEN"));
    }
}
