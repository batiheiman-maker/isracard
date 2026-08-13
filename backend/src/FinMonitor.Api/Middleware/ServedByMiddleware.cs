namespace FinMonitor.Api.Middleware;

// Stamps every response with the instance that served it, so the distributed-pods
// bonus (round-robin across replicas) is directly observable via curl/devtools.
public sealed class ServedByMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly string InstanceName =
        Environment.GetEnvironmentVariable("INSTANCE_NAME") ?? Environment.MachineName;

    public ServedByMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers["X-Served-By"] = InstanceName;
        return _next(context);
    }
}
