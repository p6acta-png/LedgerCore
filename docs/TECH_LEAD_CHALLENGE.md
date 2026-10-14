# Tech Lead Challenge: try to break LedgerCore

Don't take the README's word for anything. This document gives you 15 attacks on LedgerCore's guarantees. Each one lists exact commands, the expected outcome, the database invariant to check afterwards, the code responsible, and the automated test that already performs the attack.

Every automated test is tagged with its challenge number, so you can run one challenge across all test projects:

```bash
dotnet test LedgerCore.slnx --filter "Challenge=05"
```

## Setup (once)

```bash
docker compose up --build -d              # postgres, migrate+seed, api-1 (:8080), api-2 (:8081), 2 workers
API=http://localhost:8080; API2=http://localhost:8081
SQL() { docker compose exec -T postgres psql -U ledgercore -d ledgercore -c "$1"; }

tok() { curl -s -X POST $API/api/dev/tokens -H 'Content-Type: application/json' \
        -d "{\"subject\":\"$1\",\"roles\":[\"$2\"]}" | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p'; }
ALICE=$(tok 00000000-0000-4000-a000-00000000a11c customer)
BOB=$(tok 00000000-0000-4000-a000-000000000b0b customer)
OPS=$(tok demo-operator ledger-operator)
A=10000000-0000-4000-a000-00000000a11c     # Alice's wallet, 1 000.00 NOK after seeding
B=10000000-0000-4000-a000-000000000b0b     # Bob's wallet,     500.00 NOK

transfer() { # transfer <token> <from> <to> <amountMinor> <key> [extra curl args]
  local t=$1 f=$2 to=$3 amt=$4 key=$5; shift 5
  curl -s -w '\nHTTP %{http_code}\n' -X POST $API/api/transfers -H "Authorization: Bearer $t" \
    -H 'Content-Type: application/json' -H "Idempotency-Key: $key" "$@" \
    -d "{\"fromWalletId\":\"$f\",\"toWalletId\":\"$to\",\"amountMinor\":$amt,\"currency\":\"NOK\"}"; }
```

The **universal invariant query** to run after any challenge. Every row must show `0` / `true`:

```bash
SQL "SELECT
  (SELECT count(*) FROM (SELECT e.id FROM journal_entries e LEFT JOIN postings p ON p.journal_entry_id = e.id GROUP BY e.id
     HAVING count(p.id) < 2 OR SUM(CASE WHEN p.direction='Debit' THEN p.amount_minor ELSE -p.amount_minor END) <> 0) x) AS unbalanced_entries,
  (SELECT count(*) FROM ledger_accounts WHERE NOT allow_negative_balance AND balance_minor < 0) AS negative_balances,
  (SELECT count(*) FROM (SELECT la.id FROM ledger_accounts la LEFT JOIN postings p ON p.ledger_account_id = la.id
     GROUP BY la.id, la.balance_minor HAVING la.balance_minor <> COALESCE(SUM(CASE WHEN p.direction = la.normal_balance
     THEN p.amount_minor ELSE -p.amount_minor END),0)) y) AS projection_mismatches,
  (SELECT COALESCE(SUM(CASE WHEN direction='Debit' THEN amount_minor ELSE -amount_minor END),0) FROM postings) = 0 AS debits_equal_credits;"
```

`POST /api/admin/reconciliation` (operator) performs the same checks through the API.

---

## 01. Overdraw a wallet

* **Preconditions:** Alice has 1 000.00 NOK.
* **Do:** `transfer $ALICE $A $B 100001 ch01-overdraw-0001`
* **Expect:** `422`, `"code":"INSUFFICIENT_FUNDS"`. Balances unchanged; no `transfers` row; an `audit_events` row with outcome `Rejected`.
* **Also try:** `amountMinor` of `0`, `-1`, `1.5`, `"100"`, `99999999999999999999` → `400`.
* **Invariant:** `negative_balances = 0`. Even raw SQL cannot break it: `SQL "UPDATE ledger_accounts SET balance_minor = -1 WHERE id = (SELECT ledger_account_id FROM wallets WHERE id='$A')"` fails on `ck_ledger_accounts_non_negative`.
* **Code:** [LedgerRules.PlanTransfer](../src/LedgerCore.Domain/Ledger/LedgerRules.cs), [LedgerAccount.BalanceAfter](../src/LedgerCore.Domain/Ledger/LedgerAccount.cs), CHECK in [ModelConfiguration.cs](../src/LedgerCore.Infrastructure/Persistence/ModelConfiguration.cs).
* **Tests:** `TransferTests.Overdrawing_a_wallet_is_rejected_and_changes_nothing`, `TransferTests.Malformed_or_invalid_requests_are_rejected_with_400`.
* **Limitations:** a single operation is capped at 1 000 000 000.00 NOK by design.

