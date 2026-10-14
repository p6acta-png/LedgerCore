using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Funding;
using LedgerCore.Application.Idempotency;
using LedgerCore.Application.Reconciliation;
using LedgerCore.Application.Reversals;
using LedgerCore.Application.Transfers;
using LedgerCore.Application.Wallets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LedgerCore.Application;

public static class DependencyInjection
{
    /// <summary>Registers the use-case services. Options are bound to configuration by the host.</summary>
    public static IServiceCollection AddLedgerCoreApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IFailureInjector>(NoFailureInjector.Instance);
        services.AddOptions<IdempotencyOptions>();
        services.AddOptions<WalletOptions>();

        services.AddScoped<IdempotentCommandExecutor>();
        services.AddScoped<TransferService>();
        services.AddScoped<FundingService>();
        services.AddScoped<ReversalService>();
        services.AddScoped<WalletService>();
        services.AddScoped<ReconciliationService>();
        return services;
    }
}
