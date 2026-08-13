using FinMonitor.Domain.DTOs;
using FinMonitor.Domain.Services;

namespace FinMonitor.Api.Endpoints;

public static class TransactionEndpoints
{
    public static void MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/transactions").WithTags("Transactions");

        group.MapPost("/", async (CreateTransactionRequest request, ITransactionService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            return result.Outcome switch
            {
                CreateTransactionOutcome.Created =>
                    Results.Created($"/api/transactions/{result.Transaction!.TransactionId}", result.Transaction),
                CreateTransactionOutcome.ValidationFailed =>
                    Results.BadRequest(new { errors = result.Errors }),
                CreateTransactionOutcome.Conflict =>
                    Results.Conflict(new { errors = result.Errors }),
                _ => Results.Problem("Unexpected outcome.")
            };
        })
        .WithName("CreateTransaction")
        .WithOpenApi();

        group.MapGet("/", (ITransactionService service) => Results.Ok(service.GetAll()))
            .WithName("GetTransactions")
            .WithOpenApi();
    }
}
