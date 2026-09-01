using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;
using FinMonitor.Domain.Realtime;
using FinMonitor.Domain.Repositories;
using FinMonitor.Domain.Results;
using FinMonitor.Domain.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FinMonitor.Tests.Services;

public class TransactionServiceTests
{
    private readonly Mock<ITransactionRepository> _repository = new();
    private readonly Mock<ITransactionBroadcaster> _broadcaster = new();
    private readonly TransactionService _sut;

    public TransactionServiceTests()
    {
        _sut = new TransactionService(_repository.Object, _broadcaster.Object, NullLogger<TransactionService>.Instance);
    }

    private static CreateTransactionRequest ValidRequest(Guid? id = null) =>
        new(id ?? Guid.NewGuid(), 100m, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

    private static CreateTransactionRequest InvalidRequest() =>
        new(Guid.Empty, 100m, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

    [Fact]
    public async Task CreateAsync_WithValidRequest_AddsTransactionAndBroadcastsExactlyOnce()
    {
        var request = ValidRequest();
        _repository.Setup(r => r.TryAddAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Transaction t, CancellationToken _) => t);

        var result = await _sut.CreateAsync(request);

        result.Outcome.Should().Be(CreateTransactionOutcome.Created);
        _repository.Verify(r => r.TryAddAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Once);
        _broadcaster.Verify(
            b => b.BroadcastAsync(It.Is<Transaction>(t => t.TransactionId == request.TransactionId), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateAsync_WithInvalidRequest_DoesNotCallRepositoryOrBroadcast()
    {
        var invalidRequest = new CreateTransactionRequest(Guid.NewGuid(), -1, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

        var result = await _sut.CreateAsync(invalidRequest);

        result.Outcome.Should().Be(CreateTransactionOutcome.ValidationFailed);
        result.Errors.Should().NotBeEmpty();
        _repository.Verify(r => r.TryAddAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
        _broadcaster.Verify(b => b.BroadcastAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WithEmptyTransactionId_ReturnsValidationFailedAndDoesNotCallRepositoryOrBroadcast()
    {
        var result = await _sut.CreateAsync(InvalidRequest());

        result.Outcome.Should().Be(CreateTransactionOutcome.ValidationFailed);
        _repository.Verify(r => r.TryAddAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
        _broadcaster.Verify(b => b.BroadcastAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WhenDuplicateTransactionId_ReturnsConflictAndDoesNotBroadcast()
    {
        _repository.Setup(r => r.TryAddAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Transaction?)null);

        var result = await _sut.CreateAsync(ValidRequest());

        result.Outcome.Should().Be(CreateTransactionOutcome.Conflict);
        _broadcaster.Verify(b => b.BroadcastAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetRecentAsync_DelegatesToRepositoryWithLimitAndCursor()
    {
        var transactions = new List<Transaction> { new(Guid.NewGuid(), 1m, "USD", TransactionStatus.Pending, DateTimeOffset.UtcNow) };
        var page = new PagedResult<Transaction>(transactions, NextCursor: "cursor-token");
        var cursor = new TransactionCursor(DateTimeOffset.UtcNow, Guid.NewGuid());
        _repository.Setup(r => r.GetRecentAsync(500, cursor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

        var result = await _sut.GetRecentAsync(500, cursor);

        result.Should().BeSameAs(page);
    }
}
