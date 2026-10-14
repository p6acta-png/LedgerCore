# LedgerCore

**A double-entry ledger and transfer engine for *simulated* money, built to be challenged.**
C# / .NET 10 · ASP.NET Core · PostgreSQL 17 · EF Core + hand-written SQL · xUnit + real PostgreSQL

LedgerCore moves simulated Norwegian kroner between virtual wallets and keeps a strict double-entry journal. The interesting part is not the API surface but what happens under pressure: concurrent requests, duplicate retries, crashed processes, dropped database connections, tampered balances, and callers trying to spend money that isn't theirs. Each of those has a mechanism in the code, a rule in the database, and a test that runs against real PostgreSQL.

> No real money, bank connections, card data or payment processors are involved. This is a portfolio-grade simulator and is not certified, audited, or production-ready banking software.

---

## What hard problems does it solve?

| Problem | How LedgerCore handles it | Where |
|---|---|---|
| Two requests spend the same balance at the same time | One `SELECT … FOR UPDATE` over every affected account, `ORDER BY id`; decision made on locked rows; `CHECK (balance_minor >= 0)` as a database backstop | [NpgsqlLedgerTransaction.cs:89](src/LedgerCore.Infrastructure/Ledger/NpgsqlLedgerTransaction.cs:89) |
| A client retries (timeouts, double clicks, at-least-once queues) | Idempotency key claimed as the **first statement of the same transaction** (`INSERT … ON CONFLICT DO NOTHING` on a primary key); outcome stored and committed atomically with the transfer | [IdempotentCommandExecutor.cs:28](src/LedgerCore.Application/Idempotency/IdempotentCommandExecutor.cs:28), [NpgsqlLedgerTransaction.cs:23](src/LedgerCore.Infrastructure/Ledger/NpgsqlLedgerTransaction.cs:23) |
| The process dies before / after COMMIT | Single database transaction per command; PostgreSQL rolls back on disconnect; retry with the same key replays the committed result | [NpgsqlLedgerTransactionRunner.cs:63](src/LedgerCore.Infrastructure/Ledger/NpgsqlLedgerTransactionRunner.cs:63) |
| Deadlocks and transient database errors | Global lock order (no cycles); bounded retry with jittered backoff for `40001`/`40P01`/connection loss; bounded `lock_timeout` | [NpgsqlLedgerTransactionRunner.cs:22](src/LedgerCore.Infrastructure/Ledger/NpgsqlLedgerTransactionRunner.cs:22), [TransientFailures.cs](src/LedgerCore.Infrastructure/Ledger/TransientFailures.cs) |
| Unbalanced or rewritten history | Domain refuses unbalanced entries; **deferred constraint triggers** re-check balance at COMMIT; triggers reject UPDATE/DELETE/TRUNCATE of history | [JournalEntry.cs:66](src/LedgerCore.Domain/Ledger/JournalEntry.cs:66), [LedgerIntegrityRules migration](src/LedgerCore.Infrastructure/Persistence/Migrations/20261014212958_LedgerIntegrityRules.cs) |
| Cached balance drifts from the journal | Reconciliation recomputes every balance from postings in one snapshot; mismatching wallets are **frozen**, never silently "fixed" | [LedgerReadStore.cs:106](src/LedgerCore.Infrastructure/Queries/LedgerReadStore.cs:106), [ReconciliationService.cs:35](src/LedgerCore.Application/Reconciliation/ReconciliationService.cs:35) |
| Notifications must not be lost or posted for rolled-back transfers | Transactional outbox in the same transaction; leasing with `FOR UPDATE SKIP LOCKED`; at-least-once delivery to an idempotent consumer | [OutboxDispatcher.cs:55](src/LedgerCore.Infrastructure/Outbox/OutboxDispatcher.cs:55) |
| One customer moves another customer's money | Ownership check before the transaction **and** against the locked rows; operators can inspect but never debit | [CallerContext.cs:36](src/LedgerCore.Application/Security/CallerContext.cs:36), [LedgerRules.cs:18](src/LedgerCore.Domain/Ledger/LedgerRules.cs:18) |

