using System.Net;
using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Reconciliation;
using LedgerCore.Application.Reversals;
using LedgerCore.Application.Transfers;
using LedgerCore.TestSupport;
using Xunit;

namespace LedgerCore.IntegrationTests;

/// <summary>Scenario E: corrections are new compensating entries, never edits of history.</summary>
public sealed class ReversalTests(PostgresServer server) : ApiTestBase(server)
{
    [Fact]
    public async Task A_reversal_appends_a_compensating_entry_and_leaves_the_original_untouched()
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        var transfer = await TransferAsync(alice, bob, 4_000);
        var originalPostings = await PostingSnapshotAsync(transfer.JournalEntryId);

        using var response = await ReverseAsync(transfer.TransferId, "rev-key-0000001");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var reversal = await LedgerTestClient.ReadAsync<ReversalResponse>(response);
        Assert.Equal(transfer.JournalEntryId, reversal.OriginalJournalEntryId);
        Assert.Equal(10_000, await Db.WalletBalanceAsync(alice.WalletId));
        Assert.Equal(0, await Db.WalletBalanceAsync(bob.WalletId));
        Assert.Equal(originalPostings, await PostingSnapshotAsync(transfer.JournalEntryId));
        Assert.Equal(1, await Db.ScalarAsync(
            "SELECT count(*) FROM journal_entries WHERE entry_type = 'Reversal' AND reverses_journal_entry_id = @id",
            ("id", transfer.JournalEntryId)));

