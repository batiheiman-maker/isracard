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

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private static Transaction MakeTransaction(Guid? id = null, decimal amount = 100m) => new(
        id ?? Guid.NewGuid(), amount, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

    [Fact]
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

    [Fact]
    public async Task TryAdd_DuplicateTransactionId_ReturnsFalseAndDoesNotOverwrite()
    {
        var repo = await PostgresTransactionRepository.CreateAsync(ConnectionString);
        var id = Guid.NewGuid();

        repo.TryAdd(MakeTransaction(id, amount: 100m));
        var addedDuplicate = repo.TryAdd(MakeTransaction(id, amount: 999m));

        addedDuplicate.Should().BeFalse();
        repo.GetById(id)!.Amount.Should().Be(100m);
    }

    [Fact]
    public async Task GetById_UnknownId_ReturnsNull()
    {
        var repo = await PostgresTransactionRepository.CreateAsync(ConnectionString);

        repo.GetById(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
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

    [Fact]
    public async Task TryAdd_With200ConcurrentUniqueTransactionsAcrossMultipleInstances_AllStoredWithNoLostUpdates()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToList();
        var repos = await Task.WhenAll(ids.Select(_ => PostgresTransactionRepository.CreateAsync(ConnectionString)));

        await Task.WhenAll(ids.Select((id, i) => Task.Run(() => repos[i].TryAdd(MakeTransaction(id)))));

        var verifier = await PostgresTransactionRepository.CreateAsync(ConnectionString);
        verifier.GetAll().Should().HaveCount(200);
        ids.Should().OnlyContain(id => verifier.GetById(id) != null);
    }

    [Fact]
    public async Task TryAdd_With50ConcurrentDuplicateTransactionIds_OnlyOneSucceeds()
    {
        var id = Guid.NewGuid();
        var repos = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => PostgresTransactionRepository.CreateAsync(ConnectionString)));

        var results = await Task.WhenAll(repos.Select(r => Task.Run(() => r.TryAdd(MakeTransaction(id)))));

        results.Count(succeeded => succeeded).Should().Be(1);
    }
}
