# Карта синтаксиса C# для TypeScript-разработчика

Здесь только те конструкции, которые **реально встречаются** в LedgerCore. У каждой — пример из кода и ближайший аналог в TypeScript.

---

## Файлы, пространства имён, импорты

```csharp
using LedgerCore.Domain.Common;          // ≈ import { … } from '…' (подключает всё пространство имён)
namespace LedgerCore.Domain.Ledger;      // file-scoped namespace: всё в файле лежит в этом пространстве
```
* `namespace` похож на путь модуля, только не обязан совпадать с папкой (у нас совпадает по соглашению).
* `using static LedgerCore.UnitTests.TestData;` (в тестах) позволяет вызывать `Nok(100)` без префикса класса, как `import * as` с деструктуризацией.
* `ImplicitUsings` (в `Directory.Build.props`) автоматически подключает `System`, `System.Linq` и т. п.

## Типы: class, record, struct

| C# | Где у нас | TS-аналог |
|---|---|---|
| `sealed class TransferService(...)` | сервисы | `class` (+ `sealed` = нельзя наследовать) |
| `sealed record TransferResponse(Guid TransferId, …)` | DTO, команды | `type` + `Object.freeze`; сравнение **по значению** |
| `readonly record struct WalletId(Guid Value)` | ID в `Identifiers.cs` | брендированный тип `string & {__brand}` |
| `enum WalletStatus { Active, Frozen }` | статусы | `enum` / union `'Active' \| 'Frozen'` |
| `interface ILedgerTransaction` | порты в Application | `interface` |
| `static class LedgerRules` | чистые функции | модуль с экспортируемыми функциями |

**Positional record:** `public sealed record Posting(LedgerAccountId AccountId, PostingDirection Direction, long AmountMinor);` одной строкой объявляет конструктор, readonly-свойства, `Equals` и `ToString`.
**`with`-выражение:** `p with { Direction = p.Direction.Opposite() }` (в `JournalEntry.CreateReversal`) ≈ `{ ...p, direction: opposite(p.direction) }`.

## Primary constructors (C# 12)

```csharp
public sealed class TransferService(IdempotentCommandExecutor executor, ILedgerReadStore readStore, …)
```
Параметры доступны во всём классе, это и есть внедрение зависимостей. TS-аналог: `constructor(private readonly executor: …) {}`.

## Свойства и модификаторы

```csharp
public long MinorUnits { get; }                 // только чтение снаружи и изнутри (после конструктора)
public required string Code { get; set; }       // обязательно задать при создании объекта (Rows.cs)
public string? CorrelationId { get; set; }      // ? = может быть null (nullable reference types)
private readonly TimeSpan _retention = …;       // поле, которое нельзя переприсвоить
public const long MaxOperationMinorUnits = 100_000_000_000;   // константа; _ — разделитель цифр
```
* `T?` для ссылочных типов работает как `T | null` при `strictNullChecks`. Компилятор предупреждает, а у нас предупреждения = ошибки.
* `x!` («null-forgiving») ≈ `x!` в TS.
* `??` и `?.` работают так же, как в TS.

## Обобщения (generics)

```csharp
public sealed class Result<T> { … }
public Task<T> ExecuteAsync<T>(string operationName, Func<ILedgerTransaction, CancellationToken, Task<T>> work, …)
```
`Func<A, B, R>` ≈ `(a: A, b: B) => R`; `Action<T>` ≈ `(t: T) => void`.

## async/await, Task, CancellationToken

| C# | TS |
|---|---|
| `Task<T>` | `Promise<T>` |
| `ValueTask<T>` | `Promise<T>`, но без лишней аллокации, если результат уже готов (`IFailureInjector`) |
| `await x` | `await x` |
| `CancellationToken ct` | `AbortSignal` |
| `Task.WhenAll(tasks)` | `Promise.all(tasks)` |
| `TaskCompletionSource` | «deferred»: `new Promise(r => resolve = r)` (`CheckpointGate`, `StartGate`) |
| `Task.Run(async () => …)` | запуск в пуле потоков; в Node прямого аналога нет (там один поток) |

Важное отличие: в .NET настоящая многопоточность. Поэтому в тестах есть `Interlocked.Increment` (атомарный счётчик) и `ConcurrentBag` (потокобезопасная коллекция).

## Освобождение ресурсов: using / await using

```csharp
await using var connection = await dataSource.OpenConnectionAsync(ct);
await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
```
При выходе из блока (в том числе по исключению) вызывается `DisposeAsync()`: соединение возвращается в пул, а незакоммиченная транзакция **откатывается**. TS-аналог: `try { … } finally { await tx.rollbackIfOpen(); client.release(); }` (или `await using` из TS 5.2).

## Исключения и фильтры

