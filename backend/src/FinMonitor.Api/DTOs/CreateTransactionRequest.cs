using FinMonitor.Domain.Models;

namespace FinMonitor.Domain.DTOs;

public sealed record CreateTransactionRequest(
    Guid TransactionId,
    decimal Amount,
    string Currency,
    TransactionStatus Status,
    DateTimeOffset? Timestamp);
