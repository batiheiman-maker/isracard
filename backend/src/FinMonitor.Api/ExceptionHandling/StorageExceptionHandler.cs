using FinMonitor.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FinMonitor.Api.ExceptionHandling;

// Handles exactly one thing, named for it: a storage dependency that's temporarily unreachable.
// It only knows about StorageUnavailableException, never Npgsql or any other storage-technology
// type - that translation happens once, at the repository boundary (see
// EfTransactionRepository.ExecuteAsync). If another category of exception needs its own
// mapping later (validation, authorization, ...), add another IExceptionHandler beside this one
// rather than growing this into a big switch - ASP.NET Core already tries every registered
// IExceptionHandler in registration order until one returns true, so no extra plumbing is
// needed to support that.
public sealed class StorageExceptionHandler : IExceptionHandler
{
    private readonly ILogger<StorageExceptionHandler> _logger;

    public StorageExceptionHandler(ILogger<StorageExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not StorageUnavailableException)
        {
            return false; // not ours to handle - falls through to AddProblemDetails' generic 500
        }

        _logger.LogError(exception, "Transient storage failure handling {Method} {Path}",
            httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Storage temporarily unavailable",
            Detail = "The request could not be completed because a storage dependency is temporarily unreachable. Retry the request.",
        }, cancellationToken);

        return true;
    }
}
