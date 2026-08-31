using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;
using FinMonitor.Domain.Results;

namespace FinMonitor.Domain.Services;

public interface ITransactionService
{
    Task<CreateTransactionResult> CreateAsync(CreateTransactionRequest request, CancellationToken cancellationToken = default);

    Task<PagedResult<Transaction>> GetRecentAsync(int limit, TransactionCursor? cursor, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Transaction>> GetSinceAsync(long sinceSequence, CancellationToken cancellationToken = default);
}
