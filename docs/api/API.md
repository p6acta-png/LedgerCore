# LedgerCore HTTP API

The OpenAPI document is served at `/openapi/v1.json` and Swagger UI at `/swagger` (Development/Testing only, or with `OpenApi:Enabled=true`).

## Conventions

* **Amounts** are integers in minor units: `amountMinor: 80000` is 800.00 NOK. Strings, decimals and numbers larger than 64 bits are rejected.
* **Currency** is the exact ISO code `"NOK"` (the only supported currency).
* **Unknown JSON properties are rejected** (a typo such as `"amount"` instead of `"amountMinor"` returns 400 instead of silently being ignored).
* **Authentication:** `Authorization: Bearer <JWT>`. Roles: `customer`, `ledger-operator`.
* **Idempotency:** `Idempotency-Key` (8-128 chars, `A-Z a-z 0-9 - _ . :`) is required on every money-moving POST. A replay returns the original status and body with `Idempotent-Replayed: true`.
* **Correlation:** send `X-Correlation-Id` (≤ 64 safe chars) or one is generated. It is returned on every response and stored with journal entries and audit events.
* **Errors** use RFC 9457 problem details with a stable `code`:

```json
{ "title": "INSUFFICIENT_FUNDS", "status": 422, "detail": "Available balance 75000 is less than the requested 99999999 minor units.",
  "code": "INSUFFICIENT_FUNDS", "traceId": "e32d153e2f73e96a29f0b7f740e43dc0", "correlationId": "e32d153e2f73e96a29f0b7f740e43dc0" }
```

## Endpoints

| Method & path | Who | Purpose |
|---|---|---|
| `POST /api/wallets` | customer | Create a NOK wallet (balance 0) |
| `GET /api/wallets` | customer | My wallets |
| `GET /api/wallets/{id}` | owner, operator | Wallet details |
| `GET /api/wallets/{id}/balance` | owner, operator | Available balance |
| `GET /api/wallets/{id}/transactions?limit=50&beforePostingId=` | owner, operator | Posting history, newest first |
| `POST /api/transfers` | customer (owner of source) | Transfer. **Idempotency-Key required** |
| `GET /api/transfers/{id}` | sender, receiver, operator | Transfer details and reversal, if any |
| `POST /api/transfers/{id}/reversals` | operator | Compensating entry. **Idempotency-Key required** |
| `GET /api/journal-entries?limit=50&beforeEntryId=` | operator | Journal, newest first |
| `GET /api/journal-entries/{id}` | operator | One entry with postings |
| `POST /api/admin/reconciliation?freezeAffectedWallets=true` | operator | Recompute balances from the journal |
| `POST /api/dev/tokens` | anonymous, **Development/Testing only** | Mint a dev token |
| `POST /api/dev/funding` | operator, **Development/Testing only** | Simulated deposit. **Idempotency-Key required** |
| `GET /health/live`, `GET /health/ready` | anonymous | Liveness; readiness (DB reachable and fully migrated) |

## Error codes

| HTTP | `code` | Meaning |
|---|---|---|
| 400 | `VALIDATION_FAILED` | Malformed JSON, wrong types, unknown or missing fields, invalid description/name/paging |
| 400 | `INVALID_AMOUNT`, `UNSUPPORTED_CURRENCY`, `SELF_TRANSFER` | Input rule violations |
| 400 | `IDEMPOTENCY_KEY_REQUIRED`, `IDEMPOTENCY_KEY_INVALID` | Header missing or malformed |
| 401 | `UNAUTHENTICATED` | No token, or invalid/expired token |
| 403 | `FORBIDDEN` | Not your wallet, or the role is missing |
| 404 | `WALLET_NOT_FOUND`, `TRANSFER_NOT_FOUND`, `JOURNAL_ENTRY_NOT_FOUND`, `NOT_FOUND` | Does not exist, or is not visible to you |
| 409 | `TRANSFER_ALREADY_REVERSED` | Second reversal of the same transfer |
| 422 | `INSUFFICIENT_FUNDS`, `WALLET_NOT_ACTIVE`, `CURRENCY_MISMATCH`, `BALANCE_OVERFLOW`, `REVERSAL_INSUFFICIENT_FUNDS`, `WALLET_LIMIT_REACHED` | Business rule rejected the request (stored for idempotent replay) |
| 422 | `IDEMPOTENCY_KEY_REUSED` | Key already used with a different payload |
| 429 | `RATE_LIMITED` | Slow down; see `Retry-After` |
| 500 | `INTERNAL_ERROR`, `SIMULATED_FAILURE` | Unexpected error, or an injected failure (dev/test) |
| 503 | `LOCK_TIMEOUT`, `TRANSIENT_FAILURE` | Temporary; retry with the **same** Idempotency-Key |

