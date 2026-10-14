using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LedgerCore.Infrastructure.Persistence;

// Table definitions. Column names become snake_case through UseSnakeCaseNamingConvention(), so the raw
// SQL in CHECK constraints refers to snake_case names. Every foreign key uses RESTRICT: ledger history is
// never deleted, so cascades would only hide mistakes.
//
// Composite keys (id, currency) on ledger_accounts and journal_entries let postings reference both with
// (…, currency), which makes the database itself guarantee that a posting's currency matches both its
// account and its journal entry.

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<CustomerRow>
{
    public void Configure(EntityTypeBuilder<CustomerRow> b)
    {
        b.ToTable("customers", t => t.HasCheckConstraint("ck_customers_display_name", "char_length(display_name) >= 1"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.DisplayName).HasMaxLength(100);
    }
}

internal sealed class LedgerAccountConfiguration : IEntityTypeConfiguration<LedgerAccountRow>
{
    public void Configure(EntityTypeBuilder<LedgerAccountRow> b)
    {
        b.ToTable("ledger_accounts", t =>
        {
            // Last line of defence against overdrafts: even buggy application code cannot commit a
            // negative wallet balance.
            t.HasCheckConstraint("ck_ledger_accounts_non_negative", "allow_negative_balance OR balance_minor >= 0");
            t.HasCheckConstraint("ck_ledger_accounts_account_type", "account_type IN ('Asset', 'Liability', 'Equity', 'Revenue', 'Expense')");
            t.HasCheckConstraint(
                "ck_ledger_accounts_normal_balance",
                "(account_type IN ('Asset', 'Expense') AND normal_balance = 'Debit') OR (account_type IN ('Liability', 'Equity', 'Revenue') AND normal_balance = 'Credit')");
            t.HasCheckConstraint("ck_ledger_accounts_currency", "currency ~ '^[A-Z]{3}$'");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasAlternateKey(x => new { x.Id, x.Currency });
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Code).HasMaxLength(64);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.AccountType).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.NormalBalance).HasConversion<string>().HasMaxLength(8);
        b.Property(x => x.Currency).HasColumnType("character(3)");
    }
}

