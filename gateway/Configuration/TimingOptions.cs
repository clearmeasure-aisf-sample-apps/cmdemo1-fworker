// The timing values Program.cs reads for itself (C3): each with the fallback and the rule (TimingNumber: more than 0; TimingValue: 0 or more) it had
// where it was read. The two counts are cast to int as before (a fraction is cut off).
sealed class TimingOptions
{
    public double AckSeconds { get; init; }
    public double PlantSnapshotMaxAgeMinutes { get; init; }
    public double PassUnmergedDigestMinutes { get; init; }
    public int OfferMissLimit { get; init; }
    public double OfferCoolDownMinutes { get; init; }
    public double SessionEndGraceSeconds { get; init; }
    public double PinFallbackMinutes { get; init; }
    public double SessionRestartMinutes { get; init; }
    public int SessionRepeatQuestionLimit { get; init; }
    // before FACTORY_SWEEP_MIN_SECONDS and the cap at the sweep interval, which Program.cs applies
    public double SweepMinSeconds { get; init; }
    public double McpIdleMinutes { get; init; }
    // the interval of the automatic promotion's own loop (AutoPromoter.cs)
    public double PromoteSeconds { get; init; }

    public static TimingOptions From(InstanceConfig cfg) => new()
    {
        AckSeconds = cfg.AckSeconds,
        PlantSnapshotMaxAgeMinutes = cfg.TimingNumber("plantSnapshotMaxAgeMinutes", 30),
        PassUnmergedDigestMinutes = cfg.TimingValue("passUnmergedDigestMinutes", 5),
        OfferMissLimit = (int)cfg.TimingValue("offerMissLimit", 2),
        OfferCoolDownMinutes = cfg.TimingValue("offerCoolDownMinutes", 5),
        SessionEndGraceSeconds = cfg.TimingValue("sessionEndGraceSeconds", 90),
        PinFallbackMinutes = cfg.TimingValue("pinFallbackMinutes", 30),
        SessionRestartMinutes = cfg.TimingValue("sessionRestartMinutes", 10),
        SessionRepeatQuestionLimit = (int)cfg.TimingValue("sessionRepeatQuestionLimit", 2),
        SweepMinSeconds = cfg.TimingValue("sweepMinSeconds", 60),
        McpIdleMinutes = cfg.TimingValue("mcpIdleMinutes", 30),
        PromoteSeconds = cfg.TimingNumber("promoteSeconds", 60),
    };
}
