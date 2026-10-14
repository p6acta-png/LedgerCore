# LedgerCore: implementation progress

This file records what has been **actually executed**, where, and with what result. Nothing in this file is an estimate.

## Development environment used for verification

| Item | Value |
|---|---|
| Machine | Windows 11 Enterprise 10.0.26200, 12 logical processors, no admin rights |
| .NET SDK | 10.0.402 (runtime 10.0.13), installed per-user from builds.dotnet.microsoft.com (SHA-512 verified) |
| PostgreSQL | 17.10 portable binaries (EDB), run on 127.0.0.1:55432 by `scripts/local-postgres.ps1` |
| Docker | **Not available** (no Docker Desktop, no WSL, no admin rights) |

Consequence: every **local** database test ran against a **real PostgreSQL 17.10** server through `LEDGERCORE_TEST_POSTGRES`. The Docker-dependent paths (the Testcontainers branch of `PostgresServer.InitializeAsync`, used when that variable is unset, the `Dockerfile`, `docker-compose.yml` and `.github/workflows/ci.yml`) could not run on this machine. They were first executed on GitHub Actions, see [CI runs on GitHub Actions](#ci-runs-on-github-actions).

## Phase status

| Phase | Scope | Status |
|---|---|---|
| 1 | Solution, domain model, schema, migrations, local DB tooling | Done. Both migrations applied to real PostgreSQL; integrity triggers exercised by hand and by tests |
| 2 | Balanced ledger + transaction engine | Done |
| 3 | Idempotency + concurrency protection | Done |
| 4 | Authentication, authorization, audit | Done |
| 5 | Integration, concurrency, failure tests | Done (170 tests) |
| 6 | Outbox + worker, metrics/tracing, load test, Docker, CI | Done. Docker and CI verified on GitHub Actions (see below) |
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

## CI runs on GitHub Actions

Repository: https://github.com/p6acta-png/LedgerCore, workflow `ci` (`.github/workflows/ci.yml`), runner `ubuntu-latest`. These were the first executions of the Docker-dependent paths. Both runs passed **without any change** to the `Dockerfile`, `docker-compose.yml`, the Testcontainers code, `ProcessHost` or `scripts/demo.sh`.

| Run | Commit | `build-and-test` | `compose-smoke` |
|---|---|---|---|
| [38652061971](https://github.com/p6acta-png/LedgerCore/actions/runs/38652061971) | `0549af5` initial commit | success (91 s) | success (64 s) |
| [38652684306](https://github.com/p6acta-png/LedgerCore/actions/runs/38652684306) | `16bdbf9` adds `scripts/test-summary.sh` | success (92 s) | success (52 s) |

Run 38652684306 step by step. Test counts come from the TRX files; `scripts/test-summary.sh` publishes them as annotations on the run, readable without a login:

```
build (Release, warnings are errors)      success  10 s
unit tests                                68 passed, 0 failed, 0 not executed (68 total)    2 s
integration tests                         91 passed, 0 failed, 0 not executed (91 total)   33 s
  (Testcontainers postgres:17-alpine, including ProcessCrashTests and WorkerProcessTests with real process kills on Linux)
concurrency tests                         11 passed, 0 failed, 0 not executed (11 total)   18 s
docker compose up -d --build              success  43 s  (postgres, migrate+seed, api-1, api-2, 2 worker replicas)
scripts/demo.sh against :8080 and :8081   success   4 s  (the script exits non-zero if any check fails)
```

Run 38652061971 (before the summary step existed) passed the same steps; its per-suite counts were not published.

Why the summary step was added: `dotnet test` exits 0 when it discovers no tests, so a green step alone does not prove the tests ran. The step now fails the job if a suite has zero tests or any failure.

Not verified by CI:

* Job logs were not read: downloading Actions logs needs repository admin authentication, and the GitHub CLI was not logged in on the development machine. That includes the output of the `Report vulnerable packages` step; it succeeded, but its findings were not reviewed.
* The two worker containers start, but `demo.sh` does not assert outbox delivery inside the compose stack. Outbox delivery is covered by `OutboxTests` and `WorkerProcessTests` (which passed in CI) and by the local demo above.
* Only Docker on Linux (`ubuntu-latest`) was used. Docker Desktop on Windows or macOS has not been tried.
* The load test was not run in CI.

## Not done / open items

* The production authentication path (external OpenID Connect provider via `Authentication:Jwt:Authority`) is configured and guarded, but has not been run against a real identity provider.
* There is no unfreeze workflow for wallets frozen by reconciliation.
* The optional observability profile (Aspire dashboard image in `docker-compose.yml`) is unverified.
* Possible compose start-up race, **not observed** in the two CI runs: the postgres healthcheck runs `pg_isready` over the Unix socket. The official image's temporary init server also listens on that socket, so the container can report healthy during initialisation, and the `migrate` service does not retry its connection. Hardening options: `pg_isready -h 127.0.0.1`, or a connect retry in `migrate`.
* CI maintenance warnings from GitHub: `actions/checkout@v4`, `actions/setup-dotnet@v4` and `actions/upload-artifact@v4` target the deprecated Node.js 20 and are currently forced onto Node.js 24. `ubuntu-latest` moves to Ubuntu 26 from 2026-10-19, and the workflow has not run there yet.
