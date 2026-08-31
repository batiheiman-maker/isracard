using System.Text.RegularExpressions;
using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;

namespace FinMonitor.Domain.Validation;

public sealed class TransactionValidator
{
    // Requires 3 letters, not a real ISO 4217 code - "AAA"/"ZZZ" pass. Deliberate: the
    // assignment's own requirement is "a 3-letter currency code", not "a currency that actually
    // exists". A real ISO 4217 whitelist is a reasonable next step if that requirement changes,
    // but isn't this validator's job today.
    private static readonly Regex CurrencyPattern = new("^[A-Za-z]{3}$", RegexOptions.Compiled);

    // Validation and normalization used to be two separately-callable public methods, on the
    // unenforced assumption that callers always check Validate().IsValid before calling
    // Normalize(). Nothing stopped a direct Normalize() call on an unvalidated request, and
    // Normalize() unconditionally called request.Currency.Trim() - a NullReferenceException
    // waiting to happen, since CreateTransactionRequest.Currency is non-nullable only by its C#
    // type annotation; System.Text.Json does not enforce non-nullable reference types on
    // deserialization, so a client sending "currency": null produces a request with a genuinely
    // null Currency at runtime. Merging into one method makes that path unreachable: normalization
    // only happens after every check below (including the null/whitespace check) has passed.
    public ValidationResult ValidateAndNormalize(CreateTransactionRequest request)
    {
        var errors = new List<string>();

        if (request.TransactionId == Guid.Empty)
        {
            errors.Add("transactionId is required.");
        }

        if (request.Amount <= 0)
        {
            errors.Add("Amount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(request.Currency) || !CurrencyPattern.IsMatch(request.Currency.Trim()))
        {
            errors.Add("Currency must be a 3-letter currency code (e.g. USD).");
        }

        if (!Enum.IsDefined(typeof(TransactionStatus), request.Status))
        {
            errors.Add("Status must be one of Pending, Completed, Failed.");
        }

        if (errors.Count > 0)
        {
            return ValidationResult.Failure(errors);
        }

        var transaction = new Transaction(
            request.TransactionId,
            request.Amount,
            request.Currency.Trim().ToUpperInvariant(),
            request.Status,
            request.Timestamp ?? DateTimeOffset.UtcNow);
        return ValidationResult.Success(transaction);
    }
}
