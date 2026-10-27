// What a policy of gateway/Policies/ answers when it refuses (task C8): the HTTP status and the fields of the gateway's usual refusal JSON
// ({ ok:false, code, reason, exit }). A policy never builds an IResult: the endpoint turns this into the answer it gave before. An empty Code is a
// bare status with no body (the metrics filter's 403 for a credential that plays no part there).
sealed record PolicyRefusal(int Status, string Code, string Reason, int Exit)
{
    public static PolicyRefusal Bare(int status) => new(status, "", "", 0);
    public bool IsBare => Code == "";
}