---

## Run it

### Option A: Docker Compose (recommended)

```bash
docker compose up --build
```

This starts PostgreSQL 17, a one-shot `migrate` service (migrations plus demo data), **two API instances** (`:8080`, `:8081`) sharing one database, and two outbox workers. Swagger UI is at http://localhost:8080/swagger.

Demo identities are seeded. Get a token (Development only) and call the API:

```bash
curl -s -X POST http://localhost:8080/api/dev/tokens -H 'Content-Type: application/json' \
  -d '{"subject":"00000000-0000-4000-a000-00000000a11c","name":"Alice","roles":["customer"]}'
```

| Demo identity | Subject | Wallet | Opening balance |
|---|---|---|---|
| Alice (customer) | `00000000-0000-4000-a000-00000000a11c` | `10000000-0000-4000-a000-00000000a11c` | 1 000.00 NOK |
| Bob (customer) | `00000000-0000-4000-a000-000000000b0b` | `10000000-0000-4000-a000-000000000b0b` | 500.00 NOK |
| Carol (customer) | `00000000-0000-4000-a000-00000000ca01` | `10000000-0000-4000-a000-00000000ca01` | 0.00 NOK |
| Operator | `demo-operator` (role `ledger-operator`) | – | – |

Then run the scripted end-to-end demo (it asserts every status code and races the two instances against each other):

```bash
./scripts/demo.sh
```

### Option B: without Docker

You need the .NET 10 SDK and any PostgreSQL 15+ you control. On Windows without admin rights, [scripts/local-postgres.ps1](scripts/local-postgres.ps1) runs the official portable PostgreSQL binaries on `127.0.0.1` (this is how this repository was developed and tested).

```powershell
./scripts/local-postgres.ps1 init; ./scripts/local-postgres.ps1 start
$cs = ./scripts/local-postgres.ps1 connection-string
$env:ConnectionStrings__Ledger = $cs -replace 'Database=postgres','Database=ledgercore_dev'
dotnet run --project src/LedgerCore.Api          # http://localhost:5080; creates/migrates the DB and seeds demo data
dotnet run --project src/LedgerCore.Worker       # optional, in a second terminal: outbox delivery
```

Amounts are always **integer minor units** (`amountMinor`, øre). `80000` means 800.00 NOK. More request and response examples are in [docs/api/API.md](docs/api/API.md).

---

## Run the tests

```bash
./scripts/verify.sh                         # Linux/macOS: build + all suites (Testcontainers needs Docker)
./scripts/verify.ps1 -UseLocalPostgres      # Windows without Docker (portable PostgreSQL)
```

| Suite | What it uses | Count | Last local result |
|---|---|---|---|
| `LedgerCore.UnitTests` | Pure domain/application logic, including seeded randomized invariant runs (3 × 3 000 operations) | 68 | 68 passed |
| `LedgerCore.IntegrationTests` | Real PostgreSQL + the real API pipeline; real process-kill tests for the API and the worker | 91 | 91 passed |
| `LedgerCore.ConcurrencyTests` | Races, 100× duplicates, cross-instance contention, deterministic lock interleaving | 11 | 11 passed |

Integration and concurrency tests never mock persistence. Each test gets its own database, cloned from a migrated template. The PostgreSQL server is either a Testcontainers `postgres:17-alpine` container or, if `LEDGERCORE_TEST_POSTGRES` is set, an existing server. See [PROGRESS.md](PROGRESS.md) for exactly what was executed and where.

**The tests detect real regressions.** Removing `FOR UPDATE` from the lock query makes 6 concurrency tests fail. Ignoring the client's idempotency key makes 10 tests fail. Both mutations were applied, observed failing, and reverted. Details are in PROGRESS.md.

---

## Try to break it

