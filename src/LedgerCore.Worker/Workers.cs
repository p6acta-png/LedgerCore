using System.Diagnostics;
using LedgerCore.Application.Reconciliation;
using LedgerCore.Application.Security;
using LedgerCore.Infrastructure.Outbox;
using Microsoft.Extensions.Options;

namespace LedgerCore.Worker;

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    public TimeSpan IdempotencyCleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Zero disables scheduled reconciliation.</summary>
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromMinutes(15);

    public bool FreezeWalletsOnReconciliationMismatch { get; set; } = true;
}

/// <summary>
/// Polls the transactional outbox. Several instances may run at once: FOR UPDATE SKIP LOCKED plus leases
/// split the work between them, and a crashed instance's leases expire so others pick its messages up.
/// </summary>
internal sealed class OutboxWorker(IServiceScopeFactory scopes, IOptions<OutboxOptions> options, ILogger<OutboxWorker> logger) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox worker {WorkerId} started", _workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            var leased = 0;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                leased = await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(_workerId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Typically the database being unavailable. Messages stay in the outbox; nothing is lost.
                logger.LogError(ex, "Outbox dispatch cycle failed; will retry after the poll interval");
            }

            if (leased == 0)
            {
                await DelayAsync(options.Value.PollInterval, stoppingToken);
            }
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down; the loop condition ends the worker.
        }
    }
}

/// <summary>Enforces the idempotency retention window by deleting expired records.</summary>
internal sealed class IdempotencyCleanupWorker(
    IServiceScopeFactory scopes, IOptions<WorkerOptions> options, TimeProvider clock, ILogger<IdempotencyCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.IdempotencyCleanupInterval, clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IdempotencyCleaner>().DeleteExpiredAsync(clock.GetUtcNow(), 1000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Idempotency cleanup failed; will retry on the next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>Periodically recomputes all balances from the journal and alerts on any discrepancy.</summary>
internal sealed class ReconciliationWorker(
    IServiceScopeFactory scopes, IOptions<WorkerOptions> options, TimeProvider clock, ILogger<ReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (settings.ReconciliationInterval <= TimeSpan.Zero)
        {
            logger.LogInformation("Scheduled reconciliation is disabled");
            return;
        }

        using var timer = new PeriodicTimer(settings.ReconciliationInterval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ReconciliationService>();
                var report = await service.RunAsync(CallerContext.System("scheduled-reconciliation"), settings.FreezeWalletsOnReconciliationMismatch, stoppingToken);
                if (report.IsSuccess && report.Value.IsConsistent)
                {
                    logger.LogInformation("Scheduled reconciliation: ledger consistent ({Accounts} accounts)", report.Value.AccountsChecked);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled reconciliation failed");
            }
        }
    }
}

public sealed class WorkerFailureInjectionOptions
{
    public const string SectionName = "WorkerFailureInjection";

    /// <summary>
    /// When greater than zero, the process kills itself right after the Nth successful delivery and
    /// before acknowledging it: a real crash between "delivered" and "marked processed". Restricted to
    /// Development/Testing.
    /// </summary>
    public int KillAfterDeliveries { get; set; }
}

internal sealed class CrashAfterDeliveriesSink(INotificationSink inner, int killAfter) : INotificationSink
{
    private int _delivered;

    public async Task DeliverAsync(LeasedOutboxMessage message, CancellationToken cancellationToken)
    {
        await inner.DeliverAsync(message, cancellationToken);
        if (Interlocked.Increment(ref _delivered) >= killAfter)
        {
            Console.Error.WriteLine($"[failure-injection] Killing worker {Environment.ProcessId} after delivering {message.Id}, before acknowledging it.");
            Console.Error.Flush();
            Process.GetCurrentProcess().Kill();
        }
    }
}
