namespace FinMonitor.Domain.Models;

// Sequence defaults to 0 (unassigned) as constructed by validation/normalization - the
// repository stamps the real, storage-assigned monotonic value on successful insert, so
// reconnecting clients can ask "what's after sequence N" via GET /transactions/since/{seq}.
public sealed record Transaction(
    Guid TransactionId,
    decimal Amount,
    string Currency,
    TransactionStatus Status,
    DateTimeOffset Timestamp,
    long Sequence = 0);
