using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;
using FinMonitor.Domain.Repositories;
using FluentAssertions;

namespace FinMonitor.Tests.Repositories;

public class InMemoryTransactionRepositoryTests
{
    private static Transaction MakeTransaction(Guid? id = null, decimal amount = 100m, DateTimeOffset? timestamp = null) => new(
        id ?? Guid.NewGuid(), amount, "USD", TransactionStatus.Completed, timestamp ?? DateTimeOffset.UtcNow);

    [Fact]
    public async Task TryAddAsync_NewTransaction_ReturnsStoredTransactionWithAssignedSequenceAndCanBeRetrievedById()
    {
        var repo = new InMemoryTransactionRepository();
        var transaction = MakeTransaction();

        var stored = await repo.TryAddAsync(transaction);

        stored.Should().NotBeNull();
        stored!.Sequence.Should().BeGreaterThan(0);
        (await repo.GetByIdAsync(transaction.TransactionId)).Should().Be(stored);
    }

    [Fact]
    public async Task TryAddAsync_DuplicateTransactionId_ReturnsNullAndDoesNotOverwrite()
    {
        var repo = new InMemoryTransactionRepository();
        var id = Guid.NewGuid();
        var original = MakeTransaction(id, amount: 100m);
        var duplicate = MakeTransaction(id, amount: 999m);

        await repo.TryAddAsync(original);
        var addedDuplicate = await repo.TryAddAsync(duplicate);

        addedDuplicate.Should().BeNull();
        (await repo.GetByIdAsync(id))!.Amount.Should().Be(100m);
    }

    [Fact]
    public async Task GetByIdAsync_UnknownId_ReturnsNull()
    {
        var repo = new InMemoryTransactionRepository();

        (await repo.GetByIdAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsSnapshotUnaffectedByLaterAdds()
    {
        var repo = new InMemoryTransactionRepository();
        await repo.TryAddAsync(MakeTransaction());

        var snapshot = await repo.GetRecentAsync(500, cursor: null);
        await repo.TryAddAsync(MakeTransaction());

        snapshot.Items.Should().HaveCount(1);
        (await repo.GetRecentAsync(500, cursor: null)).Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRecentAsync_WithLimitLowerThanStoredCount_ReturnsOnlyLimitRowsAndANextCursor()
    {
        var repo = new InMemoryTransactionRepository();
        for (var i = 0; i < 10; i++)
        {
            await repo.TryAddAsync(MakeTransaction());
        }

        var page = await repo.GetRecentAsync(3, cursor: null);

        page.Items.Should().HaveCount(3);
        page.NextCursor.Should().NotBeNull();
    }

    [Fact]
    public async Task GetRecentAsync_LastPage_HasNoNextCursor()
    {
        var repo = new InMemoryTransactionRepository();
        for (var i = 0; i < 5; i++)
        {
            await repo.TryAddAsync(MakeTransaction(timestamp: DateTimeOffset.UtcNow.AddSeconds(-i)));
        }

        var firstPage = await repo.GetRecentAsync(3, cursor: null);
        TransactionCursor.TryParse(firstPage.NextCursor, out var cursor).Should().BeTrue();
        var secondPage = await repo.GetRecentAsync(3, cursor);

        secondPage.Items.Should().HaveCount(2);
        secondPage.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task GetRecentAsync_PagingThroughWithCursor_CoversEveryRowExactlyOnceInDescendingOrder()
    {
        var repo = new InMemoryTransactionRepository();
        var now = DateTimeOffset.UtcNow;
        var expectedIds = new List<Guid>();
        for (var i = 0; i < 9; i++)
        {
            // Strictly descending timestamps by insertion order, so page order is deterministic
            // instead of depending on Guid tiebreaks between same-millisecond inserts.
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

    [Fact]
    public async Task GetRecentAsync_WithCursor_IsUnaffectedByNewerInsertsAfterTheCursorPageWasFetched()
    {
        // Proves keyset pagination doesn't have offset pagination's classic drift bug: a row
        // inserted "above" an already-fetched cursor must not shift, skip, or duplicate rows in
        // the next page, because the cursor anchors to the last row's own key, not a position.
        var repo = new InMemoryTransactionRepository();
        var now = DateTimeOffset.UtcNow;
        var olderIds = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var stored = await repo.TryAddAsync(MakeTransaction(timestamp: now.AddSeconds(-i)));
            olderIds.Add(stored!.TransactionId);
        }

        var firstPage = await repo.GetRecentAsync(2, cursor: null);
        TransactionCursor.TryParse(firstPage.NextCursor, out var cursor).Should().BeTrue();

        // A brand-new, newer-than-everything transaction lands while the client holds the cursor.
        await repo.TryAddAsync(MakeTransaction(timestamp: now.AddSeconds(1)));

        var secondPage = await repo.GetRecentAsync(2, cursor);

        secondPage.Items.Select(t => t.TransactionId).Should().Equal(olderIds.Skip(2).Take(2));
    }

    [Fact]
    public async Task GetSinceAsync_ReturnsOnlyTransactionsAfterGivenSequenceInAscendingOrder()
    {
        var repo = new InMemoryTransactionRepository();
        var first = await repo.TryAddAsync(MakeTransaction());
        var second = await repo.TryAddAsync(MakeTransaction());
        var third = await repo.TryAddAsync(MakeTransaction());

        var since = await repo.GetSinceAsync(first!.Sequence);

        since.Should().HaveCount(2);
        since.Should().BeInAscendingOrder(t => t.Sequence);
        since.Select(t => t.TransactionId).Should().ContainInOrder(second!.TransactionId, third!.TransactionId);
    }

    [Fact]
    public async Task GetSinceAsync_WithNoNewerTransactions_ReturnsEmpty()
    {
        var repo = new InMemoryTransactionRepository();
        var stored = await repo.TryAddAsync(MakeTransaction());

        (await repo.GetSinceAsync(stored!.Sequence)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetSinceAsync_WithMoreThanMaxCatchUpBatchNewerTransactions_ReturnsOnlyTheOldestBatch()
    {
        var repo = new InMemoryTransactionRepository();
        for (var i = 0; i < 1_200; i++)
        {
            await repo.TryAddAsync(MakeTransaction());
        }

        var since = await repo.GetSinceAsync(0);

        since.Should().HaveCount(1_000);
        since.Should().BeInAscendingOrder(t => t.Sequence);
    }
}
