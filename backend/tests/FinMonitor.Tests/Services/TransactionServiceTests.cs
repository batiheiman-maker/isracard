using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;
using FinMonitor.Domain.Realtime;
using FinMonitor.Domain.Repositories;
using FinMonitor.Domain.Services;
using FluentAssertions;
using Moq;

namespace FinMonitor.Tests.Services;

public class TransactionServiceTests
{
    private readonly Mock<ITransactionRepository> _repository = new();
    private readonly Mock<ITransactionBroadcaster> _broadcaster = new();
    private readonly TransactionService _sut;

    public TransactionServiceTests()
    {
        _sut = new TransactionService(_repository.Object, _broadcaster.Object);
    }

    private static CreateTransactionRequest ValidRequest(Guid? id = null) =>
        new(id ?? Guid.NewGuid(), 100m, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

    private static CreateTransactionRequest InvalidRequest() =>
        new(Guid.Empty, 100m, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

    [Fact]
    public async Task CreateAsync_WithValidRequest_AddsTransactionAndBroadcastsExactlyOnce()
    {
        _repository.Setup(r => r.TryAdd(It.IsAny<Transaction>())).Returns(true);

        var result = await _sut.CreateAsync(ValidRequest());

        result.Outcome.Should().Be(CreateTransactionOutcome.Created);
        _repository.Verify(r => r.TryAdd(It.IsAny<Transaction>()), Times.Once);
        _broadcaster.Verify(b => b.BroadcastAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_WithInvalidRequest_DoesNotCallRepositoryOrBroadcaster()
    {
        var invalidRequest = new CreateTransactionRequest(Guid.NewGuid(), -1, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

        var result = await _sut.CreateAsync(invalidRequest);

        result.Outcome.Should().Be(CreateTransactionOutcome.ValidationFailed);
        result.Errors.Should().NotBeEmpty();
        _repository.Verify(r => r.TryAdd(It.IsAny<Transaction>()), Times.Never);
        _broadcaster.Verify(b => b.BroadcastAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WithEmptyTransactionId_ReturnsValidationFailedAndDoesNotCallRepositoryOrBroadcaster()
    {
        var result = await _sut.CreateAsync(InvalidRequest());

        result.Outcome.Should().Be(CreateTransactionOutcome.ValidationFailed);
        _repository.Verify(r => r.TryAdd(It.IsAny<Transaction>()), Times.Never);
        _broadcaster.Verify(b => b.BroadcastAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WhenDuplicateTransactionId_ReturnsConflictAndDoesNotBroadcast()
    {
        _repository.Setup(r => r.TryAdd(It.IsAny<Transaction>())).Returns(false);

        var result = await _sut.CreateAsync(ValidRequest());

        result.Outcome.Should().Be(CreateTransactionOutcome.Conflict);
        _broadcaster.Verify(b => b.BroadcastAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void GetAll_DelegatesToRepository()
    {
        var transactions = new List<Transaction> { new(Guid.NewGuid(), 1m, "USD", TransactionStatus.Pending, DateTimeOffset.UtcNow) };
        _repository.Setup(r => r.GetAll()).Returns(transactions);

        var result = _sut.GetAll();

        result.Should().BeSameAs(transactions);
    }
}
