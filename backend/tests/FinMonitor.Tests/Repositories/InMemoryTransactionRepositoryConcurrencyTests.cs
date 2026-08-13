using FinMonitor.Domain.Models;
using FinMonitor.Domain.Repositories;
using FluentAssertions;

namespace FinMonitor.Tests.Repositories;

public class InMemoryTransactionRepositoryConcurrencyTests
{
    private static Transaction MakeTransaction(Guid id) => new(
        id, 100m, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

    [Fact]
    public async Task TryAdd_With1000ConcurrentUniqueTransactions_AllStoredWithNoLostUpdates()
    {
        var repo = new InMemoryTransactionRepository();
        var ids = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(ids.Select(id => Task.Run(() => repo.TryAdd(MakeTransaction(id)))));

        repo.GetAll().Should().HaveCount(1000);
        ids.Should().OnlyContain(id => repo.GetById(id) != null);
    }

    [Fact]
    public async Task TryAdd_With100ConcurrentDuplicateTransactionIds_OnlyOneSucceeds()
    {
        var repo = new InMemoryTransactionRepository();
        var id = Guid.NewGuid();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 100).Select(_ => Task.Run(() => repo.TryAdd(MakeTransaction(id)))));

        results.Count(succeeded => succeeded).Should().Be(1);
        repo.GetAll().Should().HaveCount(1);
    }

    [Fact]
    public async Task GetAll_DuringConcurrentWrites_NeverThrows()
    {
        var repo = new InMemoryTransactionRepository();
        var writers = Enumerable.Range(0, 200)
            .Select(_ => (Task)Task.Run(() => repo.TryAdd(MakeTransaction(Guid.NewGuid()))));
        var readers = Enumerable.Range(0, 200)
            .Select(_ => (Task)Task.Run(() => repo.GetAll()));

        Func<Task> act = () => Task.WhenAll(writers.Concat(readers));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void TryAdd_BeyondCapacity_EvictsOldestFirst()
    {
        const int capacity = 5_000;
        var repo = new InMemoryTransactionRepository();
        var ids = Enumerable.Range(0, capacity + 1_000).Select(_ => Guid.NewGuid()).ToList();

        // Sequential, not concurrent: eviction order is only meaningful relative to insertion
        // order, which concurrent Task.Run scheduling wouldn't guarantee to match `ids`.
        foreach (var id in ids)
        {
            repo.TryAdd(MakeTransaction(id));
        }

        repo.GetAll().Should().HaveCount(capacity);
        ids.Take(1_000).Should().OnlyContain(id => repo.GetById(id) == null);
        ids.TakeLast(capacity).Should().OnlyContain(id => repo.GetById(id) != null);
    }

    [Fact]
    public async Task TryAdd_BeyondCapacity_UnderConcurrentLoad_NeverExceedsCapAndNeverThrows()
    {
        const int capacity = 5_000;
        var repo = new InMemoryTransactionRepository();
        var ids = Enumerable.Range(0, capacity + 1_000).Select(_ => Guid.NewGuid()).ToList();

        Func<Task> act = () => Task.WhenAll(ids.Select(id => Task.Run(() => repo.TryAdd(MakeTransaction(id)))));

        await act.Should().NotThrowAsync();
        repo.GetAll().Count.Should().BeLessOrEqualTo(capacity);
    }
}
