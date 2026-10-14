using System.Net;
using LedgerCore.Application.Abstractions;
using LedgerCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LedgerCore.ConcurrencyTests;

/// <summary>Scenario C: two requests racing to spend the same balance.</summary>
public sealed class OverspendTests(PostgresServer server) : ApiTestBase(server)
{
    private readonly CheckpointGate _gate = new(FailurePoint.AfterAccountsLocked);

    protected override LedgerApiFactory CreateFactory(TestDatabase db) =>
        new(db.ConnectionString, configureServices: services => services.Replace(ServiceDescriptor.Singleton<IFailureInjector>(_gate)));

    [Fact]
    [Trait("Challenge", "02")]
    public async Task Mandatory_two_concurrent_800_NOK_transfers_from_1000_NOK_exactly_one_succeeds()
    {
        const int rounds = 15;
        for (var round = 0; round < rounds; round++)
        {
            var a = await Client.CreateFundedWalletAsync(100_000, "A");
            var b = await Client.CreateFundedWalletAsync(0, "B");
            var c = await Client.CreateFundedWalletAsync(0, "C");

            var responses = await StartGate.RunConcurrentlyAsync(2, i =>
                Client.TransferAsync(a.Token, a.WalletId, i == 0 ? b.WalletId : c.WalletId, 80_000, $"overspend-{round:D3}-{i}"));

            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
            var loser = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
            Assert.Equal("INSUFFICIENT_FUNDS", await LedgerTestClient.ErrorCodeAsync(loser));
            Assert.Equal(20_000, await Db.WalletBalanceAsync(a.WalletId));
            Assert.Equal(80_000, await Db.WalletBalanceAsync(b.WalletId) + await Db.WalletBalanceAsync(c.WalletId));
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        Assert.Equal(rounds, await Db.CountAsync("transfers"));
        await Db.AssertLedgerInvariantsAsync();
    }

    [Fact]
    [Trait("Challenge", "02")]
    public async Task The_second_spend_blocks_on_the_row_lock_then_sees_the_committed_balance()
    {
        var a = await Client.CreateFundedWalletAsync(100_000, "A");
        var b = await Client.CreateFundedWalletAsync(0, "B");
        var c = await Client.CreateFundedWalletAsync(0, "C");

        // Request 1 acquires the row lock on A's account and is frozen right there, before deciding.
        _gate.Arm();
        var first = Client.TransferAsync(a.Token, a.WalletId, b.WalletId, 80_000, "lock-first-00001");
        await _gate.Reached.WaitAsync(TimeSpan.FromSeconds(15));

        // Request 2 starts while request 1 holds the lock.
        var second = Client.TransferAsync(a.Token, a.WalletId, c.WalletId, 80_000, "lock-second-0001");

        // Evidence from PostgreSQL itself: a backend is waiting on a row lock for SELECT ... FOR UPDATE.
        // There is no C# lock anywhere in this path; the database is doing the serialization.
        await WaitUntilAsync(async () => await LockWaitersAsync() == 1);
        Assert.False(second.IsCompleted);

        _gate.Release();
        using var firstResponse = await first;
        using var secondResponse = await second;

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, secondResponse.StatusCode);
        Assert.Equal("INSUFFICIENT_FUNDS", await LedgerTestClient.ErrorCodeAsync(secondResponse));
        Assert.Equal(20_000, await Db.WalletBalanceAsync(a.WalletId));
        Assert.Equal(80_000, await Db.WalletBalanceAsync(b.WalletId));
        Assert.Equal(0, await Db.WalletBalanceAsync(c.WalletId));
        await Db.AssertLedgerInvariantsAsync();
    }

    private Task<long> LockWaitersAsync() => Db.ScalarAsync("""
        SELECT count(*) FROM pg_stat_activity
        WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%FOR UPDATE%'
        """);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached within 15 seconds");
            await Task.Delay(20);
        }
    }
}

