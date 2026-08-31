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
    private readonly TransactionBroadcastQueue _broadcastQueue = new();
    private readonly TransactionService _sut;

    public TransactionServiceTests()
    {
        _sut = new TransactionService(_repository.Object, _broadcastQueue, NullLogger<TransactionService>.Instance);
    }

    private static CreateTransactionRequest ValidRequest(Guid? id = null) =>
        new(id ?? Guid.NewGuid(), 100m, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

    private static CreateTransactionRequest InvalidRequest() =>
        new(Guid.Empty, 100m, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

    [Fact]
    public async Task CreateAsync_WithValidRequest_AddsTransactionAndEnqueuesExactlyOneBroadcast()
    {
        var request = ValidRequest();
        _repository.Setup(r => r.TryAddAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Transaction t, CancellationToken _) => t with { Sequence = 1 });

        var result = await _sut.CreateAsync(request);

        result.Outcome.Should().Be(CreateTransactionOutcome.Created);
        _repository.Verify(r => r.TryAddAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Once);
        _broadcastQueue.Reader.TryRead(out var queued).Should().BeTrue();
        queued!.TransactionId.Should().Be(request.TransactionId);
    }

    [Fact]
    public async Task CreateAsync_WithInvalidRequest_DoesNotCallRepositoryOrEnqueueBroadcast()
    {
        var invalidRequest = new CreateTransactionRequest(Guid.NewGuid(), -1, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

        var result = await _sut.CreateAsync(invalidRequest);

        result.Outcome.Should().Be(CreateTransactionOutcome.ValidationFailed);
        result.Errors.Should().NotBeEmpty();
        _repository.Verify(r => r.TryAddAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
        _broadcastQueue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_WithEmptyTransactionId_ReturnsValidationFailedAndDoesNotCallRepositoryOrEnqueueBroadcast()
    {
        var result = await _sut.CreateAsync(InvalidRequest());

        result.Outcome.Should().Be(CreateTransactionOutcome.ValidationFailed);
        _repository.Verify(r => r.TryAddAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
        _broadcastQueue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_WhenDuplicateTransactionId_ReturnsConflictAndDoesNotEnqueueBroadcast()
    {
        _repository.Setup(r => r.TryAddAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Transaction?)null);

        var result = await _sut.CreateAsync(ValidRequest());

        result.Outcome.Should().Be(CreateTransactionOutcome.Conflict);
        _broadcastQueue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetRecentAsync_DelegatesToRepositoryWithLimitAndCursor()
    {
        var transactions = new List<Transaction> { new(Guid.NewGuid(), 1m, "USD", TransactionStatus.Pending, DateTimeOffset.UtcNow, 1) };
        var page = new PagedResult<Transaction>(transactions, NextCursor: "cursor-token");
        var cursor = new TransactionCursor(DateTimeOffset.UtcNow, Guid.NewGuid());
        _repository.Setup(r => r.GetRecentAsync(500, cursor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

        var result = await _sut.GetRecentAsync(500, cursor);

        result.Should().BeSameAs(page);
    }

    [Fact]
    public async Task GetSinceAsync_DelegatesToRepository()
    {
        var transactions = new List<Transaction> { new(Guid.NewGuid(), 1m, "USD", TransactionStatus.Pending, DateTimeOffset.UtcNow, 5) };
        _repository.Setup(r => r.GetSinceAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Transaction>)transactions);

        var result = await _sut.GetSinceAsync(3);

        result.Should().BeSameAs(transactions);
    }
}
