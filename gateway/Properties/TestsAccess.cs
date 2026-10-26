// tests/Gateway.Tests calls InProcess (the MCP tools' in-process endpoint call) as the tools do, with a Principal, and mints a worker token
// with Auth.WorkerToken: all three are internal. Nothing else changes for the gateway.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Gateway.Tests")]
