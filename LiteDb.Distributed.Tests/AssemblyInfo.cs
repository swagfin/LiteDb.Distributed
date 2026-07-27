using Xunit;

// LiteDB uses process-wide mapping/cache behavior, and these tests create many file-backed stores.
// Serial execution keeps cleanup and mapper/index initialization deterministic on Windows.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