[docs/TECH_LEAD_CHALLENGE.md](docs/TECH_LEAD_CHALLENGE.md) lists **15 reproducible challenges**: overdrawing, racing two instances, 100 duplicate requests, reusing keys, killing the process before and after COMMIT, killing the database connection mid-postings, forging tokens, writing unbalanced entries in raw SQL, rewriting history, tampering with cached balances, restarting the outbox worker mid-batch, and more. Each one comes with exact commands, the expected result, the database invariant, the source files involved, and the automated test that proves it:

```bash
dotnet test tests/LedgerCore.IntegrationTests --filter "Challenge=05"
```

## Correctness properties and where they are verified

| # | Property | Enforced by | Verified by |
|---|---|---|---|
| P1 | Every journal entry balances (Σdebits = Σcredits, ≥ 2 postings) | Domain `JournalEntry.Create` + deferred DB triggers | `JournalEntryTests`, `SchemaIntegrityTests` (Challenge 08), `AssertLedgerInvariantsAsync` after every API test |
| P2 | No wallet balance is ever negative | Domain rule + row locks + `ck_ledger_accounts_non_negative` | `OverspendTests`, `ContentionStressTests`, `SchemaIntegrityTests` |
| P3 | One idempotency key ⇒ at most one transfer, same response | PK on `idempotency_records`, claimed in-transaction | `DuplicateRequestTests` (100×), `MultiInstanceTests`, `IdempotencyTests` |
| P4 | No partial transfer is ever committed | One transaction per command; rollback on exception/disconnect | `FailureInjectionTests`, `ProcessCrashTests` (real kill) |
| P5 | History is append-only; corrections are compensating entries | Triggers on history tables; `ReversalService` | `SchemaIntegrityTests` (Challenge 14), `ReversalTests` |
| P6 | Cached balance = balance recomputed from postings | Atomic update in the same transaction; reconciliation | `AssertLedgerInvariantsAsync`, `ReconciliationTests` |
| P7 | Money is conserved (Σ wallets = Σ funding) | Double entry | `ContentionStressTests`, `MultiInstanceTests`, randomized unit runs, load test |
| P8 | Only the owner can debit a wallet | `WalletAccessPolicy` + re-check under lock | `AuthorizationTests` (Challenge 07) |
| P9 | A notification exists iff its transfer committed, and is delivered at least once | Transactional outbox + leases | `OutboxTests`, `WorkerProcessTests` (real kill) |

## Measured performance

Measured on one Windows laptop (12 logical processors). The load generator, two API instances, the worker and PostgreSQL all shared it, so treat these as *indicative only*: 30 s, 32 concurrent clients, 20 hot wallets, 10 % deliberate duplicate retries → **14 622 requests, 485 req/s, p50 27.7 ms, p95 332 ms, p99 560 ms**. Money was conserved exactly, reconciliation was consistent, and PostgreSQL reported **0 deadlocks**. Full context and caveats are in [docs/benchmarks/LOAD_TEST_RESULTS.md](docs/benchmarks/LOAD_TEST_RESULTS.md). Reproduce it with `dotnet run --project tools/LedgerCore.LoadTest -c Release -- --base-urls http://localhost:8080,http://localhost:8081`.

---

## Architecture at a glance

```
HTTP ──► LedgerCore.Api ──► LedgerCore.Application ──► LedgerCore.Domain
         auth, rate limit,   use cases, idempotency,    money, journal entries,
         validation, errors  ports (interfaces)         pure rules, no I/O
                                   ▲
                                   │ implements ports
                     LedgerCore.Infrastructure ──► PostgreSQL 17
                     EF Core (schema, reads), Npgsql SQL (money path),     ▲
                     outbox dispatcher                                     │
                                                    LedgerCore.Worker ─────┘
                                                    outbox, retention, reconciliation
```

A modular monolith with five projects and one database. Details are in [docs/architecture/ARCHITECTURE.md](docs/architecture/ARCHITECTURE.md), and the reasoning behind each decision is in [docs/decisions/](docs/decisions/).

