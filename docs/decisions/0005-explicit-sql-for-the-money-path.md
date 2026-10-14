# ADR-0005: Hand-written SQL for the money path, EF Core for everything else

**Status:** accepted

## Context
The specification asks for EF Core for ordinary persistence and explicit SQL where locking matters. The transfer transaction needs `FOR UPDATE` with ordering, `ON CONFLICT DO NOTHING`, `RETURNING`, `set_config`, and full control over statement order (and over where failure checkpoints sit).

## Decision
* **EF Core** owns the schema (model + migrations), wallet creation (`WalletRegistry`) and most read queries (`LedgerReadStore`), with snake_case naming.
* The **ledger transaction** (`NpgsqlLedgerTransaction`, `NpgsqlLedgerTransactionRunner`), reconciliation, history and the outbox use **Npgsql directly with parameterized SQL**. No value is ever concatenated into SQL text, and identifiers in SQL are constants.
* Both share **one `NpgsqlDataSource`** (one connection pool), created lazily from configuration.
* Persistence shapes (`*Row` classes) are separate from domain types, so the domain keeps strongly typed ids and immutability without ORM compromises.

## Consequences
* Every statement of the critical path is visible and reviewable as SQL, which matters most when explaining locks and transaction boundaries.
* The cost is some mapping code (`reader.GetGuid(0)` …) and keeping SQL column names in sync with the EF model. The integration tests catch any mismatch immediately.
* SQL injection is excluded by construction: every value goes through `NpgsqlParameter`, and EF's `ExecuteSqlAsync($"…")` converts interpolations into parameters.
