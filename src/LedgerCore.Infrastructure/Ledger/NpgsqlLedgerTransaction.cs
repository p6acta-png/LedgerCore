using System.Diagnostics;
using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Common;
using LedgerCore.Application.Idempotency;
using LedgerCore.Domain.Common;
using LedgerCore.Domain.Ledger;
using LedgerCore.Domain.Monetary;
using LedgerCore.Domain.Wallets;
using Npgsql;
using NpgsqlTypes;

namespace LedgerCore.Infrastructure.Ledger;

/// <summary>
/// All SQL executed inside a ledger transaction. Every statement is parameterized; no value is ever
/// concatenated into SQL text. Created per attempt by <see cref="NpgsqlLedgerTransactionRunner"/>.
/// </summary>
internal sealed class NpgsqlLedgerTransaction(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    IFailureInjector failureInjector) : ILedgerTransaction
{
    public async Task<IdempotencyClaim> ClaimIdempotencyKeyAsync(
        IdempotencyScope scope, string requestHash, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        // If another in-flight transaction already inserted this key, PostgreSQL makes this INSERT wait
        // until that transaction commits or rolls back. Commit -> conflict (0 rows); rollback -> we insert.
        // This is what serializes concurrent duplicates, across any number of API instances.
        await using (var insert = Command("""
            INSERT INTO idempotency_records (principal_id, operation, idempotency_key, request_hash, created_at, expires_at)
            VALUES (@principal_id, @operation, @idempotency_key, @request_hash, @now, @expires_at)
            ON CONFLICT (principal_id, operation, idempotency_key) DO NOTHING
            """))
        {
            AddScope(insert, scope);
            insert.Parameters.AddWithValue("request_hash", requestHash);
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.AddWithValue("expires_at", expiresAt);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                return new IdempotencyClaim.Claimed();
            }
        }

