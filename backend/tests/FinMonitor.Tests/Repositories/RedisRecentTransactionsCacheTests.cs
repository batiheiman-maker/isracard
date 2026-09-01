using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;
using FinMonitor.Domain.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace FinMonitor.Tests.Repositories;

// Runs against a real, disposable Redis container per test (via Testcontainers) rather than a
// mock - proving the cache-hit/cache-miss/warm/trim behavior against a real List, not just an
// in-memory stand-in. InMemoryTransactionRepository stands in for the durable store here (a
// Postgres/EF backend would behave identically from this decorator's point of view - it never
// sees which one it's wrapping) so these tests stay focused on the Redis-side behavior alone.
public class RedisRecentTransactionsCacheTests : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7-alpine").Build();

    public Task InitializeAsync() => DockerAvailability.IsAvailable ? _container.StartAsync() : Task.CompletedTask;

    public Task DisposeAsync() => DockerAvailability.IsAvailable ? _container.DisposeAsync().AsTask() : Task.CompletedTask;

    private static Transaction MakeTransaction(Guid? id = null, DateTimeOffset? timestamp = null) => new(
        id ?? Guid.NewGuid(), 100m, "USD", TransactionStatus.Completed, timestamp ?? DateTimeOffset.UtcNow);

    private IConnectionMultiplexer Connect() => ConnectionMultiplexer.Connect(_container.GetConnectionString());

    private RedisRecentTransactionsCache CreateCache(ITransactionRepository inner, IConnectionMultiplexer? redis = null) =>
        new(inner, redis ?? Connect(), NullLogger<RedisRecentTransactionsCache>.Instance);

    // Wraps a real repository and counts GetRecentAsync calls, so a test can prove a read was
    // actually served from Redis rather than silently falling through to the inner store.
    private sealed class CountingTransactionRepository : ITransactionRepository
    {
        private readonly ITransactionRepository _inner;
        public int GetRecentAsyncCallCount { get; private set; }

        public CountingTransactionRepository(ITransactionRepository inner) => _inner = inner;

        public Task<Transaction?> TryAddAsync(Transaction transaction, CancellationToken cancellationToken = default) =>
            _inner.TryAddAsync(transaction, cancellationToken);

        public Task<Transaction?> GetByIdAsync(Guid transactionId, CancellationToken cancellationToken = default) =>
            _inner.GetByIdAsync(transactionId, cancellationToken);

        public Task<PagedResult<Transaction>> GetRecentAsync(int limit, TransactionCursor? cursor, CancellationToken cancellationToken = default)
        {
            GetRecentAsyncCallCount++;
            return _inner.GetRecentAsync(limit, cursor, cancellationToken);
        }
    }

    [DockerRequiredFact]
    public async Task GetRecentAsync_AfterTryAddAsync_ServesFromCacheWithoutQueryingTheInnerRepositoryAgain()
    {
        var spy = new CountingTransactionRepository(new InMemoryTransactionRepository());
        var cache = CreateCache(spy);
        var transaction = MakeTransaction();

        await cache.TryAddAsync(transaction);
        var page = await cache.GetRecentAsync(500, cursor: null);

        page.Items.Should().ContainSingle(t => t.TransactionId == transaction.TransactionId);
        // TryAddAsync warms the cache directly - GetRecentAsync should be a pure cache hit,
        // never falling through to the inner repository at all.
        spy.GetRecentAsyncCallCount.Should().Be(0);
    }

    [DockerRequiredFact]
    public async Task GetRecentAsync_OnColdCache_FallsBackToInnerRepositoryAndWarmsTheCache()
    {
        var inner = new InMemoryTransactionRepository();
        var seeded = MakeTransaction();
        await inner.TryAddAsync(seeded); // written directly to the inner store, bypassing the cache

        var spy = new CountingTransactionRepository(inner);
        var cache = CreateCache(spy);

        var firstRead = await cache.GetRecentAsync(500, cursor: null);
        firstRead.Items.Should().ContainSingle(t => t.TransactionId == seeded.TransactionId);
        spy.GetRecentAsyncCallCount.Should().Be(1); // cold cache -> one fallback to the DB

        var secondRead = await cache.GetRecentAsync(500, cursor: null);
        secondRead.Items.Should().ContainSingle(t => t.TransactionId == seeded.TransactionId);
        // The fallback above warmed the cache - this second read must be a pure cache hit.
        spy.GetRecentAsyncCallCount.Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task TryAddAsync_BeyondCacheCapacity_TrimsTheOldestFromTheCache()
    {
        const int capacity = 1_000;
        const int overflow = 50;
        var inner = new InMemoryTransactionRepository();
        var redis = Connect();
        var cache = CreateCache(inner, redis);
        var now = DateTimeOffset.UtcNow;

        var ids = Enumerable.Range(0, capacity + overflow)
            .Select(i => (Id: Guid.NewGuid(), Timestamp: now.AddMilliseconds(i)))
            .ToList();
        await Task.WhenAll(ids.Select(x => cache.TryAddAsync(MakeTransaction(x.Id, x.Timestamp))));

        var page = await cache.GetRecentAsync(capacity, cursor: null);

        page.Items.Should().HaveCount(capacity);
        var returnedIds = page.Items.Select(t => t.TransactionId).ToHashSet();
        // The oldest `overflow` items (lowest timestamps) must have been trimmed out.
        ids.Take(overflow).Select(x => x.Id).Should().NotIntersectWith(returnedIds);
        // The newest item must still be present.
        returnedIds.Should().Contain(ids[^1].Id);
    }
}
