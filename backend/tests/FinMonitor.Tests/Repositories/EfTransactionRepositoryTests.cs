using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;
using FinMonitor.Domain.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Testcontainers.PostgreSql;

namespace FinMonitor.Tests.Repositories;

// Runs against a real, disposable Postgres container per test (via Testcontainers) rather than
// a mock or an in-memory substitute - the whole point is proving concurrent-writer behavior
// that only a real multi-writer database can demonstrate, in particular that the
// DbUpdateException/unique-violation path is as race-safe as an explicit ON CONFLICT DO NOTHING
// would be under concurrent duplicate inserts.
public class EfTransactionRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => DockerAvailability.IsAvailable ? _container.StartAsync() : Task.CompletedTask;

    public Task DisposeAsync() => DockerAvailability.IsAvailable ? _container.DisposeAsync().AsTask() : Task.CompletedTask;

    private static Transaction MakeTransaction(Guid? id = null, decimal amount = 100m, DateTimeOffset? timestamp = null) => new(
        id ?? Guid.NewGuid(), amount, "USD", TransactionStatus.Completed, timestamp ?? DateTimeOffset.UtcNow);

    private EfTransactionRepository CreateRepository()
    {
        var options = new DbContextOptionsBuilder<FinMonitorDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        var contextFactory = new PooledDbContextFactory<FinMonitorDbContext>(options);
        return new EfTransactionRepository(contextFactory, ConnectionString);
    }

    private async Task<EfTransactionRepository> CreateInitializedRepositoryAsync()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync(CancellationToken.None);
        return repository;
    }

    [DockerRequiredFact]
    public async Task TryAddAsync_NewTransaction_ReturnsStoredTransactionAndCanBeRetrievedById()
    {
        var repo = await CreateInitializedRepositoryAsync();
        var transaction = MakeTransaction();

        var stored = await repo.TryAddAsync(transaction);

        stored.Should().NotBeNull();
        var retrieved = await repo.GetByIdAsync(transaction.TransactionId);
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
    public async Task TryAddAsync_DuplicateTransactionId_ReturnsNullAndDoesNotOverwrite()
    {
        var repo = await CreateInitializedRepositoryAsync();
        var id = Guid.NewGuid();

        await repo.TryAddAsync(MakeTransaction(id, amount: 100m));
        var addedDuplicate = await repo.TryAddAsync(MakeTransaction(id, amount: 999m));

        addedDuplicate.Should().BeNull();
        (await repo.GetByIdAsync(id))!.Amount.Should().Be(100m);
    }

    [DockerRequiredFact]
    public async Task GetByIdAsync_UnknownId_ReturnsNull()
    {
        var repo = await CreateInitializedRepositoryAsync();

        (await repo.GetByIdAsync(Guid.NewGuid())).Should().BeNull();
    }

    [DockerRequiredFact]
    public async Task GetRecentAsync_WithLimitLowerThanStoredCount_ReturnsOnlyLimitRowsAndANextCursor()
    {
        var repo = await CreateInitializedRepositoryAsync();
        for (var i = 0; i < 5; i++)
        {
            await repo.TryAddAsync(MakeTransaction());
        }

        var page = await repo.GetRecentAsync(3, cursor: null);

        page.Items.Should().HaveCount(3);
        page.NextCursor.Should().NotBeNull();
    }

    [DockerRequiredFact]
    public async Task GetRecentAsync_PagingThroughWithCursor_CoversEveryRowExactlyOnceInDescendingOrder()
    {
        // Exercises the real EF/Npgsql-translated cursor predicate against actual Postgres, not
        // just the equivalent LINQ-to-Objects used by the in-memory repository - if
        // Guid.CompareTo doesn't translate the way this test assumes, this is what catches it.
        var repo = await CreateInitializedRepositoryAsync();
        var now = DateTimeOffset.UtcNow;
        var expectedIds = new List<Guid>();
        for (var i = 0; i < 9; i++)
        {
            var stored = await repo.TryAddAsync(MakeTransaction(timestamp: now.AddSeconds(-i)));
            expectedIds.Add(stored!.TransactionId);
        }

        var collected = new List<Guid>();
        TransactionCursor? cursor = null;
        do
        {
            var page = await repo.GetRecentAsync(4, cursor);
            collected.AddRange(page.Items.Select(t => t.TransactionId));
            cursor = TransactionCursor.TryParse(page.NextCursor, out var next) ? next : null;
        } while (cursor is not null);

        collected.Should().Equal(expectedIds);
    }

    [DockerRequiredFact]
    public async Task DataWrittenByOneInstance_IsVisibleToAnotherInstanceOnSameDatabase_SimulatingSharedDbAcrossPods()
    {
        var podA = await CreateInitializedRepositoryAsync();
        var podB = CreateRepository();
        var transaction = MakeTransaction();

        await podA.TryAddAsync(transaction);

        var retrieved = await podB.GetByIdAsync(transaction.TransactionId);
        retrieved.Should().NotBeNull();
        retrieved!.TransactionId.Should().Be(transaction.TransactionId);
        (await podB.GetRecentAsync(500, cursor: null)).Items.Should().ContainSingle();
    }

    [DockerRequiredFact]
    public async Task TryAddAsync_With200ConcurrentUniqueTransactionsAcrossMultipleInstances_AllStoredWithNoLostUpdates()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToList();
        var initialized = await CreateInitializedRepositoryAsync();
        var repos = ids.Select(_ => CreateRepository()).ToList();
        repos[0] = initialized;

        await Task.WhenAll(ids.Select((id, i) => Task.Run(() => repos[i].TryAddAsync(MakeTransaction(id)))));

        var verifier = repos[0];
        (await verifier.GetRecentAsync(500, cursor: null)).Items.Should().HaveCount(200);
        foreach (var id in ids)
        {
            (await verifier.GetByIdAsync(id)).Should().NotBeNull();
        }
    }

    [DockerRequiredFact]
    public async Task TryAddAsync_With50ConcurrentDuplicateTransactionIds_OnlyOneSucceeds()
    {
        var id = Guid.NewGuid();
        var initialized = await CreateInitializedRepositoryAsync();
        var repos = Enumerable.Range(0, 50).Select(_ => CreateRepository()).ToList();
        repos[0] = initialized;

        var results = await Task.WhenAll(repos.Select(r => Task.Run(() => r.TryAddAsync(MakeTransaction(id)))));

        results.Count(stored => stored is not null).Should().Be(1);
    }
}