/// <summary>
/// Not a test of LedgerCore code: a reproducible demonstration, on the real schema, of the lost-update
/// anomaly that the SELECT ... FOR UPDATE in NpgsqlLedgerTransaction prevents.
/// </summary>
public sealed class LostUpdateDemonstrationTests(PostgresServer server) : ApiTestBase(server)
{
    [Fact]
    public async Task Without_a_row_lock_two_read_then_write_transactions_both_spend_the_same_money()
    {
        var a = await Client.CreateFundedWalletAsync(100_000);
        var accountId = await AccountIdAsync(a.WalletId);

        await using var t1 = await Db.DataSource.OpenConnectionAsync();
        await using var t2 = await Db.DataSource.OpenConnectionAsync();
        await using var tx1 = await t1.BeginTransactionAsync();
        await using var tx2 = await t2.BeginTransactionAsync();

        // Both read 100 000 and both decide "enough money for 80 000".
        Assert.Equal(100_000, await ReadBalanceAsync(t1, tx1, accountId, forUpdate: false));
        Assert.Equal(100_000, await ReadBalanceAsync(t2, tx2, accountId, forUpdate: false));

        // Both write the absolute value they computed. The second write silently overwrites the first.
        await WriteBalanceAsync(t1, tx1, accountId, 20_000);
        await tx1.CommitAsync();
        await WriteBalanceAsync(t2, tx2, accountId, 20_000);
        await tx2.CommitAsync();

        // 160 000 was "sent" but only 80 000 left the account: 80 000 øre was invented.
        Assert.Equal(20_000, await Db.WalletBalanceAsync(a.WalletId));
    }

    [Fact]
    public async Task With_SELECT_FOR_UPDATE_the_second_reader_waits_and_sees_the_committed_balance()
    {
        var a = await Client.CreateFundedWalletAsync(100_000);
        var accountId = await AccountIdAsync(a.WalletId);

        await using var t1 = await Db.DataSource.OpenConnectionAsync();
        await using var t2 = await Db.DataSource.OpenConnectionAsync();
        await using var tx1 = await t1.BeginTransactionAsync();
        await using var tx2 = await t2.BeginTransactionAsync();

        Assert.Equal(100_000, await ReadBalanceAsync(t1, tx1, accountId, forUpdate: true));
        var secondRead = ReadBalanceAsync(t2, tx2, accountId, forUpdate: true);

        // Positive evidence that the second reader is blocked on the row lock, not just slow.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await Db.ScalarAsync("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'") != 1)
        {
            Assert.True(DateTime.UtcNow < deadline, "second FOR UPDATE never started waiting");
            await Task.Delay(20);
        }

        Assert.False(secondRead.IsCompleted, "the second FOR UPDATE must wait for the first transaction");

        await WriteBalanceAsync(t1, tx1, accountId, 20_000);
        await tx1.CommitAsync();

        // Under READ COMMITTED, FOR UPDATE re-reads the row after the lock is granted.
        Assert.Equal(20_000, await secondRead);
        await tx2.RollbackAsync();
    }

    private async Task<Guid> AccountIdAsync(Guid walletId)
    {
        await using var command = Db.DataSource.CreateCommand("SELECT ledger_account_id FROM wallets WHERE id = @id");
        command.Parameters.AddWithValue("id", walletId);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> ReadBalanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid accountId, bool forUpdate)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT balance_minor FROM ledger_accounts WHERE id = @id{(forUpdate ? " FOR UPDATE" : string.Empty)}", connection, transaction);
        command.Parameters.AddWithValue("id", accountId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WriteBalanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid accountId, long balance)
    {
        await using var command = new NpgsqlCommand("UPDATE ledger_accounts SET balance_minor = @balance WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("balance", balance);
        command.Parameters.AddWithValue("id", accountId);
        await command.ExecuteNonQueryAsync();
    }
}
