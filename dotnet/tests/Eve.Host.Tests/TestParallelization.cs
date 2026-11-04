using Xunit;

// The host tests share one PostgreSQL server (max_connections 100); each host runtime holds pooled connections, so they run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