## Examples

The transfer, balance, journal and reconciliation responses below were captured from a local run (ids will differ). The token response is abbreviated.

### Get a development token

```bash
curl -s -X POST http://localhost:8080/api/dev/tokens -H 'Content-Type: application/json' \
  -d '{"subject":"00000000-0000-4000-a000-00000000a11c","name":"Alice","roles":["customer"]}'
```
```json
{ "accessToken": "eyJhbGciOiJIUzI1NiIs…", "tokenType": "Bearer", "subject": "00000000-0000-4000-a000-00000000a11c",
  "roles": ["customer"], "expiresAt": "2026-10-14T22:43:06+00:00" }
```

### Transfer

```bash
curl -s -X POST http://localhost:8080/api/transfers \
  -H "Authorization: Bearer $ALICE" -H 'Content-Type: application/json' -H 'Idempotency-Key: 3f2b6c1e-8a4d-4b7e-9c0d-1e2f3a4b5c6d' \
  -d '{"fromWalletId":"10000000-0000-4000-a000-00000000a11c","toWalletId":"10000000-0000-4000-a000-000000000b0b","amountMinor":25000,"currency":"NOK","description":"Dinner"}'
```
`201 Created`, `Location: /api/transfers/01a13c5e-7dae-7d84-ba8c-a6091258b71c`
```json
{ "transferId": "01a13c5e-7dae-7d84-ba8c-a6091258b71c", "status": "Completed",
  "fromWalletId": "10000000-0000-4000-a000-00000000a11c", "toWalletId": "10000000-0000-4000-a000-000000000b0b",
  "amountMinor": 25000, "currency": "NOK", "description": "Dinner",
  "journalEntryId": "01a13c5e-7dad-7c08-9980-c1e469b8af31", "createdAt": "2026-10-14T21:43:07.9492579+00:00" }
```
Repeating the identical request returns the identical body with `Idempotent-Replayed: true`.

### Balance

```bash
curl -s http://localhost:8080/api/wallets/10000000-0000-4000-a000-00000000a11c/balance -H "Authorization: Bearer $ALICE"
```
```json
{ "walletId": "10000000-0000-4000-a000-00000000a11c", "availableBalanceMinor": 100000, "currency": "NOK",
  "availableBalanceFormatted": "1000.00 NOK", "status": "Active" }
```

### Journal entry (operator)

```bash
curl -s "http://localhost:8080/api/journal-entries?limit=1" -H "Authorization: Bearer $OPS"
```
```json
[{ "id": "01a13c5e-7dad-7c08-9980-c1e469b8af31", "entryType": "Transfer", "currency": "NOK", "description": "Dinner",
   "reversesJournalEntryId": null, "createdBy": "00000000-0000-4000-a000-00000000a11c",
   "correlationId": "0efe5e7f46708ff0628aabfadbd2c274", "createdAt": "2026-10-14T21:43:07.949257+00:00",
   "postings": [
     { "id": 5, "ledgerAccountId": "01a13c5e-788a-7249-bc7a-19e8cbba5ee5", "ledgerAccountCode": "2100-WALLET-1000000000004000a00000000000a11c", "direction": "Debit",  "amountMinor": 25000 },
     { "id": 6, "ledgerAccountId": "01a13c5e-7a53-74d3-a1c7-1f6fff8688a3", "ledgerAccountCode": "2100-WALLET-1000000000004000a000000000000b0b", "direction": "Credit", "amountMinor": 25000 } ] }]
```

### Reconciliation (operator)

```bash
curl -s -X POST http://localhost:8080/api/admin/reconciliation -H "Authorization: Bearer $OPS"
```
```json
{ "generatedAt": "2026-10-14T21:43:08.2906392+00:00", "isConsistent": true, "accountsChecked": 4, "journalEntriesChecked": 3,
  "totalDebitsMinor": 175000, "totalCreditsMinor": 175000, "discrepancies": [], "unbalancedJournalEntryIds": [],
  "negativeWalletIds": [], "frozenWalletIds": [] }
```

### Reversal (operator)

```bash
curl -s -X POST http://localhost:8080/api/transfers/$TRANSFER_ID/reversals -H "Authorization: Bearer $OPS" \
  -H 'Content-Type: application/json' -H 'Idempotency-Key: reverse-dinner-0001' -d '{"reason":"customer dispute"}'
```
`201 Created` with `reversalId`, `reversalJournalEntryId`, `originalJournalEntryId`. A second reversal with a new key returns `409 TRANSFER_ALREADY_REVERSED`.
