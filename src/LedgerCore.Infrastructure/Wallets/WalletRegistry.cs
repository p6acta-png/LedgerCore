using System.Text.Json;
using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Common;
using LedgerCore.Domain.Common;
using LedgerCore.Domain.Ledger;
using LedgerCore.Domain.Wallets;
using LedgerCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LedgerCore.Infrastructure.Wallets;

/// <summary>Wallet creation through EF Core: ordinary persistence with no money movement.</summary>
internal sealed class WalletRegistry(LedgerDbContext db) : IWalletRegistry
{
    public async Task<Result<WalletView>> CreateWalletAsync(NewWalletRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Just-in-time customer provisioning from the token subject. ON CONFLICT makes concurrent first
        // requests from the same customer safe. EF turns the interpolated values into SQL parameters.
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO customers (id, display_name, created_at) VALUES ({request.OwnerId.Value}, {request.OwnerDisplayName}, {request.Now}) ON CONFLICT (id) DO NOTHING",
            cancellationToken);

        // Serialize wallet creation per customer so concurrent requests cannot both pass the limit check.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM customers WHERE id = {request.OwnerId.Value} FOR UPDATE", cancellationToken);

        var existing = await db.Wallets.CountAsync(w => w.CustomerId == request.OwnerId.Value, cancellationToken);
        if (existing >= request.MaxWalletsPerCustomer)
        {
            return ApplicationErrors.WalletLimitReached(request.MaxWalletsPerCustomer);
        }

        var account = ChartOfAccounts.NewWalletAccount(request.LedgerAccountId, request.WalletId, request.Currency);
        db.LedgerAccounts.Add(new LedgerAccountRow
        {
            Id = account.Id.Value,
            Code = account.Code,
            Name = $"Customer wallet liability ({request.Currency.Code})",
            AccountType = account.Type,
            NormalBalance = account.NormalBalance,
            Currency = account.Currency.Code,
            AllowNegativeBalance = account.AllowNegativeBalance,
            BalanceMinor = 0,
            Version = 0,
            CreatedAt = request.Now,
            UpdatedAt = request.Now,
        });
        db.Wallets.Add(new WalletRow
        {
            Id = request.WalletId.Value,
            CustomerId = request.OwnerId.Value,
            LedgerAccountId = account.Id.Value,
            Currency = request.Currency.Code,
            Name = request.Name,
            Status = WalletStatus.Active,
            CreatedAt = request.Now,
        });
        db.AuditEvents.Add(new AuditEventRow
        {
            OccurredAt = request.Now,
            ActorId = request.ActorId,
            Action = AuditActions.WalletCreate,
            Outcome = AuditOutcome.Succeeded,
            ResourceType = "wallet",
            ResourceId = request.WalletId.ToString(),
            CorrelationId = request.CorrelationId,
            Details = JsonSerializer.Serialize(
                new Dictionary<string, string?> { ["currency"] = request.Currency.Code, ["ledgerAccountId"] = account.Id.ToString() },
                LedgerJson.Options),
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new WalletView(
            request.WalletId.Value, request.OwnerId.Value, request.Name, request.Currency.Code, nameof(WalletStatus.Active), 0,
            account.Id.Value, account.Code, request.Now);
    }
}

internal sealed class AuditLog(Npgsql.NpgsqlDataSource dataSource) : IAuditLog
{
    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await Ledger.AuditSql.InsertAsync(connection, null, auditEvent, cancellationToken);
    }
}
