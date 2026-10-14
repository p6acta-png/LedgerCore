using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LedgerCore.Infrastructure.Persistence;

/// <summary>
/// EF Core model of the ledger schema. EF owns the schema (migrations) and ordinary persistence
/// (wallet creation, read queries). The money-moving transaction itself uses explicit SQL through
/// Npgsql (see Ledger/NpgsqlLedgerTransaction.cs) so every lock and statement is visible.
/// </summary>
public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<CustomerRow> Customers => Set<CustomerRow>();

    public DbSet<LedgerAccountRow> LedgerAccounts => Set<LedgerAccountRow>();

    public DbSet<WalletRow> Wallets => Set<WalletRow>();

    public DbSet<JournalEntryRow> JournalEntries => Set<JournalEntryRow>();

    public DbSet<PostingRow> Postings => Set<PostingRow>();

    public DbSet<TransferRow> Transfers => Set<TransferRow>();

    public DbSet<TransferReversalRow> TransferReversals => Set<TransferReversalRow>();

    public DbSet<IdempotencyRecordRow> IdempotencyRecords => Set<IdempotencyRecordRow>();

    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();

    public DbSet<OutboxMessageRow> OutboxMessages => Set<OutboxMessageRow>();

    public DbSet<SimulatedNotificationRow> SimulatedNotifications => Set<SimulatedNotificationRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LedgerDbContext).Assembly);
}

/// <summary>
/// Used only by the `dotnet ef` tooling to generate migrations. No connection is opened, so the
/// connection string below is a placeholder, not a credential.
/// </summary>
public sealed class DesignTimeLedgerDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql("Host=localhost;Database=ledgercore_design_time")
            .UseSnakeCaseNamingConvention()
            .Options);
}
