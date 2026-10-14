# ADR-0008: Named checkpoints for failure injection, and real crashes

**Status:** accepted

## Context
The crash scenarios must be tested against real persistence. A unit test that throws an exception never shows what PostgreSQL does with a half-finished transaction, and an in-process exception still runs `finally` blocks that a real crash would skip.

## Decision
* The ledger transaction exposes named checkpoints: `AfterIdempotencyClaim`, `AfterAccountsLocked`, `AfterFirstPosting`, `BeforeCommit`, `AfterCommit` (`ILedgerTransaction.CheckpointAsync`).
* At a checkpoint, `IFailureInjector` may request one of three modes:
  * `ThrowException`: an **in-process simulation** of a crash. The exception unwinds and the transaction is disposed, which rolls it back.
  * `TerminateDatabaseConnection`: a **real database-side failure**. `pg_terminate_backend(pg_backend_pid())` makes the server abort our transaction and Npgsql surfaces `57P01`.
  * `KillProcess`: **real process termination** via `Process.Kill()` on the current process (TerminateProcess/SIGKILL). No `finally`, no rollback call, no graceful shutdown.
* Requests opt in with headers (`X-LedgerCore-Fail-At`, `X-LedgerCore-Fail-Mode`) through `HttpHeaderFailureInjector`, which is inert unless `FailureInjection:Enabled` is set. That setting is refused outside Development/Testing.
* Tests may also replace `IFailureInjector` to pause at a checkpoint (`CheckpointGate`), which reproduces race interleavings deterministically without `Thread.Sleep`.

## Consequences
* Simulated and real failures are kept explicitly separate in tests and documentation (`FailureInjectionTests` vs `ProcessCrashTests` / `WorkerProcessTests`).
* The checkpoint calls are visible in production code. They cost one virtual call returning `null` per checkpoint, and they document the transaction's structure.
