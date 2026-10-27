using Xunit;

// The gateway reads its configuration from process environment variables and keeps process-wide statics (CredentialPolicy, Auth.*,
// the MCP tools, ...): one host at a time, so no two tests ever run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
