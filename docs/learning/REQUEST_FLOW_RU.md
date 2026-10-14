# Путь одного запроса: `POST /api/transfers`

Пример: Алиса переводит Бобу 800 NOK (`amountMinor: 80000`), заголовок `Idempotency-Key: k-1`. Ниже разобрано, что выполняется, в каком файле и какой SQL уходит в базу.

---

## Этап 1. HTTP-конвейер (до нашего кода)

| # | Что | Где | Если не так |
|---|---|---|---|
| 1 | Correlation id: берём `X-Correlation-Id` (если он безопасный) или генерируем; добавляем в ответ и во все логи | `Api/Http/HttpResults.cs` → `CorrelationIdMiddleware` | — |
| 2 | Глобальный обработчик исключений (оборачивает всё ниже) | `LedgerExceptionHandler` | 500/503 с кодом, без внутренностей |
| 3 | Аутентификация: проверяются подпись, срок, issuer и audience JWT | `Security/AuthenticationSetup.cs` | `401 UNAUTHENTICATED` |
| 4 | Rate limiting (по `sub`): глобальный лимит и token bucket для переводов | `Hosting/RateLimitingSetup.cs` | `429 RATE_LIMITED` |
| 5 | Политика `customer`: роль `customer` и `sub` в формате GUID | `AuthenticationSetup.cs` | `403` |
| 6 | Привязка JSON → `CreateTransferCommand` (строгий JSON: без строк вместо чисел, без лишних полей) | `ApiSetup.cs` (`JsonOptions`) | `400 VALIDATION_FAILED` |

## Этап 2. Эндпоинт → сервис

`Endpoints/LedgerEndpoints.cs` → `CreateTransfer`:
1. `IdempotencyKey.Parse(header)` → иначе `400 IDEMPOTENCY_KEY_REQUIRED/INVALID`.
2. `context.GetCaller()` собирает `CallerContext` из claims (subject, роли, correlation id).
3. `TransferService.CreateAsync(...)` — дальше всё в Application.

## Этап 3. Проверки до транзакции

`Application/Transfers/TransferService.cs` → `CreateCoreAsync`:

| Проверка | Метод | Ошибка |
|---|---|---|
| Вызывающий — клиент | `caller.IsCustomer` | 403 |
| Валидация: GUID, не сам себе, `NOK`, сумма 1..1e11, описание ≤ 200 | `Validate` | 400 |
| Кошелёк-источник существует | `readStore.GetWalletAsync` | 404 |
| Источник принадлежит Алисе | `WalletAccessPolicy.CanDebit` | 403 + аудит `Denied` |
| Получатель существует | `readStore.GetWalletAsync` | 404 |

Это чтения **без блокировок**. Деньги здесь ещё не трогаются, и решение о деньгах на этих данных не принимается.

## Этап 4. Транзакция

`IdempotentCommandExecutor.ExecuteAsync` → `NpgsqlLedgerTransactionRunner.ExecuteAsync` → `ExecuteOnceAsync`. В базу уходит следующий SQL (упрощённо, все значения — параметры):

```sql
BEGIN ISOLATION LEVEL READ COMMITTED;
SELECT set_config('lock_timeout', '5000ms', true);

-- (4) идемпотентность: первым делом
INSERT INTO idempotency_records (principal_id, operation, idempotency_key, request_hash, created_at, expires_at)
VALUES ('alice-guid', 'transfers.create', 'k-1', 'sha256…', now, now+24h)
ON CONFLICT (principal_id, operation, idempotency_key) DO NOTHING;
--   1 строка  -> ключ наш, идём дальше
--   0 строк   -> SELECT сохранённого результата -> replay (или 422, если отпечаток другой) -> COMMIT -> выход

-- (6) блокировки
SELECT ledger_account_id FROM wallets WHERE id = ANY(@wallet_ids);              -- маппинг кошелёк -> счёт
SELECT id, code, account_type, currency, allow_negative_balance, balance_minor
  FROM ledger_accounts WHERE id = ANY(@account_ids) ORDER BY id FOR UPDATE;     -- ЖДЁМ, если строки заняты
SELECT id, customer_id, status, ledger_account_id FROM wallets WHERE id = ANY(@wallet_ids);  -- статус уже под блокировкой
```

Далее в C# (`TransferService.ExecuteLockedAsync`, чистая логика без базы):
* `LedgerRules.PlanTransfer`: владелец? активны? валюта? `balance >= 80000`? → `JournalEntry` (Debit Алиса 80000, Credit Боб 80000).
* `CalculateBalanceChanges`: Алиса 100000 → 20000, Боб 50000 → 130000 (с `checked`).
* Если отказ (например, `INSUFFICIENT_FUNDS`) → аудит `Rejected` → результат сохраняется на ключе → COMMIT → **422**.

Если всё в порядке:

```sql
INSERT INTO journal_entries (id, entry_type, currency, description, …) VALUES (…, 'Transfer', 'NOK', …);
INSERT INTO postings (…) VALUES (entry, alice_account, 'Debit', 80000, 'NOK', …);
-- checkpoint AfterFirstPosting (место для failure injection)
INSERT INTO postings (…) VALUES (entry, bob_account, 'Credit', 80000, 'NOK', …);

UPDATE ledger_accounts SET balance_minor = balance_minor + (-80000), version = version + 1 WHERE id = alice_account RETURNING balance_minor; -- = 20000?
UPDATE ledger_accounts SET balance_minor = balance_minor + 80000,    version = version + 1 WHERE id = bob_account   RETURNING balance_minor; -- = 130000?

INSERT INTO transfers (…);
INSERT INTO outbox_messages (…, 'transfer.completed', payload);
INSERT INTO audit_events (…, 'transfer.create', 'Succeeded', …);
UPDATE idempotency_records SET succeeded = true, response_body = '{…}', completed_at = now WHERE … AND succeeded IS NULL;
-- checkpoint BeforeCommit
COMMIT;   -- здесь срабатывают отложенные триггеры: запись сбалансирована? ключ завершён?
-- checkpoint AfterCommit
```

## Этап 5. Ответ

`HttpResults.ToHttp` → `201 Created`, `Location: /api/transfers/{id}`, тело `TransferResponse`. Для повтора — тот же статус и то же тело плюс заголовок `Idempotent-Replayed: true`. Метрики: `ledgercore.commands{operation=transfers.create, outcome=completed}` и длительность.

## Этап 6. После ответа (асинхронно)

Worker (`OutboxWorker` → `OutboxDispatcher`) арендует строку `outbox_messages`, доставляет её в `simulated_notifications` и помечает `processed_at`. На перевод это никак не влияет: если воркер лежит, перевод уже корректно проведён.

---

## Где запрос может упасть и чем это кончится

| Место | Что случится | Итог в базе |
|---|---|---|
| До `BEGIN` (валидация, авторизация) | 400/401/403/404 | Ничего |
| `lock_timeout` (5 с ожидания блокировки) | `503 LOCK_TIMEOUT` | Откат, ключ не занят |
| Deadlock/сбой сериализации/обрыв соединения | Автоповтор до 4 раз, потом `503 TRANSIENT_FAILURE` | Откат каждой неудачной попытки |
| Исключение до COMMIT | 500 | Откат (`await using`) |
| Процесс убит до COMMIT | Клиент без ответа | PostgreSQL откатывает при разрыве соединения |
| Процесс убит после COMMIT | Клиент без ответа | Перевод есть; повтор с тем же ключом → replay |
| Баг: несбалансированная запись | COMMIT падает на триггере | Откат |
| Баг: баланс ушёл в минус | `UPDATE` падает на CHECK | Откат |
