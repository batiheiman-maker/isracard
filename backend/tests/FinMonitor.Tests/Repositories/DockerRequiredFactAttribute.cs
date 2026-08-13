namespace FinMonitor.Tests.Repositories;

// A [Fact] that reports as "Skipped" instead of failing when Docker isn't available - xUnit
// evaluates Skip at test discovery, before the test method (or IAsyncLifetime.InitializeAsync)
// ever runs, so no container start is attempted at all.
public sealed class DockerRequiredFactAttribute : FactAttribute
{
    public DockerRequiredFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = "Docker is not available - skipping this Testcontainers-backed Postgres test.";
        }
    }
}
