using Npgsql;
using Xunit;

namespace LedgerCore.TestSupport;

/// <summary>
/// A per-test database plus direct SQL access for assertions. Assertions deliberately use their own SQL
/// instead of the application's read models: a test must not trust the code it is checking.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly PostgresServer _server;

    internal TestDatabase(PostgresServer server, string name, string connectionString)
    {
        _server = server;
        Name = name;
        ConnectionString = connectionString;
        DataSource = NpgsqlDataSource.Create(connectionString);
    }

    public string Name { get; }

    public string ConnectionString { get; }

    public NpgsqlDataSource DataSource { get; }

    public async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    public Task<long> CountAsync(string table) => ScalarAsync($"SELECT count(*) FROM {table}");

    public Task<long> WalletBalanceAsync(Guid walletId) => ScalarAsync(
        "SELECT la.balance_minor FROM wallets w JOIN ledger_accounts la ON la.id = w.ledger_account_id WHERE w.id = @id",
        ("id", walletId));

    /// <summary>Balance recomputed from postings only (credits minus debits for a wallet liability).</summary>
    public Task<long> WalletJournalBalanceAsync(Guid walletId) => ScalarAsync(
        """
        SELECT COALESCE(SUM(CASE WHEN p.direction = 'Credit' THEN p.amount_minor ELSE -p.amount_minor END), 0)
        FROM wallets w JOIN postings p ON p.ledger_account_id = w.ledger_account_id
        WHERE w.id = @id
        """,
        ("id", walletId));

    /// <summary>
    /// The ledger invariants every test ends with:
    ///   1. every journal entry has >= 2 postings and SUM(debits) = SUM(credits);
    ///   2. total debits = total credits across the whole journal;
    ///   3. every cached balance equals the balance recomputed from postings;
    ///   4. no wallet balance is negative;
    ///   5. every transfer points at exactly one Transfer journal entry with exactly two postings.
    /// </summary>
    public async Task AssertLedgerInvariantsAsync()
    {
        var unbalanced = await ScalarAsync("""
            SELECT count(*) FROM (
                SELECT e.id
                FROM journal_entries e LEFT JOIN postings p ON p.journal_entry_id = e.id
                GROUP BY e.id
                HAVING count(p.id) < 2
                    OR COALESCE(SUM(CASE WHEN p.direction = 'Debit' THEN p.amount_minor ELSE -p.amount_minor END), 0) <> 0
            ) bad
            """);
        Assert.Equal(0, unbalanced);

        var globalDifference = await ScalarAsync(
            "SELECT COALESCE(SUM(CASE WHEN direction = 'Debit' THEN amount_minor ELSE -amount_minor END), 0) FROM postings");
        Assert.Equal(0, globalDifference);

        var projectionMismatches = await ScalarAsync("""
            SELECT count(*) FROM (
                SELECT la.id
                FROM ledger_accounts la LEFT JOIN postings p ON p.ledger_account_id = la.id
                GROUP BY la.id, la.balance_minor
                HAVING la.balance_minor <> COALESCE(SUM(CASE WHEN p.direction = la.normal_balance THEN p.amount_minor ELSE -p.amount_minor END), 0)
            ) bad
            """);
        Assert.Equal(0, projectionMismatches);

        var negativeWallets = await ScalarAsync(
            "SELECT count(*) FROM wallets w JOIN ledger_accounts la ON la.id = w.ledger_account_id WHERE la.balance_minor < 0");
        Assert.Equal(0, negativeWallets);

        var malformedTransfers = await ScalarAsync("""
            SELECT count(*) FROM transfers t
            JOIN journal_entries e ON e.id = t.journal_entry_id
            WHERE e.entry_type <> 'Transfer'
               OR (SELECT count(*) FROM postings p WHERE p.journal_entry_id = e.id) <> 2
            """);
        Assert.Equal(0, malformedTransfers);
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _server.DropDatabaseAsync(Name);
    }
}
