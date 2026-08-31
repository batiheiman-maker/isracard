using FinMonitor.Domain.Realtime;

namespace FinMonitor.Api.Realtime;

public sealed class TransactionBroadcastWorker : BackgroundService
{
    private readonly TransactionBroadcastQueue _queue;
    private readonly ITransactionBroadcaster _broadcaster;
    private readonly ILogger<TransactionBroadcastWorker> _logger;

    public TransactionBroadcastWorker(
        TransactionBroadcastQueue queue,
        ITransactionBroadcaster broadcaster,
        ILogger<TransactionBroadcastWorker> logger)
    {
        _queue = queue;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var transaction in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _broadcaster.BroadcastAsync(transaction, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A broadcast failure (e.g. a Redis backplane hiccup) must not take down this
                // loop - that would silently stop all future real-time delivery for every pod.
                // The transaction is already durably stored; a client that misses this specific
                // push still catches up via GET /api/transactions/since/{seq} on reconnect.
                _logger.LogWarning(ex, "Failed to broadcast transaction {TransactionId}", transaction.TransactionId);
            }
        }
    }
}
