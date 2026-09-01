using System.Text.RegularExpressions;
using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;

namespace FinMonitor.Domain.Validation;

public sealed class TransactionValidator
{
    private static readonly Regex CurrencyPattern = new("^[A-Za-z]{3}$", RegexOptions.Compiled);
    
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
