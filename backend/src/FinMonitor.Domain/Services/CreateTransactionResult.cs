using FinMonitor.Domain.Models;

namespace FinMonitor.Domain.Services;

public enum CreateTransactionOutcome
{
    Created,
    ValidationFailed,
    Conflict
}

public sealed record CreateTransactionResult(
    CreateTransactionOutcome Outcome,
    Transaction? Transaction,
    IReadOnlyList<string> Errors)
{
    public static CreateTransactionResult Success(Transaction transaction) =>
        new(CreateTransactionOutcome.Created, transaction, Array.Empty<string>());

    public static CreateTransactionResult Invalid(IReadOnlyList<string> errors) =>
        new(CreateTransactionOutcome.ValidationFailed, null, errors);

    public static CreateTransactionResult Duplicate() =>
        new(CreateTransactionOutcome.Conflict, null, new[] { "A transaction with this TransactionId already exists." });
}
