using System.Text.Json.Serialization;
using FinMonitor.Api.Endpoints;
using FinMonitor.Api.Hubs;
using FinMonitor.Api.Middleware;
using FinMonitor.Api.Realtime;
using FinMonitor.Domain.Realtime;
using FinMonitor.Domain.Repositories;
using FinMonitor.Domain.Services;

var builder = WebApplication.CreateBuilder(args);

const string CorsPolicy = "Frontend";

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddCors(options =>
{
    var allowedOrigin = builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:5173";
    options.AddPolicy(CorsPolicy, policy => policy
        .WithOrigins(allowedOrigin)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

// Storage: in-memory per pod by default (single-instance/local dev); a shared PostgreSQL
// database in distributed mode so GET /api/transactions is consistent across pods. Postgres
// (not SQLite over a shared volume) because it's built for concurrent multi-writer access -
// see the ADR in README.md for why that distinction actually matters here.
var storageProvider = builder.Configuration["Storage:Provider"] ?? "InMemory";
if (storageProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration["Storage:ConnectionString"]
        ?? throw new InvalidOperationException("Storage:ConnectionString is required when Storage:Provider is Postgres.");
    var repository = PostgresTransactionRepository.CreateAsync(connectionString).GetAwaiter().GetResult();
    builder.Services.AddSingleton<ITransactionRepository>(repository);
}
else
{
    builder.Services.AddSingleton<ITransactionRepository, InMemoryTransactionRepository>();
}

builder.Services.AddSingleton<ITransactionBroadcaster, SignalRTransactionBroadcaster>();
builder.Services.AddSingleton<ITransactionService, TransactionService>();

// AddJsonProtocol configures the hub's own wire format - ConfigureHttpJsonOptions above only
// covers plain HTTP responses, so without this the enum serializes as a raw int over the hub.
var signalR = builder.Services.AddSignalR().AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
if (builder.Configuration.GetValue<bool>("Redis:Enabled"))
{
    var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379,abortConnect=false";
    signalR.AddStackExchangeRedis(redisConnectionString, options =>
    {
        options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("finmonitor:");
    });
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<ServedByMiddleware>();
app.UseCors(CorsPolicy);

app.MapTransactionEndpoints();
app.MapHub<TransactionHub>("/hubs/transactions");
app.MapGet("/healthz", () => Results.Ok("Healthy")).WithName("HealthCheck");

app.Run();

public partial class Program { }
