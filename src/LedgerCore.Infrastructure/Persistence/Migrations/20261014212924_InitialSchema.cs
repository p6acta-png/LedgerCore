using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LedgerCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    resource_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                    table.CheckConstraint("ck_audit_events_outcome", "outcome IN ('Succeeded', 'Rejected', 'Denied')");
                });

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                    table.CheckConstraint("ck_customers_display_name", "char_length(display_name) >= 1");
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    principal_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    response_body = table.Column<string>(type: "jsonb", nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => new { x.principal_id, x.operation, x.idempotency_key });
                    table.CheckConstraint("ck_idempotency_records_key_length", "char_length(idempotency_key) BETWEEN 8 AND 128");
                });

            migrationBuilder.CreateTable(
                name: "journal_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reverses_journal_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_entries", x => x.id);
                    table.UniqueConstraint("ak_journal_entries_id_currency", x => new { x.id, x.currency });
                    table.CheckConstraint("ck_journal_entries_entry_type", "entry_type IN ('Transfer', 'Funding', 'Reversal')");
                    table.CheckConstraint("ck_journal_entries_reversal_link", "(entry_type = 'Reversal') = (reverses_journal_entry_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_journal_entries_journal_entries_reverses_journal_entry_id",
                        column: x => x.reverses_journal_entry_id,
                        principalTable: "journal_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ledger_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    account_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    normal_balance = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    allow_negative_balance = table.Column<bool>(type: "boolean", nullable: false),
                    balance_minor = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_accounts", x => x.id);
                    table.UniqueConstraint("ak_ledger_accounts_id_currency", x => new { x.id, x.currency });
                    table.CheckConstraint("ck_ledger_accounts_account_type", "account_type IN ('Asset', 'Liability', 'Equity', 'Revenue', 'Expense')");
                    table.CheckConstraint("ck_ledger_accounts_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_ledger_accounts_non_negative", "allow_negative_balance OR balance_minor >= 0");
                    table.CheckConstraint("ck_ledger_accounts_normal_balance", "(account_type IN ('Asset', 'Expense') AND normal_balance = 'Debit') OR (account_type IN ('Liability', 'Equity', 'Revenue') AND normal_balance = 'Credit')");
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    available_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dead_lettered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "simulated_notifications",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    delivery_count = table.Column<int>(type: "integer", nullable: false),
                    first_delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_simulated_notifications", x => x.message_id);
                });

            migrationBuilder.CreateTable(
                name: "postings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ledger_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_postings", x => x.id);
                    table.CheckConstraint("ck_postings_amount_positive", "amount_minor > 0");
                    table.CheckConstraint("ck_postings_direction", "direction IN ('Debit', 'Credit')");
                    table.ForeignKey(
                        name: "fk_postings_journal_entries_journal_entry_id_currency",
                        columns: x => new { x.journal_entry_id, x.currency },
                        principalTable: "journal_entries",
                        principalColumns: new[] { "id", "currency" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_postings_ledger_accounts_ledger_account_id_currency",
                        columns: x => new { x.ledger_account_id, x.currency },
                        principalTable: "ledger_accounts",
                        principalColumns: new[] { "id", "currency" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ledger_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallets", x => x.id);
                    table.CheckConstraint("ck_wallets_name", "char_length(name) BETWEEN 1 AND 60");
                    table.CheckConstraint("ck_wallets_status", "status IN ('Active', 'Frozen')");
                    table.ForeignKey(
                        name: "fk_wallets_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_wallets_ledger_accounts_ledger_account_id_currency",
                        columns: x => new { x.ledger_account_id, x.currency },
                        principalTable: "ledger_accounts",
                        principalColumns: new[] { "id", "currency" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transfers", x => x.id);
                    table.CheckConstraint("ck_transfers_amount_positive", "amount_minor > 0");
                    table.CheckConstraint("ck_transfers_distinct_wallets", "from_wallet_id <> to_wallet_id");
                    table.ForeignKey(
                        name: "fk_transfers_journal_entries_journal_entry_id",
                        column: x => x.journal_entry_id,
                        principalTable: "journal_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transfers_wallets_from_wallet_id",
                        column: x => x.from_wallet_id,
                        principalTable: "wallets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transfers_wallets_to_wallet_id",
                        column: x => x.to_wallet_id,
                        principalTable: "wallets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transfer_reversals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transfer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requested_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transfer_reversals", x => x.id);
                    table.ForeignKey(
                        name: "fk_transfer_reversals_journal_entries_journal_entry_id",
                        column: x => x.journal_entry_id,
                        principalTable: "journal_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transfer_reversals_transfers_transfer_id",
                        column: x => x.transfer_id,
                        principalTable: "transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_occurred_at",
                table: "audit_events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_resource_type_resource_id",
                table: "audit_events",
                columns: new[] { "resource_type", "resource_id" });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_expires_at",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_created_at",
                table: "journal_entries",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_reverses_journal_entry_id",
                table: "journal_entries",
                column: "reverses_journal_entry_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ledger_accounts_code",
                table: "ledger_accounts",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                table: "outbox_messages",
                column: "available_at",
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_postings_journal_entry_id_currency",
                table: "postings",
                columns: new[] { "journal_entry_id", "currency" });

            migrationBuilder.CreateIndex(
                name: "ix_postings_ledger_account_id_currency",
                table: "postings",
                columns: new[] { "ledger_account_id", "currency" });

            migrationBuilder.CreateIndex(
                name: "ix_postings_ledger_account_id_id",
                table: "postings",
                columns: new[] { "ledger_account_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_transfer_reversals_journal_entry_id",
                table: "transfer_reversals",
                column: "journal_entry_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transfer_reversals_transfer_id",
                table: "transfer_reversals",
                column: "transfer_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transfers_from_wallet_id",
                table: "transfers",
                column: "from_wallet_id");

            migrationBuilder.CreateIndex(
                name: "ix_transfers_journal_entry_id",
                table: "transfers",
                column: "journal_entry_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transfers_to_wallet_id",
                table: "transfers",
                column: "to_wallet_id");

            migrationBuilder.CreateIndex(
                name: "ix_wallets_customer_id",
                table: "wallets",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_wallets_ledger_account_id",
                table: "wallets",
                column: "ledger_account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wallets_ledger_account_id_currency",
                table: "wallets",
                columns: new[] { "ledger_account_id", "currency" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "postings");

            migrationBuilder.DropTable(
                name: "simulated_notifications");

            migrationBuilder.DropTable(
                name: "transfer_reversals");

            migrationBuilder.DropTable(
                name: "transfers");

            migrationBuilder.DropTable(
                name: "journal_entries");

            migrationBuilder.DropTable(
                name: "wallets");

            migrationBuilder.DropTable(
                name: "customers");

            migrationBuilder.DropTable(
                name: "ledger_accounts");
        }
    }
}