        // READ COMMITTED: this new statement sees the row committed by the transaction we waited for.
        await using var select = Command("""
            SELECT request_hash, succeeded, response_body::text, resource_id
            FROM idempotency_records
            WHERE principal_id = @principal_id AND operation = @operation AND idempotency_key = @idempotency_key
            """);
        AddScope(select, scope);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new IdempotencyRecordVanishedException();
        }

        if (reader.IsDBNull(1) || reader.IsDBNull(2))
        {
            // Cannot happen for committed rows: a deferred trigger rejects commits of incomplete records.
            throw new InvalidOperationException($"Idempotency record for key '{scope.Key}' has no stored outcome.");
        }

        return new IdempotencyClaim.AlreadyCompleted(
            reader.GetString(0),
            new StoredOutcome(reader.GetBoolean(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3)));
    }

    public async Task CompleteIdempotencyKeyAsync(
        IdempotencyScope scope, StoredOutcome outcome, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var update = Command("""
            UPDATE idempotency_records
            SET succeeded = @succeeded, response_body = @response_body, resource_id = @resource_id, completed_at = @now
            WHERE principal_id = @principal_id AND operation = @operation AND idempotency_key = @idempotency_key
              AND succeeded IS NULL
            """);
        AddScope(update, scope);
        update.Parameters.AddWithValue("succeeded", outcome.Succeeded);
        update.Parameters.Add(new NpgsqlParameter("response_body", NpgsqlDbType.Jsonb) { Value = outcome.ResponseJson });
        update.Parameters.Add(new NpgsqlParameter("resource_id", NpgsqlDbType.Uuid) { Value = (object?)outcome.ResourceId ?? DBNull.Value });
        update.Parameters.AddWithValue("now", now);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"Idempotency record for key '{scope.Key}' was not claimed by this transaction.");
        }
    }

    public async Task<LockedLedgerState> LockAccountsAsync(
        IReadOnlyCollection<WalletId> walletIds,
        IReadOnlyCollection<LedgerAccountId> systemAccountIds,
        CancellationToken cancellationToken)
    {
        var walletGuids = walletIds.Select(id => id.Value).ToArray();

        // 1. Resolve wallet -> ledger account. The mapping is immutable, so no lock is needed for it.
        var accountIds = new List<Guid>(systemAccountIds.Select(id => id.Value));
        await using (var resolve = Command("SELECT ledger_account_id FROM wallets WHERE id = ANY(@wallet_ids)"))
        {
            resolve.Parameters.AddWithValue("wallet_ids", walletGuids);
            await using var reader = await resolve.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                accountIds.Add(reader.GetGuid(0));
            }
        }

        // 2. Lock every account row in ONE statement, ordered by id. PostgreSQL acquires the row locks in
        //    the ORDER BY order, so all transactions lock in the same global order and cannot deadlock on
        //    these rows. FOR UPDATE returns the latest committed balance even under READ COMMITTED.
        var accounts = new Dictionary<LedgerAccountId, LedgerAccount>();
        var lockStarted = Stopwatch.GetTimestamp();
        await using (var lockAccounts = Command("""
            SELECT id, code, account_type, currency, allow_negative_balance, balance_minor
            FROM ledger_accounts
            WHERE id = ANY(@account_ids)
            ORDER BY id
            FOR UPDATE
            """))
        {
            lockAccounts.Parameters.AddWithValue("account_ids", accountIds.Distinct().ToArray());
            await using var reader = await lockAccounts.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var account = new LedgerAccount(
                    new LedgerAccountId(reader.GetGuid(0)),
                    reader.GetString(1),
                    Enum.Parse<AccountType>(reader.GetString(2)),
                    Currency.FromStorage(reader.GetString(3)),
                    reader.GetBoolean(4),
                    reader.GetInt64(5));
                accounts.Add(account.Id, account);
            }
        }

        LedgerTelemetry.LockWaitDuration.Record(Stopwatch.GetElapsedTime(lockStarted).TotalMilliseconds);

        // 3. Read wallet status only now that the account locks are held. Freezing a wallet takes the same
        //    account lock first, so a freeze is either fully visible here or waits for us to finish.
        var wallets = new Dictionary<WalletId, WalletSnapshot>();
        await using (var readWallets = Command("SELECT id, customer_id, status, ledger_account_id FROM wallets WHERE id = ANY(@wallet_ids)"))
        {
            readWallets.Parameters.AddWithValue("wallet_ids", walletGuids);
            await using var reader = await readWallets.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var walletId = new WalletId(reader.GetGuid(0));
                var account = accounts[new LedgerAccountId(reader.GetGuid(3))];
                wallets.Add(walletId, new WalletSnapshot(
                    walletId,
                    new CustomerId(reader.GetGuid(1)),
                    Enum.Parse<WalletStatus>(reader.GetString(2)),
                    account));
            }
        }

        foreach (var systemAccountId in systemAccountIds)
        {
            if (!accounts.ContainsKey(systemAccountId))
            {
                throw new InvalidOperationException($"System ledger account {systemAccountId} does not exist. Were migrations applied?");
            }
        }

        return new LockedLedgerState(accounts, wallets);
    }

    public async Task InsertJournalEntryAsync(JournalEntry entry, string createdBy, string? correlationId, CancellationToken cancellationToken)
    {
        await using (var insertEntry = Command("""
            INSERT INTO journal_entries (id, entry_type, currency, description, reverses_journal_entry_id, created_by, correlation_id, created_at)
            VALUES (@id, @entry_type, @currency, @description, @reverses_journal_entry_id, @created_by, @correlation_id, @created_at)
            """))
        {
            insertEntry.Parameters.AddWithValue("id", entry.Id.Value);
            insertEntry.Parameters.AddWithValue("entry_type", entry.Type.ToString());
            insertEntry.Parameters.AddWithValue("currency", entry.Currency.Code);
            insertEntry.Parameters.AddWithValue("description", entry.Description);
            insertEntry.Parameters.Add(new NpgsqlParameter("reverses_journal_entry_id", NpgsqlDbType.Uuid)
            {
                Value = (object?)entry.ReversesEntryId?.Value ?? DBNull.Value,
            });
            insertEntry.Parameters.AddWithValue("created_by", createdBy);
            insertEntry.Parameters.Add(new NpgsqlParameter("correlation_id", NpgsqlDbType.Text) { Value = (object?)correlationId ?? DBNull.Value });
            insertEntry.Parameters.AddWithValue("created_at", entry.CreatedAt);
            await insertEntry.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var i = 0; i < entry.Postings.Count; i++)
        {
            var posting = entry.Postings[i];
            await using var insertPosting = Command("""
                INSERT INTO postings (journal_entry_id, ledger_account_id, direction, amount_minor, currency, created_at)
                VALUES (@journal_entry_id, @ledger_account_id, @direction, @amount_minor, @currency, @created_at)
                """);
            insertPosting.Parameters.AddWithValue("journal_entry_id", entry.Id.Value);
            insertPosting.Parameters.AddWithValue("ledger_account_id", posting.AccountId.Value);
            insertPosting.Parameters.AddWithValue("direction", posting.Direction.ToString());
            insertPosting.Parameters.AddWithValue("amount_minor", posting.AmountMinor);
            insertPosting.Parameters.AddWithValue("currency", entry.Currency.Code);
            insertPosting.Parameters.AddWithValue("created_at", entry.CreatedAt);
            await insertPosting.ExecuteNonQueryAsync(cancellationToken);

            if (i == 0)
            {
                // Scenario D: fail between the first and second posting. Nothing is visible to anyone yet.
                await CheckpointAsync(FailurePoint.AfterFirstPosting, cancellationToken);
            }
        }
    }

    public async Task ApplyBalanceChangesAsync(IReadOnlyList<BalanceChange> changes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            // Relative update (balance + delta) on a row we hold FOR UPDATE. The CHECK constraint
            // ck_ledger_accounts_non_negative independently rejects any negative wallet balance.
            await using var update = Command("""
                UPDATE ledger_accounts
                SET balance_minor = balance_minor + @delta, version = version + 1, updated_at = @now
                WHERE id = @id
                RETURNING balance_minor
                """);
            update.Parameters.AddWithValue("delta", change.DeltaMinor);
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("id", change.AccountId.Value);
            var newBalance = await update.ExecuteScalarAsync(cancellationToken) as long?;
            if (newBalance != change.NewBalanceMinor)
            {
                throw new LedgerInvariantViolationException(
                    $"Account {change.AccountId}: database balance {newBalance} differs from expected {change.NewBalanceMinor}.");
            }
        }
    }

    public async Task InsertTransferAsync(TransferRecord transfer, CancellationToken cancellationToken)
    {
        await using var insert = Command("""
            INSERT INTO transfers (id, journal_entry_id, from_wallet_id, to_wallet_id, amount_minor, currency, description, requested_by, idempotency_key, created_at)
            VALUES (@id, @journal_entry_id, @from_wallet_id, @to_wallet_id, @amount_minor, @currency, @description, @requested_by, @idempotency_key, @created_at)
            """);
        insert.Parameters.AddWithValue("id", transfer.Id.Value);
        insert.Parameters.AddWithValue("journal_entry_id", transfer.JournalEntryId.Value);
        insert.Parameters.AddWithValue("from_wallet_id", transfer.FromWalletId.Value);
        insert.Parameters.AddWithValue("to_wallet_id", transfer.ToWalletId.Value);
        insert.Parameters.AddWithValue("amount_minor", transfer.AmountMinor);
        insert.Parameters.AddWithValue("currency", transfer.Currency);
        insert.Parameters.AddWithValue("description", transfer.Description);
        insert.Parameters.AddWithValue("requested_by", transfer.RequestedBy.Value);
        insert.Parameters.AddWithValue("idempotency_key", transfer.IdempotencyKey);
        insert.Parameters.AddWithValue("created_at", transfer.CreatedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StoredTransfer?> FindTransferAsync(TransferId transferId, CancellationToken cancellationToken)
    {
        TransferRecord? transfer = null;
        await using (var select = Command("""
            SELECT id, journal_entry_id, from_wallet_id, to_wallet_id, amount_minor, currency, description, requested_by, idempotency_key, created_at
            FROM transfers WHERE id = @id
            """))
        {
            select.Parameters.AddWithValue("id", transferId.Value);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                transfer = new TransferRecord(
                    new TransferId(reader.GetGuid(0)),
                    new JournalEntryId(reader.GetGuid(1)),
                    new WalletId(reader.GetGuid(2)),
                    new WalletId(reader.GetGuid(3)),
                    reader.GetInt64(4),
                    reader.GetString(5).Trim(),
                    reader.GetString(6),
                    new CustomerId(reader.GetGuid(7)),
                    reader.GetString(8),
                    reader.GetFieldValue<DateTimeOffset>(9));
            }
        }

        if (transfer is null)
        {
            return null;
        }

        return new StoredTransfer(transfer, await LoadJournalEntryAsync(transfer.JournalEntryId, cancellationToken));
    }

    public async Task<bool> IsTransferReversedAsync(TransferId transferId, CancellationToken cancellationToken)
    {
        await using var select = Command("SELECT EXISTS (SELECT 1 FROM transfer_reversals WHERE transfer_id = @id)");
        select.Parameters.AddWithValue("id", transferId.Value);
        return (bool)(await select.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task InsertTransferReversalAsync(TransferReversalRecord reversal, CancellationToken cancellationToken)
    {
        await using var insert = Command("""
            INSERT INTO transfer_reversals (id, transfer_id, journal_entry_id, reason, requested_by, created_at)
            VALUES (@id, @transfer_id, @journal_entry_id, @reason, @requested_by, @created_at)
            """);
        insert.Parameters.AddWithValue("id", reversal.Id);
        insert.Parameters.AddWithValue("transfer_id", reversal.TransferId.Value);
        insert.Parameters.AddWithValue("journal_entry_id", reversal.JournalEntryId.Value);
        insert.Parameters.AddWithValue("reason", reversal.Reason);
        insert.Parameters.AddWithValue("requested_by", reversal.RequestedBy);
        insert.Parameters.AddWithValue("created_at", reversal.CreatedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WalletId>> FreezeWalletsAsync(IReadOnlyCollection<WalletId> walletIds, CancellationToken cancellationToken)
    {
        var ids = walletIds.Select(id => id.Value).ToArray();
        await using (var lockAccounts = Command("""
            SELECT la.id FROM ledger_accounts la
            JOIN wallets w ON w.ledger_account_id = la.id
            WHERE w.id = ANY(@wallet_ids)
            ORDER BY la.id
            FOR UPDATE OF la
            """))
        {
            lockAccounts.Parameters.AddWithValue("wallet_ids", ids);
            await lockAccounts.ExecuteNonQueryAsync(cancellationToken);
        }

        var frozen = new List<WalletId>();
        await using var update = Command("UPDATE wallets SET status = 'Frozen' WHERE id = ANY(@wallet_ids) AND status <> 'Frozen' RETURNING id");
        update.Parameters.AddWithValue("wallet_ids", ids);
        await using var reader = await update.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            frozen.Add(new WalletId(reader.GetGuid(0)));
        }

        return frozen;
    }

    public async Task EnqueueOutboxMessageAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        await using var insert = Command("""
            INSERT INTO outbox_messages (id, message_type, payload, correlation_id, created_at, available_at, attempt_count)
            VALUES (@id, @message_type, @payload, @correlation_id, @created_at, @created_at, 0)
            """);
        insert.Parameters.AddWithValue("id", message.Id);
        insert.Parameters.AddWithValue("message_type", message.Type);
        insert.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = message.PayloadJson });
        insert.Parameters.Add(new NpgsqlParameter("correlation_id", NpgsqlDbType.Text) { Value = (object?)message.CorrelationId ?? DBNull.Value });
        insert.Parameters.AddWithValue("created_at", message.CreatedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task AppendAuditEventAsync(AuditEvent auditEvent, CancellationToken cancellationToken) =>
        AuditSql.InsertAsync(connection, transaction, auditEvent, cancellationToken);

    public async Task CheckpointAsync(FailurePoint point, CancellationToken cancellationToken)
    {
        var mode = await failureInjector.OnCheckpointAsync(point, cancellationToken);
        switch (mode)
        {
            case null:
                return;
            case FailureMode.ThrowException:
                throw new SimulatedFailureException(point);
            case FailureMode.TerminateDatabaseConnection:
                // The server kills our own backend: a genuine database-side failure. PostgreSQL aborts the
                // open transaction and Npgsql surfaces 57P01 admin_shutdown on this very command.
                await using (var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(pg_backend_pid())", connection))
                {
                    await terminate.ExecuteNonQueryAsync(cancellationToken);
                }

                throw new InvalidOperationException("pg_terminate_backend did not terminate the connection.");
            case FailureMode.KillProcess:
                ProcessKiller.KillCurrentProcess(point);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(point), mode, "Unknown failure mode.");
        }
    }

    private async Task<JournalEntry> LoadJournalEntryAsync(JournalEntryId entryId, CancellationToken cancellationToken)
    {
        EntryType type;
        Currency currency;
        string description;
        Guid? reverses;
        DateTimeOffset createdAt;
        await using (var selectEntry = Command("""
            SELECT entry_type, currency, description, reverses_journal_entry_id, created_at
            FROM journal_entries WHERE id = @id
            """))
        {
            selectEntry.Parameters.AddWithValue("id", entryId.Value);
            await using var reader = await selectEntry.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException($"Journal entry {entryId} does not exist.");
            }

            type = Enum.Parse<EntryType>(reader.GetString(0));
            currency = Currency.FromStorage(reader.GetString(1));
            description = reader.GetString(2);
            reverses = reader.IsDBNull(3) ? null : reader.GetGuid(3);
            createdAt = reader.GetFieldValue<DateTimeOffset>(4);
        }

        var postings = new List<Posting>();
        await using (var selectPostings = Command("""
            SELECT ledger_account_id, direction, amount_minor FROM postings WHERE journal_entry_id = @id ORDER BY id
            """))
        {
            selectPostings.Parameters.AddWithValue("id", entryId.Value);
            await using var reader = await selectPostings.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                postings.Add(new Posting(
                    new LedgerAccountId(reader.GetGuid(0)),
                    Enum.Parse<PostingDirection>(reader.GetString(1)),
                    reader.GetInt64(2)));
            }
        }

        return JournalEntry.FromStorage(
            entryId, type, currency, description, postings, createdAt, reverses is null ? null : new JournalEntryId(reverses.Value));
    }

    private NpgsqlCommand Command(string sql) => new(sql, connection, transaction);

    private static void AddScope(NpgsqlCommand command, IdempotencyScope scope)
    {
        command.Parameters.AddWithValue("principal_id", scope.PrincipalId);
        command.Parameters.AddWithValue("operation", scope.Operation);
        command.Parameters.AddWithValue("idempotency_key", scope.Key.Value);
    }
}
