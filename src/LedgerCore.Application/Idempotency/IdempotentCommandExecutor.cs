using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Common;
using LedgerCore.Domain.Common;
using Microsoft.Extensions.Options;

namespace LedgerCore.Application.Idempotency;

/// <summary>
/// Shared skeleton of every money-moving command:
///
///   BEGIN (READ COMMITTED)
///     claim idempotency key            -- first statement; duplicates block here on the primary key
///       already completed?  -> same payload: replay stored outcome; different payload: reject
///     run the command body             -- lock accounts, decide, write journal + projections
///     store the outcome on the key     -- success *and* business rejections
///   COMMIT                             -- outcome and ledger change become visible atomically
///
/// Because the key and the ledger change commit in the same transaction, there is no window in which
/// a transfer exists without its idempotency record or vice versa.
/// </summary>
public sealed class IdempotentCommandExecutor(
    ILedgerTransactionRunner runner,
    TimeProvider clock,
    IOptions<IdempotencyOptions> options)
{
    private readonly TimeSpan _retention = options.Value.Retention;

    public Task<CommandResult<T>> ExecuteAsync<T>(
        IdempotencyScope scope,
        string requestHash,
        Func<T, Guid?> resourceIdOf,
        Func<ILedgerTransaction, CancellationToken, Task<Result<T>>> body,
        CancellationToken cancellationToken)
    {
        return runner.ExecuteAsync(scope.Operation, async (tx, ct) =>
        {
            var now = clock.GetUtcNow();
            var claim = await tx.ClaimIdempotencyKeyAsync(scope, requestHash, now, now + _retention, ct);

            if (claim is IdempotencyClaim.AlreadyCompleted completed)
            {
                return string.Equals(completed.RequestHash, requestHash, StringComparison.Ordinal)
                    ? new CommandResult<T>(completed.Outcome.ToResult<T>(), Replayed: true)
                    : CommandResult<T>.Rejected(ApplicationErrors.IdempotencyKeyReused);
            }

            await tx.CheckpointAsync(FailurePoint.AfterIdempotencyClaim, ct);

            var result = await body(tx, ct);

            await tx.CompleteIdempotencyKeyAsync(scope, StoredOutcome.From(result, resourceIdOf), clock.GetUtcNow(), ct);
            return CommandResult<T>.Fresh(result);
        }, cancellationToken);
    }
}
