using FinMonitor.Domain.Models;
using FinMonitor.Domain.Repositories;
using FluentAssertions;
using Testcontainers.PostgreSql;

namespace FinMonitor.Tests.Repositories;

// Runs against a real, disposable Postgres container per test (via Testcontainers) rather than
// a mock or an in-memory substitute - the whole point is proving concurrent-writer behavior
// that only a real multi-writer database can demonstrate, which SQLite fundamentally cannot
// (see the ADR in README.md for why SQLite over a shared volume was replaced by this).
public class PostgresTransactionRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private string ConnectionString => _container.GetConnectionString();

    // Guarded even though [DockerRequiredFact] already prevents these tests from running at all
    // without Docker - defense in depth, in case a future xUnit version invokes IAsyncLifetime
    // before checking Skip.
    public Task InitializeAsync() => DockerAvailability.IsAvailable ? _container.StartAsync() : Task.CompletedTask;

    public Task DisposeAsync() => DockerAvailability.IsAvailable ? _container.DisposeAsync().AsTask() : Task.CompletedTask;

    private static Transaction MakeTransaction(Guid? id = null, decimal amount = 100m) => new(
        id ?? Guid.NewGuid(), amount, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

    [DockerRequiredFact]
    public async Task TryAdd_NewTransaction_ReturnsTrueAndCanBeRetrievedById()
    {
        var repo = await PostgresTransactionRepository.CreateAsync(ConnectionString);
        var transaction = MakeTransaction();

        var added = repo.TryAdd(transaction);

        added.Should().BeTrue();
        var retrieved = repo.GetById(transaction.TransactionId);
        retrieved.Should().NotBeNull();
        retrieved!.TransactionId.Should().Be(transaction.TransactionId);
        retrieved.Amount.Should().Be(transaction.Amount);
        retrieved.Currency.Should().Be(transaction.Currency);
        retrieved.Status.Should().Be(transaction.Status);
        // Postgres timestamptz is microsecond-precision; .NET DateTimeOffset is tick-precision
        // (100ns) - round-tripping loses the last digit, so compare with a small tolerance.
        retrieved.Timestamp.Should().BeCloseTo(transaction.Timestamp, TimeSpan.FromMilliseconds(1));
    }

    [DockerRequiredFact]
    public async Task TryAdd_DuplicateTransactionId_ReturnsFalseAndDoesNotOverwrite()
    {
        var repo = await PostgresTransactionRepository.CreateAsync(ConnectionString);
        var id = Guid.NewGuid();

        repo.TryAdd(MakeTransaction(id, amount: 100m));
        var addedDuplicate = repo.TryAdd(MakeTransaction(id, amount: 999m));

        addedDuplicate.Should().BeFalse();
        repo.GetById(id)!.Amount.Should().Be(100m);
    }

    [DockerRequiredFact]
    public async Task GetById_UnknownId_ReturnsNull()
    {
        var repo = await PostgresTransactionRepository.CreateAsync(ConnectionString);

        repo.GetById(Guid.NewGuid()).Should().BeNull();
    }

    [DockerRequiredFact]
    public async Task DataWrittenByOneInstance_IsVisibleToAnotherInstanceOnSameDatabase_SimulatingSharedDbAcrossPods()
    {
        var podA = await PostgresTransactionRepository.CreateAsync(ConnectionString);
        var podB = await PostgresTransactionRepository.CreateAsync(ConnectionString);
        var transaction = MakeTransaction();

        podA.TryAdd(transaction);

        var retrieved = podB.GetById(transaction.TransactionId);
        retrieved.Should().NotBeNull();
        retrieved!.TransactionId.Should().Be(transaction.TransactionId);
        podB.GetAll().Should().ContainSingle();
    }

    [DockerRequiredFact]
    public async Task TryAdd_With200ConcurrentUniqueTransactionsAcrossMultipleInstances_AllStoredWithNoLostUpdates()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToList();
        var repos = await Task.WhenAll(ids.Select(_ => PostgresTransactionRepository.CreateAsync(ConnectionString)));

        await Task.WhenAll(ids.Select((id, i) => Task.Run(() => repos[i].TryAdd(MakeTransaction(id)))));

        var verifier = await PostgresTransactionRepository.CreateAsync(ConnectionString);
        verifier.GetAll().Should().HaveCount(200);
        ids.Should().OnlyContain(id => verifier.GetById(id) != null);
    }

    [DockerRequiredFact]
    public async Task TryAdd_With50ConcurrentDuplicateTransactionIds_OnlyOneSucceeds()
    {
        var id = Guid.NewGuid();
        var repos = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => PostgresTransactionRepository.CreateAsync(ConnectionString)));

        var results = await Task.WhenAll(repos.Select(r => Task.Run(() => r.TryAdd(MakeTransaction(id)))));

        results.Count(succeeded => succeeded).Should().Be(1);
    }
}
