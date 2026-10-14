using LedgerCore.Domain.Common;
using LedgerCore.Domain.Monetary;

namespace LedgerCore.Application.Abstractions;

/// <summary>Creates customers and wallets (ordinary persistence, implemented with EF Core).</summary>
public interface IWalletRegistry
{
    /// <summary>
    /// Creates the customer record on first use (just-in-time provisioning from the token subject),
    /// the wallet's liability account and the wallet itself in one transaction.
    /// </summary>
    Task<Result<WalletView>> CreateWalletAsync(NewWalletRequest request, CancellationToken cancellationToken);
}

public sealed record NewWalletRequest(
    WalletId WalletId,
    LedgerAccountId LedgerAccountId,
    CustomerId OwnerId,
    string OwnerDisplayName,
    string Name,
    Currency Currency,
    int MaxWalletsPerCustomer,
    string ActorId,
    string? CorrelationId,
    DateTimeOffset Now);

/// <summary>Appends audit events outside a ledger transaction (for example, denied access attempts).</summary>
public interface IAuditLog
{
    Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}
