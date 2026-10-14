using LedgerCore.Domain.Ledger;
using LedgerCore.Infrastructure.Persistence;
using LedgerCore.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LedgerCore.IntegrationTests;

/// <summary>
/// The database itself enforces the ledger rules, independent of the C# code. These tests bypass the
/// application entirely and attack the schema with raw SQL.
/// </summary>
public sealed class SchemaIntegrityTests(PostgresServer server) : ApiTestBase(server)
{
    private static readonly Guid FundingAccountId = ChartOfAccounts.FundingExpenseNokId.Value;

    [Fact]
    public async Task Migrations_are_fully_applied_and_the_system_account_exists()
    {
        await using var db = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(Db.ConnectionString).UseSnakeCaseNamingConvention().Options);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM ledger_accounts WHERE code = '5900-SIM-FUNDING-NOK' AND account_type = 'Expense'"));
        Assert.True(await Db.ScalarAsync("SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal") >= 17);
    }

    [Fact]
    [Trait("Challenge", "08")]
    public async Task An_unbalanced_journal_entry_is_rejected_at_commit()
    {
        await using var connection = await Db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var entryId = Guid.NewGuid();
        await InsertEntryAsync(connection, transaction, entryId);
        await InsertPostingAsync(connection, transaction, entryId, "Debit", 100);

        // The constraint trigger is DEFERRED: the INSERTs succeed, the COMMIT fails.
        var error = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());

        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, error.SqlState);
        Assert.Contains("unbalanced", error.MessageText, StringComparison.Ordinal);
        Assert.Equal(0, await Db.CountAsync("journal_entries"));
    }

    [Fact]
    [Trait("Challenge", "08")]
    public async Task An_entry_with_no_postings_is_rejected_at_commit()
    {
        await using var connection = await Db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertEntryAsync(connection, transaction, Guid.NewGuid());

        var error = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());

        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, error.SqlState);
    }

    [Fact]
    [Trait("Challenge", "08")]
    public async Task A_posting_added_later_to_a_committed_entry_is_rejected()
    {
        var entryId = Guid.NewGuid();
        await using (var connection = await Db.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await InsertEntryAsync(connection, transaction, entryId);
            await InsertPostingAsync(connection, transaction, entryId, "Debit", 100);
            await InsertPostingAsync(connection, transaction, entryId, "Credit", 100);
            await transaction.CommitAsync();
        }

        await using (var connection = await Db.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await InsertPostingAsync(connection, transaction, entryId, "Credit", 1);
            await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
        }

        Assert.Equal(2, await Db.CountAsync("postings"));
    }

    [Theory]
    [Trait("Challenge", "14")]
    [InlineData("UPDATE postings SET amount_minor = 1")]
    [InlineData("DELETE FROM postings")]
    [InlineData("TRUNCATE postings")]
    [InlineData("UPDATE journal_entries SET description = 'rewritten history'")]
    [InlineData("DELETE FROM journal_entries")]
    [InlineData("DELETE FROM audit_events")]
    [InlineData("UPDATE transfers SET amount_minor = 1")]
    public async Task Committed_history_cannot_be_modified_or_deleted(string sql)
    {
        // Real history created through the API, so every targeted table has rows to attack.
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        using var transfer = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 1_000, "schema-history-1");
        Assert.Equal(System.Net.HttpStatusCode.Created, transfer.StatusCode);
        var postingsBefore = await Db.CountAsync("postings");
        var transfersBefore = await Db.CountAsync("transfers");

        var error = await Assert.ThrowsAsync<PostgresException>(() => Db.ExecuteAsync(sql));

        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, error.SqlState);
        Assert.Contains("append-only", error.MessageText, StringComparison.Ordinal);
        Assert.Equal(postingsBefore, await Db.CountAsync("postings"));
        Assert.Equal(transfersBefore, await Db.CountAsync("transfers"));
        await Db.AssertLedgerInvariantsAsync();
    }

    [Fact]
    public async Task A_wallet_balance_can_never_be_negative_even_through_raw_sql()
    {
        var wallet = await Client.CreateFundedWalletAsync(1_000);

        var error = await Assert.ThrowsAsync<PostgresException>(() => Db.ExecuteAsync(
            "UPDATE ledger_accounts SET balance_minor = -1 WHERE id = (SELECT ledger_account_id FROM wallets WHERE id = @id)",
            ("id", wallet.WalletId)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_ledger_accounts_non_negative", error.ConstraintName);
    }

    [Fact]
    public async Task Account_definitions_are_immutable()
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => Db.ExecuteAsync(
            "UPDATE ledger_accounts SET allow_negative_balance = true WHERE id = @id", ("id", FundingAccountId)));

        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, error.SqlState);
    }

    [Fact]
    public async Task A_posting_cannot_use_a_different_currency_than_its_account()
    {
        await using var connection = await Db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var entryId = Guid.NewGuid();
        await using (var insertEntry = new NpgsqlCommand(
            "INSERT INTO journal_entries (id, entry_type, currency, description, created_by, created_at) VALUES (@id, 'Funding', 'SEK', 'x', 'test', now())",
            connection, transaction))
        {
            insertEntry.Parameters.AddWithValue("id", entryId);
            await insertEntry.ExecuteNonQueryAsync();
        }

        await using var insertPosting = new NpgsqlCommand(
            "INSERT INTO postings (journal_entry_id, ledger_account_id, direction, amount_minor, currency, created_at) VALUES (@entry, @account, 'Debit', 1, 'SEK', now())",
            connection, transaction);
        insertPosting.Parameters.AddWithValue("entry", entryId);
        insertPosting.Parameters.AddWithValue("account", FundingAccountId);

        var error = await Assert.ThrowsAsync<PostgresException>(() => insertPosting.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    private static async Task InsertEntryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid entryId)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO journal_entries (id, entry_type, currency, description, created_by, created_at) VALUES (@id, 'Funding', 'NOK', 'raw sql', 'test', now())",
            connection, transaction);
        command.Parameters.AddWithValue("id", entryId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertPostingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid entryId, string direction, long amount)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO postings (journal_entry_id, ledger_account_id, direction, amount_minor, currency, created_at) VALUES (@entry, @account, @direction, @amount, 'NOK', now())",
            connection, transaction);
        command.Parameters.AddWithValue("entry", entryId);
        command.Parameters.AddWithValue("account", FundingAccountId);
        command.Parameters.AddWithValue("direction", direction);
        command.Parameters.AddWithValue("amount", amount);
        await command.ExecuteNonQueryAsync();
    }
}
