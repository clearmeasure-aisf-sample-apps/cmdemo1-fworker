// The verb route (task C7d): every call of a rule-script verb by an agent, the operator or the gateway's own tools, and the one route the MCP tools reach in
// process. The route has no endpoint filter: its refusals depend on the verb and the body, so they are the pipeline's (gateway/Verbs/VerbPipeline.cs).
sealed class VerbEndpoints(VerbPipeline pipeline) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/verbs/{verb}", async (string verb, HttpContext ctx) => { return await pipeline.Run(verb, ctx); });
}
