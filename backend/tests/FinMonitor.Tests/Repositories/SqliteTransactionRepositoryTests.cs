using FinMonitor.Domain.Models;
using FinMonitor.Domain.Repositories;
using FluentAssertions;

namespace FinMonitor.Tests.Repositories;

public class SqliteTransactionRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"finmonitor-test-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_dbPath}";

    private static Transaction MakeTransaction(Guid? id = null, decimal amount = 100m) => new(
        id ?? Guid.NewGuid(), amount, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

    [Fact]
    public void TryAdd_NewTransaction_ReturnsTrueAndCanBeRetrievedById()
    {
        var repo = new SqliteTransactionRepository(ConnectionString);
        var transaction = MakeTransaction();

        var added = repo.TryAdd(transaction);

        added.Should().BeTrue();
        repo.GetById(transaction.TransactionId).Should().Be(transaction);
    }

    [Fact]
    public void TryAdd_DuplicateTransactionId_ReturnsFalseAndDoesNotOverwrite()
    {
        var repo = new SqliteTransactionRepository(ConnectionString);
        var id = Guid.NewGuid();

        repo.TryAdd(MakeTransaction(id, amount: 100m));
        var addedDuplicate = repo.TryAdd(MakeTransaction(id, amount: 999m));

        addedDuplicate.Should().BeFalse();
        repo.GetById(id)!.Amount.Should().Be(100m);
    }

    [Fact]
    public void GetById_UnknownId_ReturnsNull()
    {
        var repo = new SqliteTransactionRepository(ConnectionString);

        repo.GetById(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void DataWrittenByOneInstance_IsVisibleToAnotherInstanceOnSameFile_SimulatingSharedDbAcrossPods()
    {
        var podA = new SqliteTransactionRepository(ConnectionString);
        var podB = new SqliteTransactionRepository(ConnectionString);
        var transaction = MakeTransaction();

        podA.TryAdd(transaction);

        podB.GetById(transaction.TransactionId).Should().Be(transaction);
        podB.GetAll().Should().ContainSingle();
    }

    [Fact]
    public async Task TryAdd_With200ConcurrentUniqueTransactionsAcrossMultipleInstances_AllStoredWithNoLostUpdates()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(ids.Select(id => Task.Run(() =>
        {
            var repo = new SqliteTransactionRepository(ConnectionString);
            repo.TryAdd(MakeTransaction(id));
        })));

        var verifier = new SqliteTransactionRepository(ConnectionString);
        verifier.GetAll().Should().HaveCount(200);
        ids.Should().OnlyContain(id => verifier.GetById(id) != null);
    }

    public void Dispose()
    {
        SqliteTransactionRepository.ClearPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        foreach (var extra in new[] { $"{_dbPath}-wal", $"{_dbPath}-shm" })
        {
            if (File.Exists(extra)) File.Delete(extra);
        }
    }
}