```
src/      Domain · Application · Infrastructure · Api · Worker
tests/    UnitTests · IntegrationTests · ConcurrencyTests · TestSupport (shared fixtures)
tools/    LedgerCore.LoadTest
docs/     architecture/ decisions/ security/ learning/ (Russian) api/ benchmarks/
          TECH_LEAD_CHALLENGE.md · INTERVIEW_DEFENSE.md
scripts/  verify.sh · verify.ps1 · demo.sh · local-postgres.ps1
```

---

## Known limitations (honest list)

- **Single currency (NOK), single database.** No sharding and no cross-database transactions. One PostgreSQL primary is the consistency boundary and a single point of failure.
- **Hot accounts serialize.** All transfers touching one wallet queue on its row lock. That is correct, but throughput per wallet is bounded by lock hold time. The funding expense account is a deliberate hotspot (dev-only feature).
- **Rate limiting is per instance** and in memory. With N instances the effective limit is N×. Correctness never depends on it.
- **Idempotency retention is 24 h.** After cleanup a key can be reused for a new operation. Clients must finish retrying within the window.
- **Reconciliation scans all postings.** That is fine for a simulator, but a production ledger would reconcile incrementally (per period/checkpoint).
- **Frozen wallets have no unfreeze workflow.** Unfreezing requires an investigated, audited operator action, which is not implemented.
- **Authentication in Development is a local HMAC token issuer.** Production mode expects an external OpenID Connect provider and has not been exercised against a real one.
- **Audit log is append-only at the DB-trigger level**, but a database superuser can still disable triggers. There is no cryptographic tamper evidence (hash chaining).
- **Verification gaps:** the development machine had no Docker, so all local tests ran against a real local PostgreSQL 17.10 instead of Testcontainers. The Docker paths (Testcontainers, the Dockerfile, `docker compose` and the GitHub Actions workflow) were then verified in CI on `ubuntu-latest`: 170/170 tests and the two-instance compose demo passed. Not verified: Docker Desktop on Windows/macOS and the observability profile. See [PROGRESS.md](PROGRESS.md).

More detail: [docs/security/THREAT_MODEL.md](docs/security/THREAT_MODEL.md) and [docs/INTERVIEW_DEFENSE.md](docs/INTERVIEW_DEFENSE.md) (section "Limits of the design").

## How AI was used

This repository was implemented with an AI coding assistant (Claude, via Claude Code) working from a detailed specification. The assistant wrote the code, tests, scripts and documentation. It also ran the builds, tests, mutation checks, demo and load test on the development machine and fixed what failed. Those runs found real bugs: options built before configuration was available, a JSON serializer configured without a type-info resolver, a missing default on an optional request field, and a few flawed tests. All of them were fixed and re-verified. Every number in this README comes from an actual run recorded in [PROGRESS.md](PROGRESS.md). The design decisions are documented so a human can evaluate and defend them independently. The Russian learning notes in [docs/learning/](docs/learning/) exist for exactly that purpose.

## Documentation index

| Document | Purpose |
|---|---|
| [docs/architecture/ARCHITECTURE.md](docs/architecture/ARCHITECTURE.md) | Layers, data model, transaction design, invariants, failure matrix |
| [docs/decisions/](docs/decisions/) | Architecture decision records (ADRs) |
| [docs/security/THREAT_MODEL.md](docs/security/THREAT_MODEL.md) | Assets, threats, mitigations, residual risks |
| [docs/TECH_LEAD_CHALLENGE.md](docs/TECH_LEAD_CHALLENGE.md) | 15 reproducible ways to try to break LedgerCore |
| [docs/INTERVIEW_DEFENSE.md](docs/INTERVIEW_DEFENSE.md) | Senior-level questions answered against the real code |
| [docs/api/API.md](docs/api/API.md) | Endpoints, error codes, example requests and responses |
| [docs/benchmarks/LOAD_TEST_RESULTS.md](docs/benchmarks/LOAD_TEST_RESULTS.md) | Measured load-test results and how to reproduce them |
| [docs/learning/](docs/learning/) | Russian-language walkthroughs of the architecture, C# syntax, request flow and failure scenarios |
| [PROGRESS.md](PROGRESS.md) | Phase-by-phase status with recorded test results |
