# ADR-0003: READ COMMITTED + explicit row locks in a deterministic order

**Status:** accepted

## Context
Two concurrent transfers from the same wallet must never both pass the balance check against the same starting balance (lost update / write skew). The solution must work across many API instances.

## Decision
* Transactions run at **READ COMMITTED**.
* Every ledger command locks **all** ledger accounts it will touch in **one** statement:
  `SELECT … FROM ledger_accounts WHERE id = ANY(@ids) ORDER BY id FOR UPDATE`
  ([NpgsqlLedgerTransaction.cs:89](../../src/LedgerCore.Infrastructure/Ledger/NpgsqlLedgerTransaction.cs:89)).
  Decisions (funds, overflow, status) are made only on the rows this returns. Wallet status is read after the lock is held.
* `SET LOCAL lock_timeout = 5s`; on timeout the client gets `503 LOCK_TIMEOUT` and retries with the same idempotency key.
* Deadlock (`40P01`), serialization failure (`40001`) and connection loss are retried by re-running the whole transaction (max 4 attempts, exponential backoff with jitter).
* Defence in depth: `CHECK (allow_negative_balance OR balance_minor >= 0)` on `ledger_accounts`.

## Why this works
* In PostgreSQL, `SELECT … FOR UPDATE` under READ COMMITTED waits for a conflicting lock holder and then returns the **latest committed** version of the row (EvalPlanQual re-check). The second spender therefore sees the first spender's committed debit.
* `ORDER BY id` with `FOR UPDATE` acquires row locks in output order. With a single global order, the waits-for graph between ledger transactions cannot contain a cycle, so these locks cannot deadlock. `ContentionStressTests` assert zero `40P01` retries, and the load test recorded `pg_stat_database.deadlocks = 0`.
* No C# `lock`/`SemaphoreSlim` is involved. Such a lock would protect one process only and would be meaningless with two API instances.

## Consequences
* Transfers touching the same wallet are serialized, which is correct but bounds per-wallet throughput by lock hold time (a few ms, see `ledgercore.db.lock_wait.duration`).
* Transfers on disjoint wallets run fully in parallel.

## Alternatives considered
* **SERIALIZABLE without explicit locks:** also correct (SSI), but conflicts surface as `40001` at commit and must be retried. Under contention on hot wallets that means many aborted transactions instead of an orderly queue. Locks give deterministic, explainable behaviour.
* **Optimistic concurrency (version column, `WHERE version = @v`):** correct, but pushes contention into client-visible retries and makes the "two spends" behaviour depend on timing.
* **Atomic conditional update (`UPDATE … SET balance = balance - x WHERE balance >= x`):** correct for a single debit, but awkward for multi-posting entries, reversals and frozen-status checks that need the full account state.
* **Application-level locks:** not valid across instances (see above).
