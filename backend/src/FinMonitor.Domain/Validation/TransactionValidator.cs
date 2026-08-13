using System.Text.RegularExpressions;
using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;

namespace FinMonitor.Domain.Validation;

public sealed class TransactionValidator
{
    private static readonly Regex CurrencyPattern = new("^[A-Za-z]{3}$", RegexOptions.Compiled);

    public ValidationResult Validate(CreateTransactionRequest request)
    {
        var errors = new List<string>();

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

        return errors.Count == 0 ? ValidationResult.Success() : ValidationResult.Failure(errors.ToArray());
    }

    public Transaction Normalize(CreateTransactionRequest request) => new(
        request.TransactionId ?? Guid.NewGuid(),
        request.Amount,
        request.Currency.Trim().ToUpperInvariant(),
        request.Status,
        request.Timestamp ?? DateTimeOffset.UtcNow);
}
