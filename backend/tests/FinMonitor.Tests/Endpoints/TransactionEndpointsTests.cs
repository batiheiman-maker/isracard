using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

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
        var transactions = await response.Content.ReadFromJsonAsync<List<Transaction>>(JsonOptions);

        transactions.Should().Contain(t => t.TransactionId == request.TransactionId);
    }

    [Fact]
    public async Task PostTransactions_50ConcurrentRequests_AllSucceedAndAppearInGetAll()
    {
        var client = _factory.CreateClient();
        var requests = Enumerable.Range(0, 50).Select(_ => ValidRequest()).ToList();

        var responses = await Task.WhenAll(requests.Select(r => client.PostAsJsonAsync("/api/transactions", r)));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);

        var getResponse = await client.GetAsync("/api/transactions");
        var all = await getResponse.Content.ReadFromJsonAsync<List<Transaction>>(JsonOptions);
        requests.Should().OnlyContain(r => all!.Any(t => t.TransactionId == r.TransactionId));
    }

    [Fact]
    public async Task GetHealthz_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
