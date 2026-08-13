using System.Collections.Concurrent;
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

    public bool TryAdd(Transaction transaction)
    {
        if (!_store.TryAdd(transaction.TransactionId, transaction))
        {
            return false;
        }

        _insertionOrder.Enqueue(transaction.TransactionId);
        EvictExcess();
        return true;
    }

    public Transaction? GetById(Guid transactionId) =>
        _store.TryGetValue(transactionId, out var transaction) ? transaction : null;

    public IReadOnlyList<Transaction> GetAll() =>
        _store.Values.OrderByDescending(t => t.Timestamp).ToList();

    private void EvictExcess()
    {
        while (_store.Count > MaxStoredTransactions && _insertionOrder.TryDequeue(out var oldestId))
        {
            _store.TryRemove(oldestId, out _);
        }
    }
}
