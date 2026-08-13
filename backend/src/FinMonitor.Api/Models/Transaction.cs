namespace FinMonitor.Domain.Models;

public sealed record Transaction(
    Guid TransactionId,
    decimal Amount,
    string Currency,
    TransactionStatus Status,
    DateTimeOffset Timestamp);
