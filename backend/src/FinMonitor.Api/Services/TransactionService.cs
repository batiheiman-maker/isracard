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
    private readonly ITransactionBroadcaster _broadcaster;
    private readonly ILogger<TransactionService> _logger;
    private readonly TransactionValidator _validator = new();

    public TransactionService(ITransactionRepository repository, ITransactionBroadcaster broadcaster, ILogger<TransactionService> logger)
    {
        _repository = repository;
        _broadcaster = broadcaster;
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

        try
        {
            await _broadcaster.BroadcastAsync(stored, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to broadcast transaction {TransactionId}; it was stored but will not be pushed live.", stored.TransactionId);
        }
        return CreateTransactionResult.Success(stored);
    }

    public Task<PagedResult<Transaction>> GetRecentAsync(int limit, TransactionCursor? cursor, CancellationToken cancellationToken = default) =>
        _repository.GetRecentAsync(limit, cursor, cancellationToken);
}