## 02. Two concurrent transfers spending the same balance

* **Preconditions:** a fresh wallet with exactly 1 000.00 NOK (or use Alice right after seeding).
* **Do:**
  ```bash
  transfer $ALICE $A $B 80000 ch02-race-1 & transfer $ALICE $A 10000000-0000-4000-a000-00000000ca01 80000 ch02-race-2 & wait
  ```
* **Expect:** exactly one `201` and one `422 INSUFFICIENT_FUNDS`; Alice ends at 200.00 NOK. Repeat as often as you like: the outcome is the same, only the winner changes.
* **Invariant:** `negative_balances = 0`, `debits_equal_credits = true`.
* **Code:** `LockAccountsAsync` (`SELECT … ORDER BY id FOR UPDATE`), [NpgsqlLedgerTransaction.cs:89](../src/LedgerCore.Infrastructure/Ledger/NpgsqlLedgerTransaction.cs:89).
* **Tests:** `OverspendTests.Mandatory_two_concurrent_800_NOK_transfers_from_1000_NOK_exactly_one_succeeds` (15 rounds), and `OverspendTests.The_second_spend_blocks_on_the_row_lock_then_sees_the_committed_balance`, which freezes request 1 *while it holds the lock*, proves through `pg_stat_activity` that request 2 waits on a PostgreSQL row lock, then releases request 1. `LostUpdateDemonstrationTests` shows the money-creating bug that happens without the lock.
* **Limitations:** contention on one wallet serializes its transfers (correct, but throughput-bounded).

## 03. Send the same request 100 times at once

* **Do:**
  ```bash
  seq 100 | xargs -P 50 -I{} sh -c "curl -s -o /dev/null -w '%{http_code}\n' -X POST $API/api/transfers \
    -H 'Authorization: Bearer $ALICE' -H 'Content-Type: application/json' -H 'Idempotency-Key: ch03-hundred-0001' \
    -d '{\"fromWalletId\":\"$A\",\"toWalletId\":\"$B\",\"amountMinor\":100,\"currency\":\"NOK\"}'" | sort | uniq -c
  SQL "SELECT count(*) FROM transfers WHERE idempotency_key = 'ch03-hundred-0001'"
  ```
* **Expect:** `100 × 201`, all with the same `transferId`, 99 with header `Idempotent-Replayed: true`; exactly **1** transfer row, 1 journal entry, 2 postings, 1 outbox message.
* **Code:** [IdempotentCommandExecutor](../src/LedgerCore.Application/Idempotency/IdempotentCommandExecutor.cs), `ClaimIdempotencyKeyAsync` ([NpgsqlLedgerTransaction.cs:23](../src/LedgerCore.Infrastructure/Ledger/NpgsqlLedgerTransaction.cs:23)).
* **Tests:** `DuplicateRequestTests.Mandatory_100_simultaneous_identical_requests_record_exactly_one_transfer`, `MultiInstanceTests.Duplicate_requests_split_across_instances_create_one_transfer`.
* **Limitations:** each waiting duplicate holds a DB connection until the first completes; very large bursts are bounded by the pool and the rate limiter.

## 04. Reuse an idempotency key with different data

* **Do:** `transfer $ALICE $A $B 1000 ch04-reuse-0001`, then `transfer $ALICE $A $B 2000 ch04-reuse-0001`
* **Expect:** first `201`; second `422 IDEMPOTENCY_KEY_REUSED`, and nothing is executed. Also try a different description. The same key used by **Bob** for his own transfer succeeds, because keys are scoped per caller.
* **Code:** request fingerprint in [IdempotencyTypes.cs](../src/LedgerCore.Application/Idempotency/IdempotencyTypes.cs).
* **Tests:** `IdempotencyTests.Reusing_a_key_with_a_different_payload_is_rejected`, `IdempotencyTests.Keys_are_scoped_per_caller`, `DuplicateRequestTests.Concurrent_requests_sharing_a_key_with_different_payloads_produce_one_transfer`.
* **Limitations:** after the 24 h retention window a key is forgotten and can be reused.

