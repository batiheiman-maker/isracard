using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;
using FinMonitor.Domain.Validation;
using FluentAssertions;

namespace FinMonitor.Tests.Validation;

public class TransactionValidatorTests
{
    private static readonly TransactionValidator Validator = new();

    private static CreateTransactionRequest ValidRequest(
        decimal amount = 1500.50m,
        string currency = "USD",
        TransactionStatus status = TransactionStatus.Completed,
        DateTimeOffset? timestamp = null) =>
        new(Guid.NewGuid(), amount, currency, status, timestamp);

    [Fact]
    public void ValidateAndNormalize_WithPositiveAmountAndValidCurrencyAndStatus_ReturnsValidWithTransaction()
    {
        var result = Validator.ValidateAndNormalize(ValidRequest());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        result.Transaction.Should().NotBeNull();
    }

    [Fact]
    public void ValidateAndNormalize_WithZeroAmount_ReturnsInvalidWithAmountErrorAndNoTransaction()
    {
        var result = Validator.ValidateAndNormalize(ValidRequest(amount: 0));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Amount"));
        result.Transaction.Should().BeNull();
    }

    [Fact]
    public void ValidateAndNormalize_WithNegativeAmount_ReturnsInvalidWithAmountError()
    {
        var result = Validator.ValidateAndNormalize(ValidRequest(amount: -5));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Amount"));
    }

    [Fact]
    public void ValidateAndNormalize_WithEmptyCurrency_ReturnsInvalidWithCurrencyError()
    {
        var result = Validator.ValidateAndNormalize(ValidRequest(currency: ""));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Currency"));
    }

    [Fact]
    public void ValidateAndNormalize_WithNullCurrency_ReturnsInvalidWithCurrencyErrorAndDoesNotThrow()
    {
        // CreateTransactionRequest.Currency is non-nullable only by its C# type annotation -
        // System.Text.Json does not enforce non-nullable reference types on deserialization, so
        // a client sending `"currency": null` produces a request with a genuinely null Currency
        // at runtime. The `!` here simulates exactly that (bypassing the compile-time check the
        // real HTTP pipeline can't rely on either). Before merging Validate+Normalize, this threw
        // a NullReferenceException instead of returning a validation error.
        var request = ValidRequest(currency: null!);

        var act = () => Validator.ValidateAndNormalize(request);

        act.Should().NotThrow();
        var result = act();
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Currency"));
        result.Transaction.Should().BeNull();
    }

    [Fact]
    public void ValidateAndNormalize_WithNonThreeLetterCurrency_ReturnsInvalidWithCurrencyError()
    {
        var result = Validator.ValidateAndNormalize(ValidRequest(currency: "US"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Currency"));
    }

    [Fact]
    public void ValidateAndNormalize_WithLowercaseCurrency_IsValidAndNormalizedToUppercase()
    {
        var result = Validator.ValidateAndNormalize(ValidRequest(currency: "usd"));

        result.IsValid.Should().BeTrue();
        result.Transaction!.Currency.Should().Be("USD");
    }

    [Fact]
    public void ValidateAndNormalize_WithMissingTimestamp_DefaultsToUtcNow()
    {
        var request = ValidRequest(timestamp: null);
        var before = DateTimeOffset.UtcNow;

        var result = Validator.ValidateAndNormalize(request);

        result.Transaction!.Timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ValidateAndNormalize_WithEmptyTransactionId_ReturnsInvalidWithTransactionIdError()
    {
        var request = new CreateTransactionRequest(Guid.Empty, 10, "USD", TransactionStatus.Pending, null);

        var result = Validator.ValidateAndNormalize(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("transactionId"));
    }
}
