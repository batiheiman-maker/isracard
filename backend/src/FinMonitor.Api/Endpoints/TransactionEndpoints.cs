using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Models;
using FinMonitor.Domain.Results;
using FinMonitor.Domain.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FinMonitor.Api.Endpoints;

public static class TransactionEndpoints
{
    private const int DefaultLimit = 500;
    private const int MaxLimit = 2_000;

    public static void MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/transactions").WithTags("Transactions");

        // TypedResults (not the untyped Results.X factory) so each branch's concrete response
        // type is visible to minimal APIs' OpenAPI metadata generation without a separate
        // .Produces<T>() call per status code, and so a unit test could assert on the returned
        // type directly instead of only on serialized JSON.
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

        // ?limit= bounds page size (default 500, up to MaxLimit); ?cursor= is the previous
        // page's NextCursor. See TransactionCursor for why this is keyset, not OFFSET, pagination.
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

        // Catch-up path for SignalR reconnects: a client that was briefly disconnected (network
        // blip, backplane hiccup) may have missed broadcasts while down. It calls this with the
        // highest `sequence` it already holds and gets exactly what it missed - not the whole
        // table, and not nothing. Only one possible outcome shape, so no Results<> union needed.
        group.MapGet("/since/{sequence:long}", async Task<Ok<IReadOnlyList<Transaction>>>
            (long sequence, ITransactionService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetSinceAsync(sequence, ct)))
        .WithName("GetTransactionsSince")
        .WithOpenApi();
    }
}
