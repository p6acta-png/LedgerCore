# Interview defense: LedgerCore

Questions a senior engineer is likely to ask, answered with reference to the actual implementation. Paths are relative to the repository root.

---

### 1. Why PostgreSQL?

The core requirement is a multi-row atomic write: journal entry, two postings, two balance projections, transfer row, outbox row, audit row and idempotency outcome must all commit or all disappear. PostgreSQL provides that through ACID transactions, plus three features the design relies on directly:
* **Row-level locks with re-check** (`SELECT … FOR UPDATE` returns the latest committed row after waiting), used in `NpgsqlLedgerTransaction.LockAccountsAsync`.
* **Unique-index waits** (`INSERT … ON CONFLICT DO NOTHING` waits for a conflicting in-flight insert), used for idempotency.
* **Deferred constraint triggers** (checked at COMMIT), used to enforce balanced entries independently of the C# code.

A document store or key-value store would require reimplementing those guarantees in application code.

### 2. What exactly is inside one database transaction?

For a transfer (`TransferService.ExecuteLockedAsync`, wrapped by `IdempotentCommandExecutor` and `NpgsqlLedgerTransactionRunner`):
`BEGIN` → `set_config('lock_timeout')` → claim idempotency key → lock both account rows → read wallet status → insert journal entry → insert 2 postings → update 2 cached balances (verified with `RETURNING`) → insert transfer → insert outbox message → insert audit event → store outcome on the idempotency record → `COMMIT` (deferred triggers verify the entry balances and the key is completed).

Everything outside that transaction is either read-only (validation, ownership lookup) or happens after commit (the HTTP response, outbox delivery).

### 3. Why not just a C# `lock`?

A C# lock only serializes threads in **one process**. LedgerCore runs several API instances (compose starts two), so a `lock` would let instance 1 and instance 2 spend the same balance concurrently. It would also do nothing for a DBA's SQL session, or for the worker. The only component every writer shares is PostgreSQL, so the lock has to live there. `MultiInstanceTests` run two independent hosts against one database to prove the point. There is no C# lock anywhere on the money path.

### 4. What guarantees atomicity?

The PostgreSQL transaction. The runner (`NpgsqlLedgerTransactionRunner.ExecuteOnceAsync`) opens one connection and one transaction, and every statement runs on it. `await using var transaction` guarantees a rollback on any exception before `CommitAsync`. If the process dies instead, PostgreSQL rolls back when the connection drops. Both cases are tested: `FailureInjectionTests` (exception at 4 checkpoints) and `ProcessCrashTests` (real `Process.Kill()` before COMMIT, then the database is inspected).

### 5. How does idempotency work, including two duplicates arriving at the same time on two instances?

The first statement in the transaction is `INSERT INTO idempotency_records … ON CONFLICT (principal_id, operation, idempotency_key) DO NOTHING`. Duplicate B, arriving while A is still uncommitted, **blocks on the primary-key index** until A finishes. If A commits, B's insert affects 0 rows, and B reads A's committed outcome and replays it (identical status and body, plus `Idempotent-Replayed: true`). If A rolls back, B's insert succeeds and B executes normally. A different payload under the same key returns `422 IDEMPOTENCY_KEY_REUSED`, detected by comparing SHA-256 fingerprints of the validated request. Because the outcome is stored in the same transaction as the transfer, they commit together. Tests: 100 simultaneous duplicates (`DuplicateRequestTests`), 100 duplicates split across two instances (`MultiInstanceTests`).

### 6. What happens after a crash?

| When | Effect | Recovery |
|---|---|---|
| Before COMMIT | Transaction aborted (by `await using` or by PostgreSQL on disconnect). Nothing persisted, key not claimed | Client retries with the same key → executes once |
| During COMMIT (ack lost) | Unknown to the client | Retry with the same key: if the commit happened, the key exists and the result is replayed; otherwise it executes |
| After COMMIT, before response | Transfer durable, client sees an error | Retry with the same key → replay, no second transfer |

