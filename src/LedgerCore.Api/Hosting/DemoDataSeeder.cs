using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Funding;
using LedgerCore.Application.Idempotency;
using LedgerCore.Application.Security;
using LedgerCore.Domain.Common;
using LedgerCore.Domain.Monetary;

namespace LedgerCore.Api.Hosting;

/// <summary>
/// Well-known demo identities so a reviewer can start calling the API immediately. Development only.
/// Opening balances are created through the normal funding use case (balanced journal entries with
/// fixed idempotency keys), so seeding twice changes nothing.
/// </summary>
public static class DemoData
{
    public sealed record DemoCustomer(string Name, Guid CustomerId, Guid WalletId, long OpeningBalanceMinor);

    public static readonly DemoCustomer Alice = new(
        "Alice", Guid.Parse("00000000-0000-4000-a000-00000000a11c"), Guid.Parse("10000000-0000-4000-a000-00000000a11c"), 100_000);

    public static readonly DemoCustomer Bob = new(
        "Bob", Guid.Parse("00000000-0000-4000-a000-000000000b0b"), Guid.Parse("10000000-0000-4000-a000-000000000b0b"), 50_000);

    public static readonly DemoCustomer Carol = new(
        "Carol", Guid.Parse("00000000-0000-4000-a000-00000000ca01"), Guid.Parse("10000000-0000-4000-a000-00000000ca01"), 0);

    public static readonly IReadOnlyList<DemoCustomer> Customers = [Alice, Bob, Carol];

    /// <summary>Subject to use when requesting an operator token from POST /api/dev/tokens.</summary>
    public const string OperatorSubject = "demo-operator";
}

public sealed class DemoDataSeeder(
    ILedgerReadStore readStore,
    IWalletRegistry registry,
    FundingService funding,
    TimeProvider clock,
    ILogger<DemoDataSeeder> logger)
{
    private static readonly CallerContext SeedOperator =
        new("system:demo-seed", null, new HashSet<string> { Roles.LedgerOperator }, "demo-seed");

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var customer in DemoData.Customers)
        {
            var walletId = new WalletId(customer.WalletId);
            if (await readStore.GetWalletAsync(walletId, cancellationToken) is null)
            {
                var created = await registry.CreateWalletAsync(
                    new NewWalletRequest(
                        walletId, LedgerAccountId.New(), new CustomerId(customer.CustomerId), customer.Name, "Main",
                        Currency.Nok, MaxWalletsPerCustomer: 10, SeedOperator.Subject, SeedOperator.CorrelationId, clock.GetUtcNow()),
                    cancellationToken);
                if (created.IsFailure)
                {
                    throw new InvalidOperationException($"Seeding wallet for {customer.Name} failed: {created.Error}");
                }
            }

            if (customer.OpeningBalanceMinor > 0)
            {
                var key = IdempotencyKey.Parse($"demo-seed-opening-balance-{customer.Name.ToLowerInvariant()}").Value;
                var funded = await funding.FundAsync(
                    new FundWalletCommand(customer.WalletId, customer.OpeningBalanceMinor, Currency.Nok.Code, $"Demo opening balance for {customer.Name}"),
                    SeedOperator, key, cancellationToken);
                if (funded.Result.IsFailure)
                {
                    throw new InvalidOperationException($"Seeding funds for {customer.Name} failed: {funded.Result.Error}");
                }
            }
        }

        logger.LogInformation("Demo data ready: Alice, Bob and Carol wallets exist and are funded");
    }
}
