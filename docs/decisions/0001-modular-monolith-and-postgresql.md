# ADR-0001: Modular monolith on a single PostgreSQL database

**Status:** accepted

## Context
The core problem is transactional correctness of money movements: atomicity across journal entry, postings, balance projections, idempotency record, outbox row and audit event. Every one of those writes must succeed or fail together.

## Decision
One deployable API (horizontally scalable), one worker, one PostgreSQL database. Five projects with one-directional dependencies (Domain ← Application ← Infrastructure/Api/Worker).

## Consequences
* All writes of a ledger command share one ACID transaction, so no distributed transaction, saga or compensation protocol is needed for the core path.
* Multiple API instances are safe because they coordinate only through PostgreSQL (row locks, unique keys, triggers). This is tested with two in-process hosts and, in the demo, with two real processes.
* The database is the throughput ceiling and a single point of failure. Scaling beyond one primary would require partitioning wallets across shards, at which point cross-shard transfers need a different protocol. That is out of scope and documented as a limitation.

## Alternatives considered
* **Microservices (ledger, wallets, notifications):** would turn one local transaction into a distributed one. That adds failure modes without adding any correctness.
* **Event-sourced store / Kafka:** valid for very large ledgers, but adds infrastructure whose guarantees (ordering, exactly-once semantics) would themselves have to be defended. PostgreSQL already provides durable, ordered, transactional storage.
