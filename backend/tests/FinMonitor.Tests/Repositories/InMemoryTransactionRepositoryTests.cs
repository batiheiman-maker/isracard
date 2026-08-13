using FinMonitor.Domain.Models;
using FinMonitor.Domain.Repositories;
using FluentAssertions;

namespace FinMonitor.Tests.Repositories;

public class InMemoryTransactionRepositoryTests
{
    private static Transaction MakeTransaction(Guid? id = null, decimal amount = 100m) => new(
        id ?? Guid.NewGuid(), amount, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

    [Fact]
    public void TryAdd_NewTransaction_ReturnsTrueAndCanBeRetrievedById()
    {
        var repo = new InMemoryTransactionRepository();
        var transaction = MakeTransaction();

        var added = repo.TryAdd(transaction);

        added.Should().BeTrue();
        repo.GetById(transaction.TransactionId).Should().Be(transaction);
    }

    [Fact]
    public void TryAdd_DuplicateTransactionId_ReturnsFalseAndDoesNotOverwrite()
    {
        var repo = new InMemoryTransactionRepository();
        var id = Guid.NewGuid();
        var original = MakeTransaction(id, amount: 100m);
        var duplicate = MakeTransaction(id, amount: 999m);

        repo.TryAdd(original);
        var addedDuplicate = repo.TryAdd(duplicate);

        addedDuplicate.Should().BeFalse();
        repo.GetById(id)!.Amount.Should().Be(100m);
    }

    [Fact]
    public void GetById_UnknownId_ReturnsNull()
    {
        var repo = new InMemoryTransactionRepository();

        repo.GetById(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void GetAll_ReturnsSnapshotUnaffectedByLaterAdds()
    {
        var repo = new InMemoryTransactionRepository();
        repo.TryAdd(MakeTransaction());

        var snapshot = repo.GetAll();
        repo.TryAdd(MakeTransaction());

        snapshot.Should().HaveCount(1);
        repo.GetAll().Should().HaveCount(2);
    }
}
