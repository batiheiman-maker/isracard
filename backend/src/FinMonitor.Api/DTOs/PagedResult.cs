namespace FinMonitor.Domain.DTOs;

// NextCursor is null exactly when this page came back short of the requested limit - the
// signal that there's nothing older left, without a separate COUNT(*) query.
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor);
