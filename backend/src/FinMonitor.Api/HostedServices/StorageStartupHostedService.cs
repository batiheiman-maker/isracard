using FinMonitor.Domain.Repositories;

namespace FinMonitor.Api.HostedServices;

// Runs storage initialization (Postgres: connect-retry + schema setup; in-memory: nothing) as
// a background hosted-service step instead of a blocking `.GetAwaiter().GetResult()` call in
// Program.cs's top-level code. /healthz reflects StartupHealthState, so a k8s startupProbe can
// tell "still starting" apart from "ready" without the pod ever being killed mid-retry -
// startupProbe's periodSeconds=5 * failureThreshold=15 budgets 75s, comfortably above this
// service's own worst-case ~50s retry budget (10 attempts * (3s connect timeout + 2s backoff)),
// in either "still starting" state (503, or - if this StartAsync were still in flight when
// Kestrel starts listening - connection-refused, which k8s's httpGet probe treats identically).
public sealed class StorageStartupHostedService : IHostedService
{
    private readonly ITransactionRepository _repository;
    private readonly ILogger<StorageStartupHostedService> _logger;

    public StorageStartupHostedService(
        ITransactionRepository repository,
        ILogger<StorageStartupHostedService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_repository is IStorageInitializer initializer)
        {
            _logger.LogInformation("Initializing storage...");
            await initializer.InitializeAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
