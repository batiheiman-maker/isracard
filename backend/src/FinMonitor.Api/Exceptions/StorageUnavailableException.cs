namespace FinMonitor.Domain.Exceptions;

// Thrown by ITransactionRepository implementations when the storage backend itself is the
// problem (unreachable, connection dropped mid-query, timed out) - never for anything about the
// request being invalid. This is the one type StorageExceptionHandler needs to know about, so
// the API layer stays free of any storage-technology-specific exception type (Npgsql today,
// whatever else tomorrow).
public sealed class StorageUnavailableException : Exception
{
    public StorageUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
