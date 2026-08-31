using FinMonitor.Domain.Models;
using FinMonitor.Domain.Repositories;
using FluentAssertions;

namespace FinMonitor.Tests.Repositories;

public class InMemoryTransactionRepositoryConcurrencyTests
{
    private static Transaction MakeTransaction(Guid id) => new(
        id, 100m, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

    [Fact]
    public async Task TryAddAsync_With1000ConcurrentUniqueTransactions_AllStoredWithNoLostUpdates()
    {
        var repo = new InMemoryTransactionRepository();
        var ids = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(ids.Select(id => Task.Run(() => repo.TryAddAsync(MakeTransaction(id)))));

        (await repo.GetRecentAsync(int.MaxValue, cursor: null)).Items.Should().HaveCount(1000);
        foreach (var id in ids)
        {
            (await repo.GetByIdAsync(id)).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task TryAddAsync_With100ConcurrentDuplicateTransactionIds_OnlyOneSucceeds()
    {
        var repo = new InMemoryTransactionRepository();
        var id = Guid.NewGuid();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 100).Select(_ => Task.Run(() => repo.TryAddAsync(MakeTransaction(id)))));

        results.Count(stored => stored is not null).Should().Be(1);
        (await repo.GetRecentAsync(int.MaxValue, cursor: null)).Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetRecentAsync_DuringConcurrentWrites_NeverThrows()
    {
        var repo = new InMemoryTransactionRepository();
        var writers = Enumerable.Range(0, 200)
            .Select(_ => (Task)Task.Run(() => repo.TryAddAsync(MakeTransaction(Guid.NewGuid()))));
        var readers = Enumerable.Range(0, 200)
            .Select(_ => (Task)Task.Run(() => repo.GetRecentAsync(int.MaxValue, cursor: null)));

        Func<Task> act = () => Task.WhenAll(writers.Concat(readers));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TryAddAsync_BeyondCapacity_EvictsOldestFirst()
    {
        const int capacity = 5_000;
        var repo = new InMemoryTransactionRepository();
        var ids = Enumerable.Range(0, capacity + 1_000).Select(_ => Guid.NewGuid()).ToList();

        // Sequential, not concurrent: eviction order is only meaningful relative to insertion
        // order, which concurrent Task.Run scheduling wouldn't guarantee to match `ids`.
        foreach (var id in ids)
        {
            await repo.TryAddAsync(MakeTransaction(id));
        }

        (await repo.GetRecentAsync(int.MaxValue, cursor: null)).Items.Should().HaveCount(capacity);
        foreach (var evictedId in ids.Take(1_000))
        {
            (await repo.GetByIdAsync(evictedId)).Should().BeNull();
        }
        foreach (var survivingId in ids.TakeLast(capacity))
        {
            (await repo.GetByIdAsync(survivingId)).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task TryAddAsync_BeyondCapacity_UnderConcurrentLoad_NeverExceedsCapAndNeverThrows()
    {
        const int capacity = 5_000;
        var repo = new InMemoryTransactionRepository();
        var ids = Enumerable.Range(0, capacity + 1_000).Select(_ => Guid.NewGuid()).ToList();

        Func<Task> act = () => Task.WhenAll(ids.Select(id => Task.Run(() => repo.TryAddAsync(MakeTransaction(id)))));

        await act.Should().NotThrowAsync();
        (await repo.GetRecentAsync(int.MaxValue, cursor: null)).Items.Count.Should().BeLessOrEqualTo(capacity);
    }
}
