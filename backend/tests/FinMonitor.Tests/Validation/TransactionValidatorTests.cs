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
    public void Validate_WithPositiveAmountAndValidCurrencyAndStatus_ReturnsValid()
    {
        var result = Validator.Validate(ValidRequest());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WithZeroAmount_ReturnsInvalidWithAmountError()
    {
        var result = Validator.Validate(ValidRequest(amount: 0));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Amount"));
    }

    [Fact]
    public void Validate_WithNegativeAmount_ReturnsInvalidWithAmountError()
    {
        var result = Validator.Validate(ValidRequest(amount: -5));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Amount"));
    }

    [Fact]
    public void Validate_WithEmptyCurrency_ReturnsInvalidWithCurrencyError()
    {
        var result = Validator.Validate(ValidRequest(currency: ""));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Currency"));
    }

    [Fact]
    public void Validate_WithNonThreeLetterCurrency_ReturnsInvalidWithCurrencyError()
    {
        var result = Validator.Validate(ValidRequest(currency: "US"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Currency"));
    }

    [Fact]
    public void Validate_WithLowercaseCurrency_IsValidAndWillBeNormalizedUppercase()
    {
        var result = Validator.Validate(ValidRequest(currency: "usd"));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Normalize_WithLowercaseCurrency_ReturnsUppercaseCurrency()
    {
        var request = ValidRequest(currency: "usd");

        var normalized = Validator.Normalize(request);

        normalized.Currency.Should().Be("USD");
    }

    [Fact]
    public void Normalize_WithMissingTimestamp_DefaultsToUtcNow()
    {
        var request = ValidRequest(timestamp: null);
        var before = DateTimeOffset.UtcNow;

        var normalized = Validator.Normalize(request);

        normalized.Timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Validate_WithEmptyTransactionId_ReturnsInvalidWithTransactionIdError()
    {
        var request = new CreateTransactionRequest(Guid.Empty, 10, "USD", TransactionStatus.Pending, null);

        var result = Validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("transactionId"));
    }
}
