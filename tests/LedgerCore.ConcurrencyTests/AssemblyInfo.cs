using LedgerCore.TestSupport;
using Xunit;

[assembly: AssemblyFixture(typeof(PostgresServer))]

// Each test already hammers the database with dozens of parallel requests; running the test classes
// themselves in parallel would only add noise to timing-sensitive assertions such as "B is waiting on a lock".
[assembly: CollectionBehavior(DisableTestParallelization = true)]
