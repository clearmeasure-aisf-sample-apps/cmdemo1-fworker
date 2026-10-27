// The gateway: receives GitHub webhooks, holds every GitHub and Octopus secret, runs the rule scripts (pwsh factory.ps1 --server <verb>), runs the sweep,
// and in owner mode acts as the plant manager: a registry of worker sessions, directed assignment of jobs to them, per-item role-scoped tokens, and
// routing of triggers to the session that owns the item. Two listeners: the webhook port serves only the webhook route (this is the port a tunnel
// exposes); the API port serves the authenticated API and never leaves the machine.
//
// This file is the composition root and nothing else (gateway-composition.md 2, task C8): it reads the configuration, registers one area at a time
// (gateway/Composition/), builds the host, adds the pipeline and the endpoint modules (gateway/Endpoints/) and runs. tests/Gateway.Tests/
// ArchitectureTests.cs fails the build if it grows beyond that: a line budget, no loop, no handler, no Map*/Use* call but the ones below.

var composition = GatewayComposition.FromEnvironment(Environment.GetEnvironmentVariable);
var builder = WebApplication.CreateBuilder(args);

// the composition: the host, then one registration method per area, in the order the objects were made before C5a
// (GatewayServiceCollectionExtensions.cs says what each holds)
builder.AddGatewayHosting(composition);
builder.Services
    .AddGatewayConfiguration(composition)
    .AddGatewayCore(composition)
    .AddGatewayLeadership(composition)
    .AddGatewayIdentity(composition)
    .AddGatewayRuntimes(composition)
    .AddGatewayPlant(composition)
    .AddGatewayBackground(composition)
    .AddGatewayMcp(composition)
    .AddGatewayHttp(composition.PublicBase)
    .AddGatewayEndpoints(composition)
    .AddGatewaySeams(composition);
var app = builder.Build();

app.UseGatewayLifetime();       // the stopping callbacks in their order, the objects resolved where they were made, the runtimes' revoke
app.UseGatewayPipeline();       // forwarded headers, the test-only cause of a request, the authentication and admission
app.MapGatewayEndpoints();      // each endpoint module maps its own routes
app.Run();