## 05. Fail before COMMIT

* **Do (simulated, in-process exception):** `transfer $ALICE $A $B 3000 ch05-before-0001 -H 'X-LedgerCore-Fail-At: BeforeCommit'`
  Try other checkpoints too: `AfterIdempotencyClaim`, `AfterAccountsLocked`, `AfterFirstPosting`.
* **Expect:** `500 SIMULATED_FAILURE`; no transfer, no journal entry, no outbox row, no idempotency record. Retrying **the same key without the header** gives a fresh `201` (not a replay).
* **Do (real process kill):** `transfer $ALICE $A $B 3000 ch05-kill-0001 -H 'X-LedgerCore-Fail-At: BeforeCommit' -H 'X-LedgerCore-Fail-Mode: KillProcess'`. curl reports an empty reply, and `docker compose ps` shows `api-1` exited. Run `SQL "SELECT count(*) FROM transfers WHERE idempotency_key='ch05-kill-0001'"` (expect `0`), then `docker compose up -d api-1` and retry without the headers (expect `201`).
* **Code:** [NpgsqlLedgerTransactionRunner.ExecuteOnceAsync](../src/LedgerCore.Infrastructure/Ledger/NpgsqlLedgerTransactionRunner.cs:63), [HttpHeaderFailureInjector](../src/LedgerCore.Api/Hosting/FailureInjection.cs).
* **Tests:** `FailureInjectionTests.Scenario_A_and_D_a_failure_before_commit_leaves_no_trace` (4 checkpoints, simulated), `ProcessCrashTests.Scenario_A_process_killed_before_commit_leaves_no_partial_transfer` (**real OS process kill**).
* **Limitations:** failure injection exists only in Development/Testing (startup refuses it elsewhere).

## 06. Fail after COMMIT (the response is lost)

* **Do:** `transfer $ALICE $A $B 3000 ch06-after-0001 -H 'X-LedgerCore-Fail-At: AfterCommit' -H 'X-LedgerCore-Fail-Mode: KillProcess'`, then restart `api-1` and send the same request again without the headers.
* **Expect:** the first call has no usable response, but the transfer **is** committed (`SQL "SELECT count(*) FROM transfers WHERE idempotency_key='ch06-after-0001'"` = 1). The retry returns `201` with `Idempotent-Replayed: true` and the same `transferId`. Money moved once.
* **Tests:** `FailureInjectionTests.Scenario_B_commit_succeeds_but_the_response_is_lost_and_the_retry_replays`, `ProcessCrashTests.Scenario_B_process_killed_after_commit_and_the_retry_does_not_transfer_twice` (real kill).

## 07. Unauthorized transfers

* **Do:**
  ```bash
  transfer $BOB $A $B 100 ch07-steal-0001                    # Bob debits Alice
  transfer $OPS $A $B 100 ch07-operator-01                   # operator tries to move customer money
  transfer "eyJhbGciOiJIUzI1NiJ9.e30.forged" $A $B 1 ch07-forged-001   # forged token
  curl -s -w '\nHTTP %{http_code}\n' $API/api/wallets/$A -H "Authorization: Bearer $BOB"   # read Alice's wallet
  ```
* **Expect:** `403 FORBIDDEN`, `403`, `401`, `404` (not 403, so wallet ids cannot be probed). No balance changes. Bob's attempt appears in `audit_events` with outcome `Denied`.
* **Code:** [WalletAccessPolicy](../src/LedgerCore.Application/Security/CallerContext.cs), re-check in `LedgerRules.PlanTransfer`, policies in [AuthenticationSetup.cs](../src/LedgerCore.Api/Security/AuthenticationSetup.cs).
* **Tests:** all of `AuthorizationTests` (forged/expired tokens, operator endpoints, visibility, reversal by customer), `WalletAccessPolicyTests`.

## 08. Create an unbalanced journal entry

