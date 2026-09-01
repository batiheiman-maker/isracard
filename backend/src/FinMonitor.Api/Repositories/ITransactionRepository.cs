using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;

namespace FinMonitor.Domain.Repositories;

public interface ITransactionRepository
{
    // Returns the stored transaction on success, or null if transactionId already exists -
    // callers use the null-ness to distinguish success/conflict without a separate lookup.
    Task<Transaction?> TryAddAsync(Transaction transaction, CancellationToken cancellationToken = default);

    Task<Transaction?> GetByIdAsync(Guid transactionId, CancellationToken cancellationToken = default);

    // Keyset pagination ordered by (Timestamp DESC, TransactionId DESC): with cursor = null,
    // returns the first `limit` most recent rows; with a cursor (from a previous page's
    // NextCursor), returns the next `limit` rows strictly older than that cursor. See
    // TransactionCursor for why keyset beats offset/skip for a continuously-appended table.
    Task<PagedResult<Transaction>> GetRecentAsync(int limit, TransactionCursor? cursor, CancellationToken cancellationToken = default);
}
