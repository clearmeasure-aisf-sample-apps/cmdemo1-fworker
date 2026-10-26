// The gateway's own address for the links it writes into the journal and its session pages (task C6, gateway-composition.md 3 C and D, request
// helpers): FACTORY_PUBLIC_URL, else the scheme and host a caller last reached this gateway on. One per process: GatewayAuthMiddleware notes it
// (the admin's requests, a nonce redemption, a Copilot exchange), CallerContext.SessionLinkOf and the plant's session pages (Plant.SessionPage,
// through the composition's LinkBase) read it. Made by Program.cs before the composition, which hands LinkBase to the plant.
sealed class PublicBase(string publicUrl)
{
    string seenBase = "";

    public string LinkBase() => publicUrl != "" ? publicUrl.TrimEnd('/') : Volatile.Read(ref seenBase);

    // the scheme and host a caller reached this gateway on (behind the platform's ingress: X-Forwarded-Proto), for LinkBase
    public void NoteBase(HttpContext c)
    {
        if (publicUrl != "" || !c.Request.Host.HasValue) return;
        var fp = c.Request.Headers["X-Forwarded-Proto"].ToString();
        var proto = fp is "https" or "http" ? fp : c.Request.Scheme;
        Volatile.Write(ref seenBase, $"{proto}://{c.Request.Host.Value}");
    }
}