* **Do (bypass the application entirely):**
  ```bash
  SQL "BEGIN;
       INSERT INTO journal_entries (id, entry_type, currency, description, created_by, created_at)
         VALUES ('00000000-0000-0000-0000-0000000000e8','Funding','NOK','hack','me',now());
       INSERT INTO postings (journal_entry_id, ledger_account_id, direction, amount_minor, currency, created_at)
         VALUES ('00000000-0000-0000-0000-0000000000e8','01920000-0000-7000-8000-000000005900','Debit',100,'NOK',now());
       COMMIT;"
  ```
* **Expect:** the INSERTs succeed and the **COMMIT fails**: `journal entry … is unbalanced: 1 postings, debits 100, credits 0` (SQLSTATE `23000`). Adding a posting to an already committed entry fails the same way.
* **Code:** deferred constraint triggers in [LedgerIntegrityRules](../src/LedgerCore.Infrastructure/Persistence/Migrations/20261014212958_LedgerIntegrityRules.cs); domain check in [JournalEntry.Create](../src/LedgerCore.Domain/Ledger/JournalEntry.cs:66).
* **Tests:** `SchemaIntegrityTests` (three tests tagged 08), `JournalEntryTests`.
* **Limitations:** a database superuser can drop the triggers.

## 09. Kill and restart an outbox worker

* **Do:** create a few transfers, then `docker compose stop worker`, create more transfers, `SQL "SELECT count(*) FILTER (WHERE processed_at IS NULL) FROM outbox_messages"` (they wait), `docker compose start worker`.
* **Expect:** every message ends up `processed_at IS NOT NULL`. In `simulated_notifications` each message appears once; `delivery_count > 1` reveals a redelivery after a crash.
* **Code:** [OutboxDispatcher](../src/LedgerCore.Infrastructure/Outbox/OutboxDispatcher.cs:55), [OutboxWorker](../src/LedgerCore.Worker/Workers.cs).
* **Tests:** `WorkerProcessTests.A_worker_killed_mid_batch_loses_nothing_and_its_replacement_finishes_the_work` kills the **real worker process** after its 3rd delivery, before acknowledgement, then restarts it: 10 notifications, 11 deliveries. Also `OutboxTests` (backoff, dead-lettering, 4 concurrent dispatchers with 300 messages, no double processing).
* **Limitations:** at-least-once, no global ordering; the consumer must be idempotent.

## 10. Reconstruct wallet balances from journal history

* **Do:** `curl -s -X POST $API/api/admin/reconciliation -H "Authorization: Bearer $OPS"`, and independently:
  ```bash
  SQL "SELECT w.id, la.balance_minor AS cached,
         SUM(CASE WHEN p.direction='Credit' THEN p.amount_minor ELSE -p.amount_minor END) AS from_journal
       FROM wallets w JOIN ledger_accounts la ON la.id = w.ledger_account_id
       LEFT JOIN postings p ON p.ledger_account_id = la.id GROUP BY w.id, la.balance_minor"
  ```
* **Expect:** `isConsistent: true`, cached = from_journal for every wallet, `totalDebitsMinor = totalCreditsMinor`.
* **Code:** [LedgerReadStore.ComputeReconciliationAsync](../src/LedgerCore.Infrastructure/Queries/LedgerReadStore.cs:106) (single REPEATABLE READ snapshot).
* **Tests:** `ReconciliationTests.Balances_rebuilt_from_the_journal_match_the_cached_balances`; `AssertLedgerInvariantsAsync` runs the same check at the end of most integration tests.

## 11. Tamper with a cached balance

* **Do:** `SQL "UPDATE ledger_accounts SET balance_minor = balance_minor + 500000 WHERE id = (SELECT ledger_account_id FROM wallets WHERE id = '$A')"`, then run reconciliation, then try to spend the invented money: `transfer $ALICE $A $B 400000 ch11-spend-0001`.
* **Expect:** reconciliation reports the discrepancy (cached vs journal) and lists Alice's wallet under `frozenWalletIds`. The transfer is rejected with `422 WALLET_NOT_ACTIVE`. A `wallet.freeze` audit event is written. The cache is **not** auto-corrected (see ARCHITECTURE §7).
* **Tests:** `ReconciliationTests.A_tampered_cached_balance_is_detected_and_the_wallet_is_frozen`, `ReconciliationTests.Freezing_can_be_disabled_for_a_report_only_run`.
* **Limitations:** money spent between the tampering and the next reconciliation run is not prevented. The worker reconciles every 15 min (1 min in Development). There is no unfreeze endpoint.

