using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Common;
using LedgerCore.Application.Security;
using LedgerCore.Domain.Common;
using LedgerCore.Domain.Monetary;

namespace LedgerCore.Application.Wallets;

public sealed record CreateWalletCommand(string? Name, string? Currency);

public sealed class WalletOptions
{
    public const string SectionName = "Wallets";

    public int MaxWalletsPerCustomer { get; set; } = 10;
}

/// <summary>Wallet creation and the authorization-aware read side of the API.</summary>
public sealed class WalletService(
    IWalletRegistry registry,
    ILedgerReadStore readStore,
    IAuditLog auditLog,
    TimeProvider clock,
    Microsoft.Extensions.Options.IOptions<WalletOptions> options)
{
    public const int MaxNameLength = 60;
    public const int MaxPageSize = 200;

    public async Task<Result<WalletView>> CreateAsync(
        CreateWalletCommand command, CallerContext caller, string? callerDisplayName, CancellationToken cancellationToken)
    {
        if (!caller.IsCustomer || caller.CustomerId is not { } customerId)
        {
            return ApplicationErrors.CustomerRoleRequired;
        }

        var name = (command.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
        {
            return ApplicationErrors.Validation($"name must be 1-{MaxNameLength} printable characters.");
        }

        var currency = Currency.Parse(command.Currency);
        if (currency.IsFailure)
        {
            return currency.Error;
        }

        var displayName = string.IsNullOrWhiteSpace(callerDisplayName) ? "Customer" : callerDisplayName.Trim();
        if (displayName.Length > 100 || displayName.Any(char.IsControl))
        {
            displayName = "Customer";
        }

        return await registry.CreateWalletAsync(
            new NewWalletRequest(
                WalletId.New(),
                LedgerAccountId.New(),
                customerId,
                displayName,
                name,
                currency.Value,
                options.Value.MaxWalletsPerCustomer,
                caller.Subject,
                caller.CorrelationId,
                clock.GetUtcNow()),
            cancellationToken);
    }

    /// <summary>
    /// Returns 404 (not 403) for wallets the caller may not see, so wallet ids cannot be probed.
    /// The denied attempt is still audited.
    /// </summary>
    public async Task<Result<WalletView>> GetAsync(WalletId walletId, CallerContext caller, CancellationToken cancellationToken)
    {
        var wallet = await readStore.GetWalletAsync(walletId, cancellationToken);
        if (wallet is null)
        {
            return LedgerErrors.WalletNotFound(walletId);
        }

        if (!WalletAccessPolicy.CanView(caller, new CustomerId(wallet.CustomerId)))
        {
            await auditLog.AppendAsync(
                Audit.Event(caller, AuditActions.WalletRead, AuditOutcome.Denied, "wallet", walletId.ToString(), clock.GetUtcNow(),
                    ("reason", "wallet is not owned by the caller")),
                cancellationToken);
            return LedgerErrors.WalletNotFound(walletId);
        }

        return wallet;
    }

    public async Task<Result<IReadOnlyList<WalletView>>> ListMineAsync(CallerContext caller, CancellationToken cancellationToken)
    {
        if (!caller.IsCustomer || caller.CustomerId is not { } customerId)
        {
            return ApplicationErrors.CustomerRoleRequired;
        }

        return Result<IReadOnlyList<WalletView>>.Success(await readStore.ListWalletsAsync(customerId, cancellationToken));
    }

    public async Task<Result<IReadOnlyList<WalletTransactionView>>> GetTransactionsAsync(
        WalletId walletId, CallerContext caller, int limit, long? beforePostingId, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MaxPageSize)
        {
            return ApplicationErrors.Validation($"limit must be between 1 and {MaxPageSize}.");
        }

        var wallet = await GetAsync(walletId, caller, cancellationToken);
        if (wallet.IsFailure)
        {
            return wallet.Error;
        }

        return Result<IReadOnlyList<WalletTransactionView>>.Success(
            await readStore.GetWalletTransactionsAsync(walletId, limit, beforePostingId, cancellationToken));
    }

    /// <summary>Visible to the sending customer, the receiving customer and operators.</summary>
    public async Task<Result<TransferView>> GetTransferAsync(TransferId transferId, CallerContext caller, CancellationToken cancellationToken)
    {
        var transfer = await readStore.GetTransferAsync(transferId, cancellationToken);
        if (transfer is null)
        {
            return ApplicationErrors.TransferNotFound(transferId.Value);
        }

        var participant = caller.IsCustomer &&
            (caller.CustomerId == new CustomerId(transfer.FromCustomerId) || caller.CustomerId == new CustomerId(transfer.ToCustomerId));
        return caller.IsOperator || participant
            ? transfer
            : ApplicationErrors.TransferNotFound(transferId.Value);
    }

    public async Task<Result<JournalEntryView>> GetJournalEntryAsync(JournalEntryId entryId, CallerContext caller, CancellationToken cancellationToken)
    {
        if (!caller.IsOperator)
        {
            return ApplicationErrors.OperatorRoleRequired;
        }

        var entry = await readStore.GetJournalEntryAsync(entryId, cancellationToken);
        return entry is null ? ApplicationErrors.JournalEntryNotFound(entryId.Value) : entry;
    }

    public async Task<Result<IReadOnlyList<JournalEntryView>>> ListJournalEntriesAsync(
        CallerContext caller, int limit, Guid? beforeEntryId, CancellationToken cancellationToken)
    {
        if (!caller.IsOperator)
        {
            return ApplicationErrors.OperatorRoleRequired;
        }

        if (limit is < 1 or > MaxPageSize)
        {
            return ApplicationErrors.Validation($"limit must be between 1 and {MaxPageSize}.");
        }

        return Result<IReadOnlyList<JournalEntryView>>.Success(
            await readStore.ListJournalEntriesAsync(limit, beforeEntryId, cancellationToken));
    }
}
