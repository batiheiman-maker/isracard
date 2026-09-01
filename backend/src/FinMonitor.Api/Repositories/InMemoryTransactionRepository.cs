using System.Collections.Concurrent;
using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;

namespace FinMonitor.Domain.Repositories;

public sealed class InMemoryTransactionRepository : ITransactionRepository
{
    // This store represents "latest transactions", not a historical record - it must stay
    // bounded so a long-running single-instance process can't leak memory without limit.
    // Size-based eviction (drop oldest) rather than TTL: the assignment's own dashboard use
    // case cares about a rolling window of recent activity, not how long an entry has existed.
    private const int MaxStoredTransactions = 5_000;

    private readonly ConcurrentDictionary<Guid, Transaction> _store = new();

    // Tracks insertion order for eviction. A ConcurrentDictionary alone can't answer "which
    // entry is oldest" without an O(n) scan, so this ConcurrentQueue - itself thread-safe -
    // tracks it in O(1). It can transiently hold ids already removed by a racing eviction;
    // TryRemove below is a no-op for those, which is harmless.
    private readonly ConcurrentQueue<Guid> _insertionOrder = new();

    public Task<Transaction?> TryAddAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        if (!_store.TryAdd(transaction.TransactionId, transaction))
        {
            return Task.FromResult<Transaction?>(null);
        }

        _insertionOrder.Enqueue(transaction.TransactionId);
        EvictExcess();
        return Task.FromResult<Transaction?>(transaction);
    }

    public Task<Transaction?> GetByIdAsync(Guid transactionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.TryGetValue(transactionId, out var transaction) ? transaction : null);

    public Task<PagedResult<Transaction>> GetRecentAsync(int limit, TransactionCursor? cursor, CancellationToken cancellationToken = default)
    {
        IEnumerable<Transaction> query = _store.Values;
        if (cursor is { } c)
        {
            // Strictly "older" than the cursor row in the same (Timestamp DESC, TransactionId
            // DESC) order GetRecentAsync sorts by - Guid.CompareTo doesn't need to match
            // Postgres's own UUID ordering, since single-instance mode never talks to Postgres.
            query = query.Where(t =>
                t.Timestamp < c.Timestamp ||
                (t.Timestamp == c.Timestamp && t.TransactionId.CompareTo(c.TransactionId) < 0));
        }

        var page = query
            .OrderByDescending(t => t.Timestamp)
            .ThenByDescending(t => t.TransactionId)
            .Take(limit)
            .ToList();

        // A full page (Count == limit) means there could be more; a short page means we hit the
        // end - avoids a separate COUNT query just to know whether to hand back a NextCursor.
        string? nextCursor = page.Count == limit
            ? new TransactionCursor(page[^1].Timestamp, page[^1].TransactionId).Encode()
            : null;

        return Task.FromResult(new PagedResult<Transaction>(page, nextCursor));
    }

    private void EvictExcess()
    {
        while (_store.Count > MaxStoredTransactions && _insertionOrder.TryDequeue(out var oldestId))
        {
            _store.TryRemove(oldestId, out _);
        }
    }
}
