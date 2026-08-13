using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;
using FinMonitor.Domain.Realtime;
using FinMonitor.Domain.Repositories;
using FinMonitor.Domain.Validation;

namespace FinMonitor.Domain.Services;

public sealed class TransactionService : ITransactionService
{
    private readonly ITransactionRepository _repository;
    private readonly ITransactionBroadcaster _broadcaster;
    private readonly TransactionValidator _validator = new();

    public TransactionService(ITransactionRepository repository, ITransactionBroadcaster broadcaster)
    {
        _repository = repository;
        _broadcaster = broadcaster;
    }

    public async Task<CreateTransactionResult> CreateAsync(CreateTransactionRequest request, CancellationToken cancellationToken = default)
    {
        var validation = _validator.Validate(request);
        if (!validation.IsValid)
        {
            return CreateTransactionResult.Invalid(validation.Errors);
        }

        var transaction = _validator.Normalize(request);
        if (!_repository.TryAdd(transaction))
        {
            return CreateTransactionResult.Duplicate();
        }

        await _broadcaster.BroadcastAsync(transaction, cancellationToken);
        return CreateTransactionResult.Success(transaction);
    }

    public IReadOnlyList<Transaction> GetAll() => _repository.GetAll();
}
