namespace FinMonitor.Domain.Repositories;

// Implemented by repositories with real startup work to do (schema creation, connection
// retry) before they're safe to serve traffic against. InMemoryTransactionRepository has
// nothing to initialize and simply doesn't implement this - StorageStartupHostedService
// treats that as "already ready".
public interface IStorageInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}
