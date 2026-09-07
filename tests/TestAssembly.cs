using Xunit;

// Fixtures clear SQLite's process-wide connection pools and create/delete account stores.
// Do not overlap fixture lifetimes; individual tests still explicitly exercise concurrent
// HTTP requests and all three providers with independent completion gates.
[assembly: CollectionBehavior(DisableTestParallelization = true)]