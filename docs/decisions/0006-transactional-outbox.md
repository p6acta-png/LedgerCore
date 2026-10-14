# ADR-0006: Transactional outbox with leases, at-least-once delivery

**Status:** accepted

## Context
Transfers should trigger notifications (and could feed audit exports). Sending them directly from the request would either notify about transfers that later roll back, or lose notifications when the process dies after COMMIT.

## Decision
* The ledger transaction inserts an `outbox_messages` row. It commits or rolls back with the transfer.
* Workers lease batches with `UPDATE … WHERE id IN (SELECT … FOR UPDATE SKIP LOCKED) RETURNING`, setting `locked_until`/`locked_by` and incrementing `attempt_count`. Delivery happens outside any transaction. On success the message is acknowledged with `processed_at`; on failure it is rescheduled with exponential backoff, and dead-lettered after `MaxAttempts`.
* Delivery is **at-least-once**. The consumer deduplicates by message id (`simulated_notifications.message_id` is its primary key).
* Any number of workers may run (compose starts two).

## Consequences
* No notification for a rolled-back transfer; no lost notification for a committed one (`OutboxTests.A_message_exists_if_and_only_if…`).
* After a crash between delivery and acknowledgement, the message is redelivered once its lease expires. This was observed with a real process kill (`WorkerProcessTests`): 10 messages, 11 deliveries, 10 notifications.
* No ordering guarantee across messages, and a poison message ends up dead-lettered rather than blocking others.
* Ledger correctness does not depend on the worker at all.

## Alternatives considered
* **Exactly-once delivery:** not achievable across two independent systems without the consumer participating (via idempotency or a shared transaction). Claiming it would be false.
* **Holding the outbox row lock during delivery:** simpler, but long external calls would keep transactions and connections open, and a hung consumer would block the row indefinitely.
* **A message broker:** not needed at this scale. The outbox would still be required to get the ledger write and the publish onto the same transaction.
