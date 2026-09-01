using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;
using FinMonitor.Domain.Results;
using FinMonitor.Domain.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FinMonitor.Api.Endpoints;

public static class TransactionEndpoints
{
    private const int DefaultLimit = 1_000;
    private const int MaxLimit = 2_000;

    public static void MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/transactions").WithTags("Transactions");

        group.MapPost("/", async Task<Results<Created<Transaction>, BadRequest<ErrorResponse>, Conflict<ErrorResponse>, ProblemHttpResult>>
            (CreateTransactionRequest request, ITransactionService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            return result.Outcome switch
            {
                CreateTransactionOutcome.Created =>
                    TypedResults.Created($"/api/transactions/{result.Transaction!.TransactionId}", result.Transaction),
                CreateTransactionOutcome.ValidationFailed =>
                    TypedResults.BadRequest(new ErrorResponse(result.Errors)),
                CreateTransactionOutcome.Conflict =>
                    TypedResults.Conflict(new ErrorResponse(result.Errors)),
                _ => TypedResults.Problem("Unexpected outcome.")
            };
        })
        .WithName("CreateTransaction")
        .WithOpenApi();

        group.MapGet("/", async Task<Results<Ok<PagedResult<Transaction>>, BadRequest<ErrorResponse>>>
            (int? limit, string? cursor, ITransactionService service, CancellationToken ct) =>
        {
            var effectiveLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

            TransactionCursor? parsedCursor = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                if (!TransactionCursor.TryParse(cursor, out var c))
                {
                    return TypedResults.BadRequest(new ErrorResponse(new[] { "Invalid cursor." }));
                }
                parsedCursor = c;
            }

            return TypedResults.Ok(await service.GetRecentAsync(effectiveLimit, parsedCursor, ct));
        })
        .WithName("GetTransactions")
        .WithOpenApi();
    }
}