internal sealed class WalletConfiguration : IEntityTypeConfiguration<WalletRow>
{
    public void Configure(EntityTypeBuilder<WalletRow> b)
    {
        b.ToTable("wallets", t =>
        {
            t.HasCheckConstraint("ck_wallets_status", "status IN ('Active', 'Frozen')");
            t.HasCheckConstraint("ck_wallets_name", "char_length(name) BETWEEN 1 AND 60");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(60);
        b.Property(x => x.Currency).HasColumnType("character(3)");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.HasIndex(x => x.LedgerAccountId).IsUnique();
        b.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.LedgerAccount).WithMany()
            .HasForeignKey(x => new { x.LedgerAccountId, x.Currency })
            .HasPrincipalKey(a => new { a.Id, a.Currency })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntryRow>
{
    public void Configure(EntityTypeBuilder<JournalEntryRow> b)
    {
        b.ToTable("journal_entries", t =>
        {
            t.HasCheckConstraint("ck_journal_entries_entry_type", "entry_type IN ('Transfer', 'Funding', 'Reversal')");
            t.HasCheckConstraint("ck_journal_entries_reversal_link", "(entry_type = 'Reversal') = (reverses_journal_entry_id IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasAlternateKey(x => new { x.Id, x.Currency });
        b.Property(x => x.EntryType).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Currency).HasColumnType("character(3)");
        b.Property(x => x.Description).HasMaxLength(200);
        b.Property(x => x.CreatedBy).HasMaxLength(128);
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.HasIndex(x => x.CreatedAt);

        // An entry can be reversed at most once: unique, nullable self-reference.
        b.HasIndex(x => x.ReversesJournalEntryId).IsUnique();
        b.HasOne<JournalEntryRow>().WithMany().HasForeignKey(x => x.ReversesJournalEntryId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Postings).WithOne()
            .HasForeignKey(p => new { p.JournalEntryId, p.Currency })
            .HasPrincipalKey(e => new { e.Id, e.Currency })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PostingConfiguration : IEntityTypeConfiguration<PostingRow>
{
    public void Configure(EntityTypeBuilder<PostingRow> b)
    {
        b.ToTable("postings", t =>
        {
            t.HasCheckConstraint("ck_postings_amount_positive", "amount_minor > 0");
            t.HasCheckConstraint("ck_postings_direction", "direction IN ('Debit', 'Credit')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Direction).HasConversion<string>().HasMaxLength(8);
        b.Property(x => x.Currency).HasColumnType("character(3)");
        b.HasOne(x => x.LedgerAccount).WithMany()
            .HasForeignKey(x => new { x.LedgerAccountId, x.Currency })
            .HasPrincipalKey(a => new { a.Id, a.Currency })
            .OnDelete(DeleteBehavior.Restrict);

        // Serves wallet history (newest first) and per-account reconciliation.
        b.HasIndex(x => new { x.LedgerAccountId, x.Id });
    }
}

internal sealed class TransferConfiguration : IEntityTypeConfiguration<TransferRow>
{
    public void Configure(EntityTypeBuilder<TransferRow> b)
    {
        b.ToTable("transfers", t =>
        {
            t.HasCheckConstraint("ck_transfers_amount_positive", "amount_minor > 0");
            t.HasCheckConstraint("ck_transfers_distinct_wallets", "from_wallet_id <> to_wallet_id");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Currency).HasColumnType("character(3)");
        b.Property(x => x.Description).HasMaxLength(200);
        b.Property(x => x.IdempotencyKey).HasMaxLength(128);
        b.HasIndex(x => x.JournalEntryId).IsUnique();
        b.HasOne<JournalEntryRow>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WalletRow>().WithMany().HasForeignKey(x => x.FromWalletId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WalletRow>().WithMany().HasForeignKey(x => x.ToWalletId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TransferReversalConfiguration : IEntityTypeConfiguration<TransferReversalRow>
{
    public void Configure(EntityTypeBuilder<TransferReversalRow> b)
    {
        b.ToTable("transfer_reversals");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Reason).HasMaxLength(200);
        b.Property(x => x.RequestedBy).HasMaxLength(128);

        // A transfer can be reversed at most once, regardless of what the application believes.
        b.HasIndex(x => x.TransferId).IsUnique();
        b.HasIndex(x => x.JournalEntryId).IsUnique();
        b.HasOne<TransferRow>().WithMany().HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntryRow>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecordRow>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecordRow> b)
    {
        b.ToTable("idempotency_records", t =>
            t.HasCheckConstraint("ck_idempotency_records_key_length", "char_length(idempotency_key) BETWEEN 8 AND 128"));

        // The primary key IS the idempotency guarantee: one row per (principal, operation, key).
        b.HasKey(x => new { x.PrincipalId, x.Operation, x.IdempotencyKey });
        b.Property(x => x.PrincipalId).HasMaxLength(128);
        b.Property(x => x.Operation).HasMaxLength(64);
        b.Property(x => x.IdempotencyKey).HasMaxLength(128);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.ResponseBody).HasColumnType("jsonb");
        b.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEventRow>
{
    public void Configure(EntityTypeBuilder<AuditEventRow> b)
    {
        b.ToTable("audit_events", t => t.HasCheckConstraint("ck_audit_events_outcome", "outcome IN ('Succeeded', 'Rejected', 'Denied')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.ActorId).HasMaxLength(128);
        b.Property(x => x.Action).HasMaxLength(64);
        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.ResourceType).HasMaxLength(32);
        b.Property(x => x.ResourceId).HasMaxLength(64);
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.Property(x => x.Details).HasColumnType("jsonb");
        b.HasIndex(x => x.OccurredAt);
        b.HasIndex(x => new { x.ResourceType, x.ResourceId });
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessageRow>
{
    public void Configure(EntityTypeBuilder<OutboxMessageRow> b)
    {
        b.ToTable("outbox_messages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.MessageType).HasMaxLength(64);
        b.Property(x => x.Payload).HasColumnType("jsonb");
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.Property(x => x.LockedBy).HasMaxLength(128);
        b.Property(x => x.LastError).HasMaxLength(1000);

        // Partial index: workers only ever scan undelivered, non-dead-lettered messages.
        b.HasIndex(x => x.AvailableAt)
            .HasDatabaseName("ix_outbox_messages_pending")
            .HasFilter("processed_at IS NULL AND dead_lettered_at IS NULL");
    }
}

internal sealed class SimulatedNotificationConfiguration : IEntityTypeConfiguration<SimulatedNotificationRow>
{
    public void Configure(EntityTypeBuilder<SimulatedNotificationRow> b)
    {
        b.ToTable("simulated_notifications");
        b.HasKey(x => x.MessageId);
        b.Property(x => x.MessageId).ValueGeneratedNever();
        b.Property(x => x.MessageType).HasMaxLength(64);
        b.Property(x => x.Payload).HasColumnType("jsonb");
    }
}
