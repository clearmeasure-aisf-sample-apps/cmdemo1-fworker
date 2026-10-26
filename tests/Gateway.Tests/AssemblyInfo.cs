using Xunit;

// The gateway reads its configuration from process environment variables and keeps process-wide statics (Auth.Environment, Auth.Keeper,
// GatewayMcp.Tools, ...): one host at a time, so no two tests ever run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