Real process kills before and after COMMIT are in `ProcessCrashTests`. The runner also retries internally on connection loss, which is safe for the same reason.

### 7. How are deadlocks prevented? What if one happens anyway?

All account rows a command needs are locked in **one** statement with `ORDER BY id`. PostgreSQL takes the row locks in output order, so every transaction acquires them in the same global order, and a waits-for cycle cannot form. The only other lock (the idempotency key) is taken first and is unique per transaction. If a deadlock happened anyway (for example from a future code path that forgets the rule), PostgreSQL would abort one transaction with `40P01`. The runner retries that up to 4 attempts with jittered backoff, and the metric `ledgercore.db.transaction.retries{reason="40P01"}` makes it visible. `ContentionStressTests` assert that this metric stays at **zero** under crossing transfers. The load test observed `pg_stat_database.deadlocks = 0`.

### 8. What is the source of truth? What if the cached balance is wrong?

The append-only journal (`journal_entries` + `postings`). `ledger_accounts.balance_minor` is a projection, updated atomically with the postings. If the projection disagrees with the journal, the journal wins. `ReconciliationService` reports the difference, alerts (error log + metric + audit event), and **freezes** the affected wallets so an inflated balance cannot be spent. It does not silently overwrite the cache, because that would destroy evidence of the bug or tampering. Tested by `ReconciliationTests.A_tampered_cached_balance_is_detected_and_the_wallet_is_frozen`.

### 9. Why immutable postings?

Auditability and reconcilability. If postings could be edited, a balance could be "explained" by any history, and reconciliation would prove nothing. A wrong transfer is corrected by a **new** reversal entry that mirrors the original (`JournalEntry.CreateReversal`), so history shows both what happened and how it was corrected. The database enforces this with triggers. `ReversalTests` check that the original postings are byte-identical after a reversal.

### 10. Why READ COMMITTED instead of SERIALIZABLE?

Correctness comes from explicit row locks on exactly the rows that matter, and READ COMMITTED + `FOR UPDATE` is well defined for that. SERIALIZABLE (SSI) would also be correct, but under contention it aborts transactions at commit time (`40001`). Every conflicting transfer would then be retried instead of simply waiting its turn, which wastes work and makes latency unpredictable on hot wallets. With locks, the behaviour is an orderly queue that can be explained step by step. Reconciliation uses REPEATABLE READ, because it needs one consistent snapshot across four queries.

### 11. Why integer minor units?

Binary floating point cannot represent 0.10 exactly, so sums drift. `decimal` would work but invites mixing scales. LedgerCore uses `long` øre everywhere (`Money`), formats with integer division (`Money.Format`), uses `checked` arithmetic so overflow becomes an error instead of wrapping, and caps single operations at 1e11 minor units.

### 12. Why store business rejections in the idempotency record?

"Same key + same request = same logical result." If `INSUFFICIENT_FUNDS` were not stored, a retry after an unrelated deposit could succeed under the same key, and the client would see two different answers for one request. Validation and authorization failures are *not* stored, because nothing was decided under lock and the client must fix the request anyway. Transient failures are not stored either; they roll back. Test: `IdempotencyTests.A_business_rejection_is_replayed_deterministically…`.

### 13. How do you know the concurrency tests really test concurrency?

Three ways:
1. **Deterministic interleaving:** `CheckpointGate` freezes request A *while it holds the row lock*. The test starts B, waits until `pg_stat_activity` shows a backend in `wait_event_type = 'Lock'` for `FOR UPDATE`, asserts B has not completed, then releases A. This is positive evidence that PostgreSQL serialized the two requests.
2. **Mutation testing:** with `FOR UPDATE` removed, 6 of 11 concurrency tests fail. With the idempotency key ignored, 10 tests fail. Recorded in PROGRESS.md.
3. **Demonstration of the anomaly:** `LostUpdateDemonstrationTests` reproduces the lost update without locks on the real schema (80 000 øre is created) and shows `FOR UPDATE` preventing it.

Start gates (`StartGate`) release many requests at once, and no correctness assertion relies on `Thread.Sleep`.

