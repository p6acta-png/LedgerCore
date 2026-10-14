using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LedgerCore.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Hand-written integrity layer. These rules hold no matter which code path (or which human with a SQL
    /// console) writes to the database:
    ///
    ///   1. Ledger history is append-only: UPDATE, DELETE and TRUNCATE on journal_entries, postings,
    ///      transfers, transfer_reversals and audit_events are rejected.
    ///   2. Every journal entry must balance (SUM(debits) = SUM(credits), at least two postings). Checked by
    ///      DEFERRABLE INITIALLY DEFERRED constraint triggers, i.e. at COMMIT, after all postings exist.
    ///   3. An idempotency record cannot be committed without its stored outcome.
    ///   4. Account and wallet definitions are immutable; only the cached balance and wallet status change.
    ///   5. The simulated funding expense account exists (fixed id shared with ChartOfAccounts.cs).
    /// </summary>
    public partial class LedgerIntegrityRules : Migration
    {
        private static readonly string[] AppendOnlyTables =
            ["journal_entries", "postings", "transfers", "transfer_reversals", "audit_events"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION ledger_reject_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'table % is append-only: % is not allowed', TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'integrity_constraint_violation',
                              HINT = 'Correct mistakes with a compensating (reversal) journal entry.';
                END;
                $$;
                """);

            foreach (var table in AppendOnlyTables)
            {
                migrationBuilder.Sql($"""
                    CREATE TRIGGER {table}_append_only
                        BEFORE UPDATE OR DELETE ON {table}
                        FOR EACH ROW EXECUTE FUNCTION ledger_reject_mutation();
                    CREATE TRIGGER {table}_no_truncate
                        BEFORE TRUNCATE ON {table}
                        FOR EACH STATEMENT EXECUTE FUNCTION ledger_reject_mutation();
                    """);
            }

            migrationBuilder.Sql("""
                CREATE FUNCTION ledger_assert_entry_balanced() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    v_entry_id uuid;
                    v_postings bigint;
                    v_debits   numeric;
                    v_credits  numeric;
                BEGIN
                    IF TG_TABLE_NAME = 'postings' THEN
                        v_entry_id := NEW.journal_entry_id;
                    ELSE
                        v_entry_id := NEW.id;
                    END IF;

                    SELECT count(*),
                           COALESCE(SUM(amount_minor) FILTER (WHERE direction = 'Debit'), 0),
                           COALESCE(SUM(amount_minor) FILTER (WHERE direction = 'Credit'), 0)
                      INTO v_postings, v_debits, v_credits
                      FROM postings
                     WHERE journal_entry_id = v_entry_id;

                    IF v_postings < 2 OR v_debits <> v_credits THEN
                        RAISE EXCEPTION 'journal entry % is unbalanced: % postings, debits %, credits %',
                                        v_entry_id, v_postings, v_debits, v_credits
                            USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$;

                -- Fires at COMMIT for each new entry (catches entries with zero or one posting) ...
                CREATE CONSTRAINT TRIGGER journal_entries_must_balance
                    AFTER INSERT ON journal_entries
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION ledger_assert_entry_balanced();

                -- ... and for each new posting (catches postings added to an entry committed earlier).
                CREATE CONSTRAINT TRIGGER postings_must_balance
                    AFTER INSERT ON postings
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION ledger_assert_entry_balanced();
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION idempotency_assert_completed() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM idempotency_records
                                WHERE principal_id = NEW.principal_id
                                  AND operation = NEW.operation
                                  AND idempotency_key = NEW.idempotency_key
                                  AND succeeded IS NULL) THEN
                        RAISE EXCEPTION 'idempotency record (%, %) committed without an outcome', NEW.operation, NEW.idempotency_key
                            USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$;

                CREATE CONSTRAINT TRIGGER idempotency_records_must_complete
                    AFTER INSERT ON idempotency_records
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION idempotency_assert_completed();
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION ledger_accounts_protect_definition() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.id <> OLD.id OR NEW.code <> OLD.code OR NEW.account_type <> OLD.account_type
                       OR NEW.normal_balance <> OLD.normal_balance OR NEW.currency <> OLD.currency
                       OR NEW.allow_negative_balance <> OLD.allow_negative_balance OR NEW.created_at <> OLD.created_at THEN
                        RAISE EXCEPTION 'ledger account % definition is immutable; only the cached balance may change', OLD.code
                            USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER ledger_accounts_definition_immutable
                    BEFORE UPDATE ON ledger_accounts
                    FOR EACH ROW EXECUTE FUNCTION ledger_accounts_protect_definition();
                CREATE TRIGGER ledger_accounts_no_delete
                    BEFORE DELETE ON ledger_accounts
                    FOR EACH ROW EXECUTE FUNCTION ledger_reject_mutation();

                CREATE FUNCTION wallets_protect_definition() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.id <> OLD.id OR NEW.customer_id <> OLD.customer_id OR NEW.ledger_account_id <> OLD.ledger_account_id
                       OR NEW.currency <> OLD.currency OR NEW.created_at <> OLD.created_at THEN
                        RAISE EXCEPTION 'wallet % ownership and account binding are immutable', OLD.id
                            USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER wallets_definition_immutable
                    BEFORE UPDATE ON wallets
                    FOR EACH ROW EXECUTE FUNCTION wallets_protect_definition();
                CREATE TRIGGER wallets_no_delete
                    BEFORE DELETE ON wallets
                    FOR EACH ROW EXECUTE FUNCTION ledger_reject_mutation();
                """);

            migrationBuilder.Sql("""
                INSERT INTO ledger_accounts
                    (id, code, name, account_type, normal_balance, currency, allow_negative_balance, balance_minor, version, created_at, updated_at)
                VALUES
                    ('01920000-0000-7000-8000-000000005900', '5900-SIM-FUNDING-NOK', 'Simulated funding expense (NOK)',
                     'Expense', 'Debit', 'NOK', false, 0, 0, now(), now());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS wallets_no_delete ON wallets;
                DROP TRIGGER IF EXISTS wallets_definition_immutable ON wallets;
                DROP FUNCTION IF EXISTS wallets_protect_definition();
                DROP TRIGGER IF EXISTS ledger_accounts_no_delete ON ledger_accounts;
                DROP TRIGGER IF EXISTS ledger_accounts_definition_immutable ON ledger_accounts;
                DROP FUNCTION IF EXISTS ledger_accounts_protect_definition();
                DROP TRIGGER IF EXISTS idempotency_records_must_complete ON idempotency_records;
                DROP FUNCTION IF EXISTS idempotency_assert_completed();
                DROP TRIGGER IF EXISTS postings_must_balance ON postings;
                DROP TRIGGER IF EXISTS journal_entries_must_balance ON journal_entries;
                DROP FUNCTION IF EXISTS ledger_assert_entry_balanced();
                DELETE FROM ledger_accounts WHERE id = '01920000-0000-7000-8000-000000005900';
                """);

            foreach (var table in AppendOnlyTables)
            {
                migrationBuilder.Sql($"""
                    DROP TRIGGER IF EXISTS {table}_append_only ON {table};
                    DROP TRIGGER IF EXISTS {table}_no_truncate ON {table};
                    """);
            }

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS ledger_reject_mutation();");
        }
    }
}
