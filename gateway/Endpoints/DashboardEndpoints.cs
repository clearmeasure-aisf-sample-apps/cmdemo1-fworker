// GET /dashboard: the operator's page, an embedded resource (gateway/Templates/, task C2). Open: the page itself holds nothing; it asks
// /api/v1/dashboard with the operator token the person enters (StatusEndpoints).
sealed class DashboardEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/dashboard", () => Results.Text(DashboardPage.Html, "text/html; charset=utf-8"));
}