## 12. Deadlock storm: crossing transfers

* **Do:** many parallel transfers A→B and B→A at once (the classic deadlock recipe):
  ```bash
  for i in $(seq 1 50); do transfer $ALICE $A $B 1 ch12-ab-$i >/dev/null & transfer $BOB $B $A 1 ch12-ba-$i >/dev/null & done; wait
  SQL "SELECT deadlocks FROM pg_stat_database WHERE datname = 'ledgercore'"
  ```
* **Expect:** every request answers `201`/`422`, never `500`/`503`; `deadlocks` does not increase.
* **Code:** single-statement, `ORDER BY id` lock acquisition ([ADR-0003](decisions/0003-locking-strategy.md)).
* **Tests:** `ContentionStressTests.Random_crossing_transfers_conserve_money_and_never_deadlock` (two seeds × 400 transfers across 8 wallets) asserts **zero** `40P01` retries and exact conservation of money. In the 30 s load test PostgreSQL recorded `deadlocks = 0`.

## 13. Two API instances, one balance

* **Do:** challenge 02, but with one request to `$API` and the other to `$API2` (`scripts/demo.sh` does this).
* **Expect:** same result as challenge 02. The instances share no memory, so this can only hold because the database is doing the coordination.
* **Tests:** `MultiInstanceTests` (two independent hosts: competing spends, duplicates split across instances, 300 mixed operations).

## 14. Rewrite history

* **Do:** `SQL "UPDATE postings SET amount_minor = 1"`, `SQL "DELETE FROM journal_entries"`, `SQL "TRUNCATE postings"`, `SQL "DELETE FROM audit_events"`.
* **Expect:** every statement fails with `table … is append-only` and the hint *"Correct mistakes with a compensating (reversal) journal entry."* The legitimate way to undo a transfer:
  `curl -s -X POST $API/api/transfers/<transferId>/reversals -H "Authorization: Bearer $OPS" -H 'Content-Type: application/json' -H 'Idempotency-Key: ch14-reverse-01' -d '{"reason":"dispute"}'`
* **Tests:** `SchemaIntegrityTests.Committed_history_cannot_be_modified_or_deleted` (7 cases), `ReversalTests` (compensating entry, original postings byte-identical afterwards, double reversal → `409`, receiver already spent → `422`).

## 15. Kill the database connection in the middle of the postings

* **Do:** `transfer $ALICE $A $B 3000 ch15-dbkill-0001 -H 'X-LedgerCore-Fail-At: AfterFirstPosting' -H 'X-LedgerCore-Fail-Mode: TerminateDatabaseConnection'`
* **Expect:** `201`. The server killed the backend after the first posting (a real `57P01` from PostgreSQL). The transaction with one posting was aborted by the server; the runner treated the error as transient and re-ran the whole transaction. The result is one entry with exactly two postings. API logs show `retrying`.
* **Tests:** `FailureInjectionTests.Scenario_D_a_real_connection_loss_mid_postings_is_rolled_back_and_retried`, plus `RetryExhaustionTests` (every attempt killed → `503 TRANSIENT_FAILURE`, nothing committed, no internal details in the body).

---

## Things you will *not* be able to do (and why)

| Attempt | Blocked by |
|---|---|
| Send money from a wallet you don't own | `WalletAccessPolicy` + check on locked rows |
| Make any wallet negative | Domain rule + row locks + `CHECK` constraint |
| Commit an unbalanced or empty journal entry | Deferred constraint triggers |
| Edit or delete history | Append-only triggers |
| Execute a transfer twice with one key | Primary key on `idempotency_records`, claimed in-transaction |
| Turn on dev tokens / failure injection in Production | `StartupValidation` refuses to start |

## Things that *are* possible (known limitations)

* A database superuser can disable triggers and forge consistent history. There is no external anchoring.
* Rate limits are per instance, so N instances allow N× the configured rate.
* A wallet whose cached balance was tampered with can be spent from until the next reconciliation run.