        using var view = await Client.SendAsync(HttpMethod.Get, $"/api/transfers/{transfer.TransferId}", alice.Token);
        Assert.NotNull((await LedgerTestClient.ReadAsync<TransferView>(view)).Reversal);
        await Db.AssertLedgerInvariantsAsync();
    }

    [Fact]
    public async Task A_transfer_can_be_reversed_only_once()
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        var transfer = await TransferAsync(alice, bob, 4_000);
        using var first = await ReverseAsync(transfer.TransferId, "rev-once-000001");

        using var replay = await ReverseAsync(transfer.TransferId, "rev-once-000001");
        using var second = await ReverseAsync(transfer.TransferId, "rev-once-000002");

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("TRANSFER_ALREADY_REVERSED", await LedgerTestClient.ErrorCodeAsync(second));
        Assert.Equal(1, await Db.CountAsync("transfer_reversals"));
        await Db.AssertLedgerInvariantsAsync();
    }

    [Fact]
    public async Task A_reversal_is_refused_when_the_receiver_already_spent_the_money()
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        var carol = await Client.CreateFundedWalletAsync(0);
        var transfer = await TransferAsync(alice, bob, 4_000);
        using (var spend = await Client.TransferAsync(bob.Token, bob.WalletId, carol.WalletId, 3_500, "rev-spend-00001"))
        {
            Assert.Equal(HttpStatusCode.Created, spend.StatusCode);
        }

        using var response = await ReverseAsync(transfer.TransferId, "rev-spent-00001");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("REVERSAL_INSUFFICIENT_FUNDS", await LedgerTestClient.ErrorCodeAsync(response));
        Assert.Equal(500, await Db.WalletBalanceAsync(bob.WalletId));
        await Db.AssertLedgerInvariantsAsync();
    }

    [Fact]
    public async Task A_reason_is_required()
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        var transfer = await TransferAsync(alice, bob, 100);

        using var response = await Client.SendAsync(
            HttpMethod.Post, $"/api/transfers/{transfer.TransferId}/reversals", Client.OperatorToken(), new { reason = "  " }, "rev-reason-0001");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<TransferResponse> TransferAsync(TestWallet from, TestWallet to, long amount)
    {
        using var response = await Client.TransferAsync(from.Token, from.WalletId, to.WalletId, amount, $"rev-transfer-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await LedgerTestClient.ReadAsync<TransferResponse>(response);
    }

    private Task<HttpResponseMessage> ReverseAsync(Guid transferId, string key) =>
        Client.SendAsync(HttpMethod.Post, $"/api/transfers/{transferId}/reversals", Client.OperatorToken(), new { reason = "customer dispute" }, key);

    private async Task<string> PostingSnapshotAsync(Guid entryId)
    {
        await using var command = Db.DataSource.CreateCommand(
            "SELECT string_agg(id || ':' || ledger_account_id || ':' || direction || ':' || amount_minor, ',' ORDER BY id) FROM postings WHERE journal_entry_id = @id");
        command.Parameters.AddWithValue("id", entryId);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}

public sealed class ReconciliationTests(PostgresServer server) : ApiTestBase(server)
{
    [Fact]
    [Trait("Challenge", "10")]
    public async Task Balances_rebuilt_from_the_journal_match_the_cached_balances()
    {
        var alice = await Client.CreateFundedWalletAsync(50_000);
        var bob = await Client.CreateFundedWalletAsync(20_000);
        for (var i = 0; i < 5; i++)
        {
            using var t1 = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 1_000 + i, $"recon-ab-{i:D8}");
            using var t2 = await Client.TransferAsync(bob.Token, bob.WalletId, alice.WalletId, 700 + i, $"recon-ba-{i:D8}");
        }

        using var response = await Client.SendAsync(HttpMethod.Post, "/api/admin/reconciliation", Client.OperatorToken());
        var report = await LedgerTestClient.ReadAsync<ReconciliationReport>(response);

        Assert.True(report.IsConsistent);
        Assert.Empty(report.Discrepancies);
        Assert.Equal(report.TotalDebitsMinor, report.TotalCreditsMinor);
        Assert.Equal(await Db.WalletJournalBalanceAsync(alice.WalletId), await Db.WalletBalanceAsync(alice.WalletId));
        Assert.Equal(await Db.WalletJournalBalanceAsync(bob.WalletId), await Db.WalletBalanceAsync(bob.WalletId));
        Assert.Equal(70_000, await Db.WalletBalanceAsync(alice.WalletId) + await Db.WalletBalanceAsync(bob.WalletId));
    }

    [Fact]
    [Trait("Challenge", "11")]
    public async Task A_tampered_cached_balance_is_detected_and_the_wallet_is_frozen()
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(0);

        // Someone "adds" 5 000 øre directly in the database, bypassing the journal.
        await Db.ExecuteAsync(
            "UPDATE ledger_accounts SET balance_minor = balance_minor + 5000 WHERE id = (SELECT ledger_account_id FROM wallets WHERE id = @id)",
            ("id", alice.WalletId));

        using var response = await Client.SendAsync(HttpMethod.Post, "/api/admin/reconciliation", Client.OperatorToken());
        var report = await LedgerTestClient.ReadAsync<ReconciliationReport>(response);

        Assert.False(report.IsConsistent);
        var discrepancy = Assert.Single(report.Discrepancies);
        Assert.Equal(alice.WalletId, discrepancy.WalletId);
        Assert.Equal(15_000, discrepancy.CachedBalanceMinor);
        Assert.Equal(10_000, discrepancy.JournalBalanceMinor);
        Assert.Equal([alice.WalletId], report.FrozenWalletIds);

        // The invented money cannot be spent: the wallet is frozen until an operator investigates.
        using var spend = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 12_000, "recon-frozen-001");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, spend.StatusCode);
        Assert.Equal("WALLET_NOT_ACTIVE", await LedgerTestClient.ErrorCodeAsync(spend));
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM audit_events WHERE action = 'wallet.freeze'"));
    }

    [Fact]
    public async Task Freezing_can_be_disabled_for_a_report_only_run()
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        await Db.ExecuteAsync(
            "UPDATE ledger_accounts SET balance_minor = balance_minor - 1 WHERE id = (SELECT ledger_account_id FROM wallets WHERE id = @id)",
            ("id", alice.WalletId));

        using var response = await Client.SendAsync(HttpMethod.Post, "/api/admin/reconciliation?freezeAffectedWallets=false", Client.OperatorToken());
        var report = await LedgerTestClient.ReadAsync<ReconciliationReport>(response);

        Assert.Single(report.Discrepancies);
        Assert.Empty(report.FrozenWalletIds);
        Assert.Equal(0, await Db.ScalarAsync("SELECT count(*) FROM wallets WHERE status = 'Frozen'"));
    }
}
