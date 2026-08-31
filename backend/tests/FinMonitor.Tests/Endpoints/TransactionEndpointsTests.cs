using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Exceptions;
using FinMonitor.Domain.Models;
using FinMonitor.Domain.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinMonitor.Tests.Endpoints;

public class TransactionEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly WebApplicationFactory<Program> _factory;

    public TransactionEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private static CreateTransactionRequest ValidRequest() =>
        new(Guid.NewGuid(), 42.50m, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

    [Fact]
    public async Task PostTransactions_WithValidPayload_Returns201AndPersists()
    {
        var client = _factory.CreateClient();
        var request = ValidRequest();

        var response = await client.PostAsJsonAsync("/api/transactions", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<Transaction>(JsonOptions);
        created!.TransactionId.Should().Be(request.TransactionId);
        created.Sequence.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PostTransactions_WithInvalidAmount_Returns400()
    {
        var client = _factory.CreateClient();
        var request = new CreateTransactionRequest(Guid.NewGuid(), -5, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

        var response = await client.PostAsJsonAsync("/api/transactions", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostTransactions_WithEmptyTransactionId_Returns400()
    {
        var client = _factory.CreateClient();
        var request = new CreateTransactionRequest(Guid.Empty, 42.50m, "USD", TransactionStatus.Completed, DateTimeOffset.UtcNow);

        var response = await client.PostAsJsonAsync("/api/transactions", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetTransactions_AfterPosting_ReturnsPostedTransaction()
    {
        var client = _factory.CreateClient();
        var request = ValidRequest();
        await client.PostAsJsonAsync("/api/transactions", request);

        var response = await client.GetAsync("/api/transactions");
        var page = await response.Content.ReadFromJsonAsync<PagedResult<Transaction>>(JsonOptions);

        page!.Items.Should().Contain(t => t.TransactionId == request.TransactionId);
    }

    [Fact]
    public async Task GetTransactions_WithLimitQueryParam_ReturnsAtMostThatManyRowsAndANextCursor()
    {
        var client = _factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/transactions", ValidRequest());
        }

        var response = await client.GetAsync("/api/transactions?limit=2");
        var page = await response.Content.ReadFromJsonAsync<PagedResult<Transaction>>(JsonOptions);

        page!.Items.Should().HaveCount(2);
        page.NextCursor.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetTransactions_WithCursorFromFirstPage_ReturnsNextPageWithNoOverlap()
    {
        // Note: this class's WebApplicationFactory (and its InMemoryTransactionRepository) is
        // shared across every test in the class via IClassFixture, so other tests' rows may
        // already be sitting in the store - assertions here only rely on the one property that
        // must hold regardless of what else is in there: consecutive pages never overlap.
        var client = _factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/transactions", ValidRequest());
        }

        var firstResponse = await client.GetAsync("/api/transactions?limit=3");
        var firstPage = await firstResponse.Content.ReadFromJsonAsync<PagedResult<Transaction>>(JsonOptions);

        var secondResponse = await client.GetAsync($"/api/transactions?limit=3&cursor={Uri.EscapeDataString(firstPage!.NextCursor!)}");
        var secondPage = await secondResponse.Content.ReadFromJsonAsync<PagedResult<Transaction>>(JsonOptions);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondPage!.Items.Should().NotBeEmpty();
        secondPage.Items.Select(t => t.TransactionId)
            .Should().NotIntersectWith(firstPage.Items.Select(t => t.TransactionId));
    }

    [Fact]
    public async Task GetTransactions_WithGarbageCursor_Returns400()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/transactions?cursor=not-a-real-cursor");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetTransactionsSince_ReturnsOnlyTransactionsPostedAfterGivenSequence()
    {
        var client = _factory.CreateClient();
        var firstResponse = await client.PostAsJsonAsync("/api/transactions", ValidRequest());
        var first = await firstResponse.Content.ReadFromJsonAsync<Transaction>(JsonOptions);
        var secondRequest = ValidRequest();
        await client.PostAsJsonAsync("/api/transactions", secondRequest);

        var sinceResponse = await client.GetAsync($"/api/transactions/since/{first!.Sequence}");
        var since = await sinceResponse.Content.ReadFromJsonAsync<List<Transaction>>(JsonOptions);

        sinceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        since.Should().Contain(t => t.TransactionId == secondRequest.TransactionId);
        since.Should().NotContain(t => t.TransactionId == first.TransactionId);
    }

    [Fact]
    public async Task PostTransactions_50ConcurrentRequests_AllSucceedAndAppearInGetAll()
    {
        var client = _factory.CreateClient();
        var requests = Enumerable.Range(0, 50).Select(_ => ValidRequest()).ToList();

        var responses = await Task.WhenAll(requests.Select(r => client.PostAsJsonAsync("/api/transactions", r)));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);

        var getResponse = await client.GetAsync("/api/transactions?limit=2000");
        var all = await getResponse.Content.ReadFromJsonAsync<PagedResult<Transaction>>(JsonOptions);
        requests.Should().OnlyContain(r => all!.Items.Any(t => t.TransactionId == r.TransactionId));
    }

    [Fact]
    public async Task GetHealthz_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetTransactions_WhenRepositoryThrowsStorageUnavailable_Returns503()
    {
        // Proves the StorageExceptionHandler wiring end-to-end: a repository failure surfaces
        // as StorageUnavailableException (the only type it knows about, translated from Npgsql
        // specifics at the real repository's boundary - see PostgresTransactionRepository) and
        // gets mapped to 503, not the generic 500 AddProblemDetails would otherwise produce.
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITransactionRepository>();
                services.AddSingleton<ITransactionRepository>(new AlwaysUnavailableTransactionRepository());
            });
        }).CreateClient();

        var response = await client.GetAsync("/api/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    private sealed class AlwaysUnavailableTransactionRepository : ITransactionRepository
    {
        private static StorageUnavailableException Fail() =>
            new("Simulated storage failure.", new TimeoutException());

        public Task<Transaction?> TryAddAsync(Transaction transaction, CancellationToken cancellationToken = default) => throw Fail();
        public Task<Transaction?> GetByIdAsync(Guid transactionId, CancellationToken cancellationToken = default) => throw Fail();
        public Task<PagedResult<Transaction>> GetRecentAsync(int limit, TransactionCursor? cursor, CancellationToken cancellationToken = default) => throw Fail();
        public Task<IReadOnlyList<Transaction>> GetSinceAsync(long sinceSequence, CancellationToken cancellationToken = default) => throw Fail();
    }
}
