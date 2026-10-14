# ADR-0002: The immutable journal is the source of truth; balances are a projection

**Status:** accepted

## Context
Balances are read on every transfer, and recomputing them from all postings each time is O(history). Storing a balance creates a second copy of the truth that can diverge.

## Decision
* `journal_entries` + `postings` are the source of truth and are **append-only**. Database triggers reject UPDATE, DELETE and TRUNCATE. Corrections are new compensating (reversal) entries.
* `ledger_accounts.balance_minor` is a **cached projection** in the account's normal-balance direction. It is updated with a relative `balance + delta` in the **same transaction** that writes the postings, on a row held `FOR UPDATE`. The new value is compared with the domain's expectation (`RETURNING`).
* A **reconciliation** process recomputes every balance from postings in one snapshot. On mismatch it reports, alerts and freezes the affected wallets; it never auto-corrects.
* Entry balance (Σdebits = Σcredits, ≥ 2 postings) is enforced twice: in `JournalEntry.Create` and by deferred constraint triggers at COMMIT.

## Consequences
* O(1) balance checks under lock, while the history needed for audit and repair is kept.
* Drift can only come from a bug or out-of-band SQL, and it is detectable. `ReconciliationTests` tamper with a balance and observe detection and freezing.
* Postings are never physically corrected. A wrong transfer stays in history next to its reversal, which is what auditors expect.

## Alternatives considered
* **Maintaining the projection with a database trigger on postings:** robust against "forgot to update", but it hides a central step of the algorithm inside the database and makes the C# transaction harder to read. The explicit update plus the `RETURNING` check plus reconciliation was preferred for explainability.
* **No projection (SUM on every read):** simplest, but every balance check would scan an ever-growing history, and the balance row would no longer be a natural lock target.
