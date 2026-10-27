using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

// The rules of the operator's release record (task C8, gateway-composition.md 3 F; they were in POST /api/v1/operator/record-release and in
// Plant, Operator.cs): what a version is, what the body may be, and when a release is not written. Pure. tests/Gateway.Tests/ReleaseRecordPolicyTests.cs.
static class ReleaseRecordPolicy
{
    // writes of release records per UTC day, whatever operator.maxSelfMergesPerDay says: a flood of records is stopped here
    public const int WritesPerDay = 20;

    static readonly Regex VersionRx = new(@"^[0-9]{1,6}(\.[0-9]{1,9}){1,3}\z", RegexOptions.CultureInvariant);
    public static bool ValidVersion(string v) => VersionRx.IsMatch(v);

    public const string UsageReason = "the body is { \"pr\": <pull request number>, \"version\": \"<the version /_build shows, digits and dots>\" }";

    // The body { "pr": N, "version": "1.0.N" } and nothing else. Null: not that.
    public static (int Pr, string Version)? ParseBody(JsonObject body)
    {
        if (!(body["pr"] is JsonValue pv && pv.TryGetValue<int>(out var pr) && pr >= 1 && pr <= 999_999_999) || body["version"]?.ToString() is not { } version || !ValidVersion(version) || body.Count != 2)
            return null;
        return (pr, version);
    }

    // A gateway that runs with FACTORY_TEST_SKIP_REVOCATIONS cannot record a release: the stored refs and this process would not be in step.
    public static PolicyRefusal? CheckOn(bool revocationsOff) =>
        revocationsOff ? new(403, "releases-off", "this gateway runs with FACTORY_TEST_SKIP_REVOCATIONS: it cannot record a release", 30) : null;

    // A release already recorded for the pull request is idempotent; a new one is written only below the cap of the day (the record is the truth: the
    // cap is the cap of writes, not of releases).
    public static bool WriteAllowed(bool alreadyRecorded, int recordsToday) => alreadyRecorded || recordsToday < WritesPerDay;
}
