using System.Data;
using LedgerCore.Application.Abstractions;
using LedgerCore.Domain.Common;
using LedgerCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LedgerCore.Infrastructure.Queries;

/// <summary>
/// Read models. Simple lookups use EF Core LINQ; the history and reconciliation queries are explicit
/// SQL because their joins and aggregates are clearer that way. Nothing here takes locks.
/// </summary>
internal sealed class LedgerReadStore(LedgerDbContext db, NpgsqlDataSource dataSource) : ILedgerReadStore
{
    public async Task<WalletView?> GetWalletAsync(WalletId walletId, CancellationToken cancellationToken) =>
        (await QueryWallets(w => w.Id == walletId.Value).ToListAsync(cancellationToken)).SingleOrDefault()?.ToView();

    public async Task<IReadOnlyList<WalletView>> ListWalletsAsync(CustomerId ownerId, CancellationToken cancellationToken) =>
        (await QueryWallets(w => w.CustomerId == ownerId.Value).ToListAsync(cancellationToken))
        .Select(w => w.ToView())
        .ToList();

    public async Task<IReadOnlyList<WalletTransactionView>> GetWalletTransactionsAsync(
        WalletId walletId, int limit, long? beforePostingId, CancellationToken cancellationToken)
    {
        // Keyset pagination on the posting identity column: stable under concurrent inserts.
        await using var command = dataSource.CreateCommand("""
            SELECT p.id, e.id, e.entry_type, p.direction, p.amount_minor,
                   CASE WHEN p.direction = la.normal_balance THEN p.amount_minor ELSE -p.amount_minor END AS signed_amount,
                   p.currency, e.description, COALESCE(t.id, r.transfer_id) AS transfer_id, p.created_at
            FROM wallets w
            JOIN ledger_accounts la ON la.id = w.ledger_account_id
            JOIN postings p ON p.ledger_account_id = la.id
            JOIN journal_entries e ON e.id = p.journal_entry_id
            LEFT JOIN transfers t ON t.journal_entry_id = e.id
            LEFT JOIN transfer_reversals r ON r.journal_entry_id = e.id
            WHERE w.id = @wallet_id AND (@before_id::bigint IS NULL OR p.id < @before_id::bigint)
            ORDER BY p.id DESC
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("wallet_id", walletId.Value);
        command.Parameters.Add(new NpgsqlParameter("before_id", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)beforePostingId ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);

        var rows = new List<WalletTransactionView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new WalletTransactionView(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetString(6).Trim(),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.GetFieldValue<DateTimeOffset>(9)));
        }

        return rows;
    }

    public async Task<TransferView?> GetTransferAsync(TransferId transferId, CancellationToken cancellationToken)
    {
        var row = await (
                from transfer in db.Transfers.AsNoTracking()
                join sender in db.Wallets on transfer.FromWalletId equals sender.Id
                join receiver in db.Wallets on transfer.ToWalletId equals receiver.Id
                where transfer.Id == transferId.Value
                select new { Transfer = transfer, FromCustomerId = sender.CustomerId, ToCustomerId = receiver.CustomerId })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var reversal = await db.TransferReversals.AsNoTracking()
            .Where(r => r.TransferId == transferId.Value)
            .Select(r => new TransferReversalView(r.Id, r.JournalEntryId, r.Reason, r.RequestedBy, r.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);

        var t = row.Transfer;
        return new TransferView(
            t.Id, t.FromWalletId, t.ToWalletId, row.FromCustomerId, row.ToCustomerId, t.AmountMinor, t.Currency.Trim(),
            t.Description, t.JournalEntryId, t.RequestedBy, t.CreatedAt, reversal);
    }

    public async Task<JournalEntryView?> GetJournalEntryAsync(JournalEntryId entryId, CancellationToken cancellationToken) =>
        (await QueryJournalEntries(db.JournalEntries.Where(e => e.Id == entryId.Value), cancellationToken)).SingleOrDefault();

    public Task<IReadOnlyList<JournalEntryView>> ListJournalEntriesAsync(int limit, Guid? beforeEntryId, CancellationToken cancellationToken)
    {
        // UUIDv7 ids sort by creation time, so they double as a keyset pagination cursor.
        var query = db.JournalEntries.AsQueryable();
        if (beforeEntryId is { } before)
        {
            query = query.Where(e => e.Id.CompareTo(before) < 0);
        }

        return QueryJournalEntries(query.OrderByDescending(e => e.Id).Take(limit), cancellationToken);
    }

    public async Task<ReconciliationSnapshot> ComputeReconciliationAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // REPEATABLE READ: all four queries see one consistent snapshot. Because every ledger transaction
        // updates postings and cached balances atomically, a correct ledger always reconciles exactly
        // within one snapshot, even while transfers are running.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
        {
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        }

        var discrepancies = new List<AccountDiscrepancy>();
        var negativeWallets = new List<Guid>();
        var accountsChecked = 0;
        await using (var perAccount = new NpgsqlCommand("""
            SELECT la.id, la.code, w.id AS wallet_id, la.allow_negative_balance, la.balance_minor AS cached_balance,
                   COALESCE(SUM(CASE WHEN p.direction = la.normal_balance THEN p.amount_minor ELSE -p.amount_minor END), 0)::bigint AS journal_balance
            FROM ledger_accounts la
            LEFT JOIN wallets w ON w.ledger_account_id = la.id
            LEFT JOIN postings p ON p.ledger_account_id = la.id
            GROUP BY la.id, la.code, w.id, la.allow_negative_balance, la.balance_minor
            ORDER BY la.code
            """, connection, transaction))
        {
            await using var reader = await perAccount.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                accountsChecked++;
                var walletId = reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2);
                var allowNegative = reader.GetBoolean(3);
                var cached = reader.GetInt64(4);
                var journal = reader.GetInt64(5);
                if (cached != journal)
                {
                    discrepancies.Add(new AccountDiscrepancy(reader.GetGuid(0), reader.GetString(1), walletId, cached, journal));
                }

                if (walletId is not null && !allowNegative && (cached < 0 || journal < 0))
                {
                    negativeWallets.Add(walletId.Value);
                }
            }
        }

        long totalDebits;
        long totalCredits;
        long entriesChecked;
        await using (var totals = new NpgsqlCommand("""
            SELECT COALESCE(SUM(amount_minor) FILTER (WHERE direction = 'Debit'), 0)::bigint,
                   COALESCE(SUM(amount_minor) FILTER (WHERE direction = 'Credit'), 0)::bigint,
                   (SELECT count(*) FROM journal_entries)
            FROM postings
            """, connection, transaction))
        {
            await using var reader = await totals.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            totalDebits = reader.GetInt64(0);
            totalCredits = reader.GetInt64(1);
            entriesChecked = reader.GetInt64(2);
        }

        var unbalanced = new List<Guid>();
        await using (var unbalancedEntries = new NpgsqlCommand("""
            SELECT e.id
            FROM journal_entries e
            LEFT JOIN postings p ON p.journal_entry_id = e.id
            GROUP BY e.id
            HAVING count(p.id) < 2
                OR COALESCE(SUM(CASE WHEN p.direction = 'Debit' THEN p.amount_minor ELSE -p.amount_minor END), 0) <> 0
            """, connection, transaction))
        {
            await using var reader = await unbalancedEntries.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                unbalanced.Add(reader.GetGuid(0));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new ReconciliationSnapshot(
            accountsChecked, entriesChecked, totalDebits, totalCredits, discrepancies, unbalanced, negativeWallets.Distinct().ToList());
    }

    private IQueryable<WalletQueryRow> QueryWallets(System.Linq.Expressions.Expression<Func<WalletRow, bool>> filter) =>
        from w in db.Wallets.AsNoTracking().Where(filter)
        join a in db.LedgerAccounts on w.LedgerAccountId equals a.Id
        orderby w.CreatedAt, w.Id
        select new WalletQueryRow(w.Id, w.CustomerId, w.Name, w.Currency, w.Status, a.BalanceMinor, a.Id, a.Code, w.CreatedAt);

    private static async Task<IReadOnlyList<JournalEntryView>> QueryJournalEntries(IQueryable<JournalEntryRow> query, CancellationToken cancellationToken)
    {
        var rows = await query.AsNoTracking()
            .Select(e => new
            {
                Entry = e,
                Postings = e.Postings.OrderBy(p => p.Id).Select(p => new
                {
                    p.Id,
                    p.LedgerAccountId,
                    AccountCode = p.LedgerAccount!.Code,
                    p.Direction,
                    p.AmountMinor,
                }).ToList(),
            })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new JournalEntryView(
                r.Entry.Id,
                r.Entry.EntryType.ToString(),
                r.Entry.Currency.Trim(),
                r.Entry.Description,
                r.Entry.ReversesJournalEntryId,
                r.Entry.CreatedBy,
                r.Entry.CorrelationId,
                r.Entry.CreatedAt,
                r.Postings.Select(p => new PostingView(p.Id, p.LedgerAccountId, p.AccountCode, p.Direction.ToString(), p.AmountMinor)).ToList()))
            .ToList();
    }

    private sealed record WalletQueryRow(
        Guid Id, Guid CustomerId, string Name, string Currency, Domain.Wallets.WalletStatus Status,
        long BalanceMinor, Guid LedgerAccountId, string LedgerAccountCode, DateTimeOffset CreatedAt)
    {
        public WalletView ToView() =>
            new(Id, CustomerId, Name, Currency.Trim(), Status.ToString(), BalanceMinor, LedgerAccountId, LedgerAccountCode, CreatedAt);
    }
}
