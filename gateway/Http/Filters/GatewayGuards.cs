using static HttpResultsX;

// The gateway's switch guards, each refusal written once (task C7a, gateway-composition.md 3 C and D). The endpoint filters (EndpointFilters.cs) run
// them; until C7b and C7c move the remaining routes into modules, Program.cs's MessagingOff() and SupervisorOff() call them too, so a refusal text
// never exists twice. Each returns the refusal, or null when the switch is on. They read the settings at the moment of the request: messaging's
// Enabled is the operator's run-time switch (POST /messaging/disable and enable).
static class GatewayGuards
{
    // owner mode: the plant, its sessions and their credentials exist only there
    public static IResult? NotOwnerMode(bool ownerMode) => ownerMode ? null : Fail(403, "not-owner-mode", "the gateway is not running in owner mode", 30);

    // peer messaging (worker-messaging.md 4.5): owner mode first, then messaging.enabled as switched at run time
    public static IResult? MessagingOff(bool ownerMode, MessagingSettings messaging)
    {
        if (NotOwnerMode(ownerMode) is { } off) return off;
        if (!messaging.Enabled) return Fail(403, "messaging-disabled", "peer messaging is not enabled on this gateway (messaging.enabled)", 30);
        return null;
    }

    // the supervisor (worker-messaging.md 3.8, 3.9): messaging first, then manager.enabled
    public static IResult? SupervisorOff(bool ownerMode, MessagingSettings messaging) =>
        MessagingOff(ownerMode, messaging) ?? (messaging.ManagerEnabled ? null : Fail(403, "manager-disabled", "the supervisor is not enabled on this gateway (manager.enabled)", 30));

    // the operator session (Operator.cs, operator-session.md): operator.enabled first, then messaging, the order the operator's checks have always had
    public static IResult? OperatorOff(bool ownerMode, MessagingSettings messaging)
    {
        if (!messaging.OperatorEnabled) return Fail(403, "operator-disabled", "the operator session is not enabled on this gateway (operator.enabled)", 30);
        return MessagingOff(ownerMode, messaging);
    }
}
