using FinMonitor.Domain.Models;

namespace FinMonitor.Domain.Repositories;

public interface ITransactionRepository
{
    bool TryAdd(Transaction transaction);

    Transaction? GetById(Guid transactionId);

    IReadOnlyList<Transaction> GetAll();
}
