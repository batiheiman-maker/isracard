namespace FinMonitor.Domain.DTOs;

public sealed record ErrorResponse(IReadOnlyList<string> Errors);