### 14. Why a transactional outbox? Is delivery exactly-once?

Publishing directly from the request would notify about transfers that later roll back, or lose notifications when the process dies after COMMIT. The outbox row commits with the transfer. Delivery is **at-least-once**: a worker can crash after delivering and before acknowledging, and the lease then expires and another worker redelivers. Exactly-once *delivery* across two systems is not possible without the consumer's cooperation, so the consumer is idempotent (message id is its primary key). `WorkerProcessTests` kills the real worker mid-batch and observes 11 deliveries for 10 messages, with 10 notifications. Transaction atomicity (all-or-nothing writes) and delivery guarantees (how often a message arrives) are different properties.

### 15. How is authorization enforced? Why 404 instead of 403 for reads?

Authentication is JWT bearer. Authorization happens in layers: endpoint policies (`customer` / `ledger-operator`), then `WalletAccessPolicy` in the use case, then a re-check against the locked rows in `LedgerRules.PlanTransfer`. Reading someone else's wallet returns 404 so wallet ids cannot be confirmed by probing. Debiting someone else's wallet returns 403: the transfer endpoint necessarily confirms destination wallets anyway, and the clearer error helps legitimate clients. Both cases are audited. Operators can never debit customer wallets (segregation of duties).

### 16. How would you scale it?

* **Reads:** read replicas for history and journal queries. Decisions about money stay on the primary.
* **Writes on different wallets:** already parallel. The limit is one PostgreSQL primary (CPU, WAL, connections), so the next step is PgBouncer in transaction mode and bigger hardware.
* **Hot wallets** (one merchant receiving everything): serialization on one row. Options: split a hot account into N sub-accounts and sum them; batch credits; or let credits skip the lock (append-only postings, with the projection updated asynchronously), since credits cannot overdraw.
* **Beyond one primary:** shard by wallet. Cross-shard transfers then need a two-step protocol (a reservation/pending entry with clearing accounts and an idempotent completion). That is a different system with different guarantees.

### 17. What are the limits of the current design?

See the README "Known limitations" section and THREAT_MODEL "Residual risks". The main ones:
* One primary database is the consistency boundary and the single point of failure.
* Per-wallet throughput is bounded by lock hold time.
* Rate limiting is per instance.
* Idempotency retention is 24 h.
* Reconciliation is a full scan.
* There is no unfreeze workflow.
* A superuser can bypass the triggers.
* The production OIDC path has not been run against a real identity provider.

### 18. What would you change before calling it production-grade?

* External anchoring of the journal (hash chain or signed periodic snapshots) for tamper evidence.
* Incremental, period-based reconciliation, and an audited repair/unfreeze workflow with four-eyes approval.
* A shared rate limiter at the gateway, token revocation, mTLS between services, and secrets from a vault.
* Database roles: the application role should not be able to `DISABLE TRIGGER`, and migrations should run under a separate role.
* Load testing on production-like hardware with the PostgreSQL configuration tuned. The current numbers come from one laptop.
* Operational runbooks plus alerting on `reconciliation.discrepancies`, outbox dead letters, and lock-wait percentiles.

### 19. Why mix EF Core and raw SQL?

EF Core is very good at schema management (migrations), ordinary inserts and LINQ reads. The money path needs `FOR UPDATE` with ordering, `ON CONFLICT DO NOTHING`, `RETURNING`, `set_config`, and exact control over statement order and failure checkpoints. Hiding those behind an ORM would make the most important code the least readable. Both share one `NpgsqlDataSource`. See ADR-0005.

### 20. What happens if two operators reverse the same transfer at the same time?

Both lock the same two wallet accounts, so they serialize. The second one, after acquiring the locks, runs `IsTransferReversedAsync` (a new READ COMMITTED snapshot) and sees the first reversal, so it returns `409 TRANSFER_ALREADY_REVERSED`. As a backstop, `transfer_reversals.transfer_id` is UNIQUE and `journal_entries.reverses_journal_entry_id` is UNIQUE.
