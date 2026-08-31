using FinMonitor.Domain.Models;

namespace FinMonitor.Domain.Validation;

// Carries the normalized Transaction alongside the pass/fail verdict, not just IsValid/Errors -
// see TransactionValidator.ValidateAndNormalize for why validation and normalization are one
// operation rather than two separately-callable ones.
public sealed record ValidationResult(bool IsValid, Transaction? Transaction, IReadOnlyList<string> Errors)
{
    public static ValidationResult Success(Transaction transaction) => new(true, transaction, Array.Empty<string>());

    public static ValidationResult Failure(IReadOnlyList<string> errors) => new(false, null, errors);
}
