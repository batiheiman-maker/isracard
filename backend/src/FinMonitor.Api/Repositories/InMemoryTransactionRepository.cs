using System.Collections.Concurrent;
using FinMonitor.Domain.Models;

namespace FinMonitor.Domain.Repositories;

public sealed class InMemoryTransactionRepository : ITransactionRepository
{
    private readonly ConcurrentDictionary<Guid, Transaction> _store = new();

    public bool TryAdd(Transaction transaction) => _store.TryAdd(transaction.TransactionId, transaction);

    public Transaction? GetById(Guid transactionId) =>
        _store.TryGetValue(transactionId, out var transaction) ? transaction : null;

    public IReadOnlyList<Transaction> GetAll() =>
        _store.Values.OrderByDescending(t => t.Timestamp).ToList();
}
