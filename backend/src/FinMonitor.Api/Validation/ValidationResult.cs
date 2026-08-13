namespace FinMonitor.Domain.Validation;

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ValidationResult Success() => new(true, Array.Empty<string>());

    public static ValidationResult Failure(params string[] errors) => new(false, errors);
}
