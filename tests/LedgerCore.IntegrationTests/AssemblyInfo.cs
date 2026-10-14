using LedgerCore.TestSupport;
using Xunit;

// One PostgreSQL server for the whole test assembly; each test clones its own database from a template.
[assembly: AssemblyFixture(typeof(PostgresServer))]
