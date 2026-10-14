# Load test results

Only numbers from actual runs are recorded here. They are **indicative**: everything ran on one developer laptop, so the load generator, two API instances, the worker and PostgreSQL competed for the same CPU and disk.

## How to reproduce

```bash
docker compose up --build -d          # or: two `dotnet run` API instances + worker on one database
dotnet run --project tools/LedgerCore.LoadTest -c Release -- \
  --base-urls http://localhost:8080,http://localhost:8081 --duration 30 --concurrency 32 --customers 20 --duplicate-ratio 0.1 --seed 1
```

The tool creates the customers and funds their wallets through the API. It then runs random transfers from 32 concurrent clients, alternating between instances. 10 % of requests are deliberate retries of the previous request with the same idempotency key, and amounts are random, so some transfers overdraw and get `422`. At the end it checks that the sum of all wallet balances equals the total funded, and that `POST /api/admin/reconciliation` reports `isConsistent: true`. The process exits non-zero if either check fails. The Development configuration raises the per-user rate limits; otherwise the run would mostly measure the limiter.

## Run 1: 2026-10-14

| | |
|---|---|
| Machine | Windows 11 Enterprise 10.0.26200, 12 logical processors (shared by everything below) |
| Runtime | .NET 10.0.13, Release builds |
| Database | PostgreSQL 17.10 (portable EDB build, default configuration except `max_connections = 300`), same machine |
| Topology | 2 API instances (`dotnet run`, Development environment, JSON console logging at Information), 1 worker |
| Workload | 20 wallets × 10 000.00 NOK, 32 concurrent clients, 30 s, 10 % duplicate retries, seed 1 |

| Metric | Result |
|---|---|
| Requests | 14 622 in 30.13 s |
| Throughput | **485 requests/s** |
| Status codes | 14 440 × `201`, 182 × `422` (`INSUFFICIENT_FUNDS`), 0 errors |
| Duplicate retries sent | 1 444 |
| Latency p50 / p95 / p99 / max | **27.7 ms / 331.8 ms / 560 ms / 1 641 ms** |
| Money conserved | yes (20 000 000 øre before and after) |
| Reconciliation | consistent |
| `pg_stat_database.deadlocks` | **0** |
| API warnings/errors logged | 0 |
| Outbox | 13 054 messages, all delivered by the worker |

### Interpretation

* **The tail latency comes from contention, by design.** 32 clients spread over 20 wallets means requests regularly queue on the same wallet row lock. The median request does not wait, while p95/p99 include queueing behind several transfers on a hot wallet. `ledgercore.db.lock_wait.duration` would show this directly when a metrics exporter is attached.
* **The numbers are not a capacity statement.** PostgreSQL ran with default settings on a laptop disk, next to the load generator and with verbose logging enabled. A meaningful capacity test needs dedicated hardware, a tuned database, and runs long enough to include checkpoints and autovacuum.
* **What the run does establish:** under sustained concurrent load with retries, across two instances, no money was created or lost, every journal entry stayed balanced, and PostgreSQL never had to break a deadlock.
