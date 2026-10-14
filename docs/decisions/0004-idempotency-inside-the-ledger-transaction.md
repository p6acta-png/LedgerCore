# ADR-0004: Idempotency key claimed inside the ledger transaction

**Status:** accepted

## Context
Clients retry: after timeouts, after a crash before the response, from at-least-once queues, or simply by double-clicking. A retry must not move money twice, including when duplicates arrive concurrently on different API instances.

## Decision
* Every money-moving command requires `Idempotency-Key`. The uniqueness scope is `(principal_id, operation, key)`, enforced by the **primary key** of `idempotency_records`.
* The key is claimed as the **first statement** of the ledger transaction:
  `INSERT … ON CONFLICT (…) DO NOTHING`. If another transaction holds the same key uncommitted, PostgreSQL makes this insert wait for it. If that one commits, the insert affects 0 rows and the committed outcome is read. If it rolls back, this transaction takes over the key.
* The outcome (success body or business error) is stored on the record **in the same transaction** as the transfer. A deferred trigger forbids committing a record without an outcome.
* A replay returns the stored status and body plus `Idempotent-Replayed: true`. A key reused with a different payload (SHA-256 fingerprint of the validated request) returns `422 IDEMPOTENCY_KEY_REUSED`.
* Retention: 24 hours, enforced by a batch cleanup in the worker.

## Consequences
* There is no window in which a transfer exists without its idempotency record or vice versa, so crash-after-commit recovery is a replay, not a second transfer (`ProcessCrashTests.Scenario_B`).
* Automatic retries inside the runner are safe for the same reason, even when a COMMIT's outcome is unknown.
* Business rejections are replayed too: "insufficient funds" stays the answer for that key even after the wallet is funded. Clients must use a new key for a genuinely new attempt. This is deterministic and documented.
* Concurrent duplicates hold a connection while they wait on the key. 100 simultaneous duplicates need up to 100 connections (tested). A much larger burst would be limited by pool size and rate limiting.

## Alternatives considered
* **Check the key before the transaction, insert after:** a race; two duplicates can both pass the check.
* **In-memory dictionary / distributed cache:** does not survive restarts, is not transactional with the ledger, and is per-instance.
* **Insert the record only at the end and rely on a unique violation:** works, but every duplicate would first lock accounts and do all the work before failing.
* **Unique constraint on `transfers.idempotency_key` alone:** prevents duplicate transfers, but cannot replay rejections or detect payload mismatch, and does not generalise to funding and reversals.
