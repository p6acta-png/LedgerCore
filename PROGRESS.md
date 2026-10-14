# LedgerCore: implementation progress

This file records what has been **actually executed**, where, and with what result. Nothing in this file is an estimate.

## Development environment used for verification

| Item | Value |
|---|---|
| Machine | Windows 11 Enterprise 10.0.26200, 12 logical processors, no admin rights |
| .NET SDK | 10.0.402 (runtime 10.0.13), installed per-user from builds.dotnet.microsoft.com (SHA-512 verified) |
| PostgreSQL | 17.10 portable binaries (EDB), run on 127.0.0.1:55432 by `scripts/local-postgres.ps1` |
| Docker | **Not available** (no Docker Desktop, no WSL, no admin rights) |

Consequence: every database test ran against a **real PostgreSQL 17.10** server through `LEDGERCORE_TEST_POSTGRES`. The Testcontainers code path (`PostgresServer.InitializeAsync`, used when that variable is unset), the `Dockerfile`, `docker-compose.yml` and `.github/workflows/ci.yml` were written but **not executed on this machine**. CI on GitHub (which has Docker) is the intended first execution of those paths.

## Phase status

| Phase | Scope | Status |
|---|---|---|
| 1 | Solution, domain model, schema, migrations, local DB tooling | Done. Both migrations applied to real PostgreSQL; integrity triggers exercised by hand and by tests |
| 2 | Balanced ledger + transaction engine | Done |
| 3 | Idempotency + concurrency protection | Done |
| 4 | Authentication, authorization, audit | Done |
| 5 | Integration, concurrency, failure tests | Done (170 tests) |
| 6 | Outbox + worker, metrics/tracing, load test, Docker, CI | Done. Docker/CI not executed locally (see above) |
| 7 | Documentation, learning materials, tech-lead challenge | Done |

## Recorded test runs

Final run through the one-command verification script (`./scripts/verify.ps1 -UseLocalPostgres`, Release configuration):

```
== build              0 Warning(s), 0 Error(s)  (warnings are errors)
== unit tests         Passed!  - Failed: 0, Passed: 68, Skipped: 0, Total: 68
== integration tests  Passed!  - Failed: 0, Passed: 91, Skipped: 0, Total: 91  (~1 min)
== concurrency tests  Passed!  - Failed: 0, Passed: 11, Skipped: 0, Total: 11  (~22 s)
All suites passed.
```

The integration suite includes **real OS process termination**: the API is started as a separate process and kills itself before/after COMMIT (`ProcessCrashTests`). The worker process is killed between delivery and acknowledgement and then restarted (`WorkerProcessTests`). These passed in the runs above.

### Failures found and fixed during verification (not hidden)

| Found by | Problem | Fix |
|---|---|---|
| First manual smoke run of the API | "Fail fast if no JWT authority" actually failed lazily on the first request; also, eager configuration reads would not see test overrides | All configuration moved to the options pattern; `StartupValidation` runs after `Build()` and before serving traffic |
| Seeding on startup | `JsonSerializerOptions.MakeReadOnly()` without a type-info resolver threw | `MakeReadOnly(populateMissingResolver: true)` |
| Smoke run | With `RespectRequiredConstructorParameters`, an omitted optional `description` was rejected as malformed | Optional record parameters got default values |
| `SchemaIntegrityTests` | `UPDATE transfers` "succeeded" because the test had no transfer rows (row triggers never fired). A test bug, not a product bug | The test now creates real history through the API before attacking it |
| Demo script | Race-result comparison was sensitive to line endings | Counts status lines instead of comparing strings |
| Load test setup | Hit the dev-token rate limit (20/min) | Development settings raise dev-only limits; production defaults unchanged |

### Mutation checks (do the tests catch real regressions?)

| Mutation (applied, run, then reverted) | Result |
|---|---|
| Remove `FOR UPDATE` from the account lock query in `NpgsqlLedgerTransaction.LockAccountsAsync` | **6 of 11** concurrency tests failed (mandatory overspend, lock-wait proof, both stress seeds, two multi-instance tests) |
| Make `TransferService` ignore the client's idempotency key | **3** concurrency tests and **7** integration tests failed (100× duplicates, cross-instance duplicates, key reuse, replay, crash-after-commit recovery in-process and with a real process kill) |

## End-to-end runs on the development machine

* `scripts/demo.sh` (Git Bash) against **two API instances** (ports 5080/5081, Release build) plus the worker, all on one database: **all checks passed**. This included the cross-instance 800/800-from-1000 race (exactly one 201 and one 422) and 100 parallel identical requests (100 × 201, one transfer).
* After the demo, the worker had delivered every outbox message exactly once (12/12, `delivery_count = 1`).
* Load test (`tools/LedgerCore.LoadTest`, 30 s, 32 clients, two instances): 14 622 requests, 485 req/s, p50 27.7 ms, p95 331.8 ms, p99 560 ms. Money conserved, reconciliation consistent, `pg_stat_database.deadlocks = 0`, no warnings or errors in the API logs, all 13 054 outbox messages delivered by the worker. See `docs/benchmarks/LOAD_TEST_RESULTS.md`.

## Not done / open items

* Execute `docker compose up` and the GitHub Actions workflow on a machine with Docker (first action item for a reviewer with Docker).
* The production authentication path (external OpenID Connect provider via `Authentication:Jwt:Authority`) is configured and guarded, but has not been run against a real identity provider.
* There is no unfreeze workflow for wallets frozen by reconciliation.
* The optional observability profile (Aspire dashboard image in `docker-compose.yml`) is unverified.
* The repository is not under git yet. Initialising it, committing and pushing are left to the owner.
