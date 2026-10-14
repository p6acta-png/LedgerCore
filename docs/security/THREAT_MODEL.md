# LedgerCore threat model

Scope: the LedgerCore API, worker and PostgreSQL database as deployed by `docker-compose.yml`, or an equivalent with an external identity provider. Money is simulated, but the system is analysed as if balances mattered.

## Assets

| Asset | Why it matters |
|---|---|
| Wallet balances (journal + projection) | The thing an attacker wants to increase, or move to themselves |
| Journal history | Must stay complete and unaltered for audit and reconciliation |
| Idempotency records | Replaying someone else's stored response would leak data; losing them enables double spends on retry |
| Signing keys / DB credentials | Allow forging any identity or bypassing all application rules |
| Audit log | Evidence of who did what, including denied attempts |

## Actors and trust boundaries

```
[Internet client] --TLS (terminated in front of the API)--> [API instances] --SQL (private network)--> [PostgreSQL]
                                                            [Worker]  -------------------------------^
[Identity provider] --signs tokens--> (validated by API)
[DB administrator] -- direct SQL (trusted, but detectable) --> [PostgreSQL]
```

Actors: anonymous internet users, authenticated customers (honest or malicious), operators (`ledger-operator`), compromised client devices, and administrators with direct database access.

## Threats (STRIDE) and mitigations

| # | Threat | Mitigation in LedgerCore | Evidence |
|---|---|---|---|
| S1 | Spoofing: forged or expired token | JWT signature, issuer, audience and lifetime validated; only HS256 accepted for dev tokens; 30 s clock skew | `AuthorizationTests.Requests_without_a_valid_token_are_rejected_with_401` (wrong key, expired, none) |
| S2 | Spoofing: dev token issuer reachable in production | Endpoint only mapped when DevAuthentication is enabled; enabling it outside Development/Testing stops startup | `SecurityConfigurationTests` |
| T1 | Tampering: modify or delete history | Append-only triggers on journal, postings, transfers, reversals, audit | `SchemaIntegrityTests` (Challenge 14) |
| T2 | Tampering: edit a cached balance directly | Reconciliation detects the difference from the journal and freezes the wallet | `ReconciliationTests` (Challenge 11) |
| T3 | Tampering: unbalanced entry via SQL | Deferred constraint triggers at COMMIT | `SchemaIntegrityTests` (Challenge 08) |
| T4 | Tampering: SQL injection | Every value parameterized (`NpgsqlParameter`, EF interpolation → parameters); SQL text contains only constants; strict input formats (GUIDs, integers, key alphabet) | Code review: [NpgsqlLedgerTransaction.cs](../../src/LedgerCore.Infrastructure/Ledger/NpgsqlLedgerTransaction.cs); `IdempotencyKeyTests` reject quotes |
| R1 | Repudiation: "I never made that transfer" | Audit event (actor, action, outcome, correlation id) written in the same transaction as the transfer; journal entries carry `created_by` and `correlation_id` | `TransferTests`, `ObservabilityTests` |
| R2 | Repudiation of attacks | Denied attempts (debiting or reading someone else's wallet) are audited with outcome `Denied` | `AuthorizationTests.A_customer_cannot_move_money…` |
| I1 | Information disclosure: wallet enumeration | Reading a wallet you cannot see returns 404, not 403; ids are random UUIDs | `AuthorizationTests.Other_customers_wallets_are_invisible` |
| I2 | Disclosure via errors | One exception boundary returns stable codes and generic messages; stack traces, SQL and connection details stay in server logs | `RetryExhaustionTests` asserts no `Npgsql`/SQL state in the body |
| I3 | Disclosure via logs | No tokens or passwords logged; Npgsql parameter logging disabled; correlation ids validated before being logged | Configuration in `DependencyInjection.cs`, `CorrelationIdMiddleware` |
| I4 | Idempotency key collision across users | Key scope includes the principal; one user can never receive another user's stored response | `IdempotencyTests.Keys_are_scoped_per_caller` |
| D1 | Denial of service: request floods | Per-caller rate limits (global + transfer token bucket + dev-token window) → 429 | `SecurityConfigurationTests.Transfer_bursts_above_the_rate_limit_get_429` |
| D2 | Lock-holding requests starve others | `lock_timeout` 5 s → 503; locks held only for the duration of one short transaction | `NpgsqlLedgerTransactionRunner` |
| D3 | Unbounded payloads/pages | Description ≤ 200, name ≤ 60, page size ≤ 200, amount ≤ 1e11 minor units, idempotency key ≤ 128 | Unit + integration validation tests |
| E1 | Elevation: customer moves another customer's money | `WalletAccessPolicy.CanDebit` before the transaction and `LedgerRules.PlanTransfer` on locked rows | `AuthorizationTests` (Challenge 07) |
| E2 | Elevation: customer calls operator functions | Policy `ledger-operator` on reconciliation, journal, reversals, funding | `AuthorizationTests.Customers_cannot_use_operator_endpoints` |
| E3 | Elevation: operator steals customer funds | Operators cannot debit wallets at all; reversals require an existing transfer and are audited | `AuthorizationTests.Operators_can_inspect_but_cannot_move…` |
| E4 | Failure injection used as an attack vector | Headers ignored unless FailureInjection is enabled; enabling it outside Development/Testing stops startup | `SecurityConfigurationTests` |

## Secrets handling

* No production secrets in the repository. `appsettings.json` contains none, and missing production settings stop startup.
* Development values (DB password `ledgercore_dev_only`, dev signing key) are labelled as throwaway, can be overridden through `.env` (git-ignored), and are only honoured in Development/Testing.
* The local PostgreSQL script generates a random password and stores it under the user profile, not in the repository.
* Containers run as the non-root `$APP_UID` user from the official .NET images.

## Residual risks and limitations

* **Database superuser** access can disable triggers and rewrite anything. Reconciliation detects balance tampering only when postings and projection disagree; a consistent forgery of both is not detectable without external anchoring (hash chains, periodic signed snapshots). This is not implemented.
* **TLS** is expected to be terminated by infrastructure in front of the API. The compose stack uses plain HTTP on localhost.
* **Rate limits are per instance and in memory.** There is no shared limiter, no IP reputation and no CAPTCHA.
* **Token revocation** is not implemented. Tokens are valid until expiry (dev default 1 h).
* **Authorization data is coarse:** two roles, no per-operator scopes, and no four-eyes approval for reversals.
* **Dependency vulnerabilities** are reported in CI (`dotnet list package --vulnerable`), not automatically blocked.
* **Production OIDC** configuration has not been exercised against a real provider.
