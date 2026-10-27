// The MCP server's route (task C8; it was app.MapMcp in Program.cs): Streamable HTTP at /mcp, stateless (AddGatewayMcp). The authentication middleware
// admits it (a credential whose kind plays no role there is refused before the server reads the request); the tools are McpTools.
sealed class McpEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes) => routes.MapMcp("/mcp");
}