```csharp
catch (Exception ex) when (TransientFailures.IsRetryable(ex) && attempt < settings.MaxTransactionAttempts)
```
`when` — фильтр: если условие ложно, `catch` пропускается, и исключение идёт к следующему `catch`. В TS так можно только через `if (...) throw e` внутри `catch`.
`throw new InvalidOperationException("…")` — для багов; ожидаемые бизнес-отказы возвращаются как `Result<T>`, а не бросаются.

## Pattern matching (сопоставление с образцом)

```csharp
public static bool IsRetryable(Exception exception) => exception switch
{
    PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } => false,     // тип + свойство
    PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } => true,
    NpgsqlException { IsTransient: true } => true,
    _ => false,                                                                     // default
};
```
≈ цепочка `if (e instanceof PostgresException && e.sqlState === '55P03') …`. Ещё примеры:
* `if (claim is IdempotencyClaim.AlreadyCompleted completed)` — проверка типа с присваиванием, как type guard в TS.
* `caller.CustomerId is not { } customerId` — «если null, то…, иначе положи значение в `customerId`».
* `limit is < 1 or > MaxPageSize` — диапазоны.

## LINQ ≈ методы массивов

| C# (LINQ) | TS |
|---|---|
| `.Where(x => …)` | `.filter(x => …)` |
| `.Select(x => …)` | `.map(x => …)` |
| `.Sum(p => p.AmountMinor)` | `.reduce((s, p) => s + p.amountMinor, 0)` |
| `.Any(char.IsControl)` | `.some(isControl)` |
| `.GroupBy(p => p.AccountId)` | группировка через `reduce` |
| `.ToArray()`, `.ToList()`, `.ToDictionary(...)` | материализация |

На EF Core `IQueryable` тот же LINQ **переводится в SQL** (`LedgerReadStore.QueryWallets`), это как Prisma `findMany({ where })`.

## Строки

* Интерполяция: `$"Wallet {id} was not found."` ≈ шаблон `` `Wallet ${id} was not found.` ``.
* Raw string literal: `"""…"""` (многострочный SQL без экранирования кавычек). В миграции используется `$"""…{table}…"""`.
* **Внимание:** в SQL мы никогда не интерполируем значения, только константные имена таблиц в миграции. Значения передаются через `@параметры`.

## Коллекции

* Collection expressions (C# 12): `[transfer.From, transfer.To]` создаёт коллекцию нужного типа, `[]` — пустую.
* `Dictionary<K, V>` ≈ `Map<K, V>`; `HashSet<T>` ≈ `Set<T>`; `IReadOnlyList<T>` ≈ `readonly T[]`.

## Арифметика денег

```csharp
newBalance = checked(Balance + netChangeMinor);   // при переполнении бросит OverflowException
```
В JS числа — `double`, поэтому при больших суммах точность теряется молча. Мы используем `long` (64-битное целое) и `checked`.

## Методы расширения (extension methods)

```csharp
public static IServiceCollection AddLedgerCoreInfrastructure(this IServiceCollection services)
```
`this` перед первым параметром позволяет вызывать `services.AddLedgerCoreInfrastructure()` как метод самого объекта. TS-аналог — функция `addLedgerCoreInfrastructure(services)`, записанная в «цепочечном» стиле.

## Внедрение зависимостей (DI)

```csharp
services.AddSingleton(...)   // один экземпляр на приложение (NpgsqlDataSource — пул соединений)
services.AddScoped<TransferService>();   // один экземпляр на HTTP-запрос
services.Replace(ServiceDescriptor.Scoped<IFailureInjector, HttpHeaderFailureInjector>());
```
≈ провайдеры NestJS (`DEFAULT` = singleton, `REQUEST` = scoped). Конструктор (primary constructor) получает зависимости автоматически.

## Конфигурация: Options pattern

```csharp
services.AddOptions<LedgerOptions>().BindConfiguration("Ledger");   // секция "Ledger" из appsettings/env
public sealed class NpgsqlLedgerTransactionRunner(…, IOptions<LedgerOptions> options)
```
Значение берётся из `appsettings.json` → `appsettings.{Environment}.json` → переменные окружения (`Ledger__LockTimeout`; двойное подчёркивание = вложенность). ≈ типизированный config-модуль, читающий `process.env`.

## Атрибуты

`[Fact]`, `[Theory]`, `[InlineData(...)]`, `[Trait("Challenge", "05")]` (xUnit), `[FromBody]`, `[FromHeader(Name = "Idempotency-Key")]` (ASP.NET) — это метаданные, как декораторы в TS (`@Get()`, `@Body()`).

## Minimal API

```csharp
transfers.MapPost("/", CreateTransfer).RequireAuthorization(Policies.Customer).RequireRateLimiting("transfers");
```
≈ `router.post('/', authorize('customer'), rateLimit('transfers'), createTransfer)` в Express.
