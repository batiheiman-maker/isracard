using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;
using FinMonitor.Domain.Realtime;
using FinMonitor.Domain.Repositories;
using FinMonitor.Domain.Results;
using FinMonitor.Domain.Validation;

namespace FinMonitor.Domain.Services;

public sealed class TransactionService : ITransactionService
{
    private readonly ITransactionRepository _repository;
    private readonly TransactionBroadcastQueue _broadcastQueue;
    private readonly ILogger<TransactionService> _logger;
    private readonly TransactionValidator _validator = new();

    public TransactionService(ITransactionRepository repository, TransactionBroadcastQueue broadcastQueue, ILogger<TransactionService> logger)
    {
        _repository = repository;
        _broadcastQueue = broadcastQueue;
        _logger = logger;
    }

    public async Task<CreateTransactionResult> CreateAsync(CreateTransactionRequest request, CancellationToken cancellationToken = default)
    {
        var validation = _validator.ValidateAndNormalize(request);
        if (!validation.IsValid)
        {
            return CreateTransactionResult.Invalid(validation.Errors);
        }

        var stored = await _repository.TryAddAsync(validation.Transaction!, cancellationToken);
        if (stored is null)
        {
            return CreateTransactionResult.Duplicate();
        }

        // Enqueue, don't await a broadcast: the write above is the durable, authoritative step
        // and must complete fast regardless of whether the real-time layer is fast, slow, or
        // momentarily down. TransactionBroadcastWorker drains this queue independently - see
        // its own comments for why a broadcast failure must never affect this request.
        if (!_broadcastQueue.TryEnqueue(stored))
        {
            // The transaction is already durably stored, so this request still succeeds - real-time
            // delivery is best-effort by design (see above). TryEnqueue only fails if the channel's
            // writer has been completed (e.g. mid-shutdown); DropOldest means it never fails from
            // being "full". Log it so a failure here is visible somewhere instead of silent.
            _logger.LogWarning(
                "Failed to enqueue transaction {TransactionId} for broadcast; it was stored but will not be pushed live.",
                stored.TransactionId);
        }
        return CreateTransactionResult.Success(stored);
    }

    public Task<PagedResult<Transaction>> GetRecentAsync(int limit, TransactionCursor? cursor, CancellationToken cancellationToken = default) =>
        _repository.GetRecentAsync(limit, cursor, cancellationToken);

    public Task<IReadOnlyList<Transaction>> GetSinceAsync(long sinceSequence, CancellationToken cancellationToken = default) =>
        _repository.GetSinceAsync(sinceSequence, cancellationToken);
}
