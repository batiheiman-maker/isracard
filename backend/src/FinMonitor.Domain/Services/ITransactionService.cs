using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;

namespace FinMonitor.Domain.Services;

public interface ITransactionService
{
    Task<CreateTransactionResult> CreateAsync(CreateTransactionRequest request, CancellationToken cancellationToken = default);

    IReadOnlyList<Transaction> GetAll();
}
